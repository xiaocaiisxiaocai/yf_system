using Yf.Api.Modules.Identity;
using Yf.Api.Modules.Oem.Admin;
using Yf.Api.Modules.Oem.Approval;
using Yf.Api.Modules.Oem.Common;
using Yf.Api.Modules.Oem.Identity;
using Yf.Api.Modules.Oem.Policies;

namespace Yf.Api.Modules.Oem;

/// <summary>
/// Composition root of the OEM business line. Each feature slice (identity, directory,
/// policies, approval, transfers, uploads, scanning, delivery, purge, reconcile,
/// notifications) registers its own services and maps its own endpoints here; the
/// rest of the application only calls <see cref="AddOemModule"/> and <see cref="MapOemModule"/>.
/// </summary>
public static class OemModule
{
    public static IServiceCollection AddOemModule(this IServiceCollection services)
    {
        services.AddSingleton<OemAuditWriter>();

        // Identity slice: the realm plugs into the shared authentication middleware.
        services.AddSingleton<OemIdentityExtension>();
        services.AddSingleton<IRealmIdentityExtension>(provider => provider.GetRequiredService<OemIdentityExtension>());
        services.AddSingleton<OemAuthService>();

        // Directory slice: vendors and vendor accounts.
        services.AddSingleton<OemDirectoryService>();

        // Policy slice: retention templates and the oem.* parameter pages.
        services.AddSingleton<OemRetentionTemplateService>();
        services.AddSingleton<OemSettingsService>();

        // Approval slice: template administration and planning.
        services.AddSingleton<ApprovalPlanningService>();
        services.AddSingleton<OemFlowTemplateService>();
        return services;
    }

    public static IEndpointRouteBuilder MapOemModule(this IEndpointRouteBuilder endpoints)
    {
        var root = endpoints.MapGroup(OemApi.Prefix);
        OemIdentityEndpoints.Map(root);

        // Everything except the auth group requires a resolved OEM actor (internal staff or OEM account).
        var secured = root.MapGroup(string.Empty).AddEndpointFilter<OemAccessFilter>();
        OemDirectoryEndpoints.Map(secured);
        OemPolicyEndpoints.Map(secured);
        return endpoints;
    }
}
