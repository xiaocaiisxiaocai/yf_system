using System.Collections.Frozen;

namespace Yf.Api.Modules.Identity;

internal static class PermissionDelegationPolicy
{
    internal const string SupplierRoleName = "供应商人员";

    // Internal users cannot exercise supplier-only workflow actions, so such codes are ignored only when
    // evaluating the delegation ceiling of a role bound to supplier accounts. The single-gate flow
    // (2026-10-31) retired project:submit / project:withdraw, so no code is exempt today.
    internal static FrozenSet<string> SupplierExclusivePermissionCodes { get; } =
        FrozenSet<string>.Empty;
}
