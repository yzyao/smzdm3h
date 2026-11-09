using System.Net;

namespace Smzdm.Monitor;

public interface IDealSource
{
    Task<List<Deal>> FetchAsync(CancellationToken cancellationToken);
}

public sealed class SmzdmClient(HttpClient client, SmzdmParser parser, MonitorOptions options) : IDealSource
{
    public async Task<List<Deal>> FetchAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, options.SourceUrl);
        request.Headers.UserAgent.ParseAdd(options.UserAgent);
        request.Headers.Accept.ParseAdd("text/html,application/xhtml+xml");
        request.Headers.AcceptLanguage.ParseAdd("zh-CN,zh;q=0.9");
        if (!string.IsNullOrWhiteSpace(options.Cookie)) request.Headers.Add("Cookie", options.Cookie);
        using var response = await client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync(cancellationToken);
        return parser.Parse(html, options.TopCount);
    }

    public static HttpClient CreateHttpClient() => new(new SocketsHttpHandler
    {
        AutomaticDecompression = DecompressionMethods.All,
        UseCookies = false,
        PooledConnectionLifetime = TimeSpan.FromMinutes(10)
    }) { Timeout = TimeSpan.FromSeconds(40), MaxResponseContentBufferSize = 8 * 1024 * 1024 };
}
