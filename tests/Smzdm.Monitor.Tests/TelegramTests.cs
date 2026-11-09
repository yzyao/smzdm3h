using System.Net;
using System.Text;
using System.Text.Json;
using Smzdm.Monitor;
using Xunit;

namespace Smzdm.Monitor.Tests;

public sealed class TelegramTests
{
    private static TelegramOptions Options => new() { BotToken = "123:secret-token", ChatId = "-100123", MessageThreadId = 42 };

    [Fact]
    public async Task NewMessageReturnsIdAndIncludesTopicAndHtmlMode()
    {
        var handler = new StubHandler("{\"ok\":true,\"result\":{\"message_id\":1234}}");
        using var client = new HttpClient(handler);
        var sender = Sender(client);
        var id = await sender.PublishAsync("<b>商品</b> &amp; 😀", null, DateTimeOffset.MaxValue, default);
        Assert.Equal(1234, id);
        Assert.EndsWith("/sendMessage", handler.Uri);
        using var json = JsonDocument.Parse(handler.Body!);
        Assert.Equal("-100123", json.RootElement.GetProperty("chat_id").GetString());
        Assert.Equal("HTML", json.RootElement.GetProperty("parse_mode").GetString());
        Assert.Equal(42, json.RootElement.GetProperty("message_thread_id").GetInt32());
        Assert.False(json.RootElement.TryGetProperty("message_id", out _));
        Assert.True(json.RootElement.GetProperty("link_preview_options").GetProperty("is_disabled").GetBoolean());
    }

    [Fact]
    public async Task EditUsesMessageIdAndDoesNotSendTopicParameter()
    {
        var handler = new StubHandler("{\"ok\":true,\"result\":{\"message_id\":1234}}");
        using var client = new HttpClient(handler);
        var id = await Sender(client).PublishAsync("更新", 1234, DateTimeOffset.MaxValue, default);
        Assert.Equal(1234, id);
        Assert.EndsWith("/editMessageText", handler.Uri);
        using var json = JsonDocument.Parse(handler.Body!);
        Assert.Equal(1234, json.RootElement.GetProperty("message_id").GetInt64());
        Assert.False(json.RootElement.TryGetProperty("message_thread_id", out _));
    }

    [Fact]
    public async Task NotModifiedEditIsAcknowledgedAsSuccess()
    {
        var handler = new StubHandler("{\"ok\":false,\"error_code\":400,\"description\":\"Bad Request: message is not modified\"}");
        using var client = new HttpClient(handler);
        Assert.Equal(1234, await Sender(client).PublishAsync("相同", 1234, DateTimeOffset.MaxValue, default));
    }

    [Fact]
    public async Task ApiFailureDoesNotLeakTokenOrResponseBody()
    {
        var handler = new StubHandler("{\"ok\":false,\"error_code\":403,\"description\":\"secret-token\"}");
        using var client = new HttpClient(handler);
        var exception = await Assert.ThrowsAsync<TelegramDeliveryException>(() =>
            Sender(client).PublishAsync("test", null, DateTimeOffset.MaxValue, default));
        Assert.Contains("403", exception.Message);
        Assert.DoesNotContain("secret-token", exception.ToString());
    }

    [Fact]
    public async Task LongRateLimitIsDeferredAndKeepsServerRetryAfter()
    {
        var handler = new StubHandler("{\"ok\":false,\"error_code\":429,\"parameters\":{\"retry_after\":120}}");
        using var client = new HttpClient(handler);
        var exception = await Assert.ThrowsAsync<TelegramDeliveryException>(() =>
            Sender(client).PublishAsync("test", null, DateTimeOffset.MaxValue, default));
        Assert.Equal(120, exception.RetryAfterSeconds);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(1234L)]
    public async Task QuietTimeBlocksBothSendingAndEditingWithoutAnyHttpRequest(long? messageId)
    {
        var handler = new StubHandler("{\"ok\":true,\"result\":{\"message_id\":1234}}");
        using var client = new HttpClient(handler);
        var clock = new TestClock { Now = DateTimeOffset.Parse("2026-11-09T01:00:00+08:00") };
        await Assert.ThrowsAsync<TelegramDeliveryException>(() =>
            Sender(client, clock).PublishAsync("test", messageId, DateTimeOffset.MaxValue, default));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task InternalRetryStopsAtQuietBoundary()
    {
        var clock = new TestClock { Now = DateTimeOffset.Parse("2026-11-09T00:59:59+08:00") };
        var handler = new StubHandler("{\"ok\":false,\"error_code\":500}");
        using var client = new HttpClient(handler);
        await Assert.ThrowsAsync<TelegramDeliveryException>(() =>
            Sender(client, clock).PublishAsync("test", null, DateTimeOffset.MaxValue, default));
        Assert.Equal(1, handler.Calls);
        Assert.True(clock.Now.Hour >= 1);
    }

    [Fact]
    public async Task ExpiredPeriodNeverStartsAnHttpRequest()
    {
        var handler = new StubHandler("{\"ok\":true,\"result\":{\"message_id\":1234}}");
        using var client = new HttpClient(handler);
        var clock = new TestClock();
        await Assert.ThrowsAsync<TelegramDeliveryException>(() =>
            Sender(client, clock).PublishAsync("test", null, clock.GetUtcNow(), default));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task InternalRetryCannotPublishExpiredReportInNextPeriod()
    {
        var clock = new TestClock { Now = DateTimeOffset.Parse("2026-11-09T10:59:59+08:00") };
        var handler = new StubHandler("{\"ok\":false,\"error_code\":500}");
        using var client = new HttpClient(handler);
        await Assert.ThrowsAsync<TelegramDeliveryException>(() =>
            Sender(client, clock).PublishAsync("test", null, clock.GetUtcNow().AddSeconds(1), default));
        Assert.Equal(1, handler.Calls);
    }

    private static TelegramSender Sender(HttpClient client, TestClock? clock = null) =>
        new(client, Options, new NotificationPolicy(new()), clock ?? new());

    private sealed class StubHandler(string response) : HttpMessageHandler
    {
        public string? Body { get; private set; }
        public string? Uri { get; private set; }
        public int Calls { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Uri = request.RequestUri!.AbsoluteUri;
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response, Encoding.UTF8, "application/json") };
        }
    }
}
