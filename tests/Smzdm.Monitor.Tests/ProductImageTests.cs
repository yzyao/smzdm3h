using Smzdm.Monitor;
using Xunit;

namespace Smzdm.Monitor.Tests;

public sealed class ProductImageTests
{
    [Theory]
    [InlineData("data-original", "//qny.smzdm.com/product.jpg", "https://qny.smzdm.com/product.jpg")]
    [InlineData("data-src", "http://y.zdmimg.com/product.webp#image", "https://y.zdmimg.com/product.webp")]
    [InlineData("data-lazy-src", "https://a.zdmimg.com/product.png", "https://a.zdmimg.com/product.png")]
    [InlineData("src", "https://qny.smzdm.com/product.jpg?size=200&amp;quality=80", "https://qny.smzdm.com/product.jpg?size=200&quality=80")]
    public void ExtractsProductImagesAndNormalizesLazyAttributes(string attribute, string value, string expected)
    {
        var html = $"<ul id='feed-main-list'><li><a href='/p/1/' class='feed-block-img'><img src='data:image/gif;base64,AAAA' {attribute}='{value}'></a><h5><a href='/p/1/'>商品</a></h5></li></ul>";
        // Avoid duplicate src attributes when testing an ordinary image.
        if (attribute == "src") html = html.Replace("src='data:image/gif;base64,AAAA' ", "");
        Assert.Equal(expected, new SmzdmParser().Parse(html, 1)[0].ImageUrl);
    }

    [Theory]
    [InlineData("https://evil.test/product.jpg")]
    [InlineData("https://qny.smzdm.com.evil.test/product.jpg")]
    [InlineData("http://127.0.0.1/photo")]
    [InlineData("https://qny.smzdm.com:8443/photo")]
    [InlineData("https://user:secret@qny.smzdm.com/photo")]
    [InlineData("data:image/png;base64,AAAA")]
    public void InvalidImageDoesNotDropTheProductOrDownloadUnrelatedHosts(string value)
    {
        var html = $"<ul id='feed-main-list'><li><a href='/p/1/'><img src='{value}'></a><h5><a href='/p/1/'>商品</a></h5></li></ul>";
        var deal = Assert.Single(new SmzdmParser().Parse(html, 1));
        Assert.Equal("1", deal.Id);
        Assert.Equal("", deal.ImageUrl);
    }

    [Fact]
    public void LazyPhotoWinsOverPlaceholderAndAvatar()
    {
        const string html = "<ul id='feed-main-list'><li><img src='https://qny.smzdm.com/avatar.jpg'><a href='/p/1/'><img data-original='//qny.smzdm.com/main.jpg' src='https://qny.smzdm.com/placeholder.jpg'></a><h5><a href='/p/1/'>商品</a></h5></li></ul>";
        Assert.Equal("https://qny.smzdm.com/main.jpg", new SmzdmParser().Parse(html, 1)[0].ImageUrl);
    }
}
