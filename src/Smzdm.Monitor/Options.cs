namespace Smzdm.Monitor;

public sealed class MonitorOptions
{
    public string SourceUrl { get; set; } = "https://faxian.smzdm.com/h2s0t0f0c0p1/";
    public int IntervalMinutes { get; set; } = 30;
    public int TopCount { get; set; } = 20;
    public int DominancePolls { get; set; } = 3;
    public string DataDirectory { get; set; } = "data";
    public string Cookie { get; set; } = "";
    public string UserAgent { get; set; } = "Mozilla/5.0";

    public void Validate()
    {
        if (!Uri.TryCreate(SourceUrl, UriKind.Absolute, out var uri) ||
            uri.Scheme != "https" || uri.Host != "faxian.smzdm.com")
            throw new InvalidOperationException("Monitor:SourceUrl 必须为 https://faxian.smzdm.com/ 下的榜单地址。");
        if (IntervalMinutes is < 1 or > 1440 || TopCount is < 1 or > 20 || DominancePolls is < 2 or > 10000)
            throw new InvalidOperationException("IntervalMinutes 范围 1–1440，图文榜单 TopCount 范围 1–20，DominancePolls 范围 2–10000。");
        if (string.IsNullOrWhiteSpace(DataDirectory) || string.IsNullOrWhiteSpace(UserAgent))
            throw new InvalidOperationException("DataDirectory 和 UserAgent 不能为空。");
    }
}

public sealed class TelegramOptions
{
    public string BotToken { get; set; } = "";
    public string ChatId { get; set; } = "";
    public int? MessageThreadId { get; set; }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(BotToken) || string.IsNullOrWhiteSpace(ChatId))
            throw new InvalidOperationException("请设置 Telegram__BotToken 和 Telegram__ChatId；离线预览可使用 --dry-run。");
        if (!System.Text.RegularExpressions.Regex.IsMatch(BotToken, @"^\d+:[A-Za-z0-9_-]+$"))
            throw new InvalidOperationException("Telegram__BotToken 格式不正确。");
        if (MessageThreadId is <= 0)
            throw new InvalidOperationException("Telegram__MessageThreadId 必须为正整数或留空。");
    }
}
