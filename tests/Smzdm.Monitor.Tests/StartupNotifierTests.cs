using Microsoft.Extensions.Logging.Abstractions;
using Smzdm.Monitor;
using Xunit;

namespace Smzdm.Monitor.Tests;

public sealed class StartupNotifierTests
{
    [Fact]
    public async Task SendsIndependentStartupMessageWithScheduleAndUnknownCollectionStatus()
    {
        var clock = new TestClock();
        var sender = new StubSender();
        await Notifier(sender, clock).NotifyAsync(null, DateTimeOffset.MinValue, default);
        var call = Assert.Single(sender.Calls);
        Assert.Null(call.MessageId);
        var text = MessageFormatter.PlainText(call.Text);
        Assert.Contains("监控已启动", text);
        Assert.Contains("每 15 分钟，TOP 20", text);
        Assert.Contains("每天 07:00 建立基线", text);
        Assert.Contains("无变化不操作", text);
        Assert.Contains("连续在榜满 60 分钟", text);
        Assert.Contains("01:00–07:00", text);
        Assert.Contains("最近成功采集：暂无，等待首次采集", text);
        Assert.Contains("下次采集：立即", text);
        Assert.Equal(clock.GetUtcNow().AddMinutes(1), call.ExpiresAt);
    }

    [Theory]
    [InlineData("2026-11-10T01:00:00+08:00")]
    [InlineData("2026-11-10T06:59:59+08:00")]
    public async Task QuietStartupMakesNoTelegramRequest(string time)
    {
        var sender = new StubSender();
        var clock = new TestClock { Now = DateTimeOffset.Parse(time) };
        await Notifier(sender, clock).NotifyAsync(null, DateTimeOffset.MinValue, default);
        Assert.Empty(sender.Calls);
    }

    [Fact]
    public async Task SevenOClockStartupCanNotifyAndShowsRestoredSchedule()
    {
        var sender = new StubSender();
        var clock = new TestClock { Now = DateTimeOffset.Parse("2026-11-10T07:00:00+08:00") };
        var snapshot = new Snapshot(clock.Now.AddMinutes(-10), []);
        var nextFetch = new CollectionSchedule(new(), new(new())).AtOrAfter(clock.Now);
        await Notifier(sender, clock).NotifyAsync(snapshot, nextFetch, default);
        var text = MessageFormatter.PlainText(Assert.Single(sender.Calls).Text);
        Assert.Contains("最近成功采集：2026-11-10 06:50:00", text);
        Assert.Contains("下次采集：立即", text);
    }

    [Fact]
    public async Task StartupNotificationCanBeDisabled()
    {
        var sender = new StubSender();
        await Notifier(sender, new(), new() { NotifyOnStartup = false }).NotifyAsync(null, DateTimeOffset.MinValue, default);
        Assert.Empty(sender.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeliveryFailureOrTimeoutDoesNotPreventWorkerFromContinuing(bool timeout)
    {
        var sender = new StubSender
        {
            Failure = timeout ? new OperationCanceledException() : new TelegramDeliveryException("Unavailable")
        };
        await Notifier(sender, new()).NotifyAsync(null, DateTimeOffset.MinValue, default);
        Assert.Single(sender.Calls);
    }

    [Fact]
    public async Task StartupRateLimitDefersLeaderboardDeliveryWithoutDelayingCollection()
    {
        var clock = new TestClock();
        var sender = new StubSender { Failure = new TelegramDeliveryException("Rate limited", 120) };
        var retry = await Notifier(sender, clock).NotifyAsync(null, DateTimeOffset.MinValue, default);
        Assert.Equal(clock.GetUtcNow().AddSeconds(120), retry);
        Assert.Single(sender.Calls);
    }

    [Fact]
    public async Task ShutdownCancellationPropagates()
    {
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        var sender = new StubSender { Failure = new OperationCanceledException(stop.Token) };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Notifier(sender, new()).NotifyAsync(null, DateTimeOffset.MinValue, stop.Token));
        Assert.Empty(sender.Calls);
    }

    private static StartupNotifier Notifier(StubSender sender, TestClock clock, NotificationOptions? options = null) =>
        new(sender, new MonitorOptions(), new NotificationPolicy(options ?? new()), clock, NullLogger<StartupNotifier>.Instance);

    private sealed record Call(string Text, long? MessageId, DateTimeOffset ExpiresAt);
    private sealed class StubSender : INotificationSender
    {
        public string DestinationKey => "test";
        public Exception? Failure { get; init; }
        public List<Call> Calls { get; } = [];
        public Task<long> PublishLeaderboardAsync(PendingNotification notification, long? messageId,
            DateTimeOffset expiresAt, CancellationToken cancellationToken) => throw new InvalidOperationException("启动通知必须为独立文本消息。");
        public Task<long> PublishAsync(string message, long? messageId, DateTimeOffset expiresAt, CancellationToken cancellationToken)
        {
            Calls.Add(new(message, messageId, expiresAt));
            if (Failure is not null) return Task.FromException<long>(Failure);
            return Task.FromResult(123L);
        }
    }
}
