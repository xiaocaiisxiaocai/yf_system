using Yf.Api.Modules.Oem.Common;
using Yf.Api.Modules.Oem.Maintenance;

namespace Yf.Api.Modules.Oem.Delivery;

internal static class OemDeliveryEndpoints
{
    /// <summary>Bearer-authenticated delivery endpoints (inside the secured OEM group).</summary>
    public static void MapSecured(RouteGroupBuilder oem)
    {
        oem.MapGet("/files/{id:long}/content", (ulong id, HttpContext c, OemDeliveryService s, CancellationToken ct) =>
            s.PreviewAsync(OemActorAccessor.Get(c), id, ct));
        oem.MapPost("/files/{id:long}/download-sessions", (ulong id, HttpContext c, OemDeliveryService s, CancellationToken ct) =>
            s.StartAsync(c, OemActorAccessor.Get(c), id, ct));
        oem.MapGet("/files/{id:long}/download-status", (ulong id, HttpContext c, OemDeliveryService s, CancellationToken ct) =>
            s.StatusAsync(OemActorAccessor.Get(c), id, ct));
        oem.MapGet("/audit-logs", (HttpContext c, OemAuditQueryService s, CancellationToken ct) =>
            s.ListAsync(OemActorAccessor.Get(c), c.Request, ct));
    }

    /// <summary>The download stream authenticates with its path-scoped grant cookie, not a bearer token.</summary>
    public static void MapCookieAuthenticated(RouteGroupBuilder oem) =>
        oem.MapGet("/files/{id:long}/download", async (ulong id, HttpContext c, OemDeliveryService s, CancellationToken ct) =>
        {
            await s.StreamAsync(c, id, ct);
            return Results.Empty;
        });
}
