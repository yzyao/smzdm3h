using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Smzdm.Monitor;

public sealed class MonitorWorker(
    MonitorEngine engine, MonitorOptions options, StateStore store, ILogger<MonitorWorker> logger,
    NotificationPolicy policy) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await RunAsync(stoppingToken); }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch
        {
            Environment.ExitCode = 1;
            throw;
        }
    }

    private async Task RunAsync(CancellationToken stoppingToken)
    {
        using var dataLock = store.AcquireLock();
        await engine.InitializeAsync(stoppingToken);
        var nextFetch = engine.State.Snapshot?.CapturedAt.AddMinutes(options.IntervalMinutes) ?? DateTimeOffset.MinValue;
        if (engine.State.Snapshot?.CapturedAt > DateTimeOffset.UtcNow) nextFetch = DateTimeOffset.MinValue;
        var nextDelivery = DateTimeOffset.MinValue;
        logger.LogInformation("监控启动，每 {Minutes} 分钟采集 TOP {Count}，霸榜门槛 {Polls} 次。",
            options.IntervalMinutes, options.TopCount, options.DominancePolls);
        logger.LogInformation("每 {Hours} 小时新发一条 Telegram 榜单，其间编辑；静默 {Start:00}:00–{End:00}:00（{Zone}）。",
            policy.Options.NewMessageIntervalHours, policy.Options.QuietStartHour, policy.Options.QuietEndHour, policy.Options.TimeZoneId);
        while (!stoppingToken.IsCancellationRequested)
        {
            if (DateTimeOffset.UtcNow >= nextFetch)
            {
                nextFetch = DateTimeOffset.UtcNow.AddMinutes(options.IntervalMinutes);
                try { await engine.CollectAsync(stoppingToken); }
                catch (Exception exception) when (exception is HttpRequestException or InvalidDataException ||
                    exception is OperationCanceledException && !stoppingToken.IsCancellationRequested)
                {
                    logger.LogWarning("采集失败，保留上次榜单，下轮重试：{Reason}", exception.Message);
                }
                // Persistence errors intentionally escape and stop the host, preventing unsaved notifications.
            }
            if (engine.State.Pending is not null && DateTimeOffset.UtcNow >= nextDelivery)
            {
                try { await engine.DeliverAsync(stoppingToken); }
                catch (TelegramDeliveryException exception)
                {
                    nextDelivery = DateTimeOffset.UtcNow.AddSeconds(exception.RetryAfterSeconds);
                    logger.LogWarning("{Reason} 下次补发时间 {RetryAt:O}。", exception.Message, nextDelivery);
                }
            }
            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
        }
    }
}
