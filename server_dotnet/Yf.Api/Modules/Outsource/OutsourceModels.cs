namespace Yf.Api.Modules.Outsource;

public sealed record OutsourceVendorUpsert(string? Name, string? Remark, int? SortNo);

public sealed record OutsourceContactUpsert(string? Name, string? Email, int? SortNo);

public sealed record OutsourceTypeUpsert(string? Code, string? Name, int? SortNo);

public sealed record OutsourceTypeVendorsRequest(ulong[]? VendorIds);

public sealed record OutsourceDefaultCcRequest(ulong[]? UserIds);

public sealed record OutsourceContactResponse(ulong Id, ulong VendorId, string? Name, string Email, string Status, int SortNo);

public sealed record OutsourceVendorResponse(
    ulong Id, string Name, string? Remark, string Status, int SortNo, OutsourceContactResponse[] Contacts);

public sealed record OutsourceTypeResponse(ulong Id, string Code, string Name, string Status, int SortNo, ulong[] VendorIds);

public sealed record OutsourceCcUserResponse(ulong UserId, string EmployeeNo, string RealName, string Email, string Status);
