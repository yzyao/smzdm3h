namespace Smzdm.Monitor;

// Anchor collection slots to local midnight, rather than the process start or fetch completion.
public sealed class CollectionSchedule(MonitorOptions options, NotificationPolicy policy)
{
    public DateTimeOffset AtOrAfter(DateTimeOffset now)
    {
        var local = policy.LocalTime(now);
        var midnight = new DateTimeOffset(local.Year, local.Month, local.Day, 0, 0, 0, local.Offset);
        var interval = TimeSpan.FromMinutes(options.IntervalMinutes).Ticks;
        var elapsed = (local - midnight).Ticks;
        var slot = midnight.AddTicks((elapsed / interval + (elapsed % interval == 0 ? 0 : 1)) * interval);
        // For intervals that do not divide a day, the next day's grid starts at midnight.
        return (slot > midnight.AddDays(1) ? midnight.AddDays(1) : slot).ToUniversalTime();
    }

    public DateTimeOffset After(DateTimeOffset now) => AtOrAfter(now.AddTicks(1));
}
