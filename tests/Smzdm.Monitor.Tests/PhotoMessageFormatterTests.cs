using System.Text.Json;
using Smzdm.Monitor;
using Xunit;

namespace Smzdm.Monitor.Tests;

public sealed class PhotoMessageFormatterTests
{
    [Fact]
    public void CaptionKeepsAllTwentyClickableTitlesWithinPhotoLimit()
    {
        var options = new MonitorOptions();
        var policy = new NotificationPolicy(new());
        var deals = Enumerable.Range(1, 20).Select(i => LeaderboardTests.Deal(i.ToString(), i) with
        {
            Title = string.Concat(Enumerable.Repeat("<&😀长标题>", 100)), Price = new string('价', 200)
        }).ToList();
        var comparison = Leaderboard.Compare(null, deals, DateTimeOffset.UtcNow, options);
        var report = NotificationPolicy.BuildReport(null, comparison, policy.PeriodStart(comparison.Snapshot.CapturedAt));
        var caption = PhotoMessageFormatter.Format(report, comparison.Snapshot, options, policy);
        Assert.True(MessageFormatter.PlainText(caption).Length <= 1000);
        Assert.All(deals, deal => Assert.Contains($"href=\"{deal.Url}\"", caption));
        Assert.Contains("&lt;", caption);
        Assert.DoesNotContain("<😀", caption);
        Assert.DoesNotContain('\uFFFD', MessageFormatter.PlainText(caption));
    }

    [Fact]
    public void EveryItemHasIndependentTitleLinkAndCopyButtonWithIntactUrl()
    {
        var deal = LeaderboardTests.Deal("123") with { Title = string.Concat(Enumerable.Repeat("😀长标题", 200)), Price = new string('价', 200) };
        var keyboard = PhotoMessageFormatter.Keyboard([deal]);
        var row = Assert.Single(keyboard.Rows);
        Assert.Equal(deal.Url, row[0].Url);
        Assert.Null(row[0].CopyText);
        Assert.Null(row[1].Url);
        var text = row[1].CopyText!.Text;
        Assert.True(text.Length <= 256);
        Assert.EndsWith(deal.Url, text);
        Assert.DoesNotContain('\uFFFD', text);
        var json = JsonSerializer.Serialize(keyboard);
        Assert.Contains("inline_keyboard", json);
        Assert.Contains("copy_text", json);
    }

    [Fact]
    public void ImageModeRejectsMoreThanTwentyProductsAtConfigurationTime()
    {
        Assert.Throws<InvalidOperationException>(() => new MonitorOptions { TopCount = 21 }.Validate());
    }
}
