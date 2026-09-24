using System.Collections.Frozen;

namespace Yf.Api.Modules.Identity;

internal static class PermissionDelegationPolicy
{
    internal const string SupplierRoleName = "供应商人员";

    // Internal users cannot exercise these supplier-only workflow actions, so they are ignored only
    // when evaluating the delegation ceiling of a role that is actually bound to supplier accounts.
    internal static FrozenSet<string> SupplierExclusivePermissionCodes { get; } = new[]
    {
        "project:submit", "project:withdraw"
    }.ToFrozenSet(StringComparer.Ordinal);
}
