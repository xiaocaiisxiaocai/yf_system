using System.Text.Json.Serialization;

namespace Yf.Api.Modules.Identity;

public sealed record LoginRequest(
    string EmployeeNo,
    [property: JsonRequired] string Password,
    string? CaptchaId,
    string? CaptchaCode);

public sealed record ChangePasswordRequest(
    [property: JsonRequired] string OldPassword,
    [property: JsonRequired] string NewPassword);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record UpdateProfileRequest([property: JsonRequired] string Email);

public sealed record UserBrief(
    ulong Id,
    string EmployeeNo,
    string RealName,
    string Email,
    string UserType,
    ulong? SupplierId,
    bool IsSystemAdmin);

public sealed record LoginResponse(
    string AccessToken,
    long ExpiresAt,
    bool MustChangePassword,
    IReadOnlyList<string> Permissions,
    IReadOnlyList<string> Menus,
    UserBrief User);

public sealed record TokenResponse(string AccessToken, long ExpiresAt);

public sealed record ProfileResponse(
    UserBrief User,
    bool MustChangePassword,
    IReadOnlyList<string> Permissions,
    IReadOnlyList<string> Menus);

public sealed record CaptchaResponse(string CaptchaId, string Svg);

internal sealed class UserRow
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
}

internal sealed class RefreshTokenRow
{
    public ulong Id { get; init; }
    public ulong UserId { get; init; }
    public string SessionId { get; init; } = "";
    public string TokenHash { get; init; } = "";
    public DateTime ExpiresAt { get; init; }
    public bool Revoked { get; init; }
}
