using System.Text.Json.Serialization;

namespace Yf.Api.Modules.Identity;

public sealed record LoginRequest(
    string EmployeeNo,
    [property: JsonRequired] string Password);

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

public sealed record ProfileResponse(
    UserBrief User,
    bool MustChangePassword,
    IReadOnlyList<string> Permissions,
    IReadOnlyList<string> Menus);
