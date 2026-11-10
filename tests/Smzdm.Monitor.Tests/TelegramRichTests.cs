using System.Net;
using System.Text.Json;
using Smzdm.Monitor;
using Xunit;

namespace Smzdm.Monitor.Tests;

public sealed class TelegramRichTests
{
    [Theory]
    [InlineData(null)]
    [InlineData(1234L)]
    public async Task SendsOrEditsOneRichMessageWithEachPhotoAttachedInOrderAndNoSeparateKeyboard(long? existingId)
    {
        var clock = new TestClock();
        var handler = new RichHandler();
        var photos = new ProductPhotos();
        using var client = new HttpClient(handler);
        var notification = Notification(clock);
        var id = await Sender(client, clock, photos).PublishLeaderboardAsync(notification, existingId, clock.Now.AddHours(1), default);
        Assert.Equal(1234, id);
        Assert.Equal(1, handler.Calls);
        Assert.Equal(1, photos.Calls);
        Assert.EndsWith(existingId.HasValue ? "/editMessageText" : "/sendRichMessage", handler.Uri);
        Assert.Equal("-100123", handler.Fields["chat_id"]);
        Assert.False(handler.Fields.ContainsKey("reply_markup"));
        if (existingId.HasValue)
        {
            Assert.Equal("1234", handler.Fields["message_id"]);
            Assert.False(handler.Fields.ContainsKey("message_thread_id"));
        }
        else Assert.Equal("42", handler.Fields["message_thread_id"]);
        using var rich = JsonDocument.Parse(handler.Fields["rich_message"]);
        Assert.Equal(notification.Text, rich.RootElement.GetProperty("html").GetString());
        Assert.True(rich.RootElement.GetProperty("skip_entity_detection").GetBoolean());
        var media = rich.RootElement.GetProperty("media");
        Assert.Equal(20, media.GetArrayLength());
        for (var i = 0; i < 20; i++)
        {
            Assert.Equal($"product_{i}", media[i].GetProperty("id").GetString());
            Assert.Equal("photo", media[i].GetProperty("media").GetProperty("type").GetString());
            Assert.Equal($"attach://product_{i}", media[i].GetProperty("media").GetProperty("media").GetString());
            Assert.Equal(new byte[] { 255, 216, (byte)i, 255, 217 }, handler.Files[$"product_{i}"]);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QuietOrExpiredMessagesDoNotDownloadPhotosOrCallTelegram(bool expired)
    {
        var clock = new TestClock();
        if (!expired) clock.Now = DateTimeOffset.Parse("2026-11-10T01:00:00+08:00");
        var handler = new RichHandler();
        var photos = new ProductPhotos();
        using var client = new HttpClient(handler);
        await Assert.ThrowsAsync<TelegramDeliveryException>(() => Sender(client, clock, photos).PublishLeaderboardAsync(
            Notification(clock), null, expired ? clock.Now : clock.Now.AddHours(1), default));
        Assert.Equal(0, photos.Calls);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task RechecksQuietHoursAfterDownloadingPhotos()
    {
        var clock = new TestClock { Now = DateTimeOffset.Parse("2026-11-10T00:59:59+08:00") };
        var handler = new RichHandler();
        var photos = new ProductPhotos { BeforeReturn = () => clock.Now += TimeSpan.FromSeconds(2) };
        using var client = new HttpClient(handler);
        await Assert.ThrowsAsync<TelegramDeliveryException>(() => Sender(client, clock, photos).PublishLeaderboardAsync(
            Notification(clock), 1234, clock.Now.AddHours(1), default));
        Assert.Equal(1, photos.Calls);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task RetryRecreatesMultipartContentWithoutDownloadingAgain()
    {
        var clock = new TestClock();
        var handler = new RichHandler { FailFirstRequest = true };
        var photos = new ProductPhotos();
        using var client = new HttpClient(handler);
        Assert.Equal(1234, await Sender(client, clock, photos).PublishLeaderboardAsync(
            Notification(clock), null, clock.Now.AddHours(1), default));
        Assert.Equal(2, handler.Calls);
        Assert.Equal(1, photos.Calls);
        Assert.Equal(20, handler.Files.Count);
    }

    private static PendingNotification Notification(TestClock clock)
    {
        var policy = new NotificationPolicy(new());
        var deals = Enumerable.Range(1, 20).Select(i => LeaderboardTests.Deal(i.ToString(), i)).ToList();
        var comparison = Leaderboard.Compare(null, deals, clock.Now, new());
        var report = NotificationPolicy.BuildReport(comparison, policy.PeriodStart(clock.Now));
        return new(RichMessageFormatter.Format(report, comparison.Snapshot, new(), policy),
            report.PeriodStart, clock.Now, deals, IsRich: true);
    }

    private static TelegramSender Sender(HttpClient client, TestClock clock, ProductPhotos photos) =>
        new(client, new() { BotToken = "123:test", ChatId = "-100123", MessageThreadId = 42 },
            new(new()), clock, photos);

    private sealed class ProductPhotos : ILeaderboardPhotoBuilder
    {
        public int Calls { get; private set; }
        public Action? BeforeReturn { get; init; }
        public Task<byte[]> BuildAsync(IReadOnlyList<Deal> deals, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("图文消息不应生成拼图。");
        public Task<IReadOnlyList<byte[]>> BuildProductPhotosAsync(IReadOnlyList<Deal> deals, CancellationToken cancellationToken)
        {
            Calls++;
            BeforeReturn?.Invoke();
            return Task.FromResult<IReadOnlyList<byte[]>>(deals.Select((_, i) => new byte[] { 255, 216, (byte)i, 255, 217 }).ToList());
        }
    }

    private sealed class RichHandler : HttpMessageHandler
    {
        public bool FailFirstRequest { get; init; }
        public int Calls { get; private set; }
        public string? Uri { get; private set; }
        public Dictionary<string, string> Fields { get; } = [];
        public Dictionary<string, byte[]> Files { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Uri = request.RequestUri!.AbsoluteUri;
            foreach (var part in Assert.IsType<MultipartFormDataContent>(request.Content))
            {
                var name = part.Headers.ContentDisposition!.Name!.Trim('"');
                if (part.Headers.ContentDisposition.FileName is not null)
                {
                    Assert.Equal("image/jpeg", part.Headers.ContentType!.MediaType);
                    Files[name] = await part.ReadAsByteArrayAsync(cancellationToken);
                }
                else Fields[name] = await part.ReadAsStringAsync(cancellationToken);
            }
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent(FailFirstRequest && Calls == 1 ? "{\"ok\":false,\"error_code\":500}"
                    : "{\"ok\":true,\"result\":{\"message_id\":1234}}")
            };
        }
    }
}
