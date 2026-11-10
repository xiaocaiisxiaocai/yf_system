using Yf.Api.Infrastructure;
using Yf.Api.Modules.Admin;
using Yf.Api.Modules.Outsource.Ipms;

namespace Yf.Api.Modules.Outsource;

/// <summary>Outsourcing list (外协清单): an isolated module for a small group of internal staff. See 外协清单契约.</summary>
public static class OutsourceModule
{
    public static IServiceCollection AddOutsourceModule(this IServiceCollection services) => services
        .AddSingleton<IIpmsClient>(provider => IpmsClientFactory.Create(provider.GetRequiredService<AppOptions>()))
        .AddSingleton<OutsourceSettingsService>()
        .AddSingleton<OutsourceItemService>();

    public static IEndpointRouteBuilder MapOutsourceModule(this IEndpointRouteBuilder endpoints)
    {
        var settings = endpoints.MapGroup("/api/v1/outsource/settings");
        settings.MapGet("/vendors", (HttpContext c, OutsourceSettingsService s, CancellationToken ct) =>
            s.VendorsAsync(AccessService.GetCurrent(c), ct));
        settings.MapPost("/vendors", (OutsourceVendorUpsert r, HttpContext c, OutsourceSettingsService s, CancellationToken ct) =>
            s.CreateVendorAsync(AccessService.GetCurrent(c), r, ct));
        settings.MapPut("/vendors/{id:long}", (ulong id, OutsourceVendorUpsert r, HttpContext c, OutsourceSettingsService s, CancellationToken ct) =>
            s.UpdateVendorAsync(AccessService.GetCurrent(c), id, r, ct));
        settings.MapPut("/vendors/{id:long}/status", (ulong id, StatusRequest r, HttpContext c, OutsourceSettingsService s, CancellationToken ct) =>
            s.SetVendorStatusAsync(AccessService.GetCurrent(c), id, r.Status, ct));
        settings.MapPost("/vendors/{id:long}/contacts", (ulong id, OutsourceContactUpsert r, HttpContext c, OutsourceSettingsService s, CancellationToken ct) =>
            s.CreateContactAsync(AccessService.GetCurrent(c), id, r, ct));
        settings.MapPut("/contacts/{id:long}", (ulong id, OutsourceContactUpsert r, HttpContext c, OutsourceSettingsService s, CancellationToken ct) =>
            s.UpdateContactAsync(AccessService.GetCurrent(c), id, r, ct));
        settings.MapPut("/contacts/{id:long}/status", (ulong id, StatusRequest r, HttpContext c, OutsourceSettingsService s, CancellationToken ct) =>
            s.SetContactStatusAsync(AccessService.GetCurrent(c), id, r.Status, ct));

        settings.MapGet("/types", (HttpContext c, OutsourceSettingsService s, CancellationToken ct) =>
            s.TypesAsync(AccessService.GetCurrent(c), ct));
        settings.MapPost("/types", (OutsourceTypeUpsert r, HttpContext c, OutsourceSettingsService s, CancellationToken ct) =>
            s.CreateTypeAsync(AccessService.GetCurrent(c), r, ct));
        settings.MapPut("/types/{id:long}", (ulong id, OutsourceTypeUpsert r, HttpContext c, OutsourceSettingsService s, CancellationToken ct) =>
            s.UpdateTypeAsync(AccessService.GetCurrent(c), id, r, ct));
        settings.MapPut("/types/{id:long}/status", (ulong id, StatusRequest r, HttpContext c, OutsourceSettingsService s, CancellationToken ct) =>
            s.SetTypeStatusAsync(AccessService.GetCurrent(c), id, r.Status, ct));
        settings.MapPut("/types/{id:long}/vendors", (ulong id, OutsourceTypeVendorsRequest r, HttpContext c, OutsourceSettingsService s, CancellationToken ct) =>
            s.SetTypeVendorsAsync(AccessService.GetCurrent(c), id, r, ct));

        settings.MapGet("/default-cc", (HttpContext c, OutsourceSettingsService s, CancellationToken ct) =>
            s.DefaultCcAsync(AccessService.GetCurrent(c), ct));
        settings.MapPut("/default-cc", (OutsourceDefaultCcRequest r, HttpContext c, OutsourceSettingsService s, CancellationToken ct) =>
            s.SetDefaultCcAsync(AccessService.GetCurrent(c), r, ct));
        settings.MapGet("/cc-user-options", (HttpContext c, OutsourceSettingsService s, CancellationToken ct) =>
            s.CcUserOptionsAsync(AccessService.GetCurrent(c), c.Request.Query["keyword"], ct));

        var items = endpoints.MapGroup("/api/v1/outsource/items");
        items.MapGet("", (HttpContext c, OutsourceItemService s, CancellationToken ct) =>
        {
            var p = QueryValues.Page(c.Request);
            return s.ListAsync(AccessService.GetCurrent(c), OutsourceItemQuery.From(c.Request), p.Page, p.Size, p.Offset, ct);
        });
        items.MapGet("/summary", (HttpContext c, OutsourceItemService s, CancellationToken ct) =>
            s.SummaryAsync(AccessService.GetCurrent(c), OutsourceItemQuery.From(c.Request).IncludeClosed, ct));
        items.MapGet("/export", async (HttpContext c, OutsourceItemService s, CancellationToken ct) =>
        {
            var export = await s.ExportAsync(AccessService.GetCurrent(c), OutsourceItemQuery.From(c.Request), ct);
            return Results.File(export.Content, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", export.FileName);
        });
        items.MapPost("/import/preview", (OutsourceImportRequest r, HttpContext c, OutsourceItemService s, CancellationToken ct) =>
            s.PreviewImportAsync(AccessService.GetCurrent(c), r, ct));
        items.MapPost("/import", (OutsourceImportRequest r, HttpContext c, OutsourceItemService s, CancellationToken ct) =>
            s.ImportAsync(AccessService.GetCurrent(c), r, ct)).Produces<AdminImportResponse>();
        items.MapPut("/{id:long}", (ulong id, OutsourceItemUpdate r, HttpContext c, OutsourceItemService s, CancellationToken ct) =>
            s.UpdateAsync(AccessService.GetCurrent(c), id, r, ct));
        items.MapPut("/batch", (OutsourceBatchUpdate r, HttpContext c, OutsourceItemService s, CancellationToken ct) =>
            s.BatchUpdateAsync(AccessService.GetCurrent(c), r, ct));
        items.MapPost("/batch-delete", (OutsourceBatchDelete r, HttpContext c, OutsourceItemService s, CancellationToken ct) =>
            s.BatchDeleteAsync(AccessService.GetCurrent(c), r, ct));
        return endpoints;
    }
}
