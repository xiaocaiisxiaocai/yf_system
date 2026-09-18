using Yf.Api.Infrastructure;
using Yf.Api.Modules.Identity;
using Yf.Api.Modules.Oem.Admin;
using Yf.Api.Modules.Oem.Approval;
using Yf.Api.Modules.Oem.Common;
using Yf.Api.Modules.Oem.Delivery;
using Yf.Api.Modules.Oem.Identity;
using Yf.Api.Modules.Oem.Maintenance;
using Yf.Api.Modules.Oem.Notifications;
using Yf.Api.Modules.Oem.Policies;
using Yf.Api.Modules.Oem.Scanning;
using Yf.Api.Modules.Oem.Storage;
using Yf.Api.Modules.Oem.Transfers;
using Yf.Api.Modules.Oem.Uploads;

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
        services.AddSingleton<OemApprovalEngine>();
        services.AddSingleton<OemApprovalService>();

        // Transfer slice: drafts, sending, progression (process manager) and read model.
        services.AddSingleton<OemEventDispatcher>();

        // Notification slice: domain events become queued mail; the shared mail worker asks
        // the OEM policy (not the collaboration switches) whether each OEM mail may still go out.
        services.AddSingleton<IOemEventHandler, OemNotificationHandler>();
        services.AddSingleton<Yf.Api.Modules.SystemManagement.IOutboxRecipientPolicy, OemOutboxPolicy>();
        services.AddSingleton<OemTransferProgression>();
        services.AddSingleton<OemTransferService>();
        services.AddSingleton<OemTransferReader>();

        // Storage, upload and scanning slices.
        services.AddSingleton<OemStorage>();
        services.AddSingleton<OemUploadService>();
        services.AddSingleton<IFileScanner>(provider => FileScannerFactory.Create(provider.GetRequiredService<AppOptions>()));
        services.AddSingleton<OemScanPipeline>();
        services.AddSingleton<OemPromotionService>();
        services.AddSingleton<OemScanService>();

        // Delivery, purge and reconciliation slices.
        services.AddSingleton<OemDownloadGrantService>();
        services.AddSingleton<OemDownloadRateLimiter>();
        services.AddSingleton<OemDeliveryService>();
        services.AddSingleton<OemPurgeService>();
        services.AddSingleton<OemReconcileService>();
        services.AddSingleton<OemAuditQueryService>();

        // Background duties, driven by one composite hosted worker.
        services.AddSingleton<IOemBackgroundJob>(provider => new DelegateOemJob("scan", TimeSpan.FromSeconds(3),
            ct => provider.GetRequiredService<OemScanService>().RunOnceAsync(ct)));
        services.AddSingleton<IOemBackgroundJob>(provider => new DelegateOemJob("promote", TimeSpan.FromSeconds(5),
            ct => provider.GetRequiredService<OemPromotionService>().RunOnceAsync(ct)));
        services.AddSingleton<IOemBackgroundJob>(provider => new DelegateOemJob("upload-expiry", TimeSpan.FromMinutes(5),
            ct => provider.GetRequiredService<OemUploadService>().ExpireStaleSessionsAsync(ct)));
        services.AddSingleton<IOemBackgroundJob>(provider => new DelegateOemJob("approval-health", TimeSpan.FromMinutes(2),
            ct => provider.GetRequiredService<OemApprovalService>().RevalidateActiveInstancesAsync(ct)));
        services.AddSingleton<IOemBackgroundJob>(provider => new DelegateOemJob("purge", TimeSpan.FromSeconds(30),
            ct => provider.GetRequiredService<OemPurgeService>().RunOnceAsync(ct)));
        services.AddSingleton<IOemBackgroundJob>(provider => new DelegateOemJob("draft-expiry", TimeSpan.FromHours(1),
            ct => provider.GetRequiredService<OemPurgeService>().ExpireDraftsAsync(provider.GetRequiredService<OemTransferService>(), ct)));
        services.AddSingleton<IOemBackgroundJob>(provider => new DelegateOemJob("reconcile", TimeSpan.FromMinutes(10),
            ct => provider.GetRequiredService<OemReconcileService>().RunOnceAsync(ct)));
        services.AddHostedService<OemBackgroundWorker>();
        return services;
    }

    public static IEndpointRouteBuilder MapOemModule(this IEndpointRouteBuilder endpoints)
    {
        var root = endpoints.MapGroup(OemApi.Prefix);
        OemIdentityEndpoints.Map(root);
        OemDeliveryEndpoints.MapCookieAuthenticated(root);

        // Everything except the auth group requires a resolved OEM actor (internal staff or OEM account).
        var secured = root.MapGroup(string.Empty).AddEndpointFilter<OemAccessFilter>();
        OemDirectoryEndpoints.Map(secured);
        OemPolicyEndpoints.Map(secured);
        OemTransferEndpoints.Map(secured);
        OemDeliveryEndpoints.MapSecured(secured);
        return endpoints;
    }
}
