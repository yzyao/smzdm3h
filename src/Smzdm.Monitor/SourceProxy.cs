using System.Net;

namespace Smzdm.Monitor;

// Same authenticated HTTP/HTTPS proxy format as Vantage's GhostFleetManager.
public static class SourceProxy
{
    public static WebProxy? Parse(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") || string.IsNullOrWhiteSpace(uri.Host) ||
            uri.AbsolutePath != "/" || uri.Query.Length > 0 || uri.Fragment.Length > 0)
            throw new InvalidOperationException("Monitor:ProxyUrl 必须为 http:// 或 https:// 代理地址，可包含用户名和密码。");

        var proxy = new WebProxy(new UriBuilder(uri) { UserName = "", Password = "" }.Uri);
        if (uri.UserInfo.Length > 0)
        {
            var credentials = uri.UserInfo.Split(':', 2);
            if (credentials.Length != 2 || credentials[0].Length == 0 || credentials[1].Length == 0)
                throw new InvalidOperationException("Monitor:ProxyUrl 的认证信息必须同时包含用户名和密码。");
            proxy.Credentials = new NetworkCredential(
                Uri.UnescapeDataString(credentials[0]), Uri.UnescapeDataString(credentials[1]));
        }
        return proxy;
    }

    public static SocketsHttpHandler CreateHandler(MonitorOptions options, bool allowAutoRedirect = true) => new()
    {
        Proxy = Parse(options.ProxyUrl),
        // A null Proxy keeps the existing environment-proxy behavior. An explicit source
        // proxy is scoped to this handler; Telegram's client is never changed.
        UseProxy = true,
        AllowAutoRedirect = allowAutoRedirect,
        AutomaticDecompression = DecompressionMethods.All,
        UseCookies = false,
        ConnectTimeout = TimeSpan.FromSeconds(10),
        PooledConnectionLifetime = TimeSpan.FromMinutes(10)
    };

    public static string Display(string value) => Parse(value)?.Address?.GetComponents(
        UriComponents.SchemeAndServer, UriFormat.UriEscaped) ?? "默认网络出口";
}
