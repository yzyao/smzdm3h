using Microsoft.Extensions.Logging;
using SkiaSharp;

namespace Smzdm.Monitor;

public interface ILeaderboardPhotoBuilder
{
    Task<byte[]> BuildAsync(IReadOnlyList<Deal> deals, CancellationToken cancellationToken);
    Task<IReadOnlyList<byte[]>> BuildProductPhotosAsync(IReadOnlyList<Deal> deals, CancellationToken cancellationToken);
}

public sealed class LeaderboardPhotoBuilder(
    HttpClient client, MonitorOptions options, ILogger<LeaderboardPhotoBuilder> logger) : ILeaderboardPhotoBuilder
{
    private const int MaxImageBytes = 4 * 1024 * 1024;
    private const int ThumbnailSize = 224;

    public static HttpClient CreateHttpClient(MonitorOptions options) =>
        new(SourceProxy.CreateHandler(options, allowAutoRedirect: false)) { Timeout = TimeSpan.FromSeconds(8) };

    public Task<byte[]> BuildAsync(IReadOnlyList<Deal> deals, CancellationToken cancellationToken) =>
        WithThumbnailsAsync(deals, images => Render(deals, images), cancellationToken);

    public Task<IReadOnlyList<byte[]>> BuildProductPhotosAsync(IReadOnlyList<Deal> deals, CancellationToken cancellationToken) =>
        WithThumbnailsAsync<IReadOnlyList<byte[]>>(deals, images =>
        {
            var photos = new List<byte[]>(images.Count);
            foreach (var thumbnail in images)
            {
                using var placeholder = thumbnail is null ? CreatePlaceholder() : null;
                using var image = SKImage.FromBitmap(thumbnail ?? placeholder!);
                using var encoded = image.Encode(SKEncodedImageFormat.Jpeg, 85);
                photos.Add(encoded.ToArray());
            }
            return photos;
        }, cancellationToken);

    private async Task<T> WithThumbnailsAsync<T>(IReadOnlyList<Deal> deals,
        Func<IReadOnlyList<SKBitmap?>, T> render, CancellationToken cancellationToken)
    {
        if (deals.Count is < 1 or > 20) throw new InvalidDataException("图片榜单需要 1–20 个商品。");
        var thumbnails = new SKBitmap?[deals.Count];
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            try
            {
                await Parallel.ForEachAsync(Enumerable.Range(0, deals.Count), new ParallelOptions
                {
                    MaxDegreeOfParallelism = 4, CancellationToken = timeout.Token
                }, async (index, token) => thumbnails[index] = await DownloadAsync(deals[index].ImageUrl, token));
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning("商品图片下载超过 15 秒，使用已取得图片生成榜单。");
            }
            cancellationToken.ThrowIfCancellationRequested();
            var missing = thumbnails.Count(x => x is null);
            if (missing > 0) logger.LogWarning("{Missing}/{Total} 张商品图不可用，对应位置显示占位，商品标题和链接保留。", missing, deals.Count);
            return render(thumbnails);
        }
        finally
        {
            foreach (var image in thumbnails) image?.Dispose();
        }
    }

    private static SKBitmap CreatePlaceholder()
    {
        var bitmap = new SKBitmap(ThumbnailSize, ThumbnailSize);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(new SKColor(237, 241, 245));
        using var ink = new SKPaint { Color = new SKColor(130, 140, 150), IsAntialias = true };
        using var typeface = SKTypeface.FromFamilyName("DejaVu Sans");
        using var font = new SKFont(typeface, 18);
        canvas.DrawText("NO IMAGE", ThumbnailSize / 2f, ThumbnailSize / 2f + 6, SKTextAlign.Center, font, ink);
        return bitmap;
    }

    private async Task<SKBitmap?> DownloadAsync(string? imageUrl, CancellationToken cancellationToken)
    {
        if (!ProductImageUrl.TryNormalize(imageUrl, out var url)) return null;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd(options.UserAgent);
            request.Headers.Referrer = new Uri("https://www.smzdm.com/");
            request.Headers.Accept.ParseAdd("image/webp,image/jpeg,image/png");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode(); // Redirects are deliberately not followed to other hosts.
            if (response.Content.Headers.ContentLength > MaxImageBytes) return null;
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var content = new MemoryStream();
            var buffer = new byte[8192];
            int read;
            while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0)
            {
                if (content.Length + read > MaxImageBytes) return null;
                content.Write(buffer, 0, read);
            }
            var bytes = content.ToArray();
            var bounds = SKBitmap.DecodeBounds(bytes);
            if (bounds.Width <= 0 || bounds.Height <= 0 || (long)bounds.Width * bounds.Height > 4_000_000) return null;
            using var source = SKBitmap.Decode(bytes);
            if (source is null) return null;
            var thumbnail = new SKBitmap(ThumbnailSize, ThumbnailSize);
            using var canvas = new SKCanvas(thumbnail);
            canvas.Clear(SKColors.White);
            var scale = Math.Min((float)ThumbnailSize / source.Width, (float)ThumbnailSize / source.Height);
            var width = source.Width * scale;
            var height = source.Height * scale;
            canvas.DrawBitmap(source, new SKRect((ThumbnailSize - width) / 2, (ThumbnailSize - height) / 2,
                (ThumbnailSize + width) / 2, (ThumbnailSize + height) / 2), new SKSamplingOptions(SKFilterMode.Linear));
            return thumbnail;
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException ||
            exception is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    private static byte[] Render(IReadOnlyList<Deal> deals, IReadOnlyList<SKBitmap?> thumbnails)
    {
        const int tileWidth = 240, tileHeight = 266, gap = 12, margin = 16;
        var columns = Math.Min(4, deals.Count);
        var rows = (deals.Count + columns - 1) / columns;
        using var bitmap = new SKBitmap(columns * tileWidth + (columns - 1) * gap + margin * 2,
            rows * tileHeight + (rows - 1) * gap + margin * 2);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(new SKColor(237, 241, 245));
        using var tile = new SKPaint { Color = SKColors.White, IsAntialias = true };
        using var ink = new SKPaint { Color = new SKColor(34, 48, 62), IsAntialias = true };
        using var muted = new SKPaint { Color = new SKColor(130, 140, 150), IsAntialias = true };
        using var typeface = SKTypeface.FromFamilyName("DejaVu Sans");
        using var rankFont = new SKFont(typeface, 24);
        using var placeholderFont = new SKFont(typeface, 18);
        for (var index = 0; index < deals.Count; index++)
        {
            var x = margin + index % columns * (tileWidth + gap);
            var y = margin + index / columns * (tileHeight + gap);
            canvas.DrawRoundRect(new SKRect(x, y, x + tileWidth, y + tileHeight), 10, 10, tile);
            if (thumbnails[index] is { } image) canvas.DrawBitmap(image, x + 8, y + 8, new SKSamplingOptions(SKFilterMode.Linear));
            else canvas.DrawText("NO IMAGE", x + tileWidth / 2f, y + 120, SKTextAlign.Center, placeholderFont, muted);
            canvas.DrawText($"#{deals[index].Rank}", x + 12, y + 252, SKTextAlign.Left, rankFont, ink);
        }
        using var photo = SKImage.FromBitmap(bitmap);
        using var encoded = photo.Encode(SKEncodedImageFormat.Jpeg, 85);
        return encoded.ToArray();
    }
}
