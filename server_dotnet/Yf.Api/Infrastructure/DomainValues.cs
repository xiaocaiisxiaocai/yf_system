namespace Yf.Api.Infrastructure;

internal static class AccountStatuses
{
    internal const string Active = "ACTIVE";
    internal const string Disabled = "DISABLED";
}

/// <summary>
/// Login account names (internal, supplier and OEM): 3–32 ASCII letters, digits, underscores or dots, where a
/// dot only separates other characters (no leading, trailing or doubled dot), e.g. <c>zhuhui.cai</c>.
/// The frontend mirror is <c>web/src/utils/accountName.ts</c>.
/// </summary>
internal static class AccountNameRule
{
    internal const string Message = "账号需为 3~32 位字母、数字、下划线或点，点不能在开头、结尾或连续出现";

    internal static bool IsValid(string value) =>
        value.Length is >= 3 and <= 32
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.')
        && value[0] != '.' && value[^1] != '.' && !value.Contains("..", StringComparison.Ordinal);
}

internal static class UserTypes
{
    internal const string Internal = "INTERNAL";
    internal const string Supplier = "SUPPLIER";
}

internal static class FileStatuses
{
    internal const string Available = "AVAILABLE";
    internal const string Deleted = "DELETED";
}

internal static class BuiltInRoleNames
{
    internal const string SystemAdministrator = "系统管理员";
}
