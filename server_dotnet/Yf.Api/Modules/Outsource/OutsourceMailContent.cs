using System.Globalization;
using System.Net;
using System.Text;

namespace Yf.Api.Modules.Outsource;

internal static class OutsourceMailKinds
{
    internal const string Notice = "NOTICE";
    internal const string Withdraw = "WITHDRAW";
    internal const string ElectricalHandoff = "ELECTRICAL_HANDOFF";
    internal const string ElectricalHandoffWithdraw = "ELECTRICAL_HANDOFF_WITHDRAW";

    internal static bool IsElectricalHandoff(string kind) => kind is ElectricalHandoff or ElectricalHandoffWithdraw;
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

    /// <summary>The body table's column keys for a notice or a withdrawal (the caller drops what vendors may not see).</summary>
    internal static IReadOnlyList<string> BodyKeys(string kind) => kind == OutsourceMailKinds.Withdraw ? WithdrawKeys : OutsourceWorkbook.MailBodyKeys;

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
        IReadOnlyDictionary<ulong, IReadOnlySet<string>> changed, IReadOnlyList<OutsourceWorkbook.Field> fields)
    {
        var withdraw = kind == OutsourceMailKinds.Withdraw;
        var anyChanged = !withdraw && items.Any(item => changed.GetValueOrDefault(item.Id)?.Count > 0);
        var introduction = PurchaseIntroduction(withdraw, items.Count);
        if (anyChanged) introduction += "黄色底色为与上次通知相比有变动的内容。";

        return TableHtml(vendorName, introduction, fields.Select(field => field.Header), items.Select(item =>
        {
            var rowColor = withdraw ? null : StatusColor(item.Status);
            var itemChanged = withdraw ? null : changed.GetValueOrDefault(item.Id);
            return fields.Select(field => (field.Value(item), itemChanged?.Contains(field.Key) == true ? "#ffff00" : rowColor));
        }));
    }

    internal static string Text(string kind, string vendorName, IReadOnlyList<OutsourceItemResponse> items, IReadOnlyList<OutsourceWorkbook.Field> fields) =>
        TableText(vendorName, PurchaseIntroduction(kind == OutsourceMailKinds.Withdraw, items.Count),
            fields.Select(field => field.Header), items.Select(item => fields.Select(field => field.Value(item))));

    private static string PurchaseIntroduction(bool withdraw, int count) => withdraw
        ? $"以下 {count} 笔工令已取消外发给贵司，请停止相关作业。"
        : $"以下为本次外发清单，共 {count} 笔，完整资料见附件。";

    private static string StatusColor(string status) => status switch
    {
        OutsourceItemStatuses.Paused => "#ffe0e0",
        OutsourceItemStatuses.Cancelled => "#fff7d1",
        _ => "#e6f4ea",
    };

    internal static (string Subject, string Html, string Text) ElectricalHandoff(
        OutsourceItemResponse item, string signer, DateTime occurredAt)
    {
        var action = item.ElectricalHandoff ? "转迅得电控" : "撤回转迅得电控";
        var subject = $"【{action}】{item.VendorName} 工令 {item.WorkOrderNo}";
        const string recipient = "迅得电控同仁";
        var introduction = item.ElectricalHandoff
            ? "以下 1 笔工令已由厂商签名转交迅得电控。"
            : "厂商已签名撤回以下 1 笔工令的电控转交。";
        var time = occurredAt.AddHours(8).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        (string Label, string? Value)[] details =
        [
            ("工令", item.WorkOrderNo),
            ("客户", item.Customer),
            ("机型", item.MachineModel),
            ("外发类型", item.TypeLabel ?? item.TypeCode),
            ("外发厂商", item.VendorName),
            ("签名人", signer),
            ("操作时间", $"{time} (UTC+8)"),
        ];
        var headers = details.Select(field => field.Label);
        var color = StatusColor(item.Status);
        return (subject,
            TableHtml(recipient, introduction, headers, [details.Select(field => (field.Value, (string?)color))]),
            TableText(recipient, introduction, headers, [details.Select(field => field.Value)]));
    }

    private static string TableHtml(string recipient, string introduction, IEnumerable<string> headers,
        IEnumerable<IEnumerable<(string? Value, string? Color)>> rows)
    {
        var html = new StringBuilder("<div style=\"font-family:Arial,'Microsoft YaHei',sans-serif;font-size:14px;color:#1d2129;\">");
        html.Append("<p>").Append(E(recipient)).Append(" 您好：</p><p>").Append(E(introduction));
        html.Append("</p><table style=\"border-collapse:collapse;font-size:13px;\"><tr>");
        foreach (var header in headers)
            html.Append("<th style=\"").Append(Header).Append("\">").Append(E(header)).Append("</th>");
        html.Append("</tr>");
        foreach (var row in rows)
        {
            html.Append("<tr>");
            foreach (var (value, color) in row)
            {
                html.Append("<td style=\"").Append(Cell);
                if (color is not null) html.Append("background:").Append(color).Append(';');
                html.Append("\">").Append(E(value ?? "")).Append("</td>");
            }
            html.Append("</tr>");
        }
        return html.Append("</table><p style=\"color:#86909c;font-size:12px;\">此邮件由系统发送。</p></div>").ToString();
    }

    private static string TableText(string recipient, string introduction, IEnumerable<string> headers,
        IEnumerable<IEnumerable<string?>> rows)
    {
        var text = new StringBuilder();
        text.Append(recipient).AppendLine(" 您好：").AppendLine(introduction).AppendLine();
        text.AppendLine(string.Join(" | ", headers));
        foreach (var row in rows) text.AppendLine(string.Join(" | ", row));
        return text.ToString();
    }

    private static string E(string value) => WebUtility.HtmlEncode(value);
}
