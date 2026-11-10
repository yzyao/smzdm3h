using Microsoft.Extensions.Logging;

namespace Smzdm.Monitor;

public sealed class DatabaseProxyPool(IProxyNodeStore store, ILogger<DatabaseProxyPool> logger)
{
    private IReadOnlyList<ProxyNode> _nodes = [];
    private string? _successfulUrl;
    private string? _lastFailedUrl;

    public async Task<IReadOnlyList<ProxyNode>> RefreshAsync(CancellationToken cancellationToken)
    {
        // Clear first: an unavailable database must not silently authorize stale nodes.
        _nodes = [];
        var nodes = await store.ReadAsync(cancellationToken);
        var available = new List<ProxyNode>();
        foreach (var node in nodes.Where(x => x.IsHealthy && !x.IsLeased))
        {
            try
            {
                if (SourceProxy.Parse(node.Url) is not null && !available.Any(x => x.Url == node.Url))
                    available.Add(node);
            }
            catch (InvalidOperationException)
            {
                logger.LogWarning("Vantage 代理池存在无效节点地址，已跳过（不输出原始 URL）。");
            }
        }
        if (available.Count == 0)
            throw new InvalidDataException("Vantage 代理池没有健康且未被租用的可用节点，本轮不采集，不回退直连。");
        var start = available.FindIndex(x => x.Url == _successfulUrl);
        if (start < 0) start = (available.FindIndex(x => x.Url == _lastFailedUrl) + 1) % available.Count;
        _nodes = available.Skip(start).Concat(available.Take(start)).ToList();
        return _nodes;
    }

    public ProxyNode Current => _nodes.FirstOrDefault(x => x.Url == _successfulUrl)
        ?? throw new InvalidDataException("尚无本轮成功采集使用的代理节点，暂不下载商品图片。");

    public void MarkSuccess(ProxyNode node) => _successfulUrl = node.Url;

    public void MarkFailure(ProxyNode node)
    {
        _lastFailedUrl = node.Url;
        if (_successfulUrl == node.Url) _successfulUrl = null;
    }
}
