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
        logger.LogInformation("监控启动，按固定时点每 {Minutes} 分钟采集 TOP {Count}，霸榜门槛 {MinutesDominant} 分钟。",
            options.IntervalMinutes, options.TopCount, options.DominanceMinutes);
        logger.LogInformation("每天 07:00 建立基线；新进发新消息，其他变化更新最近消息；静默 {Start:00}:00–{End:00}:00（{Zone}）。",
            policy.Options.QuietStartHour, policy.Options.QuietEndHour, policy.Options.TimeZoneId);
        var nextDelivery = await startupNotifier.NotifyAsync(engine.State.Snapshot, clock.GetUtcNow(), stoppingToken)
            ?? DateTimeOffset.MinValue;
        // On startup, restore today's board immediately; the engine decides between a first
        // baseline, a new-item alert, an edit, or no Telegram operation at all.
        var nextFetch = clock.GetUtcNow();
        while (!stoppingToken.IsCancellationRequested)
        {
            if (clock.GetUtcNow() >= nextFetch)
            {
                nextFetch = schedule.After(clock.GetUtcNow());
                try { await engine.CollectAsync(stoppingToken); }
                catch (Exception exception) when (exception is HttpRequestException or InvalidDataException ||
                    exception is OperationCanceledException && !stoppingToken.IsCancellationRequested)
                {
                    logger.LogWarning("采集失败，保留上次榜单，下个固定时点重试：{Reason}", exception.Message);
                }
                // Persistence failures escape; never send an unsaved notification.
            }
            if (engine.State.Pending is not null && clock.GetUtcNow() >= nextDelivery)
            {
                try { await engine.DeliverAsync(stoppingToken); }
                catch (TelegramDeliveryException exception)
                {
                    nextDelivery = clock.GetUtcNow().AddSeconds(exception.RetryAfterSeconds);
                    logger.LogWarning("{Reason} 下次重试时间 {RetryAt:O}。", exception.Message, nextDelivery);
                }
            }
            // Skip elapsed slots after slow requests, keeping future scans on the clock grid.
            if (clock.GetUtcNow() >= nextFetch) nextFetch = schedule.After(clock.GetUtcNow());
            var wait = nextFetch - clock.GetUtcNow();
            if (wait > TimeSpan.FromSeconds(10)) wait = TimeSpan.FromSeconds(10);
            if (wait > TimeSpan.Zero) await Task.Delay(wait, clock, stoppingToken);
        }
    }
}
