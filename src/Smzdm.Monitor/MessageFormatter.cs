using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Smzdm.Monitor;

public static partial class MessageFormatter
{
    // Telegram counts text after HTML parsing, not hidden URLs in anchors.
    public const int MaxLength = 3500;

    public static string Format(NotificationReport report, Snapshot snapshot, MonitorOptions options, NotificationPolicy policy)
    {
        var events = report.Events.ToDictionary(x => x.Deal.Id);
        var start = policy.LocalTime(report.PeriodStart);
        var updated = policy.LocalTime(snapshot.CapturedAt);
        var end = policy.LocalTime(policy.PeriodEnd(report.PeriodStart));
        var header = $"什么值得买 TOP {options.TopCount} · {start:MM-dd HH:mm}–{end:HH:mm}\n" +
            $"更新 {updated:HH:mm}（{policy.Options.TimeZoneId}）\n" +
            $"本时段：新进 {events.Values.Count(x => x.Entered)} · 变化 {events.Values.Count(x => x.Changed)} · " +
            $"掉榜 {events.Values.Count(x => x.Exited)} · 新增霸榜 {events.Values.Count(x => x.ReachedDominance)}\n\n";
        var ids = snapshot.Items.Select(x => x.Deal.Id).ToHashSet();
        var exits = report.Events.Where(x => x.Exited && !ids.Contains(x.Deal.Id)).ToList();
        var tail = new StringBuilder();
        if (exits.Count > 0)
        {
            tail.Append("\n已掉榜：");
            tail.AppendJoin("、", exits.Take(3).Select(x => WebUtility.HtmlEncode(Short(x.Deal.Title, 18))));
            if (exits.Count > 3) tail.Append($" 等 {exits.Count} 条");
            tail.AppendLine();
        }
        tail.Append("\n新=本时段新进；霸=连续在榜次数\n");
        tail.Append($"<a href=\"{WebUtility.HtmlEncode(options.SourceUrl)}\">查看完整榜单</a>");
        var footer = tail.ToString();
        var rowBudget = (MaxLength - header.Length - PlainText(footer).Length) / snapshot.Items.Count;
        var titleLimit = Math.Max(1, Math.Min(55, rowBudget / 2));
        var priceLimit = Math.Max(1, Math.Min(25, rowBudget / 5));
        var noteLimit = Math.Max(0, rowBudget - titleLimit - priceLimit - 12);
        var html = new StringBuilder(WebUtility.HtmlEncode(header));
        foreach (var item in snapshot.Items)
        {
            var deal = item.Deal;
            var notes = new List<string>();
            if (events.TryGetValue(deal.Id, out var change))
            {
                if (change.Entered) notes.Add("新");
                if (change.FromRank is not null && change.FromRank != deal.Rank)
                    notes.Add($"#{change.FromRank}→#{deal.Rank}");
                if (change.PriceChanged) notes.Add($"价 {Short(change.FromPrice ?? "", 10)}→{Short(deal.Price, 10)}");
                else if (change.Changed && change.FromRank == deal.Rank) notes.Add("有变化");
            }
            if (Leaderboard.IsDominant(item, snapshot.CapturedAt, options)) notes.Add($"霸榜≥{options.DominanceMinutes}分钟");
            html.Append($"{deal.Rank}. <a href=\"{WebUtility.HtmlEncode(deal.Url)}\">{WebUtility.HtmlEncode(Short(deal.Title, titleLimit))}</a>\n");
            html.Append(WebUtility.HtmlEncode(Short(string.IsNullOrWhiteSpace(deal.Price) ? "未标价" : deal.Price, priceLimit)));
            if (notes.Count > 0 && noteLimit > 0)
                html.Append(" · ").Append(WebUtility.HtmlEncode(Short(string.Join(" ", notes), noteLimit)));
            html.AppendLine();
        }
        html.Append(footer);
        if (PlainText(html.ToString()).Length > MaxLength)
            throw new InvalidDataException("单条榜单超过 Telegram 长度预算，未生成待发消息。");
        return html.ToString();
    }

    public static string PlainText(string html) => WebUtility.HtmlDecode(Tags().Replace(html, ""));

    internal static string Short(string value, int length)
    {
        value = Whitespace().Replace(value, " ").Trim();
        if (value.Length <= length) return value;
        if (length <= 1) return "…";
        var end = char.IsHighSurrogate(value[length - 2]) ? length - 2 : length - 1;
        return value[..end] + "…";
    }

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex Tags();
    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
