using Microsoft.EntityFrameworkCore;
using Yf.Api.Infrastructure;
using Yf.Api.Infrastructure.Entities;
using Yf.Api.Modules.Oem.Common;

namespace Yf.Api.Modules.Announcements;

public sealed record AnnouncementReaderItemResponse(
    ulong Id, string Title, string DepartmentName, DateTime PublishedAt, DateTime? PinnedAt, bool IsRead);

public sealed record AnnouncementReaderPageResponse(
    IReadOnlyList<AnnouncementReaderItemResponse> List, ulong Total, ulong Page, ulong PageSize, ulong UnreadCount);

public sealed record AnnouncementReaderDetailResponse(
    ulong Id, string Title, string DepartmentName, DateTime PublishedAt, DateTime? PinnedAt, bool IsRead, string Body);

public sealed class AnnouncementReaderService(IDbContextFactory<YfDbContext> dbFactory)
{
    public async Task<AnnouncementReaderPageResponse> ListAsync(
        OemActor actor, string? keyword, bool? unreadOnly, int? page, int? pageSize, CancellationToken ct)
    {
        var (number, size, offset) = AnnouncementPaging.Get(page, pageSize);
        await using var uow = await OemUnitOfWork.ReadAsync(dbFactory, ct);
        var visible = await VisibleAsync(uow, actor, ct);
        var readIds = ReadIds(uow.Db, actor);
        var unread = visible.Where(item => !readIds.Contains(item.Id));
        var unreadCount = (ulong)await unread.LongCountAsync(ct);
        var query = unreadOnly is true ? unread : visible;
        if (!string.IsNullOrWhiteSpace(keyword))
        {
            var pattern = QueryValues.ContainsPattern(keyword.Trim());
            query = query.Where(item => EF.Functions.Like(item.Title, pattern, QueryValues.LikeEscape));
        }
        var total = unreadOnly is true && string.IsNullOrWhiteSpace(keyword)
            ? unreadCount : (ulong)await query.LongCountAsync(ct);
        var items = await query.OrderByDescending(item => item.PinnedAt)
            .ThenByDescending(item => item.PublishedAt).ThenByDescending(item => item.Id)
            .Page(offset, size)
            .Select(item => new AnnouncementReaderItemResponse(item.Id, item.Title, item.DepartmentName,
                item.PublishedAt!.Value, item.PinnedAt, readIds.Contains(item.Id))).ToArrayAsync(ct);
        return new(items, total, number, size, unreadCount);
    }

    public async Task<AnnouncementReaderDetailResponse> DetailAsync(OemActor actor, ulong id, CancellationToken ct)
    {
        await using var uow = await OemUnitOfWork.ReadAsync(dbFactory, ct);
        var visible = await VisibleAsync(uow, actor, ct);
        var readIds = ReadIds(uow.Db, actor);
        return await visible.Where(item => item.Id == id)
            .Select(item => new AnnouncementReaderDetailResponse(item.Id, item.Title, item.DepartmentName,
                item.PublishedAt!.Value, item.PinnedAt, readIds.Contains(item.Id), item.Body))
            .SingleOrDefaultAsync(ct) ?? throw ApiException.NotFound("公告不可查看");
    }

    public async Task<EmptyResponse> ReadAsync(OemActor actor, ulong id, CancellationToken ct)
    {
        await using var uow = await OemUnitOfWork.BeginAsync(dbFactory, ct);
        var visible = await VisibleAsync(uow, actor, ct);
        // Withdrawal updates this same parent row. Hold a shared lock through the receipt write so
        // withdrawal and reads are ordered, while independent readers can still open concurrently.
        var locked = await uow.Db.Database.SqlQuery<int>(
            $"SELECT 1 AS Value FROM announcements WHERE id={id} LOCK IN SHARE MODE").SingleOrDefaultAsync(ct);
        if (locked != 1 || !await visible.AnyAsync(item => item.Id == id, ct))
            throw ApiException.NotFound("公告不可查看");

        var account = actor.IsInternal
            ? await uow.Db.Users.Where(user => user.Id == actor.Id)
                .Select(user => new { user.EmployeeNo, user.RealName }).SingleAsync(ct)
            : await uow.Db.OemAccounts.Where(user => user.Id == actor.Id)
                .Select(user => new { user.EmployeeNo, user.RealName }).SingleAsync(ct);
        var displayName = string.IsNullOrWhiteSpace(account.RealName) ? account.EmployeeNo : account.RealName;
        // Unique (announcement, realm, account) makes concurrent opens from multiple devices a
        // single receipt. Use the database clock at the write and keep first read immutable.
        await uow.Db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO announcement_reads
                (announcement_id, realm, account_id, account_name, display_name, first_read_at, last_read_at)
            VALUES ({id}, {actor.Realm}, {actor.Id}, {account.EmployeeNo}, {displayName}, UTC_TIMESTAMP(6), UTC_TIMESTAMP(6))
            ON DUPLICATE KEY UPDATE last_read_at = GREATEST(last_read_at, UTC_TIMESTAMP(6))
            """, ct);
        await uow.CommitAsync(ct);
        return EmptyResponse.Instance;
    }

    private static IQueryable<ulong> ReadIds(YfDbContext db, OemActor actor) =>
        db.AnnouncementReads.Where(read => read.Realm == actor.Realm && read.AccountId == actor.Id)
            .Select(read => read.AnnouncementId);

    private static async Task<IQueryable<Announcement>> VisibleAsync(OemUnitOfWork uow, OemActor actor, CancellationToken ct)
    {
        var current = await OemAuthorizer.RecheckAsync(uow, actor, ct);
        var db = uow.Db;
        var published = db.Announcements.AsNoTracking().Where(item => item.Status == "PUBLISHED");
        if (current is OemAccountActor vendor)
            return published.Where(item => item.ExternalScope == "ALL" || (item.ExternalScope == "SELECTED" &&
                db.AnnouncementCompanies.Any(target => target.AnnouncementId == item.Id && target.CompanyId == vendor.CompanyId)));

        var department = await AnnouncementOrganization.DepartmentAsync(db, current.Id, ct);
        if (department is null) return published.Where(item => item.InternalScope == "ALL");
        var departmentId = department.Id;
        return published.Where(item => item.InternalScope == "ALL" ||
            (item.InternalScope == "OWN" && item.DepartmentId == departmentId) ||
            (item.InternalScope == "SELECTED" && db.AnnouncementDepartments.Any(target =>
                target.AnnouncementId == item.Id && target.DepartmentId == departmentId)));
    }
}
