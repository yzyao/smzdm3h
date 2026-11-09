using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Smzdm.Monitor;

try
{
    var dryRun = args.Contains("--dry-run");
    var once = args.Contains("--once");
    var healthcheck = args.Contains("--healthcheck");
    string? htmlPath = null;
    for (var i = 0; i < args.Length; i++)
    {
        if (args[i] == "--html" && i + 1 < args.Length) htmlPath = args[++i];
        else if (args[i] is not ("--dry-run" or "--once" or "--healthcheck"))
            throw new InvalidOperationException("参数：--once | --dry-run [--html 文件路径] | --healthcheck");
    }
    if (htmlPath is not null && !dryRun) throw new InvalidOperationException("--html 只能与 --dry-run 一起使用。");
    var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
    {
        Args = [], ContentRootPath = AppContext.BaseDirectory
    });
    var monitor = builder.Configuration.GetSection("Monitor").Get<MonitorOptions>() ?? new();
    var telegram = builder.Configuration.GetSection("Telegram").Get<TelegramOptions>() ?? new();
    var notifications = builder.Configuration.GetSection("Notifications").Get<NotificationOptions>() ?? new();
    var policy = new NotificationPolicy(notifications);
    monitor.Validate();
    var store = new StateStore(monitor);
    if (healthcheck)
    {
        var state = await store.LoadAsync(CancellationToken.None);
        var age = state.Snapshot is null ? TimeSpan.MaxValue : DateTimeOffset.UtcNow - state.Snapshot.CapturedAt;
        var healthy = age >= TimeSpan.Zero && age <= TimeSpan.FromMinutes(monitor.IntervalMinutes * 3);
        Console.WriteLine(healthy ? "healthy" : "unhealthy: 最近三个周期内未成功采集");
        return healthy ? 0 : 1;
    }
    var parser = new SmzdmParser();
    if (dryRun)
    {
        using var http = SmzdmClient.CreateHttpClient();
        var deals = htmlPath is null
            ? await new SmzdmClient(http, parser, monitor).FetchAsync(CancellationToken.None)
            : parser.Parse(await File.ReadAllTextAsync(htmlPath), monitor.TopCount);
        var preview = Leaderboard.Compare(null, deals, DateTimeOffset.UtcNow, monitor);
        var report = NotificationPolicy.BuildReport(null, preview, policy.PeriodStart(preview.Snapshot.CapturedAt));
        Console.WriteLine(MessageFormatter.PlainText(MessageFormatter.Format(report, preview.Snapshot, monitor, policy)));
        if (policy.IsQuiet(preview.Snapshot.CapturedAt)) Console.WriteLine("当前为静默时段，正式运行时不会请求 Telegram。");
        Console.WriteLine($"预览完成：{deals.Count} 条；未发 Telegram，未修改状态。");
        return 0;
    }
    telegram.Validate();
    builder.Services.AddSingleton(monitor);
    builder.Services.AddSingleton(store);
    builder.Services.AddSingleton(policy);
    builder.Services.AddSingleton(TimeProvider.System);
    builder.Services.AddSingleton<IDealSource>(_ => new SmzdmClient(SmzdmClient.CreateHttpClient(), parser, monitor));
    builder.Services.AddSingleton<INotificationSender>(_ => new TelegramSender(new HttpClient
        { Timeout = TimeSpan.FromSeconds(30) }, telegram, policy, TimeProvider.System));
    builder.Services.AddSingleton<MonitorEngine>();
    builder.Services.AddHostedService<MonitorWorker>();
    using var host = builder.Build();
    if (once)
    {
        using var dataLock = store.AcquireLock();
        var engine = host.Services.GetRequiredService<MonitorEngine>();
        await engine.InitializeAsync(CancellationToken.None);
        await engine.CollectAsync(CancellationToken.None);
        await engine.DeliverAsync(CancellationToken.None);
        return 0;
    }
    await host.RunAsync();
    return Environment.ExitCode;
}
catch (Exception exception)
{
    // Do not dump exception chains, which may contain a Telegram URL and token.
    Console.Error.WriteLine($"运行失败（{exception.GetType().Name}）。{SafeMessage(exception)}");
    return 1;
}

static string SafeMessage(Exception exception) => exception is HttpRequestException or TaskCanceledException
    ? "网络请求失败，请检查服务器网络、代理和目标站点可访问性。"
    : exception.Message;
