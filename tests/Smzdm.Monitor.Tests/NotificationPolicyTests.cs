using Smzdm.Monitor;
using Xunit;

namespace Smzdm.Monitor.Tests;

public sealed class NotificationPolicyTests
{
    [Theory]
    [InlineData("2026-11-09T00:59:59+08:00", false)]
    [InlineData("2026-11-09T01:00:00+08:00", true)]
    [InlineData("2026-11-09T06:59:59+08:00", true)]
    [InlineData("2026-11-09T07:00:00+08:00", false)]
    [InlineData("2026-11-08T17:00:00Z", true)]
    [InlineData("2026-11-08T23:00:00Z", false)]
    public void QuietHoursUseBeijingTimeWithInclusiveStartAndExclusiveEnd(string time, bool expected) =>
        Assert.Equal(expected, new NotificationPolicy(new()).IsQuiet(DateTimeOffset.Parse(time)));

    [Theory]
    [InlineData("2026-11-10T06:59:59+08:00", "2026-11-09T07:00:00+08:00")]
    [InlineData("2026-11-10T07:00:00+08:00", "2026-11-10T07:00:00+08:00")]
    [InlineData("2026-11-10T23:30:00+08:00", "2026-11-10T07:00:00+08:00")]
    [InlineData("2026-11-09T23:00:00Z", "2026-11-10T07:00:00+08:00")]
    public void NotificationDayStartsAtSevenInLocalTime(string time, string expected)
    {
        var policy = new NotificationPolicy(new());
        var start = policy.PeriodStart(DateTimeOffset.Parse(time));
        Assert.Equal(DateTimeOffset.Parse(expected), start);
        Assert.Equal(TimeSpan.FromDays(1), policy.PeriodEnd(start) - start);
    }

    [Theory]
    [InlineData(22, true)]
    [InlineData(0, true)]
    [InlineData(6, true)]
    [InlineData(7, false)]
    [InlineData(21, false)]
    public void SupportsQuietWindowAcrossMidnight(int hour, bool expected)
    {
        var policy = new NotificationPolicy(new() { QuietStartHour = 22, QuietEndHour = 7 });
        Assert.Equal(expected, policy.IsQuiet(new DateTimeOffset(2026, 11, 9, hour, 0, 0, TimeSpan.FromHours(8))));
    }

    [Fact]
    public void EqualHoursDisableQuietWindow()
    {
        var policy = new NotificationPolicy(new() { QuietStartHour = 1, QuietEndHour = 1 });
        Assert.False(policy.IsQuiet(DateTimeOffset.Parse("2026-11-09T01:30:00+08:00")));
    }
}
