using Microsoft.Extensions.Logging.Abstractions;
using Smzdm.Monitor;
using Xunit;

namespace Smzdm.Monitor.Tests;

public sealed class PersistenceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "smzdm-tests-" + Guid.NewGuid());
    private readonly TestClock _clock = new();
    private MonitorOptions Options => new() { DataDirectory = _directory, TopCount = 2 };

    [Fact]
    public async Task SilentStartupBaselineDoesNotPrepareAnyLeaderboardNotification()
    {
        _clock.Now = DateTimeOffset.Parse("2026-11-10T10:12:00+08:00");
        var sender = new StubSender();
        var engine = Engine(new StubSource(), sender);
        await engine.InitializeAsync(default);
        await engine.CollectBaselineAsync(default);
        await engine.DeliverAsync(default);
        Assert.NotNull(engine.State.Snapshot);
        Assert.Null(engine.State.Pending);
        Assert.Empty(sender.Calls);
    }

    [Fact]
    public async Task FirstHalfHourSlotDoesNotSendLateHourlyMessageButNextWholeHourDoes()
    {
        _clock.Now = DateTimeOffset.Parse("2026-11-10T10:30:00+08:00");
        var sender = new StubSender();
        var engine = Engine(new StubSource(), sender);
        await engine.InitializeAsync(default);
        await engine.CollectScheduledAsync(_clock.Now, default);
        await engine.DeliverAsync(default);
        Assert.Empty(sender.Calls);
        Assert.Null(engine.State.Pending);
        _clock.Now += TimeSpan.FromMinutes(30);
        await engine.CollectScheduledAsync(_clock.Now, default);
        await engine.DeliverAsync(default);
        Assert.Null(Assert.Single(sender.Calls).MessageId);
    }

    [Fact]
    public async Task CollectionThatFinishesInNextPeriodCannotCreateLateMessage()
    {
        _clock.Now = DateTimeOffset.Parse("2026-11-10T11:00:03+08:00");
        var sender = new StubSender();
        var engine = Engine(new StubSource(), sender);
        await engine.InitializeAsync(default);
        await engine.CollectScheduledAsync(DateTimeOffset.Parse("2026-11-10T10:00:00+08:00"), default);
        await engine.DeliverAsync(default);
        Assert.Empty(sender.Calls);
        Assert.Null(engine.State.Pending);
    }

    [Fact]
    public async Task UpgradeStartsRichMessageInsteadOfTryingToEditLegacyPhotoThenResumesEditing()
    {
        var source = new StubSource();
        var first = Engine(source, new StubSender());
        await first.InitializeAsync(default);
        await first.CollectAsync(default);
        first.State.Published = new(55, first.State.Pending!.PeriodStart, "旧图片说明");
        await new StateStore(Options).SaveAsync(first.State, default);
        var sender = new StubSender();
        var restarted = Engine(source, sender);
        await restarted.InitializeAsync(default);
        await restarted.DeliverAsync(default);
        Assert.Null(Assert.Single(sender.Calls).MessageId);
        Assert.True(restarted.State.Published!.IsRich);
        var messageId = restarted.State.Published.MessageId;
        _clock.Now += TimeSpan.FromMinutes(30);
        var again = Engine(source, sender);
        await again.InitializeAsync(default);
        await again.CollectAsync(default);
        await again.DeliverAsync(default);
        Assert.Equal(messageId, sender.Calls[1].MessageId);
    }

    [Fact]
    public async Task LongRichPendingMessageSurvivesStateRoundTripWithAllTwentyProducts()
    {
        var deals = Enumerable.Range(1, 20).Select(i => LeaderboardTests.Deal(i.ToString(), i) with
            { Title = new string('长', 220) }).ToList();
        var policy = new NotificationPolicy(new());
        var comparison = Leaderboard.Compare(null, deals, _clock.Now, Options);
        var report = NotificationPolicy.BuildReport(null, comparison, policy.PeriodStart(_clock.Now));
        var text = RichMessageFormatter.Format(report, comparison.Snapshot, Options, policy);
        Assert.True(MessageFormatter.PlainText(text).Length > MessageFormatter.MaxLength);
        var store = new StateStore(Options);
        await store.SaveAsync(new() { Pending = new(text, report.PeriodStart, _clock.Now, deals, IsRich: true) }, default);
        var pending = (await store.LoadAsync(default)).Pending!;
        Assert.True(pending.IsRich);
        Assert.Equal(text, pending.Text);
        Assert.Equal(deals, pending.Items);
    }

    [Fact]
    public async Task SendsOneMessagePerHourAndEditsSameMessageAtHalfHour()
    {
        var source = new StubSource();
        var sender = new StubSender();
        var engine = Engine(source, sender);
        await engine.InitializeAsync(default);
        await engine.CollectAsync(default);
        await engine.DeliverAsync(default);
        var firstId = engine.State.Published!.MessageId;
        _clock.Now += TimeSpan.FromMinutes(30);
        source.Deals = [LeaderboardTests.Deal("3"), LeaderboardTests.Deal("1", 2)];
        await engine.CollectAsync(default);
        await engine.DeliverAsync(default);
        Assert.Equal(firstId, engine.State.Published!.MessageId);
        Assert.Contains("新进 1", sender.Calls[1].Text);
        _clock.Now += TimeSpan.FromMinutes(30);
        await engine.CollectAsync(default);
        await engine.DeliverAsync(default);
        Assert.NotEqual(firstId, engine.State.Published!.MessageId);
        Assert.Equal(new long?[] { null, firstId, null }, sender.Calls.Select(x => x.MessageId));
        Assert.Null((await new StateStore(Options).LoadAsync(default)).Pending);
    }

    [Fact]
    public async Task RestartContinuesEditingPublishedMessage()
    {
        var source = new StubSource();
        var sender = new StubSender();
        var engine = Engine(source, sender);
        await engine.InitializeAsync(default);
        await engine.CollectAsync(default);
        await engine.DeliverAsync(default);
        var id = engine.State.Published!.MessageId;
        _clock.Now += TimeSpan.FromMinutes(30);
        var restartedSender = new StubSender();
        var restarted = Engine(source, restartedSender);
        await restarted.InitializeAsync(default);
        await restarted.CollectAsync(default);
        await restarted.DeliverAsync(default);
        Assert.Equal(id, Assert.Single(restartedSender.Calls).MessageId);
        Assert.Equal(2, restarted.State.Snapshot!.Items[0].ConsecutivePolls);
    }

    [Fact]
    public async Task QuietWindowSuppressesNewMessagesEditsAndPendingRetriesThenSendsOnlyLatestAtSeven()
    {
        _clock.Now = DateTimeOffset.Parse("2026-11-09T00:30:00+08:00");
        var source = new StubSource();
        var sender = new StubSender { FailOnCall = 1 };
        var engine = Engine(source, sender);
        await engine.InitializeAsync(default);
        await engine.CollectAsync(default);
        await Assert.ThrowsAsync<TelegramDeliveryException>(() => engine.DeliverAsync(default));
        _clock.Now = DateTimeOffset.Parse("2026-11-09T01:00:00+08:00");
        await engine.DeliverAsync(default);
        Assert.Null(engine.State.Pending);
        for (var hour = 1; hour < 7; hour++)
        {
            _clock.Now = new DateTimeOffset(2026, 11, 9, hour, 30, 0, TimeSpan.FromHours(8));
            source.Deals = [LeaderboardTests.Deal((hour + 10).ToString()), LeaderboardTests.Deal("1", 2)];
            await engine.CollectAsync(default);
            await engine.DeliverAsync(default);
            Assert.Null(engine.State.Pending);
        }
        Assert.Single(sender.Calls); // Only the failed 00:30 attempt.
        _clock.Now = DateTimeOffset.Parse("2026-11-09T07:00:00+08:00");
        source.Deals = [LeaderboardTests.Deal("99"), LeaderboardTests.Deal("1", 2)];
        await engine.CollectAsync(default);
        await engine.DeliverAsync(default);
        Assert.Equal(2, sender.Calls.Count);
        Assert.Null(sender.Calls[1].MessageId);
        Assert.Contains("/p/99/", sender.Calls[1].Text);
        Assert.DoesNotContain("/p/16/", sender.Calls[1].Text);
    }

    [Fact]
    public async Task QuietWindowAlsoSuppressesEditsToExistingMessage()
    {
        var notifications = new NotificationOptions { NewMessageIntervalHours = 3, QuietStartHour = 11, QuietEndHour = 12 };
        var sender = new StubSender();
        var source = new StubSource();
        var engine = Engine(source, sender, notifications);
        await engine.InitializeAsync(default);
        await engine.CollectAsync(default);
        await engine.DeliverAsync(default);
        _clock.Now = DateTimeOffset.Parse("2026-11-09T10:30:00+08:00");
        await engine.CollectAsync(default); // Prepared edit for the same 09:00–12:00 period.
        _clock.Now = DateTimeOffset.Parse("2026-11-09T11:00:00+08:00");
        await engine.DeliverAsync(default);
        Assert.Single(sender.Calls);
        Assert.Null(engine.State.Pending);
    }

    [Fact]
    public async Task ExpiredPendingReportIsDroppedInsteadOfSentIntoNextHour()
    {
        var sender = new StubSender();
        var engine = Engine(new StubSource(), sender);
        await engine.InitializeAsync(default);
        await engine.CollectAsync(default);
        _clock.Now += TimeSpan.FromHours(1);
        await engine.DeliverAsync(default);
        Assert.Empty(sender.Calls);
        Assert.Null(engine.State.Pending);
    }

    [Fact]
    public async Task FailedDeliveryCoalescesIntoLatestAndCanResumeAfterRestart()
    {
        var source = new StubSource();
        var engine = Engine(source, new StubSender { FailOnCall = 1 });
        await engine.InitializeAsync(default);
        await engine.CollectAsync(default);
        await Assert.ThrowsAsync<TelegramDeliveryException>(() => engine.DeliverAsync(default));
        _clock.Now += TimeSpan.FromMinutes(30);
        source.Deals = [LeaderboardTests.Deal("3"), LeaderboardTests.Deal("1", 2)];
        await engine.CollectAsync(default);
        var resumedSender = new StubSender();
        var restarted = Engine(source, resumedSender);
        await restarted.InitializeAsync(default);
        await restarted.DeliverAsync(default);
        var call = Assert.Single(resumedSender.Calls);
        Assert.Contains("/p/3/", call.Text);
        Assert.DoesNotContain("/p/2/", call.Text);
        Assert.Equal(2, restarted.State.Snapshot!.Items.Single(x => x.Deal.Id == "1").ConsecutivePolls);
        Assert.Null(restarted.State.Pending);
    }

    [Fact]
    public async Task MultipleChangesInSamePeriodRetainEarlierEvents()
    {
        var source = new StubSource();
        var engine = Engine(source, new StubSender(), new() { NewMessageIntervalHours = 3 });
        await engine.InitializeAsync(default);
        await engine.CollectAsync(default);
        _clock.Now += TimeSpan.FromMinutes(30);
        source.Deals = [LeaderboardTests.Deal("3"), LeaderboardTests.Deal("1", 2, "9元")];
        await engine.CollectAsync(default);
        _clock.Now += TimeSpan.FromMinutes(30);
        source.Deals = [LeaderboardTests.Deal("4"), LeaderboardTests.Deal("1", 2, "8元")];
        await engine.CollectAsync(default);
        Assert.Equal(2, engine.State.Report!.Events.Count(x => x.Entered));
        var changes = engine.State.Report.Events.Single(x => x.Deal.Id == "1");
        Assert.Equal("10元", changes.FromPrice);
        Assert.Contains("10元→8元", MessageFormatter.PlainText(engine.State.Pending!.Text));
    }

    [Fact]
    public async Task FetchFailureLeavesPreviousSnapshotAndPendingReportUntouched()
    {
        var source = new StubSource();
        var engine = Engine(source, new StubSender());
        await engine.InitializeAsync(default);
        await engine.CollectAsync(default);
        var before = await File.ReadAllTextAsync(Path.Combine(_directory, "state.json"));
        source.Fail = true;
        await Assert.ThrowsAsync<InvalidDataException>(() => engine.CollectAsync(default));
        Assert.Equal(before, await File.ReadAllTextAsync(Path.Combine(_directory, "state.json")));
    }

    [Fact]
    public async Task MigratesV1SnapshotWithoutSendingHistoricalOutbox()
    {
        var store = new StateStore(Options);
        var snapshot = Leaderboard.Compare(null, new StubSource().Deals, _clock.Now, Options).Snapshot;
        await store.SaveAsync(new MonitorState { Version = 1, Snapshot = snapshot }, default);
        var path = Path.Combine(_directory, "state.json");
        var json = await File.ReadAllTextAsync(path);
        await File.WriteAllTextAsync(path, json.TrimEnd().TrimEnd('}') + ",\"Outbox\":[{\"Text\":\"old message\"}]}");
        var sender = new StubSender();
        var engine = Engine(new StubSource(), sender);
        await engine.InitializeAsync(default);
        await engine.DeliverAsync(default);
        Assert.Equal(2, engine.State.Version);
        Assert.NotNull(engine.State.Snapshot);
        Assert.Empty(sender.Calls);
    }

    [Fact]
    public async Task NewDestinationDoesNotEditOldChatsMessage()
    {
        var source = new StubSource();
        var first = Engine(source, new StubSender());
        await first.InitializeAsync(default);
        await first.CollectAsync(default);
        await first.DeliverAsync(default);
        var sender = new StubSender { DestinationKey = "different-chat" };
        var next = Engine(source, sender);
        await next.InitializeAsync(default);
        Assert.Null(next.State.Published);
        await next.CollectAsync(default);
        await next.DeliverAsync(default);
        Assert.Null(Assert.Single(sender.Calls).MessageId);
    }

    [Fact]
    public async Task CorruptStateFailsLoudlyAndIsNeverReplaced()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "state.json");
        await File.WriteAllTextAsync(path, "broken json");
        await Assert.ThrowsAsync<System.Text.Json.JsonException>(() => new StateStore(Options).LoadAsync(default));
        Assert.Equal("broken json", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public void SecondWriterCannotUseSameDataDirectory()
    {
        var store = new StateStore(Options);
        using var first = store.AcquireLock();
        Assert.Throws<InvalidOperationException>(() => store.AcquireLock());
    }

    [Fact]
    public async Task ImageChangesInSameMinuteEditExistingMessageEvenWhenCaptionIsUnchanged()
    {
        var source = new StubSource();
        var sender = new StubSender();
        var engine = Engine(source, sender);
        await engine.InitializeAsync(default);
        await engine.CollectAsync(default);
        await engine.DeliverAsync(default);
        var firstText = engine.State.Published!.Text;
        var id = engine.State.Published.MessageId;
        source.Deals = source.Deals.Select(x => x with { ImageUrl = "https://qny.smzdm.com/new.jpg" }).ToList();
        await engine.CollectAsync(default);
        Assert.Equal(firstText, engine.State.Pending!.Text);
        var restarted = Engine(source, sender);
        await restarted.InitializeAsync(default);
        Assert.Equal("https://qny.smzdm.com/new.jpg", restarted.State.Pending!.Items![0].ImageUrl);
        await restarted.DeliverAsync(default);
        Assert.Equal(2, sender.Calls.Count);
        Assert.Equal(id, sender.Calls[1].MessageId);
    }

    [Fact]
    public async Task ChangingSourceCannotSilentlyCompareUnrelatedBoards()
    {
        var source = new StubSource();
        var sender = new StubSender();
        var engine = Engine(source, sender);
        await engine.InitializeAsync(default);
        await engine.CollectAsync(default);
        var changed = Options;
        changed.TopCount = 3;
        var next = new MonitorEngine(changed, new StateStore(changed), source, sender, NullLogger<MonitorEngine>.Instance,
            new NotificationPolicy(new()), _clock);
        await Assert.ThrowsAsync<InvalidOperationException>(() => next.InitializeAsync(default));
    }

    private MonitorEngine Engine(IDealSource source, INotificationSender sender, NotificationOptions? notifications = null) =>
        new(Options, new StateStore(Options), source, sender, NullLogger<MonitorEngine>.Instance,
            new NotificationPolicy(notifications ?? new()), _clock);

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private sealed class StubSource : IDealSource
    {
        public bool Fail { get; set; }
        public List<Deal> Deals { get; set; } = [LeaderboardTests.Deal("1"), LeaderboardTests.Deal("2", 2)];
        public Task<List<Deal>> FetchAsync(CancellationToken cancellationToken) => Fail
            ? throw new InvalidDataException("blocked") : Task.FromResult(Deals);
    }

    private sealed record PublishCall(string Text, long? MessageId);
    private sealed class StubSender : INotificationSender
    {
        private long _nextId = 100;
        public int FailOnCall { get; init; }
        public string DestinationKey { get; init; } = "test";
        public List<PublishCall> Calls { get; } = [];
        public Task<long> PublishLeaderboardAsync(PendingNotification notification, long? messageId,
            DateTimeOffset expiresAt, CancellationToken cancellationToken) =>
            PublishAsync(notification.Text, messageId, expiresAt, cancellationToken);
        public Task<long> PublishAsync(string message, long? messageId, DateTimeOffset expiresAt, CancellationToken cancellationToken)
        {
            Calls.Add(new(message, messageId));
            if (Calls.Count == FailOnCall) throw new TelegramDeliveryException("test failure");
            return Task.FromResult(messageId ?? ++_nextId);
        }
    }
}
