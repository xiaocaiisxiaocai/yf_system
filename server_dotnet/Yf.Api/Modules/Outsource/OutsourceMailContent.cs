using System.Globalization;
using System.Net;
using System.Text;

namespace Yf.Api.Modules.Outsource;

internal static class OutsourceMailKinds
{
    internal const string Notice = "NOTICE";
    internal const string Withdraw = "WITHDRAW";
}

/// <summary>
/// Subject and bodies of vendor mails. The HTML uses inline styles only (Outlook and similar clients drop style
/// sheets); row colors follow the item status and changed cells are yellow, matching the list and the attachment.
/// Changed cells show only the new value, as agreed with purchasing.
/// </summary>
internal static class OutsourceMailContent
{
    private const string Cell = "border:1px solid #c9cdd4;padding:4px 8px;white-space:nowrap;";
    private const string Header = Cell + "background:#f2f3f5;font-weight:600;";
    private static readonly string[] WithdrawKeys = ["workOrderNo", "customer", "machineModel", "type", "quantity"];

    internal static string Subject(string kind, string vendorName, DateOnly businessDay, int count) =>
        kind == OutsourceMailKinds.Withdraw
            ? $"【外发撤回】{vendorName} {businessDay:yyyy-MM-dd}（共 {count} 笔）"
            : $"【外发通知】{vendorName} {businessDay:yyyy-MM-dd}（共 {count} 笔）";

    internal static string AttachmentName(string vendorName, DateOnly businessDay)
    {
        var safe = new string(vendorName.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray());
        return $"外发清单_{safe}_{businessDay.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}.xlsx";
    }

    internal static string Html(string kind, string vendorName, IReadOnlyList<OutsourceItemResponse> items,
        IReadOnlyDictionary<ulong, IReadOnlySet<string>> changed)
    {
        var withdraw = kind == OutsourceMailKinds.Withdraw;
        var keys = withdraw ? WithdrawKeys : OutsourceWorkbook.MailBodyKeys;
        var fields = keys.Select(key => OutsourceWorkbook.Fields.Single(field => field.Key == key)).ToArray();
        var anyChanged = !withdraw && items.Any(item => changed.GetValueOrDefault(item.Id)?.Count > 0);
        var html = new StringBuilder();
        html.Append("<div style=\"font-family:Arial,'Microsoft YaHei',sans-serif;font-size:14px;color:#1d2129;\">");
        html.Append("<p>").Append(E(vendorName)).Append(" 您好：</p><p>");
        html.Append(withdraw
            ? $"以下 {items.Count} 笔工令已取消外发给贵司，请停止相关作业。"
            : $"以下为本次外发清单，共 {items.Count} 笔，完整资料见附件。");
        if (anyChanged) html.Append("黄色底色为与上次通知相比有变动的内容。");
        html.Append("</p><table style=\"border-collapse:collapse;font-size:13px;\"><tr>");
        foreach (var field in fields) html.Append("<th style=\"").Append(Header).Append("\">").Append(E(field.Header)).Append("</th>");
        html.Append("</tr>");
        foreach (var item in items)
        {
            var rowColor = withdraw ? null : item.Status switch
            {
                OutsourceItemStatuses.Paused => "#ffc7ce",
                OutsourceItemStatuses.Cancelled => "#d9d9d9",
                _ => null,
            };
            var itemChanged = withdraw ? null : changed.GetValueOrDefault(item.Id);
            html.Append("<tr>");
            foreach (var field in fields)
            {
                var color = itemChanged?.Contains(field.Key) == true ? "#ffff00" : rowColor;
                html.Append("<td style=\"").Append(Cell);
                if (color is not null) html.Append("background:").Append(color).Append(';');
                html.Append("\">").Append(E(field.Value(item) ?? "")).Append("</td>");
            }
            html.Append("</tr>");
        }
        html.Append("</table><p style=\"color:#86909c;font-size:12px;\">此邮件由系统发送。</p></div>");
        return html.ToString();
    }

    internal static string Text(string kind, string vendorName, IReadOnlyList<OutsourceItemResponse> items)
    {
        var withdraw = kind == OutsourceMailKinds.Withdraw;
        var keys = withdraw ? WithdrawKeys : OutsourceWorkbook.MailBodyKeys;
        var fields = keys.Select(key => OutsourceWorkbook.Fields.Single(field => field.Key == key)).ToArray();
        var text = new StringBuilder();
        text.Append(vendorName).AppendLine(" 您好：");
        text.AppendLine(withdraw
            ? $"以下 {items.Count} 笔工令已取消外发给贵司，请停止相关作业。"
            : $"以下为本次外发清单，共 {items.Count} 笔，完整资料见附件。");
        text.AppendLine();
        text.AppendLine(string.Join(" | ", fields.Select(field => field.Header)));
        foreach (var item in items) text.AppendLine(string.Join(" | ", fields.Select(field => field.Value(item) ?? "")));
        return text.ToString();
    }

    private static string E(string value) => WebUtility.HtmlEncode(value);
}
