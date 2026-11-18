using Microsoft.EntityFrameworkCore;
using Yf.Api.Infrastructure;
using Yf.Api.Infrastructure.Entities;

namespace Yf.Api.Modules.Identity;

/// <summary>Business domains whose records carry an independent data range.</summary>
public static class DataScopeDomains
{
    public const string Project = "project";
    public const string OemTransfer = "oem_transfer";

    public static readonly string[] All = [Project, OemTransfer];

    public static bool IsValid(string? domain) => domain is Project or OemTransfer;

    public static string Label(string domain) => domain switch
    {
        Project => "项目",
        OemTransfer => "ODM/OEMA 传递单",
        _ => domain,
    };
}

/// <summary>
/// Whose records an account may see: only its own (SELF), its own organisation and every organisation below
/// it (ORG), or everyone (ALL). This is deliberately not a permission code: changing a range is data, not code.
/// </summary>
public static class DataScopeValues
{
    public const string Self = "SELF";
    public const string Org = "ORG";
    public const string All = "ALL";

    public static bool IsValid(string? scope) => scope is Self or Org or All;

    public static int Rank(string scope) => scope switch
    {
        All => 2,
        Org => 1,
        Self => 0,
        _ => -1,
    };

    public static string Label(string scope) => scope switch
    {
        Self => "仅本人",
        Org => "本组织及下级",
        All => "全部",
        _ => scope,
    };
}

/// <param name="Scope">One of <see cref="DataScopeValues"/>.</param>
/// <param name="OrgIds">The organisation subtree for <see cref="DataScopeValues.Org"/>; empty otherwise.</param>
public sealed record DataScopeResolution(string Scope, IReadOnlySet<ulong> OrgIds)
{
    public bool IsAll => Scope == DataScopeValues.All;
    public bool IsOrg => Scope == DataScopeValues.Org;
    public bool IsSelf => Scope == DataScopeValues.Self;
}

/// <summary>
/// Resolves the effective data range of an account: an explicit per-user override wins over the role default,
/// and an account that leads an organisation without an explicit setting keeps the historical ORG behaviour.
/// </summary>
public static class DataScopeService
{
    private static readonly IReadOnlySet<ulong> NoOrgs = new HashSet<ulong>();

    public static async Task<DataScopeResolution> ResolveAsync(
        YfDbContext db, ulong userId, string domain, CancellationToken ct) =>
        (await ResolveManyAsync(db, [userId], domain, ct))[userId];

    /// <summary>
    /// Resolves several accounts at once so the realtime audience does not issue one query per connection. This is
    /// the only implementation: <see cref="ResolveAsync"/> goes through it so a single account and a whole audience
    /// can never disagree. The organisation tree is read only when somebody actually needs it.
    /// </summary>
    public static async Task<IReadOnlyDictionary<ulong, DataScopeResolution>> ResolveManyAsync(
        YfDbContext db, IReadOnlyCollection<ulong> userIds, string domain, CancellationToken ct)
    {
        var ids = userIds.Distinct().ToArray();
        if (ids.Length == 0) return new Dictionary<ulong, DataScopeResolution>();
        var overrides = await db.UserDataScopes.AsNoTracking()
            .Where(setting => Enumerable.Contains(ids, setting.UserId) && setting.Domain == domain)
            .ToDictionaryAsync(setting => setting.UserId, setting => setting.Scope, ct);
        var roleDefaults = await RoleDefaultRowsAsync(db, ids, domain, ct);
        // An explicit override wins over the active role's default; either one settles the account without
        // looking at the organisation tree.
        var settled = new Dictionary<ulong, string>(ids.Length);
        foreach (var id in ids)
        {
            var scope = overrides.GetValueOrDefault(id);
            if (!DataScopeValues.IsValid(scope)) scope = roleDefaults.GetValueOrDefault(id);
            if (DataScopeValues.IsValid(scope)) settled[id] = scope!;
        }
        if (settled.Count == ids.Length && !settled.Values.Contains(DataScopeValues.Org))
        {
            return ids.ToDictionary(id => id, id => new DataScopeResolution(settled[id], NoOrgs));
        }

        var departments = await db.Departments.AsNoTracking()
            .Select(department => new { department.Id, department.ParentId, department.Status, department.LeaderAccountId })
            .ToArrayAsync(ct);
        var activeOrgs = departments.Where(department => department.Status == AccountStatuses.Active)
            .Select(department => department.Id).ToHashSet();
        var children = departments.Where(department => department.ParentId.HasValue)
            .ToLookup(department => department.ParentId!.Value, department => department.Id);
        // A disabled organisation grants no authority, so an inactive leadership does not imply ORG.
        var ledByUser = departments.Where(department => department.LeaderAccountId.HasValue
                && activeOrgs.Contains(department.Id))
            .ToLookup(department => department.LeaderAccountId!.Value, department => department.Id);
        var orgUsers = new List<ulong>();
        foreach (var id in ids)
        {
            if (settled.TryGetValue(id, out var known))
            {
                if (known == DataScopeValues.Org) orgUsers.Add(id);
                continue;
            }
            if (ledByUser[id].Any()) { settled[id] = DataScopeValues.Org; orgUsers.Add(id); }
            else settled[id] = DataScopeValues.Self;
        }
        var membershipByUser = orgUsers.Count == 0
            ? new Dictionary<ulong, ulong?>()
            : (await db.Users.AsNoTracking().Where(user => Enumerable.Contains(orgUsers, user.Id))
                    .Select(user => new { user.Id, user.DepartmentId }).ToArrayAsync(ct))
                .ToDictionary(row => row.Id, row => row.DepartmentId);
        var result = new Dictionary<ulong, DataScopeResolution>(ids.Length);
        foreach (var id in ids)
        {
            if (settled[id] != DataScopeValues.Org)
            {
                result[id] = new(settled[id], NoOrgs);
                continue;
            }
            var roots = ledByUser[id].Concat(membershipByUser.GetValueOrDefault(id) is ulong own ? [own] : []);
            result[id] = new(DataScopeValues.Org, Subtree(roots, activeOrgs, children));
        }
        return result;
    }

    public static async Task<string?> RoleDefaultAsync(
        YfDbContext db, ulong userId, string domain, CancellationToken ct) =>
        (await RoleDefaultRowsAsync(db, [userId], domain, ct)).GetValueOrDefault(userId);

    /// <summary>One role row's default for a domain; used when checking a role that is about to be assigned.</summary>
    public static async Task<string?> RoleDefaultForRoleAsync(
        YfDbContext db, ulong roleId, string domain, CancellationToken ct) =>
        await db.RoleDataScopes.AsNoTracking()
            .Where(setting => setting.RoleId == roleId && setting.Domain == domain)
            .Select(setting => setting.Scope).SingleOrDefaultAsync(ct);

    /// <summary>
    /// The range an account would hold if it carried <paramref name="roleId"/>: its explicit override, then that
    /// role's default, then the organisation-leader fallback. Assigning a role is a range grant, so the caller is
    /// checked against this value rather than only against the role's permission codes.
    /// </summary>
    public static async Task<string> ScopeWithRoleAsync(
        YfDbContext db, ulong userId, ulong roleId, string domain, CancellationToken ct)
    {
        var overrideScope = await db.UserDataScopes.AsNoTracking()
            .Where(setting => setting.UserId == userId && setting.Domain == domain)
            .Select(setting => setting.Scope).SingleOrDefaultAsync(ct);
        if (DataScopeValues.IsValid(overrideScope)) return overrideScope!;
        var roleScope = await RoleDefaultForRoleAsync(db, roleId, domain, ct);
        if (DataScopeValues.IsValid(roleScope)) return roleScope!;
        return await LeadsAnyOrgAsync(db, userId, ct) ? DataScopeValues.Org : DataScopeValues.Self;
    }

    /// <summary>The widest active role default per account; an account may be bound to more than one role row.</summary>
    private static async Task<IReadOnlyDictionary<ulong, string>> RoleDefaultRowsAsync(
        YfDbContext db, ulong[] ids, string domain, CancellationToken ct)
    {
        var rows = await (
            from userRole in db.UserRoles
            join role in db.Roles on userRole.RoleId equals role.Id
            where Enumerable.Contains(ids, userRole.UserId) && role.Status == AccountStatuses.Active
            join setting in db.RoleDataScopes on role.Id equals setting.RoleId
            where setting.Domain == domain
            select new { userRole.UserId, setting.Scope }).ToArrayAsync(ct);
        return rows.Where(row => DataScopeValues.Rank(row.Scope) >= 0)
            .GroupBy(row => row.UserId)
            .ToDictionary(group => group.Key,
                group => group.OrderByDescending(row => DataScopeValues.Rank(row.Scope)).First().Scope);
    }

    /// <summary>The account is the leader of at least one active organisation (the ORG fallback trigger).</summary>
    public static async Task<bool> LeadsAnyOrgAsync(YfDbContext db, ulong userId, CancellationToken ct) =>
        await db.Departments.AsNoTracking()
            .AnyAsync(department => department.LeaderAccountId == userId
                && department.Status == AccountStatuses.Active, ct);

    private static HashSet<ulong> Subtree(
        IEnumerable<ulong> roots, IReadOnlySet<ulong> activeOrgs, ILookup<ulong, ulong> children)
    {
        var visited = new HashSet<ulong>();
        var pending = new Queue<ulong>(roots);
        while (pending.TryDequeue(out var id))
        {
            // A disabled organisation grants no authority over itself or its descendants.
            if (!activeOrgs.Contains(id) || !visited.Add(id)) continue;
            foreach (var child in children[id]) pending.Enqueue(child);
        }
        return visited;
    }

    /// <summary>
    /// Internal accounts whose current organisation falls inside <paramref name="orgIds"/>. Disabled accounts are
    /// included on purpose: transfers they sent or were notified about stay visible to their supervisor after they
    /// leave, which is the historical behaviour the old organisation query had.
    /// </summary>
    public static async Task<ulong[]> OrgUserIdsAsync(
        YfDbContext db, IReadOnlySet<ulong> orgIds, CancellationToken ct)
    {
        if (orgIds.Count == 0) return [];
        var ids = orgIds.ToArray();
        return await db.Users.AsNoTracking()
            .Where(user => user.UserType == UserTypes.Internal
                && user.DepartmentId != null && Enumerable.Contains(ids, user.DepartmentId.Value))
            .Select(user => user.Id).ToArrayAsync(ct);
    }

    /// <summary>An administrator may not grant a range wider than the one they hold themselves.</summary>
    public static bool CanGrant(string grantorScope, string targetScope) =>
        DataScopeValues.Rank(targetScope) >= 0
        && DataScopeValues.Rank(targetScope) <= DataScopeValues.Rank(grantorScope);
}
