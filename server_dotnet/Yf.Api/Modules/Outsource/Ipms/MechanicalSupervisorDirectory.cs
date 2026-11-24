using Microsoft.EntityFrameworkCore;
using Yf.Api.Infrastructure;

namespace Yf.Api.Modules.Outsource.Ipms;

internal static class MechanicalSupervisorDirectory
{
    private sealed record Account(ulong Id, string Name, ulong? DepartmentId);
    private sealed record Unit(ulong? ParentId, ulong? LeaderId);

    /// <summary>Maps original IPMS names to current supervisors, tolerating different simplified/traditional spellings.</summary>
    internal static async Task<IReadOnlyDictionary<string, string>> ResolveAsync(
        YfDbContext db, IEnumerable<string?> owners, CancellationToken ct)
    {
        var names = owners.OfType<string>().Where(name => !string.IsNullOrWhiteSpace(name)).Distinct(StringComparer.Ordinal)
            .ToLookup(name => ChineseVariants.Simplify(name.Trim()), StringComparer.Ordinal);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (names.Count == 0) return result;

        // Name conversion needs OpenCC. Fetch the directory in batches rather than issuing a query per work order.
        var accounts = await db.Users.AsNoTracking()
            .Where(user => user.UserType == UserTypes.Internal && user.Status == AccountStatuses.Active)
            .Select(user => new Account(user.Id, user.RealName, user.DepartmentId)).ToArrayAsync(ct);
        var candidates = accounts.ToLookup(account => ChineseVariants.Simplify(account.Name.Trim()), StringComparer.Ordinal);
        var units = await db.Departments.AsNoTracking().Where(unit => unit.Status == AccountStatuses.Active)
            .ToDictionaryAsync(unit => unit.Id, unit => new Unit(unit.ParentId, unit.LeaderAccountId), ct);
        var activeNames = accounts.ToDictionary(account => account.Id, account => account.Name.Trim());

        foreach (var spelling in names)
        {
            // A shared name is usable only if every matching account reports to the very same person.
            var supervisors = candidates[spelling.Key]
                .Select(account => NearestSupervisor(account, units, activeNames)).Distinct().ToArray();
            if (supervisors.Length != 1 || supervisors[0] is not ulong supervisor) continue;
            foreach (var original in spelling) result.Add(original, activeNames[supervisor]);
        }
        return result;
    }

    private static ulong? NearestSupervisor(Account account, Dictionary<ulong, Unit> units, Dictionary<ulong, string> activeNames)
    {
        ulong? supervisor = null;
        var visited = new HashSet<ulong>();
        var next = account.DepartmentId;
        while (next is ulong id && units.TryGetValue(id, out var unit))
        {
            if (!visited.Add(id)) return null;
            // A department head may be assigned to a section below the department they lead.
            // In that case their superior must be above that department.
            if (unit.LeaderId == account.Id) supervisor = null;
            else if (supervisor is null && unit.LeaderId is ulong leader &&
                     activeNames.TryGetValue(leader, out var leaderName) && leaderName.Length != 0)
                supervisor = leader;
            next = unit.ParentId;
        }
        return supervisor;
    }
}
