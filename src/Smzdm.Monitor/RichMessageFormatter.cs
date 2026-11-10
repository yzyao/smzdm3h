using System.Net;
using System.Text;

namespace Smzdm.Monitor;

// Native Rich HTML, not the limited HTML parse mode used by sendMessage/sendPhoto.
// https://core.telegram.org/bots/api#rich-html-style
public static class RichMessageFormatter
{
    // Conservative UTF-8 budget for the rendered text; media and copy payloads are separate.
    public const int MaxTextBytes = 32000;

    public static string PhotoId(int index) => $"product_{index}";

    public static string Format(NotificationReport report, Snapshot snapshot, MonitorOptions options, NotificationPolicy policy)
    {
        if (snapshot.Items.Count is < 1 or > 20) throw new InvalidDataException("图文榜单需要 1–20 个商品。");
        var events = report.Events.ToDictionary(x => x.Deal.Id);
        var start = policy.LocalTime(report.PeriodStart);
        var updated = policy.LocalTime(snapshot.CapturedAt);
        var html = new StringBuilder($"<h3>什么值得买 TOP {snapshot.Items.Count}{(report.IsBaseline ? " · 基础榜单" : "")}</h3>\n");
        html.Append($"<p>{start:MM-dd HH:mm} 起 · 更新 {updated:MM-dd HH:mm}</p>\n");
        html.Append($"<p>新进 {report.Events.Count(x => x.Entered && !x.Returned)} · 返榜 {report.Events.Count(x => x.Returned)} · 变化 {report.Events.Count(x => x.Changed)} · 掉榜 {report.Events.Count(x => x.Exited)}</p>\n");

        for (var index = 0; index < snapshot.Items.Count; index++)
        {
            var item = snapshot.Items[index];
            var deal = item.Deal;
            events.TryGetValue(deal.Id, out var change);
            // The title itself is the copy control, rather than a second keyboard below the list.
            var title = MessageFormatter.Short(deal.Title, 256);
            html.Append($"<figure><img src=\"tg://photo?id={PhotoId(index)}\"/><figcaption>");
            html.Append($"<b>#{deal.Rank}</b> <tg-button type=\"copy_text\" text=\"{Encode(title)}\">{Encode(title)}</tg-button><br/>\n");
            if (change?.PriceChanged == true && change.FromPrice != deal.Price)
                html.Append(Encode(Price(change.FromPrice, 64))).Append('→');
            html.Append("<b>").Append(Encode(Price(deal.Price, 96))).Append("</b>");
            foreach (var tag in Tags(item, change, snapshot.CapturedAt, options))
                html.Append(" <code>").Append(Encode(tag)).Append("</code>");
            html.Append($" · <a href=\"{Encode(deal.Url)}\">详情</a></figcaption></figure>\n");
            if (index + 1 < snapshot.Items.Count) html.AppendLine("<hr/>");
        }

        var currentIds = snapshot.Items.Select(x => x.Deal.Id).ToHashSet();
        var exits = report.Events.Where(x => x.Exited && !currentIds.Contains(x.Deal.Id)).ToList();
        if (exits.Count > 0)
        {
            html.Append("<footer>已掉榜：");
            html.AppendJoin("、", exits.Take(3).Select(x => Encode(MessageFormatter.Short(x.Deal.Title, 24))));
            if (exits.Count > 3) html.Append($" 等 {exits.Count} 条");
            html.AppendLine("</footer>");
        }
        html.Append($"<footer>点标题复制 · 点详情查看商品 · <a href=\"{Encode(options.SourceUrl)}\">完整榜单</a></footer>");
        var result = html.ToString();
        if (!ValidLength(result)) throw new InvalidDataException("图文榜单超过 Telegram 长度限制。");
        return result;
    }

    public static bool ValidLength(string text) => !string.IsNullOrWhiteSpace(text) &&
        Encoding.UTF8.GetByteCount(MessageFormatter.PlainText(text)) <= MaxTextBytes;

    private static IEnumerable<string> Tags(RankedDeal item, ReportEvent? change, DateTimeOffset now, MonitorOptions options)
    {
        if (change?.Entered == true) yield return change.Returned ? "返榜" : "新进";
        var moved = change?.FromRank is int rank && rank != item.Deal.Rank;
        if (moved)
        {
            var delta = change!.FromRank!.Value - item.Deal.Rank;
            yield return $"{(delta > 0 ? "↑" : "↓")}{Math.Abs(delta)}";
        }
        if (change?.PriceChanged == true) yield return "变价";
        if (change?.Changed == true && !moved && !change.PriceChanged) yield return "更新";
        if (Leaderboard.IsDominant(item, now, options)) yield return $"霸榜≥{options.DominanceMinutes}分钟";
    }

    private static string Price(string? price, int limit) =>
        MessageFormatter.Short(string.IsNullOrWhiteSpace(price) ? "未标价" : price, limit);

    private static string Encode(string value) => WebUtility.HtmlEncode(value);
}
