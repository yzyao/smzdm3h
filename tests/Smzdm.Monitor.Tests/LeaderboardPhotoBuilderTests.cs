using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;
using Smzdm.Monitor;
using Xunit;

namespace Smzdm.Monitor.Tests;

public sealed class LeaderboardPhotoBuilderTests
{
    [Fact]
    public async Task IndependentProductPhotosKeepOrderAndUsePlaceholderForUnavailableImages()
    {
        using var client = new HttpClient(new ImageHandler());
        var builder = new LeaderboardPhotoBuilder(client, new(), NullLogger<LeaderboardPhotoBuilder>.Instance);
        var photos = await builder.BuildProductPhotosAsync([
            LeaderboardTests.Deal("1") with { ImageUrl = "https://qny.smzdm.com/red.jpg" },
            LeaderboardTests.Deal("2", 2) with { ImageUrl = "https://qny.smzdm.com/blue.jpg" },
            LeaderboardTests.Deal("3", 3) with { ImageUrl = "https://evil.test/image.jpg" }
        ], default);
        Assert.Equal(3, photos.Count);
        using var red = SKBitmap.Decode(photos[0]);
        using var blue = SKBitmap.Decode(photos[1]);
        using var missing = SKBitmap.Decode(photos[2]);
        Assert.Equal(224, red.Width);
        Assert.Equal(224, red.Height);
        Assert.True(red.GetPixel(30, 30).Red > 200);
        Assert.True(blue.GetPixel(30, 30).Blue > 200);
        Assert.InRange(missing.GetPixel(30, 30).Red, 230, 245);
        Assert.All(photos, photo => Assert.True(photo.Length < 100_000));
    }

    [Fact]
    public async Task RendersTwentyThumbnailsInRankOrderAndWithinTelegramDimensions()
    {
        using var client = new HttpClient(new ImageHandler());
        var builder = new LeaderboardPhotoBuilder(client, new(), NullLogger<LeaderboardPhotoBuilder>.Instance);
        var deals = Enumerable.Range(1, 20).Select(i => LeaderboardTests.Deal(i.ToString(), i) with
            { ImageUrl = $"https://qny.smzdm.com/{(i % 2 == 1 ? "red" : "blue")}.jpg" }).ToList();
        var result = await builder.BuildAsync(deals, default);
        using var bitmap = SKBitmap.Decode(result);
        Assert.NotNull(bitmap);
        Assert.Equal(1028, bitmap.Width);
        Assert.Equal(1410, bitmap.Height);
        Assert.True(result.Length < 10 * 1024 * 1024);
        Assert.True(bitmap.GetPixel(136, 136).Red > 200);
        Assert.True(bitmap.GetPixel(388, 136).Blue > 200);
        var output = Environment.GetEnvironmentVariable("SMZDM_TEST_PHOTO_OUTPUT");
        if (!string.IsNullOrEmpty(output)) await File.WriteAllBytesAsync(output, result);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.Redirect)]
    [InlineData(HttpStatusCode.OK)]
    public async Task MissingBlockedRedirectedOrInvalidImagesStillProduceNumberedBoard(HttpStatusCode status)
    {
        var handler = new ImageHandler { InvalidResponse = status };
        using var client = new HttpClient(handler);
        var builder = new LeaderboardPhotoBuilder(client, new(), NullLogger<LeaderboardPhotoBuilder>.Instance);
        var result = await builder.BuildAsync([
            LeaderboardTests.Deal("1") with { ImageUrl = "https://qny.smzdm.com/invalid.jpg" },
            LeaderboardTests.Deal("2", 2) with { ImageUrl = "https://evil.test/unexpected.jpg" }
        ], default);
        using var image = SKBitmap.Decode(result);
        Assert.NotNull(image);
        Assert.Equal(1, handler.Calls);
    }

    private sealed class ImageHandler : HttpMessageHandler
    {
        public HttpStatusCode? InvalidResponse { get; init; }
        private int _calls;
        public int Calls => _calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            if (InvalidResponse.HasValue) return Task.FromResult(new HttpResponseMessage(InvalidResponse.Value)
            {
                Content = new StringContent("not an image")
            });
            using var bitmap = new SKBitmap(16, 16);
            using var canvas = new SKCanvas(bitmap);
            canvas.Clear(request.RequestUri!.AbsolutePath.Contains("red") ? SKColors.Red : SKColors.Blue);
            using var image = SKImage.FromBitmap(bitmap);
            using var bytes = image.Encode(SKEncodedImageFormat.Png, 100);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes.ToArray()) });
        }
    }
}
