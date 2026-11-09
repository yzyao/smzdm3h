using Microsoft.Extensions.Logging;

namespace Smzdm.Monitor;

public sealed class MonitorEngine(
    MonitorOptions options, StateStore store, IDealSource source, INotificationSender sender,
    ILogger<MonitorEngine> logger, NotificationPolicy policy, TimeProvider clock)
{
    public MonitorState State { get; private set; } = new();

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        State = await store.LoadAsync(cancellationToken);
        var sourceKey = $"{new Uri(options.SourceUrl).GetLeftPart(UriPartial.Path)}|top={options.TopCount}";
        if (State.SourceKey.Length > 0 && State.SourceKey != sourceKey)
            throw new InvalidOperationException("监控地址或 TopCount 与历史状态不一致；请使用新的 DataDirectory，或备份并移走旧 state.json。");
        if (State.Version == 1)
            logger.LogInformation("升级旧状态：保留榜单与连续计数，丢弃旧版逐条待发通知，改为最新时段榜单。");
        State.Version = 2;
        State.SourceKey = sourceKey;
        if (State.DestinationKey != sender.DestinationKey)
        {
            State.DestinationKey = sender.DestinationKey;
            State.Published = null;
            State.Pending = null;
        }
    }

    public async Task CollectAsync(CancellationToken cancellationToken)
    {
        var deals = await source.FetchAsync(cancellationToken);
        var now = clock.GetUtcNow();
        var comparison = Leaderboard.Compare(State.Snapshot, deals, now, options);
        var period = policy.PeriodStart(now);
        var report = NotificationPolicy.BuildReport(State.Report, comparison, period);
        var quiet = policy.IsQuiet(now);
        var next = new MonitorState
        {
            SourceKey = State.SourceKey,
            DestinationKey = State.DestinationKey,
            Snapshot = comparison.Snapshot,
            Report = report,
            Published = State.Published,
            // Coalesce retries into one current report; never accumulate historical messages.
            Pending = quiet ? null : new PendingNotification(
                MessageFormatter.Format(report, comparison.Snapshot, options, policy), period, now)
        };
        await SaveAsync(next, cancellationToken);
        logger.LogInformation("榜单已保存：{Count} 条，新进 {New}，掉榜 {Exits}，变化 {Changes}，新增霸榜 {Dominant}；{Delivery}。",
            deals.Count, comparison.NewEntries.Count, comparison.Exits.Count, comparison.Changes.Count,
            comparison.NewlyDominant.Count, quiet ? "静默时段不发送或编辑" : "待发布最新榜单");
    }

    public async Task DeliverAsync(CancellationToken cancellationToken)
    {
        if (State.Pending is not { } pending) return;
        var now = clock.GetUtcNow();
        if (policy.IsQuiet(now) || pending.PeriodStart != policy.PeriodStart(now) ||
            pending.CapturedAt > now || now - pending.CapturedAt > TimeSpan.FromMinutes(options.IntervalMinutes * 1.5))
        {
            await SaveAsync(Copy(pending: null), cancellationToken);
            return;
        }
        var published = State.Published?.PeriodStart == pending.PeriodStart ? State.Published : null;
        if (published?.Text == pending.Text)
        {
            await SaveAsync(Copy(pending: null), cancellationToken);
            return;
        }
        var messageId = await sender.PublishAsync(pending.Text, published?.MessageId,
            policy.PeriodEnd(pending.PeriodStart), cancellationToken);
        var next = Copy(pending: null);
        next.Published = new PublishedMessage(messageId, pending.PeriodStart, pending.Text);
        await SaveAsync(next, cancellationToken);
        logger.LogInformation("Telegram 榜单{Action}成功，消息 ID {MessageId}。", published is null ? "发送" : "编辑", messageId);
    }

    private MonitorState Copy(PendingNotification? pending) => new()
    {
        SourceKey = State.SourceKey, DestinationKey = State.DestinationKey, Snapshot = State.Snapshot,
        Report = State.Report, Published = State.Published, Pending = pending
    };

    private async Task SaveAsync(MonitorState next, CancellationToken cancellationToken)
    {
        await store.SaveAsync(next, cancellationToken);
        State = next;
    }
}
