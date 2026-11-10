namespace Smzdm.Monitor;

public sealed record Deal(string Id, int Rank, string Title, string Price, string Url, string ImageUrl = "");
public sealed record RankedDeal(Deal Deal, int ConsecutivePolls, DateTimeOffset FirstSeenAt);
public sealed record Snapshot(DateTimeOffset CapturedAt, List<RankedDeal> Items);
public sealed record PendingNotification(string Text, DateTimeOffset PeriodStart, DateTimeOffset CapturedAt,
    List<Deal>? Items = null, bool IsRich = false, bool SendNew = false, List<string>? NewItemIds = null);
public sealed record PublishedMessage(long MessageId, DateTimeOffset PeriodStart, string Text,
    string? PayloadKey = null, bool IsRich = false);
public sealed record ReportEvent(Deal Deal, bool Entered = false, bool Changed = false, bool Exited = false,
    bool ReachedDominance = false, int? FromRank = null, string? FromPrice = null, bool PriceChanged = false,
    bool Returned = false);
public sealed record NotificationReport(DateTimeOffset PeriodStart, List<ReportEvent> Events, bool IsBaseline = false);

public sealed class MonitorState
{
    public int Version { get; set; } = 3;
    public string SourceKey { get; set; } = "";
    public Snapshot? Snapshot { get; set; }
    public string DestinationKey { get; set; } = "";
    public NotificationReport? Report { get; set; }
    public PendingNotification? Pending { get; set; }
    public PublishedMessage? Published { get; set; }
    public DateTimeOffset? NotificationDay { get; set; }
    public HashSet<string> SeenIds { get; set; } = [];
    public HashSet<string> NotifiedIds { get; set; } = [];
}

public sealed record DealChange(RankedDeal Current, RankedDeal? Previous);
public sealed record Comparison(
    Snapshot Snapshot,
    bool IsInitial,
    List<RankedDeal> NewEntries,
    List<RankedDeal> Exits,
    List<DealChange> Changes,
    List<RankedDeal> NewlyDominant)
{
    public bool ShouldNotify => IsInitial || NewEntries.Count > 0 || Exits.Count > 0 ||
                                Changes.Count > 0 || NewlyDominant.Count > 0;
}
