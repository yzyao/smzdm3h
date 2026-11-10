using Microsoft.Extensions.Logging;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

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

    public Task CollectAsync(CancellationToken cancellationToken) => CollectAsync(null, true, cancellationToken);

    public Task CollectBaselineAsync(CancellationToken cancellationToken) => CollectAsync(null, false, cancellationToken);

    public Task CollectScheduledAsync(DateTimeOffset scheduledAt, CancellationToken cancellationToken) =>
        CollectAsync(scheduledAt, true, cancellationToken);

    private async Task CollectAsync(DateTimeOffset? scheduledAt, bool prepareNotification, CancellationToken cancellationToken)
    {
        var deals = await source.FetchAsync(cancellationToken);
        var now = clock.GetUtcNow();
        var comparison = Leaderboard.Compare(State.Snapshot, deals, now, options);
        var period = policy.PeriodStart(now);
        var report = NotificationPolicy.BuildReport(State.Report, comparison, period);
        var quiet = policy.IsQuiet(now);
        var canNotify = prepareNotification && !quiet;
        if (scheduledAt is { } slot)
        {
            // An update slot may edit an existing rich message, but cannot create a late
            // hourly message. Also discard a collection that finished in a different period.
            canNotify &= policy.PeriodStart(slot) == period &&
                (slot == period || State.Published is { IsRich: true } published && published.PeriodStart == period);
        }
        var next = new MonitorState
        {
            SourceKey = State.SourceKey,
            DestinationKey = State.DestinationKey,
            Snapshot = comparison.Snapshot,
            Report = report,
            Published = State.Published,
            // Coalesce retries into one current report; never accumulate historical messages.
            Pending = canNotify ? new PendingNotification(
                RichMessageFormatter.Format(report, comparison.Snapshot, options, policy), period, now, deals, IsRich: true) : null
        };
        await SaveAsync(next, cancellationToken);
        logger.LogInformation("榜单已保存：{Count} 条，新进 {New}，掉榜 {Exits}，变化 {Changes}，新增霸榜 {Dominant}；{Delivery}。",
            deals.Count, comparison.NewEntries.Count, comparison.Exits.Count, comparison.Changes.Count,
            comparison.NewlyDominant.Count, canNotify ? "待发布最新榜单" : "仅采集，不发送或编辑");
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
        // Photo messages cannot be converted to rich messages using editMessageText.
        var published = State.Published?.PeriodStart == pending.PeriodStart && State.Published.IsRich == pending.IsRich
            ? State.Published : null;
        var payloadKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(new { pending.Text, pending.Items, pending.IsRich }))));
        if (published?.PayloadKey == payloadKey)
        {
            await SaveAsync(Copy(pending: null), cancellationToken);
            return;
        }
        var messageId = await sender.PublishLeaderboardAsync(pending, published?.MessageId,
            policy.PeriodEnd(pending.PeriodStart), cancellationToken);
        var next = Copy(pending: null);
        next.Published = new PublishedMessage(messageId, pending.PeriodStart, pending.Text, payloadKey, pending.IsRich);
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
