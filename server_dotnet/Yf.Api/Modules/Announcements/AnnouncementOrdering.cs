using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yf.Api.Infrastructure;
using Yf.Api.Infrastructure.Entities;
using Yf.Api.Modules.Oem.Common;

namespace Yf.Api.Modules.Announcements;

public sealed record AnnouncementOrderRow(ulong Id, string Title, string DepartmentName, DateTime PublishedAt,
    DateTime? PinnedAt, int? SortOrder, ulong Version);
public sealed record AnnouncementOrderResponse(string Revision, AnnouncementOrderRow[] List);
public sealed record AnnouncementOrderChange(ulong Id, int? SortOrder);
public sealed record AnnouncementOrderRequest(bool Pinned, string Revision, AnnouncementOrderChange[] Changes);

internal static class AnnouncementOrdering
{
    internal static IOrderedQueryable<Announcement> InReaderOrder(this IQueryable<Announcement> rows) =>
        rows.OrderByDescending(a => a.PinnedAt.HasValue)
            .ThenByDescending(a => a.PinnedAt.HasValue ? a.PinnedSortOrder.HasValue : a.NormalSortOrder.HasValue)
            .ThenBy(a => a.PinnedAt.HasValue ? a.PinnedSortOrder : a.NormalSortOrder)
            .ThenByDescending(a => a.PinnedAt ?? a.PublishedAt)
            .ThenByDescending(a => a.Id);

    internal static void MapAnnouncementOrdering(this RouteGroupBuilder group)
    {
        group.MapGet("/ordering", (bool pinned, HttpContext ctx, AnnouncementManagementService service,
            CancellationToken ct) => service.GetOrderAsync(AccessService.GetCurrent(ctx), pinned, ct));
        group.MapPut("/ordering", (AnnouncementOrderRequest request, HttpContext ctx, AnnouncementManagementService service,
            CancellationToken ct) => service.SetOrderAsync(AccessService.GetCurrent(ctx), request, ct));
    }
}

public sealed partial class AnnouncementManagementService
{
    public async Task<AnnouncementOrderResponse> GetOrderAsync(CurrentUser actor, bool pinned, CancellationToken ct)
    {
        await using var uow = await OemUnitOfWork.ReadAsync(dbFactory, ct);
        var access = await AuthorizeAsync(uow, actor, ct);
        return await OrderSnapshotAsync(uow.Db, access, pinned, ct);
    }

    private static async Task<AnnouncementOrderResponse> OrderSnapshotAsync(YfDbContext db, ManagementAccess access,
        bool pinned, CancellationToken ct)
    {
        if (!access.Pin) throw ApiException.Forbidden();
        var query = db.Announcements.AsNoTracking().Where(a => a.Status == "PUBLISHED" && a.PinnedAt.HasValue == pinned);
        if (!access.Scope.IsAll)
        {
            var departments = access.Scope.OrgIds.ToArray();
            query = access.Scope.IsOrg
                ? query.Where(a => a.DepartmentId.HasValue && departments.Contains(a.DepartmentId.Value))
                : query.Where(a => a.PublisherId == access.ActorId);
        }
        var rows = await query.InReaderOrder().Select(a => new AnnouncementOrderRow(a.Id, a.Title, a.DepartmentName,
            a.PublishedAt!.Value, a.PinnedAt, pinned ? a.PinnedSortOrder : a.NormalSortOrder, a.Version)).ToArrayAsync(ct);
        // Membership, pin transitions and any saved reorder are part of the preview's baseline.
        var token = JsonSerializer.SerializeToUtf8Bytes(new { pinned, rows = rows.Select(r => new { r.Id, r.Version }) });
        return new(Convert.ToHexString(SHA256.HashData(token)), rows);
    }

    public async Task<EmptyResponse> SetOrderAsync(CurrentUser actor, AnnouncementOrderRequest request, CancellationToken ct)
    {
        // The existing exclusive management gate orders this compare-and-write with publication, pinning,
        // withdrawal and permission changes, including when two people edit different rows of the group.
        await using var uow = await OemUnitOfWork.BeginManagementAsync(dbFactory, ct);
        var access = await AuthorizeAsync(uow, actor, ct);
        var snapshot = await OrderSnapshotAsync(uow.Db, access, request.Pinned, ct);
        if (string.IsNullOrEmpty(request.Revision) || request.Changes is not { Length: > 0 }
            || request.Changes.Any(c => c is null || c.SortOrder < 0)
            || request.Changes.DistinctBy(c => c.Id).Count() != request.Changes.Length)
            throw ApiException.BadRequest("顺序号须为非负整数，同一公告只能提交一次");
        if (snapshot.Revision != request.Revision)
            throw ApiException.VersionConflict("公告顺序或可管理的公告已变更，请刷新后重新确认");
        var managed = snapshot.List.ToDictionary(r => r.Id);
        if (request.Changes.Any(c => !managed.ContainsKey(c.Id))) throw ApiException.Forbidden();
        var changed = request.Changes.Where(c => managed[c.Id].SortOrder != c.SortOrder).ToArray();
        if (changed.Length > 0)
        {
            foreach (var change in changed)
            {
                var old = managed[change.Id];
                // Attach only the affected scalars, without materialising published documents and files.
                var row = new Announcement { Id = old.Id, Version = old.Version };
                if (request.Pinned) row.PinnedSortOrder = old.SortOrder;
                else row.NormalSortOrder = old.SortOrder;
                uow.Db.Announcements.Attach(row);
                if (request.Pinned) row.PinnedSortOrder = change.SortOrder;
                else row.NormalSortOrder = change.SortOrder;
                row.Version++;
                row.UpdatedAt = uow.Now;
            }
            await uow.Db.SaveChangesAsync(ct);
            await audit.WriteAsync(uow.Connection, uow.Transaction, actor.Id, "ANNOUNCEMENT_ORDER", "announcement", null,
                new { pinned = request.Pinned, changes = changed.Select(c => new { c.Id, before = managed[c.Id].SortOrder, after = c.SortOrder }) }, null, ct);
        }
        await uow.CommitAsync(ct);
        return EmptyResponse.Instance;
    }
}
