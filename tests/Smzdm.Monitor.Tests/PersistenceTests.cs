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
    public async Task ExpiredFailedAlertCanBeRebuiltForStillUnnotifiedCurrentProduct()
    {
        var source = new StubSource();
        var sender = new StubSender { FailOnCall = 2 };
        var engine = Engine(source, sender);
        await engine.InitializeAsync(default);
        await Scan(engine);
        _clock.Now += TimeSpan.FromMinutes(15);
        source.Deals = [LeaderboardTests.Deal("3"), LeaderboardTests.Deal("1", 2)];
        await engine.CollectAsync(default);
        await Assert.ThrowsAsync<TelegramDeliveryException>(() => engine.DeliverAsync(default));
        _clock.Now += TimeSpan.FromMinutes(23);
        await engine.DeliverAsync(default);
        Assert.Null(engine.State.Pending);
        _clock.Now += TimeSpan.FromMinutes(7);
        await Scan(engine);
        Assert.Null(sender.Calls[2].MessageId);
        Assert.Contains("3", engine.State.NotifiedIds);
    }

    [Fact]
    public async Task DailyResetAllowsYesterdayAlertedProductToTriggerOneNewMessageAgain()
    {
        var source = new StubSource();
        var sender = new StubSender();
        var engine = Engine(source, sender);
        await engine.InitializeAsync(default);
        await Scan(engine);
        _clock.Now += TimeSpan.FromMinutes(15);
        source.Deals = [LeaderboardTests.Deal("3"), LeaderboardTests.Deal("1", 2)];
        await Scan(engine);
        Assert.Contains("3", engine.State.NotifiedIds);
        _clock.Now = DateTimeOffset.Parse("2026-11-10T07:00:00+08:00");
        source.Deals = [LeaderboardTests.Deal("1"), LeaderboardTests.Deal("2", 2)];
        await Scan(engine);
        Assert.DoesNotContain("3", engine.State.NotifiedIds);
        _clock.Now += TimeSpan.FromMinutes(15);
        source.Deals = [LeaderboardTests.Deal("3"), LeaderboardTests.Deal("1", 2)];
        await Scan(engine);
        Assert.Null(sender.Calls[3].MessageId);
        Assert.Contains("<code>新进</code>", sender.Calls[3].Text);
        Assert.Equal(4, sender.Calls.Count);
    }

    [Fact]
    public async Task FreshPendingFromYesterdayIsDroppedAtSeven()
    {
        _clock.Now = DateTimeOffset.Parse("2026-11-10T06:59:00+08:00");
        var sender = new StubSender();
        var engine = Engine(new StubSource(), sender, new() { QuietStartHour = 0, QuietEndHour = 0 });
        await engine.InitializeAsync(default);
        await engine.CollectAsync(default);
        _clock.Now += TimeSpan.FromMinutes(1);
        await engine.DeliverAsync(default);
        Assert.Empty(sender.Calls);
        Assert.Null(engine.State.Pending);
    }

    [Fact]
    public async Task QuietBoundarySuppressesPendingEditAsWellAsNewMessages()
    {
        var source = new StubSource();
        var sender = new StubSender();
        var engine = Engine(source, sender, new() { QuietStartHour = 11, QuietEndHour = 12 });
        await engine.InitializeAsync(default);
        await Scan(engine);
        _clock.Now = DateTimeOffset.Parse("2026-11-09T10:59:00+08:00");
        source.Deals = [LeaderboardTests.Deal("1", price: "9元"), LeaderboardTests.Deal("2", 2)];
        await engine.CollectAsync(default);
        Assert.False(engine.State.Pending!.SendNew);
        _clock.Now += TimeSpan.FromMinutes(1);
        await engine.DeliverAsync(default);
        Assert.Single(sender.Calls);
        Assert.Null(engine.State.Pending);
    }

    [Fact]
    public async Task StartupCreatesBaselineThenNewProductsSendAndOtherChangesEditLatestMessage()
    {
        var source = new StubSource();
        var sender = new StubSender();
        var engine = Engine(source, sender);
        await engine.InitializeAsync(default);
        await Scan(engine);
        Assert.Contains("基础榜单", sender.Calls[0].Text);
        Assert.True(engine.State.NotifiedIds.SetEquals(["1", "2"]));
        _clock.Now += TimeSpan.FromMinutes(15);
        source.Deals = [LeaderboardTests.Deal("3"), LeaderboardTests.Deal("4", 2)];
        await Scan(engine);
        var latestId = engine.State.Published!.MessageId;
        Assert.Contains("新进 2", sender.Calls[1].Text);
        _clock.Now += TimeSpan.FromMinutes(15);
        source.Deals = [LeaderboardTests.Deal("3", price: "9元"), LeaderboardTests.Deal("4", 2)];
        await Scan(engine);
        Assert.Equal(new long?[] { null, null, latestId }, sender.Calls.Select(x => x.MessageId));
        Assert.Contains("10元→9元", MessageFormatter.PlainText(sender.Calls[2].Text));
        _clock.Now += TimeSpan.FromMinutes(15);
        await Scan(engine);
        Assert.Equal(3, sender.Calls.Count);
        Assert.Null(engine.State.Pending);
    }

    [Fact]
    public async Task RestartRestoresDailyDedupAndReturneesOnlyEditMostRecentMessage()
    {
        var source = new StubSource();
        var sender = new StubSender();
        var engine = Engine(source, sender);
        await engine.InitializeAsync(default);
        await Scan(engine);
        _clock.Now += TimeSpan.FromMinutes(15);
        source.Deals = [LeaderboardTests.Deal("3"), LeaderboardTests.Deal("1", 2)];
        await Scan(engine);
        var latestId = engine.State.Published!.MessageId;
        _clock.Now += TimeSpan.FromMinutes(15);
        source.Deals = [LeaderboardTests.Deal("1"), LeaderboardTests.Deal("2", 2)];
        var restarted = Engine(source, sender);
        await restarted.InitializeAsync(default);
        Assert.True(restarted.State.NotifiedIds.SetEquals(["1", "2", "3"]));
        await Scan(restarted);
        Assert.Equal(latestId, sender.Calls[2].MessageId);
        Assert.Contains("<code>返榜</code>", sender.Calls[2].Text);
        _clock.Now += TimeSpan.FromMinutes(15);
        source.Deals = [LeaderboardTests.Deal("3"), LeaderboardTests.Deal("1", 2)];
        await Scan(restarted);
        Assert.Equal(latestId, sender.Calls[3].MessageId);
        Assert.Equal(2, sender.Calls.Count(x => x.MessageId is null));
    }

    [Fact]
    public async Task UnchangedStartupDoesNotResendOrEditTodaysMessage()
    {
        var source = new StubSource();
        var sender = new StubSender();
        var engine = Engine(source, sender);
        await engine.InitializeAsync(default);
        await Scan(engine);
        _clock.Now += TimeSpan.FromMinutes(5);
        var restarted = Engine(source, sender);
        await restarted.InitializeAsync(default);
        await Scan(restarted);
        Assert.Single(sender.Calls);
    }

    [Fact]
    public async Task MidnightKeepsSameDayAndSevenCreatesFreshBaselineEvenWithoutChanges()
    {
        _clock.Now = DateTimeOffset.Parse("2026-11-09T23:45:00+08:00");
        var sender = new StubSender();
        var engine = Engine(new StubSource(), sender, new() { QuietStartHour = 0, QuietEndHour = 0 });
        await engine.InitializeAsync(default);
        await Scan(engine);
        var previousDay = engine.State.NotificationDay;
        _clock.Now += TimeSpan.FromMinutes(15);
        await Scan(engine);
        Assert.Single(sender.Calls);
        Assert.Equal(previousDay, engine.State.NotificationDay);
        _clock.Now = DateTimeOffset.Parse("2026-11-10T07:00:00+08:00");
        await Scan(engine);
        Assert.Equal(2, sender.Calls.Count);
        Assert.Null(sender.Calls[1].MessageId);
        Assert.Contains("基础榜单", sender.Calls[1].Text);
        Assert.Contains("新进 0", sender.Calls[1].Text);
        Assert.Equal(_clock.GetUtcNow(), engine.State.NotificationDay);
    }

    [Fact]
    public async Task QuietHoursDropRetriesAndSevenSendsOnlyLatestBaseline()
    {
        _clock.Now = DateTimeOffset.Parse("2026-11-09T00:45:00+08:00");
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
            _clock.Now = new DateTimeOffset(2026, 11, 9, hour, 15, 0, TimeSpan.FromHours(8));
            source.Deals = [LeaderboardTests.Deal((hour + 10).ToString()), LeaderboardTests.Deal("1", 2)];
            await Scan(engine);
            Assert.Null(engine.State.Pending);
        }
        Assert.Single(sender.Calls);
        _clock.Now = DateTimeOffset.Parse("2026-11-09T07:00:00+08:00");
        source.Deals = [LeaderboardTests.Deal("99"), LeaderboardTests.Deal("1", 2)];
        await Scan(engine);
        Assert.Equal(2, sender.Calls.Count);
        Assert.Null(sender.Calls[1].MessageId);
        Assert.Contains("基础榜单", sender.Calls[1].Text);
        Assert.Contains("/p/99/", sender.Calls[1].Text);
        Assert.True(engine.State.NotifiedIds.SetEquals(["99", "1"]));
        Assert.True(engine.State.SeenIds.SetEquals(["99", "1"]));
    }

    [Fact]
    public async Task FailedNewItemAlertSurvivesRestartAndCoalescesWithoutConsumingDedup()
    {
        var source = new StubSource();
        var sender = new StubSender { FailOnCall = 2 };
        var engine = Engine(source, sender);
        await engine.InitializeAsync(default);
        await Scan(engine);
        _clock.Now += TimeSpan.FromMinutes(15);
        source.Deals = [LeaderboardTests.Deal("3"), LeaderboardTests.Deal("1", 2)];
        await engine.CollectAsync(default);
        await Assert.ThrowsAsync<TelegramDeliveryException>(() => engine.DeliverAsync(default));
        Assert.DoesNotContain("3", engine.State.NotifiedIds);
        _clock.Now += TimeSpan.FromMinutes(15);
        var restarted = Engine(source, sender);
        await restarted.InitializeAsync(default);
        await restarted.CollectAsync(default);
        Assert.True(restarted.State.Pending!.SendNew);
        Assert.Contains("3", restarted.State.Pending.NewItemIds!);
        Assert.Contains("<code>新进</code>", restarted.State.Pending.Text);
        await restarted.DeliverAsync(default);
        Assert.Null(sender.Calls[2].MessageId);
        Assert.Contains("3", restarted.State.NotifiedIds);
        _clock.Now += TimeSpan.FromMinutes(15);
        await Scan(restarted);
        Assert.Equal(3, sender.Calls.Count);
    }

    [Fact]
    public async Task UnsentProductThatLeavesBoardDoesNotCreateStaleNewMessage()
    {
        var source = new StubSource();
        var sender = new StubSender { FailOnCall = 2 };
        var engine = Engine(source, sender);
        await engine.InitializeAsync(default);
        await Scan(engine);
        var baselineId = engine.State.Published!.MessageId;
        _clock.Now += TimeSpan.FromMinutes(15);
        source.Deals = [LeaderboardTests.Deal("3"), LeaderboardTests.Deal("1", 2)];
        await engine.CollectAsync(default);
        await Assert.ThrowsAsync<TelegramDeliveryException>(() => engine.DeliverAsync(default));
        _clock.Now += TimeSpan.FromMinutes(15);
        source.Deals = [LeaderboardTests.Deal("1"), LeaderboardTests.Deal("2", 2)];
        await Scan(engine);
        Assert.Equal(baselineId, sender.Calls[2].MessageId);
        Assert.DoesNotContain("3", engine.State.NotifiedIds);
        Assert.DoesNotContain("href=\"https://www.smzdm.com/p/3/\"", sender.Calls[2].Text);
    }

    [Fact]
    public async Task TagsCompareWithPreviousScanInsteadOfAccumulatingAllDay()
    {
        var source = new StubSource();
        var engine = Engine(source, new StubSender());
        await engine.InitializeAsync(default);
        await Scan(engine);
        _clock.Now += TimeSpan.FromMinutes(15);
        source.Deals = [LeaderboardTests.Deal("1", price: "9元"), LeaderboardTests.Deal("2", 2)];
        await Scan(engine);
        _clock.Now += TimeSpan.FromMinutes(15);
        source.Deals = [LeaderboardTests.Deal("1", price: "8元"), LeaderboardTests.Deal("2", 2)];
        await engine.CollectAsync(default);
        Assert.Equal("9元", Assert.Single(engine.State.Report!.Events).FromPrice);
        Assert.Contains("9元→8元", MessageFormatter.PlainText(engine.State.Pending!.Text));
    }

    [Fact]
    public async Task PendingExpiresAfterCollectionGapOrDayBoundary()
    {
        var engine = Engine(new StubSource(), new StubSender());
        await engine.InitializeAsync(default);
        await engine.CollectAsync(default);
        _clock.Now += TimeSpan.FromMinutes(23);
        await engine.DeliverAsync(default);
        Assert.Null(engine.State.Pending);
    }

    [Fact]
    public async Task FetchFailureLeavesSavedSnapshotAndPendingUntouched()
    {
        var source = new StubSource();
        var engine = Engine(source, new StubSender());
        await engine.InitializeAsync(default);
        await engine.CollectAsync(default);
        var path = Path.Combine(_directory, "state.json");
        var before = await File.ReadAllTextAsync(path);
        source.Fail = true;
        await Assert.ThrowsAsync<InvalidDataException>(() => engine.CollectAsync(default));
        Assert.Equal(before, await File.ReadAllTextAsync(path));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task LegacyStateKeepsSnapshotAndCreatesDailyBaselineWithoutSendingOldPending(int version)
    {
        var source = new StubSource();
        var snapshot = Leaderboard.Compare(null, source.Deals, _clock.Now, Options).Snapshot;
        await new StateStore(Options).SaveAsync(new()
        {
            Version = version, DestinationKey = "test", Snapshot = snapshot,
            Pending = new("old message", _clock.Now, _clock.Now),
            Published = new(55, _clock.Now, "old message", IsRich: true)
        }, default);
        var sender = new StubSender();
        var engine = Engine(source, sender);
        await engine.InitializeAsync(default);
        await engine.DeliverAsync(default);
        Assert.Equal(3, engine.State.Version);
        Assert.Equal(snapshot.CapturedAt, engine.State.Snapshot!.CapturedAt);
        Assert.Empty(sender.Calls);
        await Scan(engine);
        Assert.Contains("基础榜单", Assert.Single(sender.Calls).Text);
        Assert.Null(sender.Calls[0].MessageId);
    }

    [Fact]
    public async Task NewDestinationStartsNewBaselineAndOwnDedup()
    {
        var source = new StubSource();
        var first = Engine(source, new StubSender());
        await first.InitializeAsync(default);
        await Scan(first);
        var sender = new StubSender { DestinationKey = "different-chat" };
        var next = Engine(source, sender);
        await next.InitializeAsync(default);
        Assert.Null(next.State.Published);
        Assert.Empty(next.State.NotifiedIds);
        await Scan(next);
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
    public async Task ImageOnlyChangesUpdateExistingMessage()
    {
        var source = new StubSource();
        var sender = new StubSender();
        var engine = Engine(source, sender);
        await engine.InitializeAsync(default);
        await Scan(engine);
        var id = engine.State.Published!.MessageId;
        source.Deals = source.Deals.Select(x => x with { ImageUrl = "https://qny.smzdm.com/new.jpg" }).ToList();
        await engine.CollectAsync(default);
        var restarted = Engine(source, sender);
        await restarted.InitializeAsync(default);
        Assert.Equal("https://qny.smzdm.com/new.jpg", restarted.State.Pending!.Items![0].ImageUrl);
        await restarted.DeliverAsync(default);
        Assert.Equal(id, sender.Calls[1].MessageId);
    }

    [Fact]
    public async Task ChangingSourceCannotSilentlyCompareUnrelatedBoards()
    {
        var source = new StubSource();
        var sender = new StubSender();
        var engine = Engine(source, sender);
        await engine.InitializeAsync(default);
        await Scan(engine);
        var changed = Options;
        changed.TopCount = 3;
        var next = new MonitorEngine(changed, new(changed), source, sender, NullLogger<MonitorEngine>.Instance,
            new(new()), _clock);
        await Assert.ThrowsAsync<InvalidOperationException>(() => next.InitializeAsync(default));
    }

    [Fact]
    public async Task LongRichPendingMessageSurvivesStateRoundTrip()
    {
        var deals = Enumerable.Range(1, 20).Select(i => LeaderboardTests.Deal(i.ToString(), i) with
            { Title = new string('长', 220) }).ToList();
        var policy = new NotificationPolicy(new());
        var comparison = Leaderboard.Compare(null, deals, _clock.Now, Options);
        var report = NotificationPolicy.BuildReport(comparison, policy.PeriodStart(_clock.Now));
        var text = RichMessageFormatter.Format(report, comparison.Snapshot, Options, policy);
        Assert.True(MessageFormatter.PlainText(text).Length > MessageFormatter.MaxLength);
        var store = new StateStore(Options);
        await store.SaveAsync(new() { Pending = new(text, report.PeriodStart, _clock.Now, deals, IsRich: true) }, default);
        Assert.Equal(text, (await store.LoadAsync(default)).Pending!.Text);
    }

    private static async Task Scan(MonitorEngine engine)
    {
        await engine.CollectAsync(default);
        await engine.DeliverAsync(default);
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
