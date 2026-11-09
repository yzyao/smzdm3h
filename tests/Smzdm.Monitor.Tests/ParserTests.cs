using Smzdm.Monitor;
using Xunit;

namespace Smzdm.Monitor.Tests;

public sealed class ParserTests
{
    [Fact]
    public void TakesFirst20InSourceOrderAndIgnoresAdsDuplicatesAndSidebar()
    {
        var html = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "leaderboard.html"));
        var deals = new SmzdmParser().Parse(html, 20);
        Assert.Equal(20, deals.Count);
        Assert.Equal(Enumerable.Range(1, 20).Select(x => x.ToString()), deals.Select(x => x.Id));
        Assert.Equal(Enumerable.Range(1, 20), deals.Select(x => x.Rank));
        Assert.Equal("测试商品 & 一号", deals[0].Title);
        Assert.Equal("9.9元", deals[0].Price);
        Assert.Equal("https://www.smzdm.com/p/1/", deals[0].Url);
    }

    [Theory]
    [InlineData("<html>验证码</html>")]
    [InlineData("<ul id='feed-main-list'></ul>")]
    [InlineData("<ul id='feed-main-list'><li><h5><a href='https://evil.test/p/1/'>商品</a></h5></li></ul>")]
    [InlineData("<ul id='feed-main-list'><li><h5><a href='/p/1/'>商品</a></h5></li></ul>")]
    [InlineData("<ul id='feed-main-list'><li><a href='/p/1/'>缺少标题节点的商品</a></li></ul>")]
    public void InvalidOrPartialPageCannotBecomeEmptyLeaderboard(string html) =>
        Assert.Throws<InvalidDataException>(() => new SmzdmParser().Parse(html, 20));

    [Fact]
    public void PreservesMissingPriceAndNormalizesMobileTrackingLink()
    {
        var deals = new SmzdmParser().Parse("<ul id='feed-main-list'><li><h5><a href='//www.smzdm.com/p/123/?utm_source=test#x'> 多行\n 商品 </a></h5></li></ul>", 1);
        Assert.Equal("多行 商品", deals[0].Title);
        Assert.Equal("", deals[0].Price);
        Assert.Equal("123", deals[0].Id);
        Assert.Equal("https://www.smzdm.com/p/123/", deals[0].Url);
    }
}
