using Yf.Api.Infrastructure;

namespace Yf.Api.Modules.Oem.Common;

/// <summary>Writes OEM audit rows with the correct realm identity (user id for internal staff, realm+account for OEM accounts, none for system automation).</summary>
public sealed class OemAuditWriter(AuditService audit)
{
    internal Task WriteAsync(OemUnitOfWork uow, OemActor? actor, string action, string? targetType, ulong? targetId,
        object? detail, CancellationToken ct) =>
        audit.WriteAsync(uow.Connection, uow.Transaction, actor?.AuditUserId, action, targetType, targetId, detail, null, ct,
            null, actor?.AuditRealmActor);
}

/// <summary>Input validation shared by OEM endpoints. Every message is user-facing Chinese, matching the rest of the API.</summary>
internal static class OemValidation
{
    public static string RequiredText(string? value, string label, int maxRunes)
    {
        var text = value?.Trim() ?? string.Empty;
        if (text.Length == 0) throw ApiException.BadRequest($"{label}不能为空");
        if (text.EnumerateRunes().Count() > maxRunes) throw ApiException.BadRequest($"{label}不能超过 {maxRunes} 个字符");
        if (text.Any(char.IsControl)) throw ApiException.BadRequest($"{label}不能包含控制字符");
        return text;
    }

    public static string? OptionalText(string? value, string label, int maxRunes)
    {
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text)) return null;
        if (text.EnumerateRunes().Count() > maxRunes) throw ApiException.BadRequest($"{label}不能超过 {maxRunes} 个字符");
        if (text.Any(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t'))) throw ApiException.BadRequest($"{label}不能包含控制字符");
        return text;
    }

    public static string EmployeeNo(string? value)
    {
        var text = value?.Trim() ?? string.Empty;
        if (text.Length is < 3 or > 32 || text.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_'))
            throw ApiException.BadRequest("登录账号需为 3~32 位字母/数字/下划线");
        return text;
    }

    public static string Email(string? value)
    {
        var text = value?.Trim() ?? string.Empty;
        if (text.EnumerateRunes().Count() > 128 || !System.Net.Mail.MailAddress.TryCreate(text, out _))
            throw ApiException.BadRequest("邮箱格式不正确或超过 128 字符");
        return text;
    }

    public static string? OptionalEmail(string? value) => string.IsNullOrWhiteSpace(value) ? null : Email(value);

    public static ulong ExpectedVersion(ulong? version) =>
        version ?? throw ApiException.BadRequest("缺少数据版本号，请刷新后重试");

    public static void MatchVersion(ulong actual, ulong expected)
    {
        if (actual != expected) throw ApiException.Conflict("数据已被他人修改，请刷新后重试");
    }
}
