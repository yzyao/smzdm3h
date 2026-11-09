using System.Net;
using System.Text.RegularExpressions;
using HtmlAgilityPack;

namespace Smzdm.Monitor;

public sealed partial class SmzdmParser
{
    // Deliberately scoped to the main feed: sidebar recommendations must never become ranks.
    public List<Deal> Parse(string html, int topCount)
    {
        var document = new HtmlDocument();
        document.LoadHtml(html);
        var feed = document.GetElementbyId("feed-main-list")
            ?? throw new InvalidDataException("未找到 feed-main-list，可能遇到验证码、反爬或页面结构变化。");
        var rows = feed.SelectNodes("./li")
            ?? throw new InvalidDataException("榜单主列表没有商品。");
        var results = new List<Deal>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            if (row.GetAttributeValue("data-type", "").Equals("ad", StringComparison.OrdinalIgnoreCase) ||
                row.SelectSingleNode(".//*[contains(concat(' ',normalize-space(@class),' '),' feed-ad ')]") is not null)
                continue;

            var heading = row.SelectSingleNode(".//h5//a[@href] | .//h2//a[@href] | .//h3//a[@href]");
            if (heading is null)
            {
                if (row.SelectNodes(".//a[@href]")?.Any(x => TryIdentity(x.GetAttributeValue("href", ""), out _, out _)) == true)
                    throw new InvalidDataException("榜单存在无法识别标题的商品，停止本轮以避免错误排名。");
                continue; // Non-product slots such as ad placeholders.
            }
            var link = heading.GetAttributeValue("href", "");
            if (!TryIdentity(link, out var id, out var canonicalUrl))
                throw new InvalidDataException("榜单商品链接无法识别，停止本轮以避免错误掉榜。");
            var title = Clean(heading.InnerText);
            if (title.Length == 0)
                throw new InvalidDataException("榜单商品标题为空，停止本轮。");
            if (!ids.Add(id)) continue; // The same article can have multiple feed slots.

            var price = row.SelectSingleNode(".//*[contains(concat(' ',normalize-space(@class),' '),' z-highlight ')]")
                ?? row.SelectSingleNode(".//*[contains(concat(' ',normalize-space(@class),' '),' feed-block-price ')]")
                ?? row.SelectSingleNode(".//*[contains(concat(' ',normalize-space(@class),' '),' feed-price ')]");
            var image = row.SelectSingleNode(".//*[contains(concat(' ',normalize-space(@class),' '),' feed-block-img ')]//img")
                ?? row.SelectSingleNode(".//a[contains(@href,'/p/')]//img");
            var imageUrl = "";
            if (image is not null)
            {
                foreach (var attribute in new[] { "data-original", "data-src", "data-lazy-src", "src" })
                {
                    if (ProductImageUrl.TryNormalize(image.GetAttributeValue(attribute, ""), out imageUrl)) break;
                }
            }
            results.Add(new Deal(id, results.Count + 1, title, Clean(price?.InnerText ?? ""), canonicalUrl, imageUrl));
            if (results.Count == topCount) break;
        }
        if (results.Count < topCount)
            throw new InvalidDataException($"只解析到 {results.Count} 条有效商品，要求 {topCount} 条；本轮不更新榜单。");
        return results;
    }

    private static bool TryIdentity(string href, out string id, out string url)
    {
        id = url = "";
        if (!Uri.TryCreate(new Uri("https://faxian.smzdm.com/"), WebUtility.HtmlDecode(href), out var uri) ||
            (uri.Scheme != "http" && uri.Scheme != "https") ||
            uri.Host is not ("www.smzdm.com" or "faxian.smzdm.com")) return false;
        var match = ArticlePath().Match(uri.AbsolutePath);
        if (!match.Success) return false;
        id = match.Groups[1].Value;
        url = $"https://www.smzdm.com/p/{id}/";
        return true;
    }

    private static string Clean(string text) => Whitespace().Replace(WebUtility.HtmlDecode(text), " ").Trim();

    [GeneratedRegex(@"^/p/(\d+)/?$")]
    private static partial Regex ArticlePath();
    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
