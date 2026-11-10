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
        if (State.Version < 3 || State.DestinationKey != sender.DestinationKey)
        {
            // Hourly messages and pending reports have different semantics. Keep the source
            // snapshot, then publish a fresh daily baseline on the next scan.
            State.Published = null;
            State.Pending = null;
            State.Report = null;
            State.NotificationDay = null;
            State.SeenIds = [];
            State.NotifiedIds = [];
        }
        State.Version = 3;
        State.SourceKey = sourceKey;
        State.DestinationKey = sender.DestinationKey;
    }

    public async Task CollectAsync(CancellationToken cancellationToken)
    {
        var deals = await source.FetchAsync(cancellationToken);
        var now = clock.GetUtcNow();
        var day = policy.PeriodStart(now);
        var sameDay = State.NotificationDay == day;
        var seen = sameDay ? new HashSet<string>(State.SeenIds) : [];
        var notified = sameDay ? new HashSet<string>(State.NotifiedIds) : [];
        var published = sameDay && State.Published?.PeriodStart == day ? State.Published : null;
        var oldPending = sameDay ? State.Pending : null;
        var comparison = Leaderboard.Compare(State.Snapshot, deals, now, options);
        var baseline = published is not { IsRich: true };
        var report = baseline ? new NotificationReport(day, [], IsBaseline: true)
            : NotificationPolicy.BuildReport(comparison, day);
        if (!baseline)
            report = report with { Events = report.Events.Select(x => x.Entered && seen.Contains(x.Deal.Id)
                ? x with { Returned = true } : x).ToList() };

        var ids = deals.Select(x => x.Id).ToHashSet();
        // Daily acknowledgement, rather than only the previous snapshot, is the source of
        // truth. An alert can expire during an outage without losing a still-current item.
        var newIds = baseline ? new HashSet<string>() : ids.Where(id => !notified.Contains(id)).ToHashSet();
        seen.UnionWith(ids);
        var next = new MonitorState
        {
            SourceKey = State.SourceKey, DestinationKey = State.DestinationKey,
            Snapshot = comparison.Snapshot, Report = report, Published = State.Published,
            NotificationDay = day, SeenIds = seen, NotifiedIds = notified
        };
        if (!policy.IsQuiet(now) && (baseline || newIds.Count > 0 || comparison.ShouldNotify || oldPending is not null))
        {
            // Unsent new products remain visibly marked even if another scan is unchanged.
            var pendingReport = report;
            if (newIds.Count > 0)
            {
                var events = report.Events.ToDictionary(x => x.Deal.Id);
                foreach (var deal in deals.Where(x => newIds.Contains(x.Id)))
                    events[deal.Id] = (events.GetValueOrDefault(deal.Id) ?? new(deal)) with { Entered = true, Returned = false };
                pendingReport = report with { Events = events.Values.ToList() };
            }
            next.Pending = new(RichMessageFormatter.Format(pendingReport, comparison.Snapshot, options, policy),
                day, now, deals, IsRich: true, SendNew: baseline || newIds.Count > 0, NewItemIds: newIds.ToList());
            if (oldPending is not null && !comparison.ShouldNotify && oldPending.Items?.SequenceEqual(deals) == true)
                next.Pending = oldPending with { CapturedAt = now };
        }
        await SaveAsync(next, cancellationToken);
        logger.LogInformation("榜单已保存：{Count} 条，新进 {New}，返榜 {Returns}，变化 {Changes}；{Delivery}。",
            deals.Count, newIds.Count, report.Events.Count(x => x.Returned), comparison.Changes.Count,
            next.Pending is null ? "仅采集" : next.Pending.SendNew ? "待发送新榜单" : "待更新最近榜单");
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
        var published = !pending.SendNew && State.Published?.PeriodStart == pending.PeriodStart &&
            State.Published.IsRich == pending.IsRich ? State.Published : null;
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
        next.Published = new(messageId, pending.PeriodStart, pending.Text, payloadKey, pending.IsRich);
        // Only acknowledged deliveries consume the daily notification allowance.
        next.NotifiedIds.UnionWith(pending.Items?.Select(x => x.Id) ?? []);
        await SaveAsync(next, cancellationToken);
        logger.LogInformation("Telegram 榜单{Action}成功，消息 ID {MessageId}。", published is null ? "发送" : "编辑", messageId);
    }

    private MonitorState Copy(PendingNotification? pending) => new()
    {
        SourceKey = State.SourceKey, DestinationKey = State.DestinationKey, Snapshot = State.Snapshot,
        Report = State.Report, Published = State.Published, Pending = pending,
        NotificationDay = State.NotificationDay, SeenIds = new(State.SeenIds), NotifiedIds = new(State.NotifiedIds)
    };

    private async Task SaveAsync(MonitorState state, CancellationToken cancellationToken)
    {
        await store.SaveAsync(state, cancellationToken);
        State = state;
    }
}
