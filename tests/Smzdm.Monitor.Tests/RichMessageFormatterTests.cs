using System.Net;
using HtmlAgilityPack;
using Smzdm.Monitor;
using Xunit;

namespace Smzdm.Monitor.Tests;

public sealed class RichMessageFormatterTests
{
    [Fact]
    public void ReturneeUsesReturnTagInsteadOfNewEntryTag()
    {
        var deal = LeaderboardTests.Deal("1");
        var caption = Parse(Format([deal], [new(deal, Entered: true, Returned: true)]))
            .DocumentNode.SelectSingleNode("/figure/figcaption");
        Assert.Equal("返榜", Assert.Single(caption.SelectNodes("./code")).InnerText);
    }

    [Fact]
    public void TwentyProductsKeepTheirOwnPhotoCopyableTitlePriceAndDetailLinkInOneColumn()
    {
        var deals = Enumerable.Range(1, 20).Select(i => LeaderboardTests.Deal(i.ToString(), i) with
        {
            Title = $"商品 {i} <&😀> " + new string('长', 80), Price = $"{i}.99元"
        }).ToList();
        var html = Format(deals);
        var document = Parse(html);
        var cards = document.DocumentNode.SelectNodes("/figure");
        Assert.Equal(20, cards.Count);
        for (var i = 0; i < deals.Count; i++)
        {
            var card = cards[i];
            Assert.Equal($"tg://photo?id=product_{i}", card.SelectSingleNode("./img").GetAttributeValue("src", ""));
            var caption = card.SelectSingleNode("./figcaption");
            var title = caption.SelectSingleNode("./tg-button");
            Assert.Equal("copy_text", title.GetAttributeValue("type", ""));
            Assert.Equal(deals[i].Title, WebUtility.HtmlDecode(title.InnerText));
            Assert.Equal(deals[i].Title, WebUtility.HtmlDecode(title.GetAttributeValue("text", "")));
            Assert.Equal(deals[i].Url, caption.SelectSingleNode("./a").GetAttributeValue("href", ""));
            Assert.Contains(deals[i].Price, caption.InnerText);
        }
        Assert.Null(document.DocumentNode.SelectSingleNode("//tg-button-row | //tg-collage | //tg-slideshow | //table"));
        Assert.True(RichMessageFormatter.ValidLength(html));
        Assert.True(MessageFormatter.PlainText(html).Length > PhotoMessageFormatter.MaxLength);
    }

    [Fact]
    public void AllChangeTagsStayWithTheirProductAndCurrentPriceIsNotLostBehindOldPrice()
    {
        var deal = LeaderboardTests.Deal("1") with { Price = "999.99元" };
        var html = Format([deal], [new(deal, Entered: true, Changed: true, FromRank: 5,
            FromPrice: new string('价', 200), PriceChanged: true)], polls: 12);
        var card = Parse(html).DocumentNode.SelectSingleNode("/figure/figcaption");
        Assert.Equal(new[] { "新进", "↑4", "变价", "霸榜≥60分钟" }, card.SelectNodes("./code").Select(x => x.InnerText));
        Assert.Contains("999.99元", card.SelectNodes("./b").Last().InnerText);
        Assert.DoesNotContain(new string('价', 100), card.InnerText);
    }

    [Theory]
    [InlineData(1, 3, false, "↓2")]
    [InlineData(3, 3, false, "更新")]
    [InlineData(3, 3, true, "变价")]
    public void RankTitleAndPriceChangesHaveDistinctTags(int fromRank, int rank, bool priceChanged, string expected)
    {
        var deal = LeaderboardTests.Deal("1", rank);
        var html = Format([deal], [new(deal, Changed: true, FromRank: fromRank, PriceChanged: priceChanged)]);
        var tag = Assert.Single(Parse(html).DocumentNode.SelectNodes("/figure/figcaption/code"));
        Assert.Equal(expected, tag.InnerText);
    }

    [Fact]
    public void InitialBoardDoesNotLabelEveryProductAsNewAndBlankPriceIsExplicit()
    {
        var html = Format([LeaderboardTests.Deal("1") with { Price = "" }]);
        var document = Parse(html);
        Assert.Null(document.DocumentNode.SelectSingleNode("//code"));
        Assert.Contains("未标价", document.DocumentNode.InnerText);
    }

    [Fact]
    public void LongUnicodeAndMaliciousTitlesRemainSafeAndCopyPayloadFitsTelegram()
    {
        var title = "\"><script>bad()</script><&😀" + string.Concat(Enumerable.Repeat("😀长标题", 200));
        var deals = Enumerable.Range(1, 20).Select(i => LeaderboardTests.Deal(i.ToString(), i) with
        {
            Title = title, Price = new string('价', 200)
        }).ToList();
        var html = Format(deals, deals.Select(x => new ReportEvent(x, Changed: true,
            PriceChanged: true, FromPrice: new string('旧', 200))).ToList(), polls: 10000);
        var document = Parse(html);
        Assert.Null(document.DocumentNode.SelectSingleNode("//script"));
        foreach (var button in document.DocumentNode.SelectNodes("//tg-button"))
        {
            var copy = WebUtility.HtmlDecode(button.GetAttributeValue("text", ""));
            Assert.InRange(copy.Length, 1, 256);
            Assert.Equal(WebUtility.HtmlDecode(button.InnerText), copy);
            Assert.DoesNotContain('\uFFFD', copy);
            Assert.False(char.IsHighSurrogate(copy[^1]));
        }
        Assert.True(RichMessageFormatter.ValidLength(html));
    }

    [Fact]
    public void ExitedProductsAppearOnlyInFooterAndDoNotGetClickableCards()
    {
        var current = LeaderboardTests.Deal("1");
        var exited = LeaderboardTests.Deal("2") with { Title = "已离榜商品" };
        var document = Parse(Format([current], [new(exited, Exited: true)]));
        Assert.Single(document.DocumentNode.SelectNodes("/figure"));
        Assert.Contains("已掉榜：已离榜商品", document.DocumentNode.InnerText);
    }

    private static string Format(List<Deal> deals, List<ReportEvent>? events = null, int polls = 1)
    {
        var now = new TestClock().GetUtcNow();
        var policy = new NotificationPolicy(new());
        return RichMessageFormatter.Format(new(policy.PeriodStart(now), events ?? []),
            new(now, deals.Select(x => new RankedDeal(x, polls, now.AddMinutes(-(polls - 1) * 15))).ToList()), new(), policy);
    }

    private static HtmlDocument Parse(string html)
    {
        var document = new HtmlDocument();
        document.LoadHtml(html);
        return document;
    }
}
