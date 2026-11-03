using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Yf.Api.Modules.SystemManagement;

/// <summary>
/// Renders the HTML alternative of a notification from its plain-text body at send time, so every producer keeps
/// writing plain text and the outbox stores only that. The text layout it understands is the one the notification
/// services already write: <c>标签：值</c> detail lines, <c>- 项目</c> list entries with indented URL/detail lines,
/// <c>请登录平台查看：URL</c> action lines and a trailing "此邮件/本邮件…" footer. Anything else becomes a paragraph.
/// All text is HTML-encoded and only http(s) URLs become links. Layout uses tables and inline styles for Outlook.
/// </summary>
internal static partial class MailHtml
{
    private const string Primary = "#165DFF";

    internal static string Render(string subject, string body)
    {
        var (brand, title) = SplitSubject(subject);
        var content = new StringBuilder();
        var footer = new List<string>();
        var details = new List<(string Label, string Value)>();
        ListItem? item = null;
        var items = new List<ListItem>();
        var actions = new List<(string Label, string Url)>();

        void FlushDetails()
        {
            if (details.Count == 0) return;
            content.Append("<table role=\"presentation\" cellpadding=\"0\" cellspacing=\"0\" width=\"100%\" style=\"margin:0 0 16px;border-collapse:collapse;background:#f7f8fa;border-radius:6px\">");
            foreach (var (label, value) in details)
                content.Append("<tr><td style=\"padding:8px 12px;width:96px;color:#86909c;font-size:13px;vertical-align:top;white-space:nowrap\">")
                    .Append(Encode(label)).Append("</td><td style=\"padding:8px 12px 8px 0;color:#1d2129;font-size:14px;word-break:break-all\">")
                    .Append(Encode(value)).Append("</td></tr>");
            content.Append("</table>");
            details.Clear();
        }

        void FlushItems()
        {
            if (item is not null) { items.Add(item); item = null; }
            if (items.Count == 0) return;
            content.Append("<table role=\"presentation\" cellpadding=\"0\" cellspacing=\"0\" width=\"100%\" style=\"margin:0 0 16px;border-collapse:separate;border-spacing:0 8px\">");
            foreach (var entry in items)
            {
                content.Append("<tr><td style=\"padding:10px 12px;border:1px solid #e5e6eb;border-radius:6px\">")
                    .Append("<div style=\"color:#1d2129;font-size:14px;font-weight:600;word-break:break-all\">").Append(Encode(entry.Title)).Append("</div>");
                foreach (var (label, value) in entry.Details)
                    content.Append("<div style=\"margin-top:4px;color:#4e5969;font-size:13px\">").Append(Encode(label)).Append("：").Append(Encode(value)).Append("</div>");
                if (entry.Links.Count > 0)
                {
                    content.Append("<div style=\"margin-top:6px\">");
                    for (var index = 0; index < entry.Links.Count; index++)
                    {
                        if (index > 0) content.Append("<span style=\"color:#c9cdd4\">&nbsp;&nbsp;|&nbsp;&nbsp;</span>");
                        content.Append("<a href=\"").Append(Encode(entry.Links[index].Url)).Append("\" style=\"color:").Append(Primary)
                            .Append(";font-size:13px;text-decoration:none\">").Append(Encode(entry.Links[index].Label)).Append(" →</a>");
                    }
                    content.Append("</div>");
                }
                content.Append("</td></tr>");
            }
            content.Append("</table>");
            items.Clear();
        }

        void FlushActions()
        {
            if (actions.Count == 0) return;
            content.Append("<table role=\"presentation\" cellpadding=\"0\" cellspacing=\"0\" style=\"margin:4px 0 16px\"><tr>");
            for (var index = 0; index < actions.Count; index++)
            {
                var (label, url) = actions[index];
                var filled = index == 0;
                if (index > 0) content.Append("<td style=\"width:8px\"></td>");
                content.Append("<td style=\"border-radius:6px;border:1px solid ").Append(Primary).Append(";background:").Append(filled ? Primary : "#ffffff")
                    .Append("\"><a href=\"").Append(Encode(url))
                    .Append("\" style=\"display:inline-block;padding:9px 18px;color:").Append(filled ? "#ffffff" : Primary)
                    .Append(";font-size:14px;font-weight:600;text-decoration:none\">").Append(Encode(label)).Append("</a></td>");
            }
            content.Append("</tr></table>");
            actions.Clear();
        }

        void FlushAll() { FlushItems(); FlushDetails(); FlushActions(); }

        foreach (var raw in body.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (item is not null && raw.StartsWith("  ", StringComparison.Ordinal) && line.Trim().Length > 0)
            {
                var inner = line.Trim();
                if (IsHttpUrl(inner)) item.Links.Add(("查看", inner));
                else if (DetailLine().Match(inner) is { Success: true } innerLink && IsHttpUrl(innerLink.Groups[2].Value))
                    item.Links.Add((innerLink.Groups[1].Value, innerLink.Groups[2].Value));
                else if (DetailLine().Match(inner) is { Success: true } innerDetail) item.Details.Add((innerDetail.Groups[1].Value, innerDetail.Groups[2].Value));
                else item.Details.Add(("", inner));
                continue;
            }
            if (line.Length == 0) { FlushAll(); continue; }
            if (line.StartsWith("- ", StringComparison.Ordinal))
            {
                FlushDetails();
                if (item is not null) items.Add(item);
                item = new ListItem(line[2..].Trim());
                continue;
            }
            FlushItems();
            var trimmed = line.Trim();
            if (ActionLine().Match(trimmed) is { Success: true } action && IsHttpUrl(action.Groups[2].Value))
            {
                FlushDetails();
                var label = action.Groups[1].Value.Trim();
                if (label.StartsWith('请')) label = label[1..];
                actions.Add((label.Length > 0 ? label : "登录平台查看", action.Groups[2].Value));
                continue;
            }
            FlushActions();
            if (IsFooter(trimmed)) { FlushDetails(); footer.Add(trimmed.Trim('（', '）', '(', ')')); continue; }
            if (!trimmed.EndsWith('：') && DetailLine().Match(trimmed) is { Success: true } detail)
            {
                details.Add((detail.Groups[1].Value, detail.Groups[2].Value));
                continue;
            }
            FlushDetails();
            content.Append("<p style=\"margin:0 0 12px;color:#1d2129;font-size:14px;line-height:22px\">").Append(Encode(trimmed)).Append("</p>");
        }
        FlushAll();

        var html = new StringBuilder();
        html.Append("<!DOCTYPE html><html lang=\"zh-CN\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><title>")
            .Append(Encode(title)).Append("</title></head>")
            .Append("<body style=\"margin:0;padding:0;background:#f2f3f5;font-family:'Microsoft YaHei','PingFang SC',Arial,sans-serif\">")
            .Append("<table role=\"presentation\" cellpadding=\"0\" cellspacing=\"0\" width=\"100%\" style=\"background:#f2f3f5\"><tr><td align=\"center\" style=\"padding:24px 12px\">")
            .Append("<table role=\"presentation\" cellpadding=\"0\" cellspacing=\"0\" width=\"100%\" style=\"max-width:600px;background:#ffffff;border:1px solid #e5e6eb;border-radius:8px;overflow:hidden\">")
            .Append("<tr><td style=\"padding:14px 24px;background:").Append(Primary).Append(";color:#ffffff;font-size:14px;font-weight:600;letter-spacing:1px\">SAA · ")
            .Append(Encode(brand)).Append("</td></tr>")
            .Append("<tr><td style=\"padding:24px 24px 8px\"><h1 style=\"margin:0 0 16px;color:#1d2129;font-size:18px;line-height:26px;font-weight:600\">")
            .Append(Encode(title)).Append("</h1>").Append(content).Append("</td></tr>");
        if (footer.Count == 0) footer.Add("此邮件由系统自动发送，请勿直接回复。");
        html.Append("<tr><td style=\"padding:12px 24px 20px;border-top:1px solid #f2f3f5;color:#86909c;font-size:12px;line-height:18px\">")
                .Append(string.Join("<br>", footer.Select(Encode))).Append("</td></tr>");
        html.Append("</table></td></tr></table></body></html>");
        return html.ToString();
    }

    private static (string Brand, string Title) SplitSubject(string subject)
    {
        var match = SubjectPrefix().Match(subject);
        if (!match.Success) return ("供应商协作平台", subject);
        var tag = match.Groups[1].Value;
        var brand = tag.Contains("OEM", StringComparison.OrdinalIgnoreCase) ? "ODM/OEM 文件传递" : "供应商协作平台";
        return (brand, match.Groups[2].Value.Trim());
    }

    private static bool IsFooter(string line) =>
        line.StartsWith("此邮件", StringComparison.Ordinal) || line.StartsWith("本邮件", StringComparison.Ordinal)
        || line.StartsWith("（本邮件", StringComparison.Ordinal);

    private static bool IsHttpUrl(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    private static string Encode(string value) => WebUtility.HtmlEncode(value);

    [GeneratedRegex(@"^\[([^\]]+)\]\s*(.*)$")]
    private static partial Regex SubjectPrefix();

    [GeneratedRegex(@"^([^：:\s][^：:]{0,9})：(.+)$")]
    private static partial Regex DetailLine();

    [GeneratedRegex(@"^(.*?)[：:]\s*(\S+)$")]
    private static partial Regex ActionLine();

    private sealed class ListItem(string title)
    {
        public string Title { get; } = title;
        public List<(string Label, string Url)> Links { get; } = [];
        public List<(string Label, string Value)> Details { get; } = [];
    }
}
