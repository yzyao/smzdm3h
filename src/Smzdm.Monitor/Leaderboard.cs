namespace Smzdm.Monitor;

public static class Leaderboard
{
    public static Comparison Compare(Snapshot? previous, IReadOnlyList<Deal> deals,
        DateTimeOffset now, MonitorOptions options)
    {
        var old = previous?.Items.ToDictionary(x => x.Deal.Id) ?? [];
        // A long collection gap is not evidence that an item stayed on the board.
        var continuous = previous is not null && now >= previous.CapturedAt &&
            now - previous.CapturedAt <= TimeSpan.FromMinutes(options.IntervalMinutes * 1.5);
        var current = deals.Select(deal => old.TryGetValue(deal.Id, out var entry) && continuous
            ? new RankedDeal(deal, Math.Min(entry.ConsecutivePolls, int.MaxValue - 1) + 1, entry.FirstSeenAt)
            : new RankedDeal(deal, 1, now)).ToList();
        var ids = deals.Select(x => x.Id).ToHashSet();
        return new Comparison(
            new Snapshot(now, current), previous is null,
            current.Where(x => !old.ContainsKey(x.Deal.Id)).ToList(),
            old.Values.Where(x => !ids.Contains(x.Deal.Id)).OrderBy(x => x.Deal.Rank).ToList(),
            current.Where(x => old.TryGetValue(x.Deal.Id, out var entry) &&
                (x.Deal.Rank != entry.Deal.Rank || x.Deal.Price != entry.Deal.Price || x.Deal.Title != entry.Deal.Title))
                .Select(x => new DealChange(x, old[x.Deal.Id])).ToList(),
            current.Where(x => x.ConsecutivePolls == options.DominancePolls).ToList());
    }
}
