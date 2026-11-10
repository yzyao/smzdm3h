using System.Net;
using Microsoft.Extensions.Logging;

namespace Smzdm.Monitor;

public sealed class StartupNotifier(
    INotificationSender sender, MonitorOptions monitor, NotificationPolicy policy,
    TimeProvider clock, ILogger<StartupNotifier> logger)
{
    // Return a retry deadline so the worker can respect Telegram backoff while continuing to collect.
    public async Task<DateTimeOffset?> NotifyAsync(Snapshot? snapshot, DateTimeOffset nextFetch, CancellationToken stoppingToken)
    {
        stoppingToken.ThrowIfCancellationRequested();
        if (!policy.Options.NotifyOnStartup) return null;
        var now = clock.GetUtcNow();
        if (policy.IsQuiet(now))
        {
            logger.LogInformation("静默时段跳过 Telegram 启动通知，不延后补发。");
            return null;
        }

        var quiet = policy.Options.QuietStartHour == policy.Options.QuietEndHour
            ? "关闭"
            : $"{policy.Options.QuietStartHour:00}:00–{policy.Options.QuietEndHour:00}:00";
        var lastFetch = snapshot is null ? "暂无，等待首次采集" : $"{policy.LocalTime(snapshot.CapturedAt):yyyy-MM-dd HH:mm:ss}";
        var next = nextFetch <= now ? "立即" : $"{policy.LocalTime(nextFetch):yyyy-MM-dd HH:mm:ss}";
        var text = string.Join('\n',
            "✅ 什么值得买监控已启动",
            $"启动时间：{policy.LocalTime(now):yyyy-MM-dd HH:mm:ss}（{policy.Options.TimeZoneId}）",
            $"采集：每 {monitor.IntervalMinutes} 分钟，TOP {monitor.TopCount}（按本地零点对齐）",
            $"榜单：每 {policy.Options.NewMessageIntervalHours} 小时新发 1 条，其间更新原消息",
            $"霸榜：连续 {monitor.DominancePolls} 次在榜",
            $"静默：{quiet}",
            $"最近成功采集：{lastFetch}",
            $"下次采集：{next}");

        // This is an independent service-status message. It must never replace the
        // saved hourly leaderboard message ID or become a persistent pending report.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            await sender.PublishAsync(WebUtility.HtmlEncode(text), null, now.AddMinutes(1), timeout.Token);
            logger.LogInformation("Telegram 启动通知已发送。");
        }
        catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
        {
            logger.LogWarning("Telegram 启动通知发送超时，继续正常监控，不延后补发启动通知。");
        }
        catch (TelegramDeliveryException exception)
        {
            logger.LogWarning("启动通知未发送，继续正常监控：{Reason}", exception.Message);
            return clock.GetUtcNow().AddSeconds(exception.RetryAfterSeconds);
        }
        catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
        {
            // An HTTP exception may contain a token-bearing URL; log only the type.
            logger.LogWarning("启动通知失败（{ErrorType}），继续正常监控。", exception.GetType().Name);
        }
        return null;
    }
}
