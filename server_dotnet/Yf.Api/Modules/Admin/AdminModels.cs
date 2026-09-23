using System.Text.Json;
using System.Text.Json.Serialization;

namespace Yf.Api.Modules.Admin;

public sealed record StatusRequest([property: JsonRequired] string Status);
public sealed record PasswordRequest([property: JsonRequired] string NewPassword);
public sealed record DepartmentUpsert([property: JsonRequired] string Name, ulong? ParentId, int? SortNo);
public sealed record RoleUpsert([property: JsonRequired] string Name, string? Description);
public sealed record PermissionAssign([property: JsonRequired] IReadOnlyList<ulong> PermissionIds);
public sealed record RoleAssign([property: JsonRequired] IReadOnlyList<ulong> RoleIds);
public sealed record UserCreate(
    [property: JsonRequired] string EmployeeNo,
    [property: JsonRequired] string Password,
    [property: JsonRequired] string RealName,
    [property: JsonRequired] string Email,
    ulong? DepartmentId,
    ulong? RoleId,
    IReadOnlyList<ulong>? RoleIds);
public sealed record UserUpdate(
    string? RealName = null,
    string? Email = null,
    JsonElement DepartmentId = default,
    ulong? RoleId = null,
    IReadOnlyList<ulong>? RoleIds = null);
public sealed record SupplierUpsert([property: JsonRequired] string Name, string? Remark);
public sealed record SupplierAccountCreate(
    [property: JsonRequired] string EmployeeNo,
    [property: JsonRequired] string Password,
    [property: JsonRequired] string RealName,
    [property: JsonRequired] string Email,
    ulong? RoleId = null);
public sealed record SupplierAccountUpdate(string? RealName, string? Email);

internal sealed class AdminUserRow
{
    public ulong Id { get; init; }
    public string EmployeeNo { get; init; } = "";
    public string PasswordHash { get; init; } = "";
    public string RealName { get; init; } = "";
    public string Email { get; init; } = "";
    public string UserType { get; init; } = "";
    public ulong? SupplierId { get; init; }
    public ulong? DepartmentId { get; init; }
    public string Status { get; init; } = "";
    public bool MustChangePassword { get; init; }
    public DateTime? LastLoginAt { get; init; }
    public DateTime CreatedAt { get; init; }
    public ulong? RoleId { get; init; }
    public string? RoleName { get; init; }
}

internal sealed class RoleRow { public ulong Id { get; init; } public string Name { get; init; } = ""; public string? Description { get; init; } public bool IsBuiltIn { get; init; } public string Status { get; init; } = ""; public DateTime CreatedAt { get; init; } }
internal sealed class DeptRow { public ulong Id { get; init; } public string Name { get; init; } = ""; public ulong? ParentId { get; init; } public string Kind { get; init; } = ""; public int SortNo { get; init; } public string Status { get; init; } = ""; }
internal sealed class SupplierRow { public ulong Id { get; init; } public string Name { get; init; } = ""; public string? Remark { get; init; } public string Status { get; init; } = ""; public DateTime CreatedAt { get; init; } }

internal static class AdminValidation
{
    public static string Status(string? value) => value is "ACTIVE" or "DISABLED" ? value : throw Yf.Api.Infrastructure.ApiException.BadRequest("非法状态");
    public static void EmployeeNo(string? value)
    {
        var v = value?.Trim() ?? "";
        if (v.Length is < 3 or > 32 || v.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_')) throw Yf.Api.Infrastructure.ApiException.BadRequest("工号需为 3~32 位字母/数字/下划线");
    }
    public static void Email(string? value)
    {
        var v = value?.Trim() ?? "";
        if (v.EnumerateRunes().Count() > 128 || !System.Net.Mail.MailAddress.TryCreate(v, out _)) throw Yf.Api.Infrastructure.ApiException.BadRequest("邮箱格式不正确或超过 128 字符");
    }
    public static ulong OneRole(ulong? roleId, IReadOnlyList<ulong>? roleIds, bool required)
    {
        var legacy = roleIds?.Distinct().ToArray();
        if (legacy is { Length: not 1 }) throw Yf.Api.Infrastructure.ApiException.BadRequest("启用的内部用户必须且只能绑定一个角色");
        if (roleId == 0 || legacy is { Length: 1 } && legacy[0] == 0) throw Yf.Api.Infrastructure.ApiException.BadRequest("角色不存在: 0");
        if (roleId.HasValue && legacy is { Length: 1 } && legacy[0] != roleId) throw Yf.Api.Infrastructure.ApiException.BadRequest("roleId 与 roleIds 不一致");
        return roleId ?? legacy?.SingleOrDefault() ?? (required ? throw Yf.Api.Infrastructure.ApiException.BadRequest("请选择角色") : 0UL);
    }
}

public sealed record DepartmentResponse(ulong Id, string Name, ulong? ParentId, string Kind, int SortNo, string Status);

public sealed record DepartmentTreeNode(
    ulong Id, string Name, ulong? ParentId, string Kind, int SortNo, string Status, IReadOnlyList<DepartmentTreeNode> Children);

public sealed record PermissionResponse(
    ulong Id, string Code, string Name, string Type, ulong? ParentId, int SortNo, bool Grantable, bool SupplierAssignable);

public sealed record RoleResponse(
    ulong Id,
    string Name,
    string? Description,
    bool IsBuiltIn,
    string Status,
    IReadOnlyList<ulong> PermissionIds,
    ulong AssignedUserCount,
    bool CanManage,
    bool SupplierRestricted,
    DateTime CreatedAt);

public sealed record RoleOption(ulong Id, string Name);

public sealed record UserResponse(
    ulong Id,
    string EmployeeNo,
    string RealName,
    string Email,
    string UserType,
    ulong? SupplierId,
    ulong? DepartmentId,
    string? DepartmentName,
    string Status,
    bool MustChangePassword,
    DateTime? LastLoginAt,
    DateTime CreatedAt,
    ulong? RoleId,
    string? RoleName,
    IReadOnlyList<ulong> RoleIds,
    IReadOnlyList<string> RoleNames);

public sealed record SupplierResponse(ulong Id, string Name, string? Remark, string Status, DateTime CreatedAt);

public sealed record SupplierAccountResponse(
    ulong Id,
    string EmployeeNo,
    string RealName,
    string Email,
    ulong? SupplierId,
    string Status,
    DateTime? LastLoginAt,
    DateTime CreatedAt,
    ulong? RoleId,
    string? RoleName);
