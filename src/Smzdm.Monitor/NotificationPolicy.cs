namespace Smzdm.Monitor;

public sealed class NotificationOptions
{
    public bool NotifyOnStartup { get; set; } = true;
    public int QuietStartHour { get; set; } = 1;
    public int QuietEndHour { get; set; } = 7;
    public string TimeZoneId { get; set; } = "Asia/Shanghai";

    public void Validate()
    {
        if (QuietStartHour is < 0 or > 23 || QuietEndHour is < 0 or > 23)
            throw new InvalidOperationException("静默开始和结束小时必须为 0–23；设置相同值可关闭静默。");
        _ = TimeZoneInfo.FindSystemTimeZoneById(TimeZoneId);
    }
}

public sealed class NotificationPolicy
{
    public NotificationOptions Options { get; }
    private readonly TimeZoneInfo _zone;

    public NotificationPolicy(NotificationOptions options)
    {
        options.Validate();
        Options = options;
        _zone = TimeZoneInfo.FindSystemTimeZoneById(options.TimeZoneId);
    }

    public DateTimeOffset LocalTime(DateTimeOffset now) => TimeZoneInfo.ConvertTime(now, _zone);

    public bool IsQuiet(DateTimeOffset now)
    {
        var hour = LocalTime(now).Hour;
        var start = Options.QuietStartHour;
        var end = Options.QuietEndHour;
        if (start == end) return false;
        return start < end ? hour >= start && hour < end : hour >= start || hour < end;
    }

    // A notification day starts at 07:00, independently of the quiet-hour configuration.
    public DateTimeOffset PeriodStart(DateTimeOffset now)
    {
        var local = LocalTime(now);
        var boundary = local.Date.AddHours(7);
        if (local.DateTime < boundary) boundary = boundary.AddDays(-1);
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(boundary, _zone));
    }

    public DateTimeOffset PeriodEnd(DateTimeOffset start) =>
        new(TimeZoneInfo.ConvertTimeToUtc(LocalTime(start).Date.AddDays(1).AddHours(7), _zone));

    public static NotificationReport BuildReport(Comparison comparison, DateTimeOffset period)
    {
        // Tags describe this scan, rather than accumulating all of the day's movements.
        var events = new Dictionary<string, ReportEvent>();
        ReportEvent Existing(Deal deal) => events.GetValueOrDefault(deal.Id) ?? new ReportEvent(deal);
        if (!comparison.IsInitial)
        {
            foreach (var item in comparison.NewEntries)
                events[item.Deal.Id] = Existing(item.Deal) with { Deal = item.Deal, Entered = true };
        }
        foreach (var change in comparison.Changes)
        {
            var deal = change.Current.Deal;
            var entry = Existing(deal);
            var old = change.Previous!.Deal;
            events[deal.Id] = entry with
            {
                Deal = deal, Changed = true, FromRank = entry.FromRank ?? old.Rank,
                FromPrice = entry.FromPrice ?? old.Price,
                PriceChanged = entry.PriceChanged || old.Price != deal.Price
            };
        }
        foreach (var item in comparison.Exits)
            events[item.Deal.Id] = Existing(item.Deal) with { Deal = item.Deal, Exited = true };
        foreach (var item in comparison.NewlyDominant)
            events[item.Deal.Id] = Existing(item.Deal) with { Deal = item.Deal, ReachedDominance = true };
        return new NotificationReport(period, events.Values.ToList());
    }
}
