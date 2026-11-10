using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Smzdm.Monitor;

// Source clients refresh and fail over; image clients use exactly the successful source
// node. Both share a pool, while Telegram has its own, unchanged HTTP transport.
public sealed class DatabaseProxyHandler(DatabaseProxyPool pool, bool forImages,
    ILogger<DatabaseProxyHandler> logger) : HttpMessageHandler
{
    private readonly ConcurrentDictionary<string, HttpMessageInvoker> _clients = new(StringComparer.Ordinal);
    public const int MaxAttempts = 3;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Method != HttpMethod.Get || request.Content is not null)
            throw new InvalidOperationException("采集代理只支持无正文的 GET 请求。");
        var nodes = forImages ? new[] { pool.Current } : await pool.RefreshAsync(cancellationToken);
        foreach (var node in nodes.Take(forImages ? 1 : MaxAttempts))
        {
            var client = _clients.GetOrAdd(node.Url, url => new HttpMessageInvoker(
                SourceProxy.CreateHandler(new() { ProxyUrl = url }, allowAutoRedirect: !forImages)));
            // Each node gets a fresh message. A request cannot be reused after an attempt.
            using var attempt = new HttpRequestMessage(request.Method, request.RequestUri)
            {
                Version = request.Version, VersionPolicy = request.VersionPolicy
            };
            foreach (var header in request.Headers) attempt.Headers.TryAddWithoutValidation(header.Key, header.Value);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(forImages ? 8 : 9));
            try
            {
                var response = await client.SendAsync(attempt, timeout.Token);
                // Any HTTP response (including 403/429/5xx) is returned as-is. Failover is
                // only for transport failures, not for bypassing a source's rate limit.
                if (!forImages)
                {
                    if (response.IsSuccessStatusCode) pool.MarkSuccess(node);
                    logger.LogInformation("值得买代理出口 {Endpoint}，HTTP {Status}。",
                        SourceProxy.Display(node.Url), (int)response.StatusCode);
                }
                response.RequestMessage = request;
                return response;
            }
            catch (HttpRequestException exception) when (exception.StatusCode is { } status)
            {
                // CONNECT failures can be surfaced as exceptions rather than responses.
                // Preserve HTTP refusal semantics here as well; do not rotate on 403/429.
                throw new HttpRequestException($"代理或目标返回 HTTP {(int)status}，本轮结束，不切换节点。", null, status);
            }
            catch (Exception exception) when (exception is HttpRequestException ||
                exception is OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                if (!forImages)
                {
                    pool.MarkFailure(node);
                    logger.LogWarning("代理 {Endpoint} 连接失败或超时，尝试下一个节点（最多 3 个）。", SourceProxy.Display(node.Url));
                }
            }
        }
        throw new HttpRequestException("本轮可用代理连接均失败，保留旧榜单，等待下轮扫描（未回退直连）。");
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) foreach (var client in _clients.Values) client.Dispose();
        base.Dispose(disposing);
    }
}
