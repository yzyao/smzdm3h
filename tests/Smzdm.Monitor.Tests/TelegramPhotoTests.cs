using System.Net;
using System.Text;
using System.Text.Json;
using Smzdm.Monitor;
using Xunit;

namespace Smzdm.Monitor.Tests;

public sealed class TelegramPhotoTests
{
    private static PendingNotification Notification(TestClock clock) => new(
        "1. <a href=\"https://www.smzdm.com/p/1/\">商品 &amp; 1</a> · 10元",
        new NotificationPolicy(new()).PeriodStart(clock.GetUtcNow()), clock.GetUtcNow(),
        [LeaderboardTests.Deal("1") with { ImageUrl = "https://qny.smzdm.com/product.jpg" }]);

    [Theory]
    [InlineData(null)]
    [InlineData(1234L)]
    public async Task SendsOrEditsExactlyOnePhotoWithClickableTitlesAndCopyButtons(long? existingId)
    {
        var clock = new TestClock();
        var builder = new StubPhotoBuilder();
        var handler = new MultipartHandler();
        using var client = new HttpClient(handler);
        var sender = Sender(client, clock, builder);
        var report = Notification(clock);
        var id = await sender.PublishLeaderboardAsync(report, existingId, clock.GetUtcNow().AddHours(1), default);
        Assert.Equal(1234, id);
        Assert.Equal(1, handler.Calls);
        Assert.Equal(1, builder.Calls);
        Assert.EndsWith(existingId.HasValue ? "/editMessageMedia" : "/sendPhoto", handler.Uri);
        Assert.Equal("-100123", handler.Fields["chat_id"]);
        Assert.Equal("image/jpeg", handler.PhotoType);
        Assert.Equal(new byte[] { 255, 216, 255, 217 }, handler.Photo);
        if (existingId.HasValue)
        {
            Assert.Equal("1234", handler.Fields["message_id"]);
            Assert.False(handler.Fields.ContainsKey("message_thread_id"));
            using var media = JsonDocument.Parse(handler.Fields["media"]);
            Assert.Equal("photo", media.RootElement.GetProperty("type").GetString());
            Assert.Equal("attach://photo", media.RootElement.GetProperty("media").GetString());
            Assert.Equal(report.Text, media.RootElement.GetProperty("caption").GetString());
            Assert.Equal("HTML", media.RootElement.GetProperty("parse_mode").GetString());
        }
        else
        {
            Assert.Equal("42", handler.Fields["message_thread_id"]);
            Assert.Equal(report.Text, handler.Fields["caption"]);
            Assert.Equal("HTML", handler.Fields["parse_mode"]);
        }
        using var keyboard = JsonDocument.Parse(handler.Fields["reply_markup"]);
        var row = keyboard.RootElement.GetProperty("inline_keyboard")[0];
        Assert.Equal(report.Items![0].Url, row[0].GetProperty("url").GetString());
        Assert.False(row[0].TryGetProperty("copy_text", out _));
        Assert.Contains(report.Items[0].Url, row[1].GetProperty("copy_text").GetProperty("text").GetString());
        Assert.False(row[1].TryGetProperty("url", out _));
    }

    [Fact]
    public async Task QuietTimeSkipsBothImageDownloadsAndTelegramCalls()
    {
        var clock = new TestClock { Now = DateTimeOffset.Parse("2026-11-10T01:00:00+08:00") };
        var builder = new StubPhotoBuilder();
        var handler = new MultipartHandler();
        using var client = new HttpClient(handler);
        await Assert.ThrowsAsync<TelegramDeliveryException>(() =>
            Sender(client, clock, builder).PublishLeaderboardAsync(Notification(clock), null, clock.GetUtcNow().AddHours(1), default));
        Assert.Equal(0, builder.Calls);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task CrossingIntoQuietHoursWhileBuildingPhotoDoesNotSendIt()
    {
        var clock = new TestClock { Now = DateTimeOffset.Parse("2026-11-10T00:59:59+08:00") };
        var builder = new StubPhotoBuilder { BeforeReturn = () => clock.Now = clock.Now.AddSeconds(2) };
        var handler = new MultipartHandler();
        using var client = new HttpClient(handler);
        await Assert.ThrowsAsync<TelegramDeliveryException>(() =>
            Sender(client, clock, builder).PublishLeaderboardAsync(Notification(clock), 1234, clock.GetUtcNow().AddHours(1), default));
        Assert.Equal(1, builder.Calls);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task RetryingUploadBuildsPhotoOnlyOnceAndRecreatesMultipartContent()
    {
        var clock = new TestClock();
        var builder = new StubPhotoBuilder();
        var handler = new MultipartHandler { FailFirstRequest = true };
        using var client = new HttpClient(handler);
        var id = await Sender(client, clock, builder).PublishLeaderboardAsync(Notification(clock), null, clock.GetUtcNow().AddHours(1), default);
        Assert.Equal(1234, id);
        Assert.Equal(1, builder.Calls);
        Assert.Equal(2, handler.Calls);
        Assert.Equal(4, handler.Photo!.Length);
    }

    [Fact]
    public async Task AlreadyAppliedMediaEditIsAcknowledgedWithoutSendingNewMessage()
    {
        var clock = new TestClock();
        var handler = new MultipartHandler { NotModified = true };
        using var client = new HttpClient(handler);
        Assert.Equal(1234, await Sender(client, clock, new StubPhotoBuilder()).PublishLeaderboardAsync(
            Notification(clock), 1234, clock.GetUtcNow().AddHours(1), default));
        Assert.Equal(1, handler.Calls);
        Assert.EndsWith("/editMessageMedia", handler.Uri);
    }

    private static TelegramSender Sender(HttpClient client, TestClock clock, ILeaderboardPhotoBuilder builder) =>
        new(client, new TelegramOptions { BotToken = "123:test", ChatId = "-100123", MessageThreadId = 42 },
            new NotificationPolicy(new()), clock, builder);

    private sealed class StubPhotoBuilder : ILeaderboardPhotoBuilder
    {
        public Task<IReadOnlyList<byte[]>> BuildProductPhotosAsync(IReadOnlyList<Deal> deals, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("旧图片消息不应生成独立商品图。");

        public int Calls { get; private set; }
        public Action? BeforeReturn { get; init; }
        public Task<byte[]> BuildAsync(IReadOnlyList<Deal> deals, CancellationToken cancellationToken)
        {
            Calls++;
            BeforeReturn?.Invoke();
            return Task.FromResult(new byte[] { 255, 216, 255, 217 });
        }
    }

    private sealed class MultipartHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public bool FailFirstRequest { get; init; }
        public bool NotModified { get; init; }
        public string? Uri { get; private set; }
        public Dictionary<string, string> Fields { get; } = [];
        public byte[]? Photo { get; private set; }
        public string? PhotoType { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Uri = request.RequestUri!.AbsoluteUri;
            var form = Assert.IsType<MultipartFormDataContent>(request.Content);
            foreach (var part in form)
            {
                var name = part.Headers.ContentDisposition!.Name!.Trim('"');
                if (name == "photo")
                {
                    Photo = await part.ReadAsByteArrayAsync(cancellationToken);
                    PhotoType = part.Headers.ContentType!.MediaType;
                }
                else Fields[name] = await part.ReadAsStringAsync(cancellationToken);
            }
            var body = NotModified ? "{\"ok\":false,\"error_code\":400,\"description\":\"Bad Request: message is not modified\"}"
                : FailFirstRequest && Calls == 1 ? "{\"ok\":false,\"error_code\":500}"
                : "{\"ok\":true,\"result\":{\"message_id\":1234}}";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }
}
