using Smzdm.Monitor;
using Xunit;

namespace Smzdm.Monitor.Tests;

public sealed class CollectionScheduleTests
{
    [Theory]
    [InlineData(15, "2026-11-10T10:12:00+08:00", "2026-11-10T10:15:00+08:00")]
    [InlineData(10, "2026-11-10T10:12:00+08:00", "2026-11-10T10:20:00+08:00")]
    [InlineData(45, "2026-11-10T06:59:00+08:00", "2026-11-10T07:00:00+08:00")]
    public void FasterScansStayAlignedAndAlwaysIncludeDailyBaseline(int minutes, string now, string expected) =>
        Assert.Equal(DateTimeOffset.Parse(expected), Schedule(minutes).AtOrAfter(DateTimeOffset.Parse(now)));

    [Theory]
    [InlineData("2026-11-10T10:12:00+08:00", "2026-11-10T10:30:00+08:00")]
    [InlineData("2026-11-10T10:29:59.9999999+08:00", "2026-11-10T10:30:00+08:00")]
    [InlineData("2026-11-10T10:30:00+08:00", "2026-11-10T10:30:00+08:00")]
    [InlineData("2026-11-10T10:30:00.0000001+08:00", "2026-11-10T11:00:00+08:00")]
    [InlineData("2026-11-10T23:50:00+08:00", "2026-11-11T00:00:00+08:00")]
    [InlineData("2026-11-10T02:12:00Z", "2026-11-10T02:30:00Z")]
    public void DefaultSlotsAlignToWholeAndHalfHours(string now, string expected) =>
        Assert.Equal(DateTimeOffset.Parse(expected), Schedule().AtOrAfter(DateTimeOffset.Parse(now)));

    [Theory]
    [InlineData("2026-11-10T10:00:00+08:00", "2026-11-10T10:30:00+08:00")]
    [InlineData("2026-11-10T10:30:00+08:00", "2026-11-10T11:00:00+08:00")]
    [InlineData("2026-11-10T10:30:17+08:00", "2026-11-10T11:00:00+08:00")]
    public void NextSlotIsStrictlyFutureAndDoesNotDriftAfterSlowCollection(string now, string expected) =>
        Assert.Equal(DateTimeOffset.Parse(expected), Schedule().After(DateTimeOffset.Parse(now)));

    [Theory]
    [InlineData(45, "2026-11-10T10:40:00+08:00", "2026-11-10T11:15:00+08:00")]
    [InlineData(100, "2026-11-10T23:21:00+08:00", "2026-11-11T00:00:00+08:00")]
    [InlineData(1440, "2026-11-10T10:12:00+08:00", "2026-11-11T00:00:00+08:00")]
    public void CustomIntervalsUseLocalMidnightGrid(int minutes, string now, string expected) =>
        Assert.Equal(DateTimeOffset.Parse(expected), Schedule(minutes).AtOrAfter(DateTimeOffset.Parse(now)));

    private static CollectionSchedule Schedule(int minutes = 30) =>
        new(new() { IntervalMinutes = minutes }, new(new()));
}
