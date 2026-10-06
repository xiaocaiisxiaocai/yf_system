using Yf.Api.Infrastructure;
using Yf.Api.Modules.Oem.Approval;
using Yf.Api.Modules.Oem.Common;

namespace Yf.Api.Modules.Oem.Policies;

internal static class OemPolicyEndpoints
{
    public static void Map(RouteGroupBuilder oem)
    {
        oem.MapGet("/retention-templates", (HttpContext c, OemRetentionTemplateService s, CancellationToken ct) =>
            s.ListAsync(OemActorAccessor.Get(c), ct)).Produces<IReadOnlyList<OemRetentionTemplateResponse>>();
        oem.MapGet("/retention-template-options", (HttpContext c, OemRetentionTemplateService s, CancellationToken ct) =>
            s.OptionsAsync(OemActorAccessor.Get(c), ct)).Produces<IReadOnlyList<OemRetentionTemplateResponse>>();
        oem.MapPost("/retention-templates", (RetentionTemplateCreate r, HttpContext c, OemRetentionTemplateService s, CancellationToken ct) =>
            s.CreateAsync(OemActorAccessor.Get(c), r, ct)).Produces<OemRetentionTemplateResponse>();
        oem.MapPut("/retention-templates/{id:long}", (ulong id, RetentionTemplateUpdate r, HttpContext c, OemRetentionTemplateService s, CancellationToken ct) =>
            s.UpdateAsync(OemActorAccessor.Get(c), id, r, ct)).Produces<OemRetentionTemplateResponse>();

        oem.MapGet("/file-policies", (HttpContext c, OemSettingsService s, CancellationToken ct) =>
            s.GetAsync(OemActorAccessor.Get(c), OemSettingGroup.File, ct)).Produces<IReadOnlyList<OemSettingResponse>>();
        oem.MapPut("/file-policies", (OemSettingsUpdate r, HttpContext c, OemSettingsService s, CancellationToken ct) =>
            s.UpdateAsync(OemActorAccessor.Get(c), OemSettingGroup.File, r, ct)).Produces<IReadOnlyList<OemSettingResponse>>();
        oem.MapGet("/notify-policies", (HttpContext c, OemSettingsService s, CancellationToken ct) =>
            s.GetAsync(OemActorAccessor.Get(c), OemSettingGroup.Notify, ct)).Produces<IReadOnlyList<OemSettingResponse>>();
        oem.MapPut("/notify-policies", (OemSettingsUpdate r, HttpContext c, OemSettingsService s, CancellationToken ct) =>
            s.UpdateAsync(OemActorAccessor.Get(c), OemSettingGroup.Notify, r, ct)).Produces<IReadOnlyList<OemSettingResponse>>();

        oem.MapGet("/flow-templates", (HttpContext c, OemFlowTemplateService s, CancellationToken ct) =>
            s.ListAsync(OemActorAccessor.Get(c), ct)).Produces<IReadOnlyList<OemFlowTemplateResponse>>();
        oem.MapPost("/flow-templates", (FlowTemplateCreate r, HttpContext c, OemFlowTemplateService s, CancellationToken ct) =>
            s.CreateAsync(OemActorAccessor.Get(c), r, ct)).Produces<OemFlowTemplateResponse>();
        oem.MapGet("/flow-templates/preview", (HttpContext c, OemFlowTemplateService s, CancellationToken ct) =>
            s.PreviewAsync(OemActorAccessor.Get(c), QueryValues.OptionalUInt64(c.Request, "userId") ?? throw ApiException.BadRequest("请选择员工"), ct))
            .Produces<OemRoutingPreviewResponse>();
        oem.MapGet("/flow-templates/{id:long}", (ulong id, HttpContext c, OemFlowTemplateService s, CancellationToken ct) =>
            s.DetailAsync(OemActorAccessor.Get(c), id, ct)).Produces<OemFlowTemplateResponse>();
        oem.MapPut("/flow-templates/{id:long}", (ulong id, FlowTemplateUpdate r, HttpContext c, OemFlowTemplateService s, CancellationToken ct) =>
            s.UpdateAsync(OemActorAccessor.Get(c), id, r, ct)).Produces<OemFlowTemplateResponse>();
        oem.MapPut("/flow-templates/{id:long}/definition", (ulong id, FlowTemplateDefinitionUpdate r, HttpContext c, OemFlowTemplateService s, CancellationToken ct) =>
            s.UpdateDefinitionAsync(OemActorAccessor.Get(c), id, r, ct)).Produces<OemFlowTemplateResponse>();
        oem.MapPut("/flow-templates/{id:long}/nodes", (ulong id, FlowTemplateNodesUpdate r, HttpContext c, OemFlowTemplateService s, CancellationToken ct) =>
            s.ReplaceNodesAsync(OemActorAccessor.Get(c), id, r, ct)).Produces<OemFlowTemplateResponse>();
        oem.MapPut("/flow-templates/{id:long}/scopes", (ulong id, FlowTemplateScopesUpdate r, HttpContext c, OemFlowTemplateService s, CancellationToken ct) =>
            s.ReplaceScopesAsync(OemActorAccessor.Get(c), id, r, ct)).Produces<OemFlowTemplateResponse>();
        oem.MapGet("/internal-user-options", (HttpContext c, OemFlowTemplateService s, CancellationToken ct) =>
            s.InternalUserOptionsAsync(OemActorAccessor.Get(c), c.Request.Query["keyword"], ct))
            .Produces<IReadOnlyList<OemInternalUserOptionResponse>>();
        oem.MapGet("/approver-options", (HttpContext c, OemFlowTemplateService s, CancellationToken ct) =>
            s.ApproverOptionsAsync(OemActorAccessor.Get(c), c.Request.Query["keyword"], ct))
            .Produces<IReadOnlyList<OemFlowPersonResponse>>();
    }
}
