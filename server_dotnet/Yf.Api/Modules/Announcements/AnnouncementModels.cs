using Microsoft.EntityFrameworkCore;
using Yf.Api.Infrastructure;

namespace Yf.Api.Modules.Announcements;

public sealed record AnnouncementOption(ulong Id, string Name);
public sealed record AnnouncementCompanyOption(ulong Id, string Name, int AccountCount);
public sealed record AnnouncementOptionsResponse(ulong? DepartmentId, string? DepartmentName,
    AnnouncementOption[] Departments, AnnouncementCompanyOption[] Companies);
public sealed record AnnouncementDraftRequest(string? Title, string? Body, string? InternalScope,
    string? ExternalScope, ulong[]? DepartmentIds, ulong[]? CompanyIds, ulong? Version = null,
    string? DocumentJson = null, ulong[]? AttachmentIds = null, ulong? CopyFromId = null, string? CreateKey = null);
public sealed record AnnouncementAssetResponse(ulong Id, string Kind, string FileName, string ContentType, long SizeBytes);
public sealed record AnnouncementReadRow(string Realm, ulong AccountId, string AccountName, string DisplayName,
    DateTime FirstReadAt, DateTime LastReadAt);

public record AnnouncementManagementRow
{
    public ulong Id { get; init; }
    public ulong Version { get; init; }
    public required string Title { get; init; }
    public required string Status { get; init; }
    public ulong? DepartmentId { get; init; }
    public required string DepartmentName { get; init; }
    public ulong? PublisherId { get; init; }
    public string? PublisherName { get; init; }
    public DateTime? PublishedAt { get; init; }
    public DateTime? PinnedAt { get; init; }
    public DateTime UpdatedAt { get; init; }
    public bool CanEdit { get; init; }
    public bool CanPublish { get; init; }
    public bool CanWithdraw { get; init; }
    public bool CanPin { get; init; }
    public bool CanReadDetails { get; init; }
}

public sealed record AnnouncementManagementDetailResponse : AnnouncementManagementRow
{
    public AnnouncementManagementDetailResponse() { }

    [System.Diagnostics.CodeAnalysis.SetsRequiredMembers]
    public AnnouncementManagementDetailResponse(AnnouncementManagementRow row, string body, string internalScope,
        string? externalScope, AnnouncementOption[] departments, AnnouncementOption[] companies,
        DateTime? withdrawnAt, string? withdrawnByName) : base(row)
    {
        Body = body;
        InternalScope = internalScope;
        ExternalScope = externalScope;
        Departments = departments;
        Companies = companies;
        WithdrawnAt = withdrawnAt;
        WithdrawnByName = withdrawnByName;
    }

    public required string Body { get; init; }
    public string? DocumentJson { get; init; }
    public AnnouncementAssetResponse[] Assets { get; init; } = [];
    public required string InternalScope { get; init; }
    public string? ExternalScope { get; init; }
    public required AnnouncementOption[] Departments { get; init; }
    public required AnnouncementOption[] Companies { get; init; }
    public DateTime? WithdrawnAt { get; init; }
    public string? WithdrawnByName { get; init; }
}

internal static class AnnouncementOrganization
{
    public static Task<AnnouncementOption?> DepartmentAsync(YfDbContext db, ulong userId, CancellationToken ct)
    {
        var departments = db.Departments.Where(d => d.Status == AccountStatuses.Active && d.Kind == "DEPARTMENT");
        return (from user in db.Users
                join member in db.Departments on user.DepartmentId equals member.Id
                where user.Id == userId && member.Status == AccountStatuses.Active
                from department in departments
                where member.Kind == "DEPARTMENT" && member.Id == department.Id
                   || member.Kind == "SECTION" && member.ParentId == department.Id
                select new AnnouncementOption(department.Id, department.Name)).SingleOrDefaultAsync(ct);
    }
}

internal static class AnnouncementPaging
{
    public static (ulong Page, uint Size, ulong Offset) Get(int? page, int? pageSize)
    {
        if (page < 0 || pageSize < 0) throw ApiException.BadRequest("分页参数不能为负数");
        return QueryValues.NormalizePage((ulong)(page ?? 1), (ulong?)pageSize);
    }
}
