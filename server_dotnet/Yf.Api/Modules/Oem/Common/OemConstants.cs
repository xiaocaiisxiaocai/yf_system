using Yf.Api.Modules.Identity;

namespace Yf.Api.Modules.Oem.Common;

public static class OemRealms
{
    public const string Oem = "oem";
    public const string Internal = IdentityRealms.Internal;
    /// <summary>Background automation (workers, reconciliation); never an authenticated caller.</summary>
    public const string System = "system";
}

public static class OemApi
{
    public const string Prefix = "/api/v1/oem";
}

/// <summary>Internal-account permission codes of the OEM business line (seeded by the AddOemPlatform migration).</summary>
public static class OemPermissions
{
    public const string TransferCreate = "oem:transfer_create";
    public const string TransferView = "oem:transfer_view";
    public const string FileDownload = "oem:file_download";
    public const string FlowApprove = "oem:flow_approve";
    public const string ApprovalRecover = "oem:approval_recover";
    public const string CompanyManage = "oem:company_manage";
    public const string AccountManage = "oem:account_manage";
    public const string CompanyDelete = "oem:company_delete";
    public const string AccountDelete = "oem:account_delete";
    public const string FlowTemplateManage = "oem:flow_template_manage";
    public const string RetentionTemplateManage = "oem:retention_template_manage";
    public const string FilePolicyManage = "oem:file_policy_manage";
    public const string NotifyManage = "oem:notify_manage";
    public const string AuditView = "oem:audit_view";
    public const string DepartmentLeaderManage = "dept:leader_manage";

    public const string MenuCode = "oem";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        TransferCreate, TransferView, FileDownload, FlowApprove, ApprovalRecover, CompanyManage, AccountManage, CompanyDelete, AccountDelete,
        FlowTemplateManage, RetentionTemplateManage, FilePolicyManage, NotifyManage, AuditView, MenuCode,
    };
}

public static class OemStatus
{
    public const string Active = "ACTIVE";
    public const string Disabled = "DISABLED";

    public static string Normalize(string? status) => status?.Trim().ToUpperInvariant() switch
    {
        Active => Active,
        Disabled => Disabled,
        _ => throw Infrastructure.ApiException.BadRequest("状态只能为 ACTIVE 或 DISABLED"),
    };
}
