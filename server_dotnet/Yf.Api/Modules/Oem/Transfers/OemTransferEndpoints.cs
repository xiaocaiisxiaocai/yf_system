using Yf.Api.Infrastructure;
using Yf.Api.Modules.Oem.Approval;
using Yf.Api.Modules.Oem.Common;
using Yf.Api.Modules.Oem.Uploads;

namespace Yf.Api.Modules.Oem.Transfers;

internal static class OemTransferEndpoints
{
    public static void Map(RouteGroupBuilder oem)
    {
        oem.MapGet("/transfers", (HttpContext c, OemTransferReader r, CancellationToken ct) => r.ListAsync(OemActorAccessor.Get(c), c.Request, ct));
        oem.MapPost("/transfers", (TransferCreate body, HttpContext c, OemTransferService s, CancellationToken ct) =>
            s.CreateAsync(OemActorAccessor.Get(c), body, ct));
        oem.MapGet("/transfers/{id:long}", (ulong id, HttpContext c, OemTransferReader r, CancellationToken ct) =>
            r.DetailAsync(OemActorAccessor.Get(c), id, ct));
        oem.MapPut("/transfers/{id:long}", (ulong id, TransferUpdate body, HttpContext c, OemTransferService s, CancellationToken ct) =>
            s.UpdateAsync(OemActorAccessor.Get(c), id, body, ct));
        oem.MapDelete("/transfers/{id:long}", async (ulong id, HttpContext c, OemTransferService s, CancellationToken ct) =>
        {
            await s.DeleteAsync(OemActorAccessor.Get(c), id, QueryValues.OptionalUInt64(c.Request, "version"), ct);
            return Results.Json(new { });
        });
        oem.MapPost("/transfers/{id:long}/send", (ulong id, TransferVersionRequest body, HttpContext c, OemTransferService s, CancellationToken ct) =>
            s.SendAsync(OemActorAccessor.Get(c), id, body.Version, ct));
        oem.MapPost("/transfers/{id:long}/cancel", (ulong id, CancelTransferRequest body, HttpContext c, OemApprovalService s, CancellationToken ct) =>
            s.CancelAsync(OemActorAccessor.Get(c), id, body, ct));

        oem.MapPost("/transfers/{id:long}/uploads/init", (ulong id, OemUploadInit body, HttpContext c, OemUploadService s, CancellationToken ct) =>
            s.InitAsync(OemActorAccessor.Get(c), id, body, ct));
        oem.MapGet("/uploads/{sessionId}", (string sessionId, HttpContext c, OemUploadService s, CancellationToken ct) =>
            s.GetAsync(OemActorAccessor.Get(c), sessionId, ct));
        oem.MapPut("/uploads/{sessionId}/chunks/{index:int}", async (string sessionId, int index, HttpContext c, OemUploadService s, CancellationToken ct) =>
        {
            await s.PutChunkAsync(OemActorAccessor.Get(c), sessionId, index, c.Request, ct);
            return Results.Json(new { });
        }).DisableAntiforgery();
        oem.MapPost("/uploads/{sessionId}/merge", (string sessionId, HttpContext c, OemUploadService s, CancellationToken ct) =>
            s.MergeAsync(OemActorAccessor.Get(c), sessionId, ct));
        oem.MapDelete("/uploads/{sessionId}", async (string sessionId, HttpContext c, OemUploadService s, CancellationToken ct) =>
        {
            await s.AbortAsync(OemActorAccessor.Get(c), sessionId, ct);
            return Results.Json(new { });
        });

        oem.MapGet("/files/{id:long}/scan-status", (ulong id, HttpContext c, OemTransferReader r, CancellationToken ct) =>
            r.FileScanStatusAsync(OemActorAccessor.Get(c), id, ct));
        oem.MapDelete("/files/{id:long}", async (ulong id, HttpContext c, OemTransferService s, CancellationToken ct) =>
        {
            await s.RemoveFileAsync(OemActorAccessor.Get(c), id, ct);
            return Results.Json(new { });
        });

        oem.MapGet("/approvals/pending", (HttpContext c, OemApprovalService s, CancellationToken ct) => s.PendingAsync(OemActorAccessor.Get(c), ct));
        oem.MapPost("/approvals/{taskId:long}/approve", (ulong taskId, ApprovalDecisionRequest body, HttpContext c, OemApprovalService s, CancellationToken ct) =>
            s.ApproveAsync(OemActorAccessor.Get(c), taskId, body, ct));
        oem.MapPost("/approvals/{taskId:long}/reject", (ulong taskId, ApprovalDecisionRequest body, HttpContext c, OemApprovalService s, CancellationToken ct) =>
            s.RejectAsync(OemActorAccessor.Get(c), taskId, body, ct));
        oem.MapPost("/approvals/{taskId:long}/reassign", (ulong taskId, ReassignTaskRequest body, HttpContext c, OemApprovalService s, CancellationToken ct) =>
            s.ReassignAsync(OemActorAccessor.Get(c), taskId, body, ct));
    }
}
