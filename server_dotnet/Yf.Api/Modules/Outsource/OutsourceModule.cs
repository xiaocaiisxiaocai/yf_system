using Yf.Api.Infrastructure;
using Yf.Api.Modules.Admin;
using Yf.Api.Modules.Outsource.Ipms;

namespace Yf.Api.Modules.Outsource;

/// <summary>Outsourcing list (外协清单): an isolated module for a small group of internal staff. See 外协清单契约.</summary>
public static class OutsourceModule
{
    public static IServiceCollection AddOutsourceModule(this IServiceCollection services) => services
        .AddSingleton<IIpmsClient>(provider => IpmsClientFactory.Create(provider.GetRequiredService<AppOptions>()))
        .AddSingleton<OutsourceSettingsService>();

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
        return endpoints;
    }
}
