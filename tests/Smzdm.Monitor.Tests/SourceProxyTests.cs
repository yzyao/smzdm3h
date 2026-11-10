using System.Net;
using System.Net.Sockets;
using System.Text;
using Smzdm.Monitor;
using Xunit;

namespace Smzdm.Monitor.Tests;

public sealed class SourceProxyTests
{
    [Theory]
    [InlineData("http")]
    [InlineData("https")]
    public void VantageStyleCredentialsAreDecodedAndKeptOutOfEndpointAndLogs(string scheme)
    {
        var value = $"{scheme}://user%40name:pa%3Ass%24word@proxy.example:8080";
        var proxy = SourceProxy.Parse(value)!;
        Assert.Equal($"{scheme}://proxy.example:8080/", proxy.Address!.AbsoluteUri);
        var credentials = Assert.IsType<NetworkCredential>(proxy.Credentials);
        Assert.Equal("user@name", credentials.UserName);
        Assert.Equal("pa:ss$word", credentials.Password);
        Assert.Equal($"{scheme}://proxy.example:8080", SourceProxy.Display(value));
    }

    [Theory]
    [InlineData("not-a-url-secret")]
    [InlineData("ftp://user:secret@proxy.example")]
    [InlineData("http://user:secret@proxy.example/path")]
    [InlineData("http://user:secret@proxy.example/?token=secret")]
    [InlineData("http://user:secret@proxy.example/#secret")]
    [InlineData("http://secret@proxy.example")]
    [InlineData("http://:secret@proxy.example")]
    public void InvalidConfigurationFailsWithoutExposingCredentials(string value)
    {
        var exception = Assert.Throws<InvalidOperationException>(() => new MonitorOptions { ProxyUrl = value }.Validate());
        Assert.DoesNotContain("secret", exception.ToString());
    }

    [Fact]
    public void EmptyConfigurationPreservesDefaultNetworkingAndSourceProxyDoesNotChangeTelegram()
    {
        var defaultProxy = HttpClient.DefaultProxy;
        Assert.Null(SourceProxy.Parse("  "));
        using var defaults = SourceProxy.CreateHandler(new());
        Assert.Null(defaults.Proxy);
        Assert.True(defaults.UseProxy);
        using var configured = SourceProxy.CreateHandler(new() { ProxyUrl = "http://user:password@proxy.example:8080" }, false);
        Assert.NotNull(configured.Proxy);
        Assert.False(configured.AllowAutoRedirect);
        Assert.Equal(TimeSpan.FromSeconds(10), configured.ConnectTimeout);
        Assert.Same(defaultProxy, HttpClient.DefaultProxy);
    }

    [Fact]
    public async Task HttpsConnectUsesConfiguredProxyAndRespondsToAuthenticationChallenge()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var proxyRequests = CaptureAsync(listener, timeout.Token);
        using var client = SmzdmClient.CreateHttpClient(new() { ProxyUrl = $"http://test-user:test-password@127.0.0.1:{port}" });
        // The local test proxy rejects the tunnel after verifying credentials. No request
        // reaches the external host; this tests actual .NET proxy negotiation, not a stub.
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("https://source.invalid/", timeout.Token));
        var requests = await proxyRequests;
        Assert.All(requests, request => Assert.StartsWith("CONNECT source.invalid:443 HTTP/1.1", request));
        Assert.DoesNotContain("Proxy-Authorization:", requests[0]);
        var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes("test-user:test-password"));
        Assert.Contains($"Proxy-Authorization: Basic {basic}", requests[1]);
    }

    private static async Task<List<string>> CaptureAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        var requests = new List<string>();
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var connection = await listener.AcceptTcpClientAsync(cancellationToken);
            using var stream = connection.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            var request = new StringBuilder();
            while (await reader.ReadLineAsync(cancellationToken) is { Length: > 0 } line)
                request.AppendLine(line);
            requests.Add(request.ToString());
            var response = attempt == 0
                ? "HTTP/1.1 407 Proxy Authentication Required\r\nProxy-Authenticate: Basic realm=\"test\"\r\n"
                : "HTTP/1.1 502 Bad Gateway\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(response + "Content-Length: 0\r\nConnection: close\r\n\r\n"), cancellationToken);
        }
        return requests;
    }
}
