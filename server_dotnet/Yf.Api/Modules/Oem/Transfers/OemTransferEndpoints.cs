using Yf.Api.Infrastructure;
using Yf.Api.Modules.Oem.Approval;
using Yf.Api.Modules.Oem.Common;
using Yf.Api.Modules.Oem.Uploads;

namespace Yf.Api.Modules.Oem.Transfers;

internal static class OemTransferEndpoints
{
    public static void Map(RouteGroupBuilder oem)
    {
        oem.MapGet("/transfers", (HttpContext c, OemTransferReader r, CancellationToken ct) => r.ListAsync(OemActorAccessor.Get(c), c.Request, ct))
            .Produces<OemPageResponse<OemTransferSummaryResponse>>();
        oem.MapPost("/transfers", (TransferCreate body, HttpContext c, OemTransferService s, CancellationToken ct) =>
            s.CreateAsync(OemActorAccessor.Get(c), body, ct)).Produces<OemTransferDetailResponse>();
        oem.MapGet("/transfers/{id:long}", (ulong id, HttpContext c, OemTransferReader r, CancellationToken ct) =>
            r.DetailAsync(OemActorAccessor.Get(c), id, ct)).Produces<OemTransferDetailResponse>();
        oem.MapPut("/transfers/{id:long}", (ulong id, TransferUpdate body, HttpContext c, OemTransferService s, CancellationToken ct) =>
            s.UpdateAsync(OemActorAccessor.Get(c), id, body, ct)).Produces<OemTransferDetailResponse>();
        oem.MapDelete("/transfers/{id:long}", async (ulong id, HttpContext c, OemTransferService s, CancellationToken ct) =>
        {
            await s.DeleteAsync(OemActorAccessor.Get(c), id, QueryValues.OptionalUInt64(c.Request, "version"), ct);
            return EmptyResponse.Instance;
        }).Produces<EmptyResponse>();
        oem.MapPost("/transfers/{id:long}/send", (ulong id, TransferVersionRequest body, HttpContext c, OemTransferService s, CancellationToken ct) =>
            s.SendAsync(OemActorAccessor.Get(c), id, body.Version, ct)).Produces<OemTransferDetailResponse>();
        oem.MapPost("/transfers/{id:long}/cancel", (ulong id, CancelTransferRequest body, HttpContext c, OemApprovalService s, CancellationToken ct) =>
            s.CancelAsync(OemActorAccessor.Get(c), id, body, ct)).Produces<OemTransferDetailResponse>();

        oem.MapPost("/transfers/{id:long}/uploads/init", (ulong id, OemUploadInit body, HttpContext c, OemUploadService s, CancellationToken ct) =>
            s.InitAsync(OemActorAccessor.Get(c), id, body, ct)).Produces<OemUploadSessionInitResponse>();
        oem.MapGet("/uploads/{sessionId}", (string sessionId, HttpContext c, OemUploadService s, CancellationToken ct) =>
            s.GetAsync(OemActorAccessor.Get(c), sessionId, ct)).Produces<OemUploadSessionResponse>();
        oem.MapPut("/uploads/{sessionId}/chunks/{index:int}", async (string sessionId, int index, HttpContext c, OemUploadService s, CancellationToken ct) =>
        {
            await s.PutChunkAsync(OemActorAccessor.Get(c), sessionId, index, c.Request, ct);
            return EmptyResponse.Instance;
        }).DisableAntiforgery().Produces<EmptyResponse>();
        oem.MapPost("/uploads/{sessionId}/merge", (string sessionId, HttpContext c, OemUploadService s, CancellationToken ct) =>
            s.MergeAsync(OemActorAccessor.Get(c), sessionId, ct)).Produces<OemUploadedFileResponse>();
        oem.MapDelete("/uploads/{sessionId}", async (string sessionId, HttpContext c, OemUploadService s, CancellationToken ct) =>
        {
            await s.AbortAsync(OemActorAccessor.Get(c), sessionId, ct);
            return EmptyResponse.Instance;
        }).Produces<EmptyResponse>();

        oem.MapGet("/files/{id:long}/validation-status", (ulong id, HttpContext c, OemTransferReader r, CancellationToken ct) =>
            r.FileValidationStatusAsync(OemActorAccessor.Get(c), id, ct)).Produces<OemFileValidationStatusResponse>();
        oem.MapDelete("/files/{id:long}", async (ulong id, HttpContext c, OemTransferService s, CancellationToken ct) =>
        {
            await s.RemoveFileAsync(OemActorAccessor.Get(c), id, ct);
            return EmptyResponse.Instance;
        }).Produces<EmptyResponse>();

        oem.MapGet("/approvals/pending", (HttpContext c, OemApprovalService s, CancellationToken ct) => s.PendingAsync(OemActorAccessor.Get(c), ct))
            .Produces<IReadOnlyList<OemPendingApprovalResponse>>();
        oem.MapPost("/approvals/{taskId:long}/approve", (ulong taskId, ApprovalDecisionRequest body, HttpContext c, OemApprovalService s, CancellationToken ct) =>
            s.ApproveAsync(OemActorAccessor.Get(c), taskId, body, ct)).Produces<OemTransferDetailResponse>();
        oem.MapPost("/approvals/{taskId:long}/reject", (ulong taskId, ApprovalDecisionRequest body, HttpContext c, OemApprovalService s, CancellationToken ct) =>
            s.RejectAsync(OemActorAccessor.Get(c), taskId, body, ct)).Produces<OemTransferDetailResponse>();
        oem.MapPost("/approvals/{taskId:long}/reassign", (ulong taskId, ReassignTaskRequest body, HttpContext c, OemApprovalService s, CancellationToken ct) =>
            s.ReassignAsync(OemActorAccessor.Get(c), taskId, body, ct)).Produces<OemTransferDetailResponse>();
    }
}
