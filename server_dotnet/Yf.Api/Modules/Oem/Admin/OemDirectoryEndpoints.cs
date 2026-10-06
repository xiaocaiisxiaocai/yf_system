using Yf.Api.Infrastructure;
using Yf.Api.Modules.Oem.Common;

namespace Yf.Api.Modules.Oem.Admin;

internal static class OemDirectoryEndpoints
{
    public static void Map(RouteGroupBuilder oem)
    {
        oem.MapGet("/companies", (HttpContext c, OemDirectoryService s, CancellationToken ct) =>
        {
            var p = QueryValues.Page(c.Request);
            return s.ListCompaniesAsync(OemActorAccessor.Get(c), p.Page, p.Size, p.Offset, c.Request.Query["keyword"], c.Request.Query["status"], ct);
        }).Produces<OemPageResponse<OemCompanyListItemResponse>>();
        oem.MapGet("/company-options", (HttpContext c, OemDirectoryService s, CancellationToken ct) =>
            s.CompanyOptionsAsync(OemActorAccessor.Get(c), ct)).Produces<IReadOnlyList<OemOptionResponse>>();
        oem.MapPost("/companies", (OemCompanyUpsert r, HttpContext c, OemDirectoryService s, CancellationToken ct) =>
            s.CreateCompanyAsync(OemActorAccessor.Get(c), r, ct)).Produces<OemCompanyResponse>();
        oem.MapGet("/companies/{id:long}", (ulong id, HttpContext c, OemDirectoryService s, CancellationToken ct) =>
            s.CompanyDetailAsync(OemActorAccessor.Get(c), id, ct)).Produces<OemCompanyResponse>();
        oem.MapPut("/companies/{id:long}", (ulong id, OemCompanyUpsert r, HttpContext c, OemDirectoryService s, CancellationToken ct) =>
            s.UpdateCompanyAsync(OemActorAccessor.Get(c), id, r, ct)).Produces<OemCompanyResponse>();
        oem.MapPut("/companies/{id:long}/status", (ulong id, OemStatusRequest r, HttpContext c, OemDirectoryService s, CancellationToken ct) =>
            s.SetCompanyStatusAsync(OemActorAccessor.Get(c), id, r.Status, ct)).Produces<OemCompanyResponse>();
        oem.MapDelete("/companies/{id:long}", async (ulong id, HttpContext c, OemDirectoryService s, CancellationToken ct) =>
        {
            await s.DeleteCompanyAsync(OemActorAccessor.Get(c), id, ct);
            return EmptyResponse.Instance;
        }).Produces<EmptyResponse>();

        oem.MapGet("/companies/{id:long}/accounts", (ulong id, HttpContext c, OemDirectoryService s, CancellationToken ct) =>
            s.ListAccountsAsync(OemActorAccessor.Get(c), id, ct)).Produces<IReadOnlyList<OemAccountResponse>>();
        oem.MapPost("/companies/{id:long}/accounts", (ulong id, OemAccountCreate r, HttpContext c, OemDirectoryService s, CancellationToken ct) =>
            s.CreateAccountAsync(OemActorAccessor.Get(c), id, r, ct)).Produces<OemAccountResponse>();
        oem.MapGet("/accounts/{id:long}", (ulong id, HttpContext c, OemDirectoryService s, CancellationToken ct) =>
            s.AccountDetailAsync(OemActorAccessor.Get(c), id, ct)).Produces<OemAccountResponse>();
        oem.MapPut("/accounts/{id:long}", (ulong id, OemAccountUpdate r, HttpContext c, OemDirectoryService s, CancellationToken ct) =>
            s.UpdateAccountAsync(OemActorAccessor.Get(c), id, r, ct)).Produces<OemAccountResponse>();
        oem.MapPut("/accounts/{id:long}/status", (ulong id, OemStatusRequest r, HttpContext c, OemDirectoryService s, CancellationToken ct) =>
            s.SetAccountStatusAsync(OemActorAccessor.Get(c), id, r.Status, ct)).Produces<OemAccountResponse>();
        oem.MapPut("/accounts/{id:long}/password", async (ulong id, OemPasswordReset r, HttpContext c, OemDirectoryService s, CancellationToken ct) =>
        {
            await s.ResetAccountPasswordAsync(OemActorAccessor.Get(c), id, r, ct);
            return EmptyResponse.Instance;
        }).Produces<EmptyResponse>();
        oem.MapDelete("/accounts/{id:long}", async (ulong id, HttpContext c, OemDirectoryService s, CancellationToken ct) =>
        {
            await s.DeleteAccountAsync(OemActorAccessor.Get(c), id, ct);
            return EmptyResponse.Instance;
        }).Produces<EmptyResponse>();
    }
}
