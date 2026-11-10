using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Smzdm.Monitor;
using Xunit;

namespace Smzdm.Monitor.Tests;

public sealed class DatabaseProxyTests
{
    [Fact]
    public async Task RefreshFiltersInvalidUnhealthyAndLeasedNodesAndKeepsSuccessfulNodeSticky()
    {
        var store = new Store
        {
            Nodes = [new("bad", "ftp://user:secret@invalid", true, false),
                new("unhealthy", "http://unhealthy:8080", false, false),
                new("leased", "http://leased:8080", true, true),
                new("a", "http://first:8080", true, false), new("b", "http://second:8080", true, false)]
        };
        var pool = Pool(store);
        var first = await pool.RefreshAsync(default);
        Assert.Equal(new[] { "a", "b" }, first.Select(x => x.Name));
        pool.MarkSuccess(first[1]);
        Assert.Equal("b", (await pool.RefreshAsync(default))[0].Name);
        Assert.Equal("b", pool.Current.Name);
        store.Nodes = [first[0], first[1] with { IsLeased = true }];
        Assert.Equal("a", Assert.Single(await pool.RefreshAsync(default)).Name);
        Assert.Throws<InvalidDataException>(() => pool.Current);
    }

    [Fact]
    public async Task FailedRefreshClearsOldCandidatesInsteadOfUsingStaleProxyOrDirectConnection()
    {
        var store = new Store { Nodes = [new("a", "http://first:8080", true, false)] };
        var pool = Pool(store);
        pool.MarkSuccess(Assert.Single(await pool.RefreshAsync(default)));
        store.Failure = new InvalidDataException("Database unavailable");
        await Assert.ThrowsAsync<InvalidDataException>(() => pool.RefreshAsync(default));
        Assert.Throws<InvalidDataException>(() => pool.Current);
    }

    [Fact]
    public async Task TransportFailureSwitchesToNextProxyThenReusesItForNextScanAndImages()
    {
        using var proxy = new LocalProxy();
        var store = new Store { Nodes = [RefusedNode("a"), new("b", proxy.Url, true, false)] };
        var pool = Pool(store);
        using var clients = Clients(pool);
        using var first = await clients.Source.GetAsync("http://source.invalid/first");
        Assert.Equal("proxy-response", await first.Content.ReadAsStringAsync());
        Assert.Equal("b", pool.Current.Name);
        using var second = await clients.Source.GetAsync("http://source.invalid/second");
        using var image = await clients.Images.GetAsync("http://source.invalid/image.jpg");
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(HttpStatusCode.OK, image.StatusCode);
        Assert.Equal(2, store.Reads);
        Assert.Equal(3, proxy.Requests.Count);
        Assert.Contains(proxy.Requests, x => x.StartsWith("GET http://source.invalid/image.jpg "));
    }

    [Theory]
    [InlineData("http", 403)]
    [InlineData("http", 429)]
    [InlineData("http", 500)]
    [InlineData("https", 403)]
    [InlineData("https", 429)]
    [InlineData("https", 500)]
    public async Task SourceHttpErrorsDoNotRotateToAnotherProxy(string scheme, int status)
    {
        using var first = new LocalProxy(status);
        using var second = new LocalProxy();
        var store = new Store { Nodes = [new("a", first.Url, true, false), new("b", second.Url, true, false)] };
        using var clients = Clients(Pool(store));
        if (scheme == "http")
        {
            using var response = await clients.Source.GetAsync("http://source.invalid/test");
            Assert.Equal(status, (int)response.StatusCode);
        }
        else
        {
            var exception = await Assert.ThrowsAsync<HttpRequestException>(() => clients.Source.GetAsync("https://source.invalid/test"));
            Assert.Equal((HttpStatusCode)status, exception.StatusCode);
        }
        Assert.Single(first.Requests);
        Assert.Empty(second.Requests);
    }

    [Fact]
    public async Task ThreeTransportFailuresStopRoundAndNextRoundContinuesFromFourthNode()
    {
        using var unused = new LocalProxy();
        var store = new Store { Nodes = [RefusedNode("a"), RefusedNode("b"), RefusedNode("c"), new("d", unused.Url, true, false)] };
        var pool = Pool(store);
        using var clients = Clients(pool);
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => clients.Source.GetAsync("http://source.invalid/test"));
        Assert.Contains("未回退直连", error.Message);
        Assert.Empty(unused.Requests);
        using var next = await clients.Source.GetAsync("http://source.invalid/next-round");
        Assert.Equal(HttpStatusCode.OK, next.StatusCode);
        Assert.Equal("d", pool.Current.Name);
        Assert.Single(unused.Requests);
    }

    [Fact]
    public async Task EmptyPoolAndImagesWithoutSuccessfulSourceFailWithoutDirectRequests()
    {
        var pool = Pool(new());
        using var clients = Clients(pool);
        await Assert.ThrowsAsync<InvalidDataException>(() => clients.Source.GetAsync("http://source.invalid/test"));
        await Assert.ThrowsAsync<InvalidDataException>(() => clients.Images.GetAsync("http://source.invalid/image"));
    }

    [Fact]
    public async Task ImageDownloadDoesNotFollowRedirectOrChangeTheSourceProxy()
    {
        using var proxy = new LocalProxy();
        var store = new Store { Nodes = [new("a", proxy.Url, true, false)] };
        var pool = Pool(store);
        using var clients = Clients(pool);
        using var source = await clients.Source.GetAsync("http://source.invalid/start");
        proxy.Status = 302;
        using var image = await clients.Images.GetAsync("http://source.invalid/image");
        Assert.Equal(HttpStatusCode.Redirect, image.StatusCode);
        Assert.Equal(2, proxy.Requests.Count);
        Assert.Equal(1, store.Reads);
    }

    [Fact]
    public void DatabaseAndFixedProxyAreMutuallyExclusiveAndInvalidDatabaseConfigIsRedacted()
    {
        Assert.Throws<InvalidOperationException>(() => new MonitorOptions
        {
            ProxyUrl = "http://user:secret@proxy:8080", ProxyDbConnectionString = "Host=db;Password=secret"
        }.Validate());
        var exception = Assert.Throws<InvalidOperationException>(() => new MonitorOptions
        {
            ProxyDbConnectionString = "secret-invalid-connection"
        }.Validate());
        Assert.DoesNotContain("secret", exception.ToString());
    }

    private static DatabaseProxyPool Pool(Store store) => new(store, NullLogger<DatabaseProxyPool>.Instance);
    private static ClientPair Clients(DatabaseProxyPool pool) => new(pool);
    private static ProxyNode RefusedNode(string name)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return new(name, $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}", true, false);
    }

    private sealed class ClientPair(DatabaseProxyPool pool) : IDisposable
    {
        public HttpClient Source { get; } = new(new DatabaseProxyHandler(pool, false, NullLogger<DatabaseProxyHandler>.Instance))
            { Timeout = TimeSpan.FromSeconds(10) };
        public HttpClient Images { get; } = new(new DatabaseProxyHandler(pool, true, NullLogger<DatabaseProxyHandler>.Instance))
            { Timeout = TimeSpan.FromSeconds(10) };
        public void Dispose() { Source.Dispose(); Images.Dispose(); }
    }

    private sealed class Store : IProxyNodeStore
    {
        public IReadOnlyList<ProxyNode> Nodes { get; set; } = [];
        public Exception? Failure { get; set; }
        public int Reads { get; private set; }
        public Task<IReadOnlyList<ProxyNode>> ReadAsync(CancellationToken cancellationToken)
        {
            Reads++;
            return Failure is null ? Task.FromResult(Nodes) : Task.FromException<IReadOnlyList<ProxyNode>>(Failure);
        }
    }

    private sealed class LocalProxy : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _loop;
        public string Url { get; }
        public int Status { get; set; }
        public ConcurrentQueue<string> Requests { get; } = new();
        public LocalProxy(int status = 200)
        {
            Status = status;
            _listener.Start();
            Url = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";
            _loop = RunAsync();
        }
        private async Task RunAsync()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    using var connection = await _listener.AcceptTcpClientAsync(_stop.Token);
                    using var stream = connection.GetStream();
                    using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                    var request = new StringBuilder();
                    while (await reader.ReadLineAsync(_stop.Token) is { Length: > 0 } line) request.AppendLine(line);
                    Requests.Enqueue(request.ToString());
                    var body = "proxy-response";
                    var response = $"HTTP/1.1 {Status} Test\r\nContent-Length: {body.Length}\r\nConnection: close\r\n";
                    if (Status == 302) response += "Location: http://other.invalid/redirect\r\n";
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(response + "\r\n" + body), _stop.Token);
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        }
        public void Dispose()
        {
            _stop.Cancel();
            _loop.GetAwaiter().GetResult();
            _listener.Stop();
            _stop.Dispose();
        }
    }
}
