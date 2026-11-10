using Smzdm.Monitor;
using Xunit;

namespace Smzdm.Monitor.Tests;

public sealed class LeaderboardTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-11-09T00:00:00Z");
    private readonly MonitorOptions _options = new() { TopCount = 2, IntervalMinutes = 30 };

    internal static Deal Deal(string id, int rank = 1, string price = "10元") =>
        new(id, rank, $"商品 {id}", price, $"https://www.smzdm.com/p/{id}/");

    [Theory]
    [InlineData(10)]
    [InlineData(15)]
    public void DominanceDependsOnElapsedHourInsteadOfNumberOfScans(int interval)
    {
        var options = new MonitorOptions { IntervalMinutes = interval };
        var previous = Leaderboard.Compare(null, [Deal("1")], Start, options).Snapshot;
        for (var minute = interval; minute <= 60; minute += interval)
        {
            var current = Leaderboard.Compare(previous, [Deal("1")], Start.AddMinutes(minute), options);
            Assert.Equal(minute == 60 ? 1 : 0, current.NewlyDominant.Count);
            previous = current.Snapshot;
        }
        Assert.Empty(Leaderboard.Compare(previous, [Deal("1")], Start.AddMinutes(60 + interval), options).NewlyDominant);
    }

    [Fact]
    public void DetectsNewExitRankPriceAndTitleChanges()
    {
        var first = Leaderboard.Compare(null, [Deal("1"), Deal("2", 2)], Start, _options);
        var next = Leaderboard.Compare(first.Snapshot, [Deal("3"), Deal("1", 2, "9元") with { Title = "新标题" }], Start.AddMinutes(30), _options);
        Assert.Equal("3", Assert.Single(next.NewEntries).Deal.Id);
        Assert.Equal("2", Assert.Single(next.Exits).Deal.Id);
        var change = Assert.Single(next.Changes);
        Assert.Equal("1", change.Current.Deal.Id);
        Assert.Equal(2, change.Current.ConsecutivePolls);
        Assert.Equal(1, change.Previous!.Deal.Rank);
        var policy = new NotificationPolicy(new());
        var report = NotificationPolicy.BuildReport(next, policy.PeriodStart(next.Snapshot.CapturedAt));
        var text = MessageFormatter.PlainText(MessageFormatter.Format(report, next.Snapshot, _options, policy));
        Assert.Contains("#1→#2", text);
        Assert.Contains("10元→9元", text);
        Assert.Contains("变化 1", text);
    }

    [Fact]
    public void DominanceAlertsOnlyOnThresholdAndReentryRestartsCounter()
    {
        var first = Leaderboard.Compare(null, [Deal("1")], Start, _options);
        Assert.True(first.ShouldNotify);
        var second = Leaderboard.Compare(first.Snapshot, [Deal("1")], Start.AddMinutes(30), _options);
        Assert.False(second.ShouldNotify);
        var third = Leaderboard.Compare(second.Snapshot, [Deal("1")], Start.AddMinutes(60), _options);
        Assert.Single(third.NewlyDominant);
        Assert.Equal(Start, third.Snapshot.Items[0].FirstSeenAt);
        var fourth = Leaderboard.Compare(third.Snapshot, [Deal("1")], Start.AddMinutes(90), _options);
        Assert.False(fourth.ShouldNotify);
        var exit = Leaderboard.Compare(fourth.Snapshot, [Deal("2")], Start.AddMinutes(120), _options);
        var reentry = Leaderboard.Compare(exit.Snapshot, [Deal("1")], Start.AddMinutes(150), _options);
        Assert.Equal(1, reentry.Snapshot.Items[0].ConsecutivePolls);
        Assert.Single(reentry.NewEntries);
    }

    [Fact]
    public void CollectionGapResetsDominanceWithoutReportingFalseNewEntry()
    {
        var first = Leaderboard.Compare(null, [Deal("1")], Start, _options);
        var next = Leaderboard.Compare(first.Snapshot, [Deal("1")], Start.AddMinutes(60), _options);
        Assert.Equal(1, next.Snapshot.Items[0].ConsecutivePolls);
        Assert.Empty(next.NewEntries);
        Assert.False(next.ShouldNotify);
    }

    [Theory]
    [InlineData(20)]
    [InlineData(100)]
    public void SingleMessageFitsTelegramLimitAndContainsEveryArticle(int count)
    {
        var deals = Enumerable.Range(1, count).Select(i => Deal(i.ToString(), i) with
        {
            Title = string.Concat(Enumerable.Repeat("😀测试商品", 100)), Price = new string('价', 200)
        }).ToList();
        var options = new MonitorOptions { TopCount = count };
        var comparison = Leaderboard.Compare(null, deals, Start, options);
        var policy = new NotificationPolicy(new());
        var report = NotificationPolicy.BuildReport(comparison, policy.PeriodStart(Start));
        var text = MessageFormatter.Format(report, comparison.Snapshot, options, policy);
        Assert.True(MessageFormatter.PlainText(text).Length <= MessageFormatter.MaxLength);
        Assert.All(deals, x => Assert.Contains(x.Url, text));
        Assert.DoesNotContain('\uFFFD', text);
    }
}
