using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Smzdm.Monitor;

public sealed class MonitorWorker(
    MonitorEngine engine, MonitorOptions options, StateStore store, ILogger<MonitorWorker> logger,
    NotificationPolicy policy, StartupNotifier startupNotifier, TimeProvider clock) : BackgroundService
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
        var schedule = new CollectionSchedule(options, policy);
        var now = clock.GetUtcNow();
        var nextFetch = schedule.AtOrAfter(now);
        if (engine.State.Snapshot?.CapturedAt >= nextFetch) nextFetch = schedule.After(now);
        var nextDelivery = DateTimeOffset.MinValue;
        var deliveryEnabled = false;
        logger.LogInformation("监控启动，按本地零点对齐，每 {Minutes} 分钟采集 TOP {Count}，霸榜门槛 {Polls} 次。",
            options.IntervalMinutes, options.TopCount, options.DominancePolls);
        logger.LogInformation("每 {Hours} 小时新发一条 Telegram 榜单，其间编辑；静默 {Start:00}:00–{End:00}:00（{Zone}）。",
            policy.Options.NewMessageIntervalHours, policy.Options.QuietStartHour, policy.Options.QuietEndHour, policy.Options.TimeZoneId);
        nextDelivery = await startupNotifier.NotifyAsync(engine.State.Snapshot, nextFetch, stoppingToken)
            ?? DateTimeOffset.MinValue;

        // Keep health checks and the comparison baseline useful on a first or stale start,
        // without creating an off-schedule leaderboard notification.
        now = clock.GetUtcNow();
        if (now < nextFetch && (engine.State.Snapshot is not { } snapshot || snapshot.CapturedAt > now ||
            now - snapshot.CapturedAt >= TimeSpan.FromMinutes(options.IntervalMinutes)))
        {
            try { await engine.CollectBaselineAsync(stoppingToken); }
            catch (Exception exception) when (IsCollectionFailure(exception, stoppingToken))
            {
                logger.LogWarning("启动基线采集失败，将在固定时间点重试：{Reason}", exception.Message);
            }
        }
        while (!stoppingToken.IsCancellationRequested)
        {
            if (clock.GetUtcNow() >= nextFetch)
            {
                var slot = nextFetch;
                nextFetch = schedule.After(clock.GetUtcNow());
                try
                {
                    await engine.CollectScheduledAsync(slot, stoppingToken);
                    deliveryEnabled = true;
                }
                catch (Exception exception) when (IsCollectionFailure(exception, stoppingToken))
                {
                    logger.LogWarning("采集失败，保留上次榜单，下轮重试：{Reason}", exception.Message);
                }
                // Persistence errors intentionally escape and stop the host, preventing unsaved notifications.
            }
            if (deliveryEnabled && engine.State.Pending is not null && clock.GetUtcNow() >= nextDelivery)
            {
                try { await engine.DeliverAsync(stoppingToken); }
                catch (TelegramDeliveryException exception)
                {
                    nextDelivery = clock.GetUtcNow().AddSeconds(exception.RetryAfterSeconds);
                    logger.LogWarning("{Reason} 下次补发时间 {RetryAt:O}。", exception.Message, nextDelivery);
                }
            }
            // Slow requests or a suspended host must not replay missed slots in a burst.
            if (clock.GetUtcNow() >= nextFetch) nextFetch = schedule.After(clock.GetUtcNow());
            var wait = nextFetch - clock.GetUtcNow();
            if (wait > TimeSpan.FromSeconds(10)) wait = TimeSpan.FromSeconds(10);
            if (wait > TimeSpan.Zero) await Task.Delay(wait, clock, stoppingToken);
        }
    }

    private static bool IsCollectionFailure(Exception exception, CancellationToken stoppingToken) =>
        exception is HttpRequestException or InvalidDataException ||
        exception is OperationCanceledException && !stoppingToken.IsCancellationRequested;
}
