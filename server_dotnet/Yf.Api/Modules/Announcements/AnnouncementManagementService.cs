using Microsoft.EntityFrameworkCore;
using Yf.Api.Infrastructure;
using Yf.Api.Infrastructure.Entities;
using Yf.Api.Modules.Identity;
using Yf.Api.Modules.Oem.Common;

namespace Yf.Api.Modules.Announcements;

public sealed partial class AnnouncementManagementService(IDbContextFactory<YfDbContext> dbFactory, AuditService audit)
{
    private sealed record ManagementAccess(ulong ActorId, bool Publish, bool Withdraw, bool Pin, bool Reads,
        DataScopeResolution Scope)
    {
        public bool InScope(Announcement a) =>
            Scope.IsAll || (Scope.IsOrg && a.DepartmentId is ulong departmentId && Scope.OrgIds.Contains(departmentId))
            || (Scope.IsSelf && a.PublisherId == ActorId);

        public IQueryable<Announcement> Visible(YfDbContext db)
        {
            var orgIds = Scope.OrgIds.ToArray();
            var all = Scope.IsAll;
            var org = Scope.IsOrg;
            var self = Scope.IsSelf;
            return db.Announcements.AsNoTracking().Where(a => a.Status == "DRAFT"
                ? Publish && a.CreatedBy == ActorId
                : (Withdraw && a.PublisherId == ActorId) || ((Publish || Reads || Pin) &&
                    (all || (self && a.PublisherId == ActorId)
                    || (org && a.DepartmentId.HasValue && orgIds.Contains(a.DepartmentId.Value)))));
        }
    }

    private static async Task<ManagementAccess> AuthorizeAsync(OemUnitOfWork uow, CurrentUser actor, CancellationToken ct)
    {
        var current = await AccessService.RecheckActorAsync(uow.Connection, uow.Transaction, actor, ct);
        AccessService.RequireInternal(current);
        var codes = await AccessService.PermissionCodesAsync(uow.Connection, uow.Transaction, current.Id, ct);
        var publish = codes.Contains("announcement:publish");
        var withdraw = codes.Contains("announcement:withdraw");
        var pin = codes.Contains("announcement:pin");
        var reads = codes.Contains("announcement:reads");
        if (!publish && !withdraw && !pin && !reads) throw ApiException.Forbidden();
        var scope = await DataScopeService.ResolveAsync(uow.Db, current.Id, DataScopeDomains.Announcement, ct);
        return new(current.Id, publish, withdraw, pin, reads, scope);
    }

    private static async Task<AnnouncementOptionsResponse> LoadOptionsAsync(YfDbContext db, ulong actorId, CancellationToken ct)
    {
        var department = await AnnouncementOrganization.DepartmentAsync(db, actorId, ct);
        var departments = await db.Departments.AsNoTracking()
            .Where(d => d.Status == AccountStatuses.Active && d.Kind == "DEPARTMENT")
            .OrderBy(d => d.Name).ThenBy(d => d.Id).Select(d => new AnnouncementOption(d.Id, d.Name)).ToArrayAsync(ct);
        var companies = await db.OemCompanies.AsNoTracking().Where(c => c.Status == OemStatus.Active)
            .OrderBy(c => c.Name).ThenBy(c => c.Id).Select(c => new AnnouncementCompanyOption(c.Id, c.Name,
                db.OemAccounts.Count(a => a.Status == OemStatus.Active && a.OemCompanyId == c.Id))).ToArrayAsync(ct);
        return new(department?.Id, department?.Name, departments, companies);
    }

    private static async Task<Announcement> LockedAsync(OemUnitOfWork uow, ulong id, CancellationToken ct)
    {
        // Terminal states and receipt creation synchronize on this row. Lock before checking status.
        var exists = await uow.Db.Database.SqlQuery<int>($"SELECT 1 AS Value FROM announcements WHERE id={id} FOR UPDATE")
            .SingleOrDefaultAsync(ct);
        if (exists != 1) throw ApiException.NotFound("公告不存在");
        return await uow.Db.Announcements.SingleAsync(a => a.Id == id, ct);
    }

    private static async Task<DateTime> OperationTimeAsync(YfDbContext db, CancellationToken ct) =>
        await db.Database.SqlQuery<DateTime>($"SELECT UTC_TIMESTAMP(6) AS Value").SingleAsync(ct);

    private static Task<string> ActorNameAsync(YfDbContext db, ulong actorId, CancellationToken ct) =>
        db.Users.Where(u => u.Id == actorId).Select(u => u.RealName == "" ? u.EmployeeNo : u.RealName).SingleAsync(ct);

    public async Task<AnnouncementManagementDetailResponse> SaveAsync(CurrentUser actor, ulong? id,
        AnnouncementDraftRequest request, CancellationToken ct)
    {
        await using var uow = await OemUnitOfWork.BeginAsync(dbFactory, ct);
        var access = await AuthorizeAsync(uow, actor, ct);
        if (!access.Publish) throw ApiException.Forbidden();
        var db = uow.Db;
        var now = await OperationTimeAsync(db, ct);
        string? createKey = null;
        if (request.CreateKey is { } key)
        {
            if (id.HasValue || !Guid.TryParse(key, out var parsed) || parsed == Guid.Empty)
                throw ApiException.BadRequest("新建草稿标识无效");
            createKey = parsed.ToString("N");
            // A response may be lost after commit. Serialize creates for this author so concurrent retries
            // recover the same ID without replaying the payload over a newer save or publication.
            await db.Database.SqlQuery<ulong>($"SELECT id AS Value FROM users WHERE id={actor.Id} FOR UPDATE").SingleAsync(ct);
            var createdId = await db.Announcements.Where(a => a.CreatedBy == actor.Id && a.CreateKey == createKey)
                .Select(a => (ulong?)a.Id).SingleOrDefaultAsync(ct);
            if (createdId is { } prior)
            {
                var existing = await LockedAsync(uow, prior, ct);
                if (!await access.Visible(db).AnyAsync(a => a.Id == prior, ct)) throw ApiException.NotFound("公告不可查看");
                return await DescribeAsync(uow, existing, access, ct);
            }
        }
        if (request.CopyFromId is { } source)
        {
            if (id.HasValue) throw ApiException.BadRequest("仅新建草稿时可指定复制来源");
            if (!await access.Visible(db).AnyAsync(a => a.Id == source, ct)) throw ApiException.NotFound("公告不可查看");
        }
        Announcement row;
        if (id.HasValue)
        {
            row = await LockedAsync(uow, id.Value, ct);
            if (row.CreatedBy != actor.Id) throw ApiException.Forbidden();
            if (row.Status != "DRAFT") throw ApiException.Conflict("仅草稿可编辑");
            if (request.Version != row.Version) throw ApiException.VersionConflict("草稿已变更，请刷新后再保存");
            row.Version++;
        }
        else
        {
            row = new() { CreatedBy = actor.Id, CreatedAt = now, CreateKey = createKey };
            db.Announcements.Add(row);
        }

        var title = request.Title?.Trim() ?? "";
        if (title.Length > 200) throw ApiException.BadRequest("公告标题最多 200 字");
        if (row.DocumentJson is not null && request.DocumentJson is null)
            throw ApiException.Conflict("请刷新并使用图文编辑器保存此公告");
        var document = AnnouncementDocument.FromDraft(request);
        var internalScope = request.InternalScope ?? "OWN";
        if (internalScope is not ("OWN" or "ALL" or "SELECTED")
            || request.ExternalScope is not (null or "ALL" or "SELECTED"))
            throw ApiException.BadRequest("公告可见范围不正确");
        var options = await LoadOptionsAsync(db, actor.Id, ct);
        var departmentIds = internalScope == "SELECTED" ? (request.DepartmentIds ?? []).ToHashSet() : [];
        var companyIds = request.ExternalScope == "SELECTED" ? (request.CompanyIds ?? []).ToHashSet() : [];
        var departments = options.Departments.Where(d => departmentIds.Contains(d.Id)).ToArray();
        var companies = options.Companies.Where(c => companyIds.Contains(c.Id)).ToArray();
        if (departments.Length != departmentIds.Count || companies.Length != companyIds.Count)
            throw ApiException.BadRequest("所选部门或公司已停用或不存在，请重新选择");
        row.Title = title;
        row.Body = document.Text;
        row.DocumentJson = document.Json;
        row.InternalScope = internalScope;
        row.ExternalScope = request.ExternalScope;
        row.DepartmentId = options.DepartmentId;
        row.DepartmentName = options.DepartmentName ?? "";
        row.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        await AnnouncementContentAssets.BindAsync(db, row.Id, request.CopyFromId, document.Images, request.AttachmentIds, now, ct);
        if (id.HasValue)
        {
            await db.AnnouncementDepartments.Where(d => d.AnnouncementId == row.Id).ExecuteDeleteAsync(ct);
            await db.AnnouncementCompanies.Where(c => c.AnnouncementId == row.Id).ExecuteDeleteAsync(ct);
        }
        db.AnnouncementDepartments.AddRange(departments.Select(d => new AnnouncementDepartment {
            AnnouncementId = row.Id, DepartmentId = d.Id, DepartmentName = d.Name }));
        db.AnnouncementCompanies.AddRange(companies.Select(c => new AnnouncementCompany {
            AnnouncementId = row.Id, CompanyId = c.Id, CompanyName = c.Name }));
        await db.SaveChangesAsync(ct);
        var result = await DescribeAsync(uow, row, access, ct);
        await uow.CommitAsync(ct);
        return result;
    }

    public async Task<EmptyResponse> PublishAsync(CurrentUser actor, ulong id, ulong? version, CancellationToken ct)
    {
        await using var uow = await OemUnitOfWork.BeginAsync(dbFactory, ct);
        var access = await AuthorizeAsync(uow, actor, ct);
        if (!access.Publish) throw ApiException.Forbidden();
        var row = await LockedAsync(uow, id, ct);
        if (row.CreatedBy != actor.Id) throw ApiException.Forbidden();
        if (row.Status == "PUBLISHED" && row.PublisherId == actor.Id) return EmptyResponse.Instance;
        if (row.Status != "DRAFT") throw ApiException.Conflict("仅草稿可发布");
        if (version.HasValue && version != row.Version) throw ApiException.VersionConflict("草稿已变更，请重新预览后发布");
        var db = uow.Db;
        if (row.Title.Length == 0 || (row.Body.Length == 0 && !await db.AnnouncementAssetLinks
                .AnyAsync(link => link.AnnouncementId == id && link.SortOrder != null, ct)))
            throw ApiException.BadRequest("请填写公告标题，并添加正文、图片或附件");
        var department = await AnnouncementOrganization.DepartmentAsync(db, actor.Id, ct)
            ?? throw ApiException.BadRequest("未确定发布部门，请先设置账号所属部门");
        if (row.DepartmentId != department.Id || row.DepartmentName != department.Name)
            throw ApiException.Conflict("发布部门已变更，请重新保存并预览草稿");
        if (row.InternalScope == "SELECTED")
        {
            var departments = db.AnnouncementDepartments.Where(d => d.AnnouncementId == id);
            if (!await departments.AnyAsync(ct) || await departments.AnyAsync(d => !db.Departments.Any(x =>
                x.Id == d.DepartmentId && x.Status == AccountStatuses.Active && x.Kind == "DEPARTMENT"), ct))
                throw ApiException.BadRequest("请选择至少一个启用的部门，并移除已失效的部门");
        }
        if (row.ExternalScope == "ALL")
        {
            if (!await db.OemCompanies.AnyAsync(c => c.Status == OemStatus.Active, ct))
                throw ApiException.BadRequest("没有可接收公告的启用公司");
        }
        else if (row.ExternalScope == "SELECTED")
        {
            var companies = db.AnnouncementCompanies.Where(c => c.AnnouncementId == id);
            if (!await companies.AnyAsync(ct) || await companies.AnyAsync(c => !db.OemCompanies.Any(x =>
                x.Id == c.CompanyId && x.Status == OemStatus.Active), ct))
                throw ApiException.BadRequest("请选择至少一家启用的 ODM/OEMA 公司，并移除已失效的公司");
        }
        else throw ApiException.BadRequest("请选择外部可见范围");

        await db.AnnouncementAssetLinks.Where(link => link.AnnouncementId == id && link.SortOrder == null)
            .ExecuteDeleteAsync(ct);
        row.PublisherId = actor.Id;
        row.PublisherName = await ActorNameAsync(db, actor.Id, ct);
        row.Status = "PUBLISHED";
        row.PublishedAt = await OperationTimeAsync(db, ct);
        return await CommitOperationAsync(uow, row, actor.Id, "ANNOUNCEMENT_PUBLISH", row.PublishedAt.Value, ct);
    }

    public async Task<EmptyResponse> WithdrawAsync(CurrentUser actor, ulong id, CancellationToken ct)
    {
        await using var uow = await OemUnitOfWork.BeginAsync(dbFactory, ct);
        var access = await AuthorizeAsync(uow, actor, ct);
        if (!access.Withdraw) throw ApiException.Forbidden();
        var row = await LockedAsync(uow, id, ct);
        if (row.PublisherId != actor.Id) throw ApiException.Forbidden("只有公告发布人可以下架");
        if (row.Status == "WITHDRAWN") return EmptyResponse.Instance;
        if (row.Status != "PUBLISHED") throw ApiException.Conflict("只有在架公告可下架");
        row.Status = "WITHDRAWN";
        row.PinnedAt = null;
        row.WithdrawnBy = actor.Id;
        row.WithdrawnByName = await ActorNameAsync(uow.Db, actor.Id, ct);
        row.WithdrawnAt = await OperationTimeAsync(uow.Db, ct);
        return await CommitOperationAsync(uow, row, actor.Id, "ANNOUNCEMENT_WITHDRAW", row.WithdrawnAt.Value, ct);
    }

    public async Task<EmptyResponse> PinAsync(CurrentUser actor, ulong id, bool pinned, CancellationToken ct)
    {
        await using var uow = await OemUnitOfWork.BeginAsync(dbFactory, ct);
        var access = await AuthorizeAsync(uow, actor, ct);
        if (!access.Pin) throw ApiException.Forbidden();
        var row = await LockedAsync(uow, id, ct);
        if (!access.InScope(row)) throw ApiException.Forbidden();
        if (row.Status != "PUBLISHED") throw ApiException.Conflict("只有在架公告可调整置顶");
        if (row.PinnedAt.HasValue == pinned) return EmptyResponse.Instance;
        var now = await OperationTimeAsync(uow.Db, ct);
        row.PinnedAt = pinned ? now : null;
        return await CommitOperationAsync(uow, row, actor.Id, pinned ? "ANNOUNCEMENT_PIN" : "ANNOUNCEMENT_UNPIN", now, ct);
    }

    private async Task<EmptyResponse> CommitOperationAsync(OemUnitOfWork uow, Announcement row, ulong actorId,
        string action, DateTime now, CancellationToken ct)
    {
        row.Version++;
        row.UpdatedAt = now;
        await uow.Db.SaveChangesAsync(ct);
        await audit.WriteAsync(uow.Connection, uow.Transaction, actorId, action, "announcement", row.Id,
            new { targetName = row.Title, status = row.Status, department = row.DepartmentName }, null, ct);
        await uow.CommitAsync(ct);
        return EmptyResponse.Instance;
    }
}
