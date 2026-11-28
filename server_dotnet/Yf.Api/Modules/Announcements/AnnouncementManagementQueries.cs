using Microsoft.EntityFrameworkCore;
using Yf.Api.Infrastructure;
using Yf.Api.Infrastructure.Entities;
using Yf.Api.Modules.Oem.Common;

namespace Yf.Api.Modules.Announcements;

public sealed partial class AnnouncementManagementService
{
    public async Task<PageResponse<AnnouncementManagementRow>> ListAsync(CurrentUser actor, string? keyword,
        string? status, int? page, int? pageSize, CancellationToken ct)
    {
        var (number, size, offset) = AnnouncementPaging.Get(page, pageSize);
        await using var uow = await OemUnitOfWork.ReadAsync(dbFactory, ct);
        var access = await AuthorizeAsync(uow, actor, ct);
        var query = access.Visible(uow.Db).AsNoTracking();
        if (!string.IsNullOrWhiteSpace(status))
        {
            if (status is not ("DRAFT" or "PUBLISHED" or "WITHDRAWN"))
                throw ApiException.BadRequest("公告状态无效");
            query = query.Where(a => a.Status == status);
        }
        if (!string.IsNullOrWhiteSpace(keyword))
        {
            var pattern = QueryValues.ContainsPattern(keyword.Trim());
            query = query.Where(a => EF.Functions.Like(a.Title, pattern, QueryValues.LikeEscape));
        }
        var total = (ulong)await query.LongCountAsync(ct);
        // Body can be long text; the list needs only its summary and the fields for action permissions.
        var items = await query.OrderByDescending(a => a.UpdatedAt).ThenByDescending(a => a.Id)
            .Page(offset, size).Select(a => new Announcement
            {
                Id = a.Id, Version = a.Version, Title = a.Title, Status = a.Status, CreatedBy = a.CreatedBy,
                DepartmentId = a.DepartmentId, DepartmentName = a.DepartmentName, PublisherId = a.PublisherId,
                PublisherName = a.PublisherName, PublishedAt = a.PublishedAt, PinnedAt = a.PinnedAt,
                UpdatedAt = a.UpdatedAt,
            }).ToArrayAsync(ct);
        return new(items.Select(a => Row(a, access)).ToArray(), total, number, size);
    }

    public async Task<AnnouncementManagementDetailResponse> DetailAsync(CurrentUser actor, ulong id, CancellationToken ct)
    {
        await using var uow = await OemUnitOfWork.ReadAsync(dbFactory, ct);
        var access = await AuthorizeAsync(uow, actor, ct);
        var entity = await access.Visible(uow.Db).AsNoTracking().SingleOrDefaultAsync(a => a.Id == id, ct)
            ?? throw ApiException.NotFound("公告不可查看");
        return await DescribeAsync(uow, entity, access, ct);
    }

    public async Task<AnnouncementOptionsResponse> OptionsAsync(CurrentUser actor, CancellationToken ct)
    {
        await using var uow = await OemUnitOfWork.ReadAsync(dbFactory, ct);
        var access = await AuthorizeAsync(uow, actor, ct);
        if (!access.Publish) throw ApiException.Forbidden();
        return await LoadOptionsAsync(uow.Db, access.ActorId, ct);
    }

    public async Task<PageResponse<AnnouncementReadRow>> ReadsAsync(CurrentUser actor, ulong id, int? page,
        int? pageSize, CancellationToken ct)
    {
        var (number, size, offset) = AnnouncementPaging.Get(page, pageSize);
        await using var uow = await OemUnitOfWork.ReadAsync(dbFactory, ct);
        var access = await AuthorizeAsync(uow, actor, ct);
        if (!access.Reads) throw ApiException.Forbidden();
        var announcement = await access.Visible(uow.Db).AsNoTracking().SingleOrDefaultAsync(a => a.Id == id, ct)
            ?? throw ApiException.NotFound("公告不可查看");
        if (announcement.Status == "DRAFT" || !access.InScope(announcement)) throw ApiException.Forbidden();
        var query = uow.Db.AnnouncementReads.AsNoTracking().Where(r => r.AnnouncementId == id);
        var total = (ulong)await query.LongCountAsync(ct);
        var rows = await query.OrderByDescending(r => r.LastReadAt).ThenBy(r => r.Realm).ThenBy(r => r.AccountId)
            .Page(offset, size).Select(r => new AnnouncementReadRow(r.Realm, r.AccountId, r.AccountName, r.DisplayName,
                r.FirstReadAt, r.LastReadAt)).ToArrayAsync(ct);
        return new(rows, total, number, size);
    }

    private static AnnouncementManagementRow Row(Announcement a, ManagementAccess access)
    {
        var inScope = access.InScope(a);
        var draft = a.Status == "DRAFT" && a.CreatedBy == access.ActorId && access.Publish;
        return new()
        {
            Id = a.Id, Version = a.Version, Title = a.Title, Status = a.Status,
            DepartmentId = a.DepartmentId, DepartmentName = a.DepartmentName,
            PublisherId = a.PublisherId, PublisherName = a.PublisherName, PublishedAt = a.PublishedAt,
            PinnedAt = a.PinnedAt, UpdatedAt = a.UpdatedAt, CanEdit = draft, CanPublish = draft,
            CanWithdraw = a.Status == "PUBLISHED" && access.Withdraw && a.PublisherId == access.ActorId,
            CanPin = a.Status == "PUBLISHED" && access.Pin && inScope,
            CanReadDetails = a.Status != "DRAFT" && access.Reads && inScope,
        };
    }

    private static async Task<AnnouncementManagementDetailResponse> DescribeAsync(
        OemUnitOfWork uow, Announcement entity, ManagementAccess access, CancellationToken ct)
    {
        var departments = await uow.Db.AnnouncementDepartments.AsNoTracking().Where(d => d.AnnouncementId == entity.Id)
            .OrderBy(d => d.DepartmentName).ThenBy(d => d.DepartmentId)
            .Select(d => new AnnouncementOption(d.DepartmentId, d.DepartmentName)).ToArrayAsync(ct);
        var companies = await uow.Db.AnnouncementCompanies.AsNoTracking().Where(c => c.AnnouncementId == entity.Id)
            .OrderBy(c => c.CompanyName).ThenBy(c => c.CompanyId)
            .Select(c => new AnnouncementOption(c.CompanyId, c.CompanyName)).ToArrayAsync(ct);
        return new(Row(entity, access), entity.Body, entity.InternalScope, entity.ExternalScope, departments,
            companies, entity.WithdrawnAt, entity.WithdrawnByName)
        {
            DocumentJson = entity.DocumentJson,
            Assets = await AnnouncementContentAssets.ListAsync(uow.Db, entity.Id, ct),
        };
    }
}
