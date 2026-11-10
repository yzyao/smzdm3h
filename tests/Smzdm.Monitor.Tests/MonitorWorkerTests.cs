using Microsoft.Extensions.Logging.Abstractions;
using Smzdm.Monitor;
using Xunit;

namespace Smzdm.Monitor.Tests;

public sealed class MonitorWorkerTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "smzdm-worker-" + Guid.NewGuid());

    [Fact]
    public async Task StartupBaselineThenQuarterHourScansSendOnlyNewProductsAndEditPriceChanges()
    {
        var clock = new TestClock { Now = At("10:12") };
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var source = new Source(clock)
        {
            FetchDuration = TimeSpan.FromSeconds(17),
            DealsAt = now => now >= At("10:45") ? [LeaderboardTests.Deal("3", price: "9元"), LeaderboardTests.Deal("1", 2)]
                : now >= At("10:30") ? [LeaderboardTests.Deal("3"), LeaderboardTests.Deal("1", 2)]
                : [LeaderboardTests.Deal("1"), LeaderboardTests.Deal("2", 2)]
        };
        var sender = new Sender(clock, stop, stopAfter: 3);
        await RunAsync(clock, source, sender, stop.Token);
        Assert.Equal(new[] { At("10:12"), At("10:15"), At("10:30"), At("10:45") }, source.Calls);
        Assert.Equal(new[] { At("10:12").AddSeconds(17), At("10:30").AddSeconds(17), At("10:45").AddSeconds(17) },
            sender.Calls.Select(x => x.At));
        Assert.Null(sender.Calls[0].EditedId);
        Assert.Null(sender.Calls[1].EditedId);
        Assert.Equal(101, sender.Calls[2].EditedId);
    }

    [Fact]
    public async Task RestartRestoresTodaysMessageWithoutDuplicateThenKeepsQuarterHourGrid()
    {
        var clock = new TestClock { Now = At("10:20") };
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var source = new Source(clock)
        {
            DealsAt = now => now >= At("10:45") ? [LeaderboardTests.Deal("3"), LeaderboardTests.Deal("1", 2)]
                : now >= At("10:30") ? [LeaderboardTests.Deal("1", price: "9元"), LeaderboardTests.Deal("2", 2)]
                : [LeaderboardTests.Deal("1"), LeaderboardTests.Deal("2", 2)]
        };
        var sender = new Sender(clock, stop, stopAfter: 2);
        var options = Options();
        var store = new StateStore(options);
        var snapshot = Leaderboard.Compare(null, source.Deals, At("10:12"), options).Snapshot;
        await store.SaveAsync(new()
        {
            DestinationKey = sender.DestinationKey,
            Snapshot = snapshot,
            Published = new(55, At("07:00"), "已发榜单", IsRich: true),
            NotificationDay = At("07:00"), SeenIds = ["1", "2"], NotifiedIds = ["1", "2"]
        }, default);
        await RunAsync(clock, source, sender, stop.Token);
        Assert.Equal(new[] { At("10:20"), At("10:30"), At("10:45") }, source.Calls);
        Assert.Equal(55, sender.Calls[0].EditedId);
        Assert.Null(sender.Calls[1].EditedId);
    }

    private async Task RunAsync(TestClock clock, Source source, Sender sender, CancellationToken stop)
    {
        var options = Options();
        var store = new StateStore(options);
        var policy = new NotificationPolicy(new() { NotifyOnStartup = false });
        var engine = new MonitorEngine(options, store, source, sender, NullLogger<MonitorEngine>.Instance, policy, clock);
        var notifier = new StartupNotifier(sender, options, policy, clock, NullLogger<StartupNotifier>.Instance);
        using var worker = new MonitorWorker(engine, options, store, NullLogger<MonitorWorker>.Instance, policy, notifier, clock);
        await worker.StartAsync(stop);
        await worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.True(stop.IsCancellationRequested);
    }

    private MonitorOptions Options() => new() { DataDirectory = _directory, TopCount = 2 };
    private static DateTimeOffset At(string time) => DateTimeOffset.Parse($"2026-11-10T{time}:00+08:00");

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private sealed class Source(TestClock clock) : IDealSource
    {
        public List<Deal> Deals { get; } = [LeaderboardTests.Deal("1"), LeaderboardTests.Deal("2", 2)];
        public List<DateTimeOffset> Calls { get; } = [];
        public TimeSpan FetchDuration { get; init; }
        public Func<DateTimeOffset, List<Deal>>? DealsAt { get; init; }
        public Task<List<Deal>> FetchAsync(CancellationToken cancellationToken)
        {
            var now = clock.GetUtcNow();
            Calls.Add(now);
            clock.Now += FetchDuration;
            return Task.FromResult(DealsAt?.Invoke(now) ?? Deals);
        }
    }

    private sealed record Call(DateTimeOffset At, long? EditedId);
    private sealed class Sender(TestClock clock, CancellationTokenSource stop, int stopAfter) : INotificationSender
    {
        public string DestinationKey => "test";
        public List<Call> Calls { get; } = [];
        private long _nextId = 100;
        public Task<long> PublishAsync(string message, long? messageId, DateTimeOffset expiresAt, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("此测试关闭启动通知。");
        public Task<long> PublishLeaderboardAsync(PendingNotification notification, long? messageId,
            DateTimeOffset expiresAt, CancellationToken cancellationToken)
        {
            Calls.Add(new(clock.GetUtcNow(), messageId));
            if (Calls.Count == stopAfter) stop.Cancel();
            return Task.FromResult(messageId ?? _nextId++);
        }
    }
}
