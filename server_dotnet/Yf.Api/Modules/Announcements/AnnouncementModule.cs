using Yf.Api.Infrastructure;
using Yf.Api.Modules.Oem.Common;
namespace Yf.Api.Modules.Announcements;

public static class AnnouncementModule
{
    public static IServiceCollection AddAnnouncementModule(this IServiceCollection services) =>
        services.AddSingleton<AnnouncementManagementService>().AddSingleton<AnnouncementReaderService>()
            .AddSingleton<AnnouncementAssetStore>()
            .AddSingleton<IOemBackgroundJob>(provider => new DelegateOemJob("announcement-asset-cleanup", TimeSpan.FromHours(1),
                ct => provider.GetRequiredService<AnnouncementAssetStore>().CleanupAsync(ct)));

    public static IEndpointRouteBuilder MapAnnouncementModule(this IEndpointRouteBuilder endpoints)
    {
        var root = endpoints.MapGroup("/api/v1/announcements");
        MapReader(root, false);
        MapReader(endpoints.MapGroup(OemApi.Prefix + "/announcements").AddEndpointFilter<OemAccessFilter>(), true);
        var management = root.MapGroup("/manage");
        AnnouncementAssetEndpoints.MapManagement(management);
        management.MapAnnouncementOrdering();
        management.MapGet("", (HttpContext c, AnnouncementManagementService s, string? keyword, string? status,
            int? page, int? pageSize, CancellationToken ct) => s.ListAsync(AccessService.GetCurrent(c), keyword, status, page, pageSize, ct));
        management.MapGet("/options", (HttpContext c, AnnouncementManagementService s, CancellationToken ct) =>
            s.OptionsAsync(AccessService.GetCurrent(c), ct));
        management.MapGet("/{id:long}", (ulong id, HttpContext c, AnnouncementManagementService s, CancellationToken ct) =>
            s.DetailAsync(AccessService.GetCurrent(c), id, ct));
        management.MapPost("", (AnnouncementDraftRequest r, HttpContext c, AnnouncementManagementService s, CancellationToken ct) =>
            s.SaveAsync(AccessService.GetCurrent(c), null, r, ct));
        management.MapPut("/{id:long}", (ulong id, AnnouncementDraftRequest r, HttpContext c, AnnouncementManagementService s, CancellationToken ct) =>
            s.SaveAsync(AccessService.GetCurrent(c), id, r, ct));
        management.MapPost("/{id:long}/publish", (ulong id, ulong? version, HttpContext c, AnnouncementManagementService s, CancellationToken ct) =>
            s.PublishAsync(AccessService.GetCurrent(c), id, version, ct));
        management.MapPost("/{id:long}/withdraw", (ulong id, HttpContext c, AnnouncementManagementService s, CancellationToken ct) =>
            s.WithdrawAsync(AccessService.GetCurrent(c), id, ct));
        management.MapPost("/{id:long}/pin", (ulong id, HttpContext c, AnnouncementManagementService s, CancellationToken ct) =>
            s.PinAsync(AccessService.GetCurrent(c), id, true, ct));
        management.MapPost("/{id:long}/unpin", (ulong id, HttpContext c, AnnouncementManagementService s, CancellationToken ct) =>
            s.PinAsync(AccessService.GetCurrent(c), id, false, ct));
        management.MapGet("/{id:long}/reads", (ulong id, HttpContext c, AnnouncementManagementService s, int? page,
            int? pageSize, CancellationToken ct) => s.ReadsAsync(AccessService.GetCurrent(c), id, page, pageSize, ct));
        return endpoints;
    }

    private static void MapReader(RouteGroupBuilder group, bool portal)
    {
        OemActor Actor(HttpContext context)
        {
            if (!portal) return OemActorAccessor.GetInternal(context);
            return OemActorAccessor.Get(context) as OemAccountActor ?? throw ApiException.Forbidden();
        }
        group.MapGet("", (HttpContext c, AnnouncementReaderService s, string? keyword, bool? unreadOnly, int? page,
            int? pageSize, CancellationToken ct) => s.ListAsync(Actor(c), keyword, unreadOnly, page, pageSize, ct));
        AnnouncementAssetEndpoints.MapReader(group, Actor);
        group.MapGet("/{id:long}", (ulong id, HttpContext c, AnnouncementReaderService s, CancellationToken ct) =>
            s.DetailAsync(Actor(c), id, ct));
        group.MapPost("/{id:long}/read", (ulong id, HttpContext c, AnnouncementReaderService s, CancellationToken ct) =>
            s.ReadAsync(Actor(c), id, ct));
    }
}
