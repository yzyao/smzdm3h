using System.Net;
using System.Text;
using System.Text.Json.Serialization;

namespace Smzdm.Monitor;

public sealed record CopyTextButton([property: JsonPropertyName("text")] string Text);
public sealed record ProductButton(
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("url")] string? Url = null,
    [property: JsonPropertyName("copy_text")] CopyTextButton? CopyText = null);
public sealed record ProductKeyboard(
    [property: JsonPropertyName("inline_keyboard")] List<List<ProductButton>> Rows);

public static class PhotoMessageFormatter
{
    // Photo captions permit 1024 characters after HTML parsing.
    public const int MaxLength = 1000;

    public static string Format(NotificationReport report, Snapshot snapshot, MonitorOptions options, NotificationPolicy policy)
    {
        var events = report.Events.ToDictionary(x => x.Deal.Id);
        var start = policy.LocalTime(report.PeriodStart);
        var end = policy.LocalTime(policy.PeriodEnd(report.PeriodStart));
        var updated = policy.LocalTime(snapshot.CapturedAt);
        var header = $"什么值得买 TOP {snapshot.Items.Count} · {start:HH:mm}–{end:HH:mm}\n" +
            $"更新 {updated:MM-dd HH:mm}（{policy.Options.TimeZoneId}）\n" +
            $"新进 {report.Events.Count(x => x.Entered)} · 变化 {report.Events.Count(x => x.Changed)} · " +
            $"掉榜 {report.Events.Count(x => x.Exited)} · 新增霸榜 {report.Events.Count(x => x.ReachedDominance)}\n\n";
        const string footer = "\n点击商品标题查看详情；复制按钮复制标题、价格和链接。";
        var budget = (MaxLength - header.Length - footer.Length) / snapshot.Items.Count;
        var titleLimit = Math.Max(1, budget / 2 - 2);
        var priceLimit = Math.Max(1, Math.Min(16, budget / 4));
        var markerLimit = Math.Max(0, budget - titleLimit - priceLimit - 10);
        var html = new StringBuilder(WebUtility.HtmlEncode(header));
        foreach (var item in snapshot.Items)
        {
            var deal = item.Deal;
            var markers = new List<string>();
            if (events.TryGetValue(deal.Id, out var change))
            {
                if (change.Entered) markers.Add("新");
                if (change.FromRank is int rank && rank != deal.Rank)
                    markers.Add($"{(deal.Rank < rank ? "↑" : "↓")}{Math.Abs(rank - deal.Rank)}");
            }
            if (Leaderboard.IsDominant(item, snapshot.CapturedAt, options)) markers.Add($"霸≥{options.DominanceMinutes}分");
            html.Append($"{deal.Rank}. <a href=\"{WebUtility.HtmlEncode(deal.Url)}\">{WebUtility.HtmlEncode(MessageFormatter.Short(deal.Title, titleLimit))}</a>");
            var price = string.IsNullOrWhiteSpace(deal.Price) ? "未标价" : deal.Price;
            if (events.TryGetValue(deal.Id, out var priceChange) && priceChange.PriceChanged)
                price = $"{priceChange.FromPrice}→{price}";
            html.Append(" · ").Append(WebUtility.HtmlEncode(MessageFormatter.Short(price, priceLimit)));
            if (markers.Count > 0 && markerLimit > 0)
                html.Append(' ').Append(WebUtility.HtmlEncode(MessageFormatter.Short(string.Join(" ", markers), markerLimit)));
            html.Append('\n');
        }
        html.Append(WebUtility.HtmlEncode(footer));
        if (MessageFormatter.PlainText(html.ToString()).Length > MaxLength)
            throw new InvalidDataException("图片榜单说明超过 Telegram 长度限制，请将 TopCount 设置为 1–20。");
        return html.ToString();
    }

    public static ProductKeyboard Keyboard(IReadOnlyList<Deal> deals) => new(deals.Select(deal =>
        new List<ProductButton>
        {
            new($"{deal.Rank}. {MessageFormatter.Short(deal.Title, 40)}", Url: deal.Url),
            new($"复制 {deal.Rank}", CopyText: new CopyTextButton(CopyText(deal)))
        }).ToList());

    private static string CopyText(Deal deal)
    {
        var price = MessageFormatter.Short(string.IsNullOrWhiteSpace(deal.Price) ? "未标价" : deal.Price, 48);
        var suffix = $"\n{price}\n{deal.Url}";
        if (suffix.Length >= 255) throw new InvalidDataException("商品链接过长，无法生成复制按钮。");
        return MessageFormatter.Short(deal.Title, 256 - suffix.Length) + suffix;
    }
}
