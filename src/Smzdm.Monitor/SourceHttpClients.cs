using Microsoft.Extensions.Logging;

namespace Smzdm.Monitor;

public sealed class SourceHttpClients : IDisposable
{
    public HttpClient Crawler { get; }
    public HttpClient Images { get; }

    public SourceHttpClients(MonitorOptions options, ILoggerFactory loggerFactory, IProxyNodeStore? nodeStore = null)
    {
        if (string.IsNullOrWhiteSpace(options.ProxyDbConnectionString))
        {
            Crawler = SmzdmClient.CreateHttpClient(options);
            Images = LeaderboardPhotoBuilder.CreateHttpClient(options);
            return;
        }
        var pool = new DatabaseProxyPool(nodeStore ?? new ProxyNodeStore(options), loggerFactory.CreateLogger<DatabaseProxyPool>());
        var logger = loggerFactory.CreateLogger<DatabaseProxyHandler>();
        Crawler = new(new DatabaseProxyHandler(pool, false, logger))
        {
            Timeout = TimeSpan.FromSeconds(40), MaxResponseContentBufferSize = 8 * 1024 * 1024
        };
        Images = new(new DatabaseProxyHandler(pool, true, logger)) { Timeout = TimeSpan.FromSeconds(8) };
    }

    public void Dispose()
    {
        Crawler.Dispose();
        Images.Dispose();
    }
}
