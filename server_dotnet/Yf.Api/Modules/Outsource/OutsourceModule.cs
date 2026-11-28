using Microsoft.Extensions.Primitives;
using Yf.Api.Infrastructure;
using Yf.Api.Modules.Admin;
using Yf.Api.Modules.Outsource.Ipms;
using Yf.Api.Modules.Outsource.Ipms.Mes;

namespace Yf.Api.Modules.Outsource;

/// <summary>Outsourcing list (外协清单): an isolated module for a small group of internal staff. See 外协清单契约.</summary>
public static class OutsourceModule
{
    public static IServiceCollection AddOutsourceModule(this IServiceCollection services) => services
        .AddSingleton<DbMesSettings>()
        .AddSingleton<IMesSettings>(provider => provider.GetRequiredService<DbMesSettings>())
        .AddSingleton<IIpmsClient>(provider => IpmsClientFactory.Create(provider.GetRequiredService<AppOptions>(), provider))
        .AddSingleton<IMesSource>(provider => MesSourceFactory.Create(provider.GetRequiredService<AppOptions>(), provider))
        .AddSingleton<MesMirrorSyncService>()
        .AddSingleton<IHostedService>(provider => provider.GetRequiredService<MesMirrorSyncService>())
        .AddSingleton<OutsourceSettingsService>()
        .AddSingleton<OutsourceVendorViewService>()
        .AddSingleton<OutsourceFieldService>()
        .AddSingleton<OutsourceItemService>()
        .AddSingleton<OutsourceContactService>()
        .AddSingleton<OutsourceContactVendorService>()
        .AddSingleton<OutsourceVendorEmailService>()
        .AddSingleton<OutsourceDateImportService>()
        .AddSingleton<OutsourceIpmsSyncService>()
        .AddSingleton<IHostedService>(provider => provider.GetRequiredService<OutsourceIpmsSyncService>())
        .AddSingleton<OutsourceMailSignal>()
        .AddSingleton<IOutsourceMailTransport, MailKitOutsourceTransport>()
        .AddSingleton<OutsourceMailService>()
        .AddSingleton<OutsourceMailWorker>()
        .AddSingleton<IHostedService>(provider => provider.GetRequiredService<OutsourceMailWorker>());

    public static IEndpointRouteBuilder MapOutsourceModule(this IEndpointRouteBuilder endpoints)
    {
        var settings = endpoints.MapGroup("/api/v1/outsource/settings");
        settings.MapGet("/vendors", (HttpContext c, OutsourceSettingsService s, CancellationToken ct) =>
            s.VendorsAsync(AccessService.GetCurrent(c), ct));

        // Each vendor's outsourcing mail addresses (外协清单契约 → 外协邮箱); vendors are the ODM/OEMA companies.
        settings.MapGet("/vendor-emails", (HttpContext c, OutsourceVendorEmailService s, CancellationToken ct) =>
            s.ListAsync(AccessService.GetCurrent(c), ct));
        settings.MapPost("/vendor-emails", (OutsourceVendorEmailCreate r, HttpContext c, OutsourceVendorEmailService s, CancellationToken ct) =>
            s.CreateAsync(AccessService.GetCurrent(c), r, ct));
        settings.MapPost("/vendor-emails/import", (OutsourceVendorEmailImportRequest r, HttpContext c, OutsourceVendorEmailService s, CancellationToken ct) =>
            s.ImportAsync(AccessService.GetCurrent(c), r, ct)).Produces<AdminImportResponse>();
        settings.MapPut("/vendor-emails/{id:long}", (ulong id, OutsourceVendorEmailUpdate r, HttpContext c, OutsourceVendorEmailService s, CancellationToken ct) =>
            s.UpdateAsync(AccessService.GetCurrent(c), id, r, ct));
        settings.MapDelete("/vendor-emails/{id:long}", (ulong id, HttpContext c, OutsourceVendorEmailService s, CancellationToken ct) =>
            s.DeleteAsync(AccessService.GetCurrent(c), id, ct));
        settings.MapGet("/customers", (HttpContext c, OutsourceSettingsService s, CancellationToken ct) =>
            s.CustomerAliasesAsync(AccessService.GetCurrent(c), ct));
        settings.MapGet("/customers/missing", (HttpContext c, OutsourceSettingsService s, CancellationToken ct) =>
            s.MissingCustomerAliasesAsync(AccessService.GetCurrent(c), ct));
        settings.MapPost("/customers", (OutsourceCustomerAliasCreate r, HttpContext c, OutsourceSettingsService s, CancellationToken ct) =>
            s.CreateCustomerAliasAsync(AccessService.GetCurrent(c), r, ct));
        settings.MapDelete("/customers/{id:long}", (ulong id, HttpContext c, OutsourceSettingsService s, CancellationToken ct) =>
            s.DeleteCustomerAliasAsync(AccessService.GetCurrent(c), id, ct));
        settings.MapPost("/customers/sync", (HttpContext c, OutsourceSettingsService s, CancellationToken ct) =>
            s.SyncCustomerAliasesAsync(AccessService.GetCurrent(c), ct));
        settings.MapPut("/customers/{id:long}", (ulong id, OutsourceCustomerAliasRequest r, HttpContext c, OutsourceSettingsService s, CancellationToken ct) =>
            s.SetCustomerAliasAsync(AccessService.GetCurrent(c), id, r, ct));

        settings.MapGet("/types", (HttpContext c, OutsourceSettingsService s, CancellationToken ct) =>
            s.TypesAsync(AccessService.GetCurrent(c), ct));
        settings.MapPost("/types", (OutsourceTypeCreate r, HttpContext c, OutsourceSettingsService s, CancellationToken ct) =>
            s.CreateTypeAsync(AccessService.GetCurrent(c), r, ct));
        settings.MapPut("/types/{id:long}", (ulong id, OutsourceTypeUpsert r, HttpContext c, OutsourceSettingsService s, CancellationToken ct) =>
            s.UpdateTypeAsync(AccessService.GetCurrent(c), id, r, ct));

        settings.MapGet("/default-cc", (HttpContext c, OutsourceSettingsService s, CancellationToken ct) =>
            s.DefaultCcAsync(AccessService.GetCurrent(c), ct));
        settings.MapPut("/default-cc", (OutsourceDefaultCcRequest r, HttpContext c, OutsourceSettingsService s, CancellationToken ct) =>
            s.SetDefaultCcAsync(AccessService.GetCurrent(c), r, ct));
        settings.MapGet("/cc-user-options", (HttpContext c, OutsourceSettingsService s, CancellationToken ct) =>
            s.CcUserOptionsAsync(AccessService.GetCurrent(c), c.Request.Query["keyword"], ct));

        settings.MapGet("/fields", (HttpContext c, OutsourceFieldService s, CancellationToken ct) =>
            s.SettingsAsync(AccessService.GetCurrent(c), ct));
        settings.MapPut("/fields/order", (OutsourceFieldOrderRequest r, HttpContext c, OutsourceFieldService s, CancellationToken ct) =>
            s.SetOrderAsync(AccessService.GetCurrent(c), r, ct));
        settings.MapPut("/fields/{id:long}", (ulong id, OutsourceFieldUpdate r, HttpContext c, OutsourceFieldService s, CancellationToken ct) =>
            s.UpdateAsync(AccessService.GetCurrent(c), id, r, ct));
        settings.MapPut("/fields/{id:long}/status", (ulong id, StatusRequest r, HttpContext c, OutsourceFieldService s, CancellationToken ct) =>
            s.SetStatusAsync(AccessService.GetCurrent(c), id, r.Status, ct));
        // The list's columns for the current account (anyone who may view the list).
        endpoints.MapGet("/api/v1/outsource/fields", (HttpContext c, OutsourceFieldService s, CancellationToken ct) =>
            s.LayoutAsync(AccessService.GetCurrent(c), ct));

        var items = endpoints.MapGroup("/api/v1/outsource/items");
        items.MapGet("", (HttpContext c, OutsourceItemService s, CancellationToken ct) =>
            ListItemsAsync(c.Request.Query, c, s, ct));
        items.MapPost("/query", (Dictionary<string, string> parameters, HttpContext c, OutsourceItemService s, CancellationToken ct) =>
            ListItemsAsync(BodyParameters(parameters), c, s, ct));
        items.MapGet("/export", (HttpContext c, OutsourceItemService s, CancellationToken ct) =>
            ExportItemsAsync(c.Request.Query, c, s, ct));
        items.MapPost("/export", (Dictionary<string, string> parameters, HttpContext c, OutsourceItemService s, CancellationToken ct) =>
            ExportItemsAsync(BodyParameters(parameters), c, s, ct));
        items.MapPost("/import/preview", (OutsourceImportRequest r, HttpContext c, OutsourceItemService s, CancellationToken ct) =>
            s.PreviewImportAsync(AccessService.GetCurrent(c), r, ct));
        items.MapPost("/import", (OutsourceImportRequest r, HttpContext c, OutsourceItemService s, CancellationToken ct) =>
            s.ImportAsync(AccessService.GetCurrent(c), r, ct)).Produces<OutsourceImportResponse>();
        items.MapPost("/date-import/preview", (OutsourceDateImportRequest r, HttpContext c, OutsourceDateImportService s, CancellationToken ct) =>
            s.PreviewAsync(AccessService.GetCurrent(c), r, ct));
        items.MapPost("/date-import", (OutsourceDateImportRequest r, HttpContext c, OutsourceDateImportService s, CancellationToken ct) =>
            s.ImportAsync(AccessService.GetCurrent(c), r, ct)).Produces<AdminImportResponse>();
        items.MapPut("/{id:long}", (ulong id, OutsourceItemUpdate r, HttpContext c, OutsourceItemService s, CancellationToken ct) =>
            s.UpdateAsync(AccessService.GetCurrent(c), id, r, ct));
        items.MapPut("/batch", (OutsourceBatchUpdate r, HttpContext c, OutsourceItemService s, CancellationToken ct) =>
            s.BatchUpdateAsync(AccessService.GetCurrent(c), r, ct));
        items.MapPost("/batch-delete", (OutsourceBatchDelete r, HttpContext c, OutsourceItemService s, CancellationToken ct) =>
            s.BatchDeleteAsync(AccessService.GetCurrent(c), r, ct));
        items.MapPut("/skip-notice", (OutsourceSkipNoticeRequest r, HttpContext c, OutsourceItemService s, CancellationToken ct) =>
            s.SkipNoticeAsync(AccessService.GetCurrent(c), r, ct));
        items.MapPut("/completion", (OutsourceCompletionRequest r, HttpContext c, OutsourceItemService s, CancellationToken ct) =>
            s.SetCompletionAsync(AccessService.GetCurrent(c), r, ct));
        items.MapGet("/{id:long}/changes", (ulong id, HttpContext c, OutsourceItemService s, CancellationToken ct) =>
            s.ChangesAsync(AccessService.GetCurrent(c), id, ct));
        items.MapGet("/{id:long}/files", (ulong id, HttpContext c, OutsourceItemService s, CancellationToken ct) =>
            s.FilesAsync(AccessService.GetCurrent(c), id, ct));
        items.MapPost("/sync", (OutsourceSyncRequest r, HttpContext c, OutsourceIpmsSyncService s, CancellationToken ct) =>
            s.SyncNowAsync(AccessService.GetCurrent(c), r, ct));
        endpoints.MapGet("/api/v1/outsource/send-queue", (HttpContext c, OutsourceItemService s, CancellationToken ct) =>
            s.SendQueueAsync(AccessService.GetCurrent(c), ct));
        endpoints.MapGet("/api/v1/outsource/sync-status", (HttpContext c, OutsourceIpmsSyncService s, CancellationToken ct) =>
            s.StatusAsync(AccessService.GetCurrent(c), ct));
        var mails = endpoints.MapGroup("/api/v1/outsource/mails");
        mails.MapPost("/preview", (OutsourceMailPreviewRequest r, HttpContext c, OutsourceMailService s, CancellationToken ct) =>
            s.PreviewAsync(AccessService.GetCurrent(c), r, ct));
        mails.MapPost("/send", (OutsourceMailSendRequest r, HttpContext c, OutsourceMailService s, CancellationToken ct) =>
            s.SendAsync(AccessService.GetCurrent(c), r, ct));
        mails.MapGet("", (HttpContext c, OutsourceMailService s, CancellationToken ct) =>
        {
            var p = QueryValues.Page(c.Request);
            var status = c.Request.Query["status"].ToString();
            return s.ListAsync(AccessService.GetCurrent(c), QueryValues.OptionalUInt64(c.Request, "vendorId"),
                status.Length == 0 ? null : status, p.Page, p.Size, p.Offset, ct);
        });
        mails.MapPost("/{id:long}/retry", (ulong id, HttpContext c, OutsourceMailService s, CancellationToken ct) =>
            s.RetryAsync(AccessService.GetCurrent(c), id, ct));
        mails.MapGet("/{id:long}/attachment", async (ulong id, HttpContext c, OutsourceMailService s, CancellationToken ct) =>
        {
            var (content, name) = await s.AttachmentAsync(AccessService.GetCurrent(c), id, ct);
            return Results.File(content, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", name);
        });
        settings.MapPut("/sync-time", (OutsourceSyncTimeRequest r, HttpContext c, OutsourceIpmsSyncService s, CancellationToken ct) =>
            s.SetSyncTimeAsync(AccessService.GetCurrent(c), r, ct));
        settings.MapPut("/sync-schedule", (ScheduleSwitchRequest r, HttpContext c, OutsourceIpmsSyncService s, CancellationToken ct) =>
            s.SetScheduleAsync(AccessService.GetCurrent(c), r, ct));

        var mirror = endpoints.MapGroup("/api/v1/outsource/mes-mirror");
        mirror.MapPut("/schedule", (ScheduleSwitchRequest r, HttpContext c, MesMirrorSyncService s, CancellationToken ct) =>
            s.SetScheduleAsync(AccessService.GetCurrent(c), r, ct));
        mirror.MapGet("/status", (HttpContext c, MesMirrorSyncService s, CancellationToken ct) =>
            s.StatusAsync(AccessService.GetCurrent(c), ct));
        mirror.MapPost("/run", (MesMirrorRunRequest r, HttpContext c, MesMirrorSyncService s, CancellationToken ct) =>
            s.StartAsync(AccessService.GetCurrent(c), r, ct));
        mirror.MapPost("/stop", (HttpContext c, MesMirrorSyncService s, CancellationToken ct) =>
            s.StopAsync(AccessService.GetCurrent(c), ct));
        mirror.MapPost("/confirm", (MesMirrorConfirmRequest r, HttpContext c, MesMirrorSyncService s, CancellationToken ct) =>
            s.ConfirmAsync(AccessService.GetCurrent(c), r, ct));
        mirror.MapPost("/import/preview", (MesImportRequest r, HttpContext c, MesMirrorSyncService s, CancellationToken ct) =>
            s.PreviewImportAsync(AccessService.GetCurrent(c), r, ct));
        mirror.MapPost("/import", (MesImportConfirmRequest r, HttpContext c, MesMirrorSyncService s, CancellationToken ct) =>
            s.ConfirmImportAsync(AccessService.GetCurrent(c), r, ct));
        mirror.MapPost("/orders/query", (MesOrdersQuery r, HttpContext c, MesMirrorSyncService s, CancellationToken ct) =>
        {
            var (page, size, offset) = QueryValues.NormalizePage(r.Page ?? 1, r.PageSize);
            return s.OrdersAsync(AccessService.GetCurrent(c), page, size, offset, r.Filters, ct);
        });
        mirror.MapGet("/settings", (HttpContext c, MesMirrorSyncService s, CancellationToken ct) =>
            s.SettingsAsync(AccessService.GetCurrent(c), ct));
        mirror.MapPut("/settings", (MesMirrorSettingsRequest r, HttpContext c, MesMirrorSyncService s, CancellationToken ct) =>
            s.SaveSettingsAsync(AccessService.GetCurrent(c), r, ct));
        mirror.MapPost("/test", (MesMirrorTestRequest r, HttpContext c, MesMirrorSyncService s, CancellationToken ct) =>
            s.TestAsync(AccessService.GetCurrent(c), r, ct));

        var contacts = endpoints.MapGroup("/api/v1/outsource/contacts");
        contacts.MapGet("", (HttpContext c, OutsourceContactService s, CancellationToken ct) =>
            s.GetMatrixAsync(AccessService.GetCurrent(c), c.Request.Query["group"], c.Request.Query["keyword"],
                QueryValues.OptionalUInt64(c.Request, "vendorId"), ct));
        contacts.MapGet("/export", async (HttpContext c, OutsourceContactService s, CancellationToken ct) =>
        {
            var export = await s.ExportAsync(AccessService.GetCurrent(c), c.Request.Query["group"], c.Request.Query["keyword"],
                QueryValues.OptionalUInt64(c.Request, "vendorId"), ct);
            return Results.File(export.Content, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", export.FileName);
        });
        contacts.MapGet("/template", async (HttpContext c, OutsourceContactService s, CancellationToken ct) =>
        {
            var export = await s.TemplateAsync(AccessService.GetCurrent(c), ct);
            return Results.File(export.Content, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", export.FileName);
        });
        contacts.MapGet("/columns", (HttpContext c, OutsourceContactService s, CancellationToken ct) =>
            s.ListColumnsAsync(AccessService.GetCurrent(c), ct));
        contacts.MapPost("/columns", (OutsourceContactColumnCreate r, HttpContext c, OutsourceContactService s, CancellationToken ct) =>
            s.CreateColumnAsync(AccessService.GetCurrent(c), r, ct));
        contacts.MapPut("/columns/order", (OutsourceContactColumnOrderRequest r, HttpContext c, OutsourceContactService s, CancellationToken ct) =>
            s.ReorderColumnsAsync(AccessService.GetCurrent(c), r, ct));
        contacts.MapPut("/columns/{id:long}", (ulong id, OutsourceContactColumnUpdate r, HttpContext c, OutsourceContactService s, CancellationToken ct) =>
            s.UpdateColumnAsync(AccessService.GetCurrent(c), id, r, ct));
        contacts.MapDelete("/columns/{id:long}", (ulong id, HttpContext c, OutsourceContactService s, CancellationToken ct) =>
            s.DeleteColumnAsync(AccessService.GetCurrent(c), id, ct));
        contacts.MapPost("/rows", (OutsourceContactRowCreate r, HttpContext c, OutsourceContactService s, CancellationToken ct) =>
            s.CreateRowAsync(AccessService.GetCurrent(c), r, ct));
        contacts.MapPost("/rows/{id:long}/duplicate", (ulong id, HttpContext c, OutsourceContactService s, CancellationToken ct) =>
            s.DuplicateRowAsync(AccessService.GetCurrent(c), id, ct));
        contacts.MapPut("/rows/order", (OutsourceContactRowOrderRequest r, HttpContext c, OutsourceContactService s, CancellationToken ct) =>
            s.ReorderRowsAsync(AccessService.GetCurrent(c), r, ct));
        contacts.MapPut("/rows/visibility", (OutsourceContactVisibilityUpdateRequest r, HttpContext c, OutsourceContactService s, CancellationToken ct) =>
            s.UpdateVisibilityAsync(AccessService.GetCurrent(c), r, ct));
        contacts.MapDelete("/rows/{id:long}", (ulong id, uint expectedVersion, HttpContext c, OutsourceContactService s, CancellationToken ct) =>
            s.DeleteRowAsync(AccessService.GetCurrent(c), id, expectedVersion, ct));
        contacts.MapPut("/cells", (OutsourceContactCellsUpdateRequest r, HttpContext c, OutsourceContactService s, CancellationToken ct) =>
            s.UpdateCellsAsync(AccessService.GetCurrent(c), r, ct));
        contacts.MapGet("/people", (HttpContext c, OutsourceContactService s, CancellationToken ct) =>
            s.ListPeopleAsync(AccessService.GetCurrent(c), c.Request.Query["scopeType"],
                QueryValues.OptionalUInt64(c.Request, "vendorId"), c.Request.Query["keyword"], c.Request.Query["status"], ct));
        contacts.MapGet("/user-options", (HttpContext c, OutsourceContactService s, CancellationToken ct) =>
            s.ListUserOptionsAsync(AccessService.GetCurrent(c), c.Request.Query["keyword"], ct));
        contacts.MapPost("/people", (OutsourceContactPersonUpsert r, HttpContext c, OutsourceContactService s, CancellationToken ct) =>
            s.CreatePersonAsync(AccessService.GetCurrent(c), r, ct));
        contacts.MapPut("/people/{id:long}", (ulong id, OutsourceContactPersonUpsert r, HttpContext c, OutsourceContactService s, CancellationToken ct) =>
            s.UpdatePersonAsync(AccessService.GetCurrent(c), id, r, ct));
        contacts.MapPut("/people/{id:long}/status", (ulong id, StatusRequest r, HttpContext c, OutsourceContactService s, CancellationToken ct) =>
            s.SetPersonStatusAsync(AccessService.GetCurrent(c), id, r.Status, ct));
        contacts.MapGet("/people/{id:long}/references", (ulong id, HttpContext c, OutsourceContactService s, CancellationToken ct) =>
            s.GetPersonReferencesAsync(AccessService.GetCurrent(c), id, ct));
        contacts.MapPost("/people/{id:long}/handover", (ulong id, OutsourceContactHandoverRequest r, HttpContext c, OutsourceContactService s, CancellationToken ct) =>
            s.HandoverPersonAsync(AccessService.GetCurrent(c), id, r, ct));
        contacts.MapPost("/import/preview", (OutsourceContactImportPreviewRequest r, HttpContext c, OutsourceContactService s, CancellationToken ct) =>
            s.PreviewImportAsync(AccessService.GetCurrent(c), r, ct));
        contacts.MapPost("/import", (OutsourceContactImportRequest r, HttpContext c, OutsourceContactService s, CancellationToken ct) =>
            s.ImportAsync(AccessService.GetCurrent(c), r, ct));
        return endpoints;
    }

    // Long filters travel in a JSON object with the same parameter names and string values as the GET query.
    internal static IQueryCollection BodyParameters(Dictionary<string, string> parameters)
    {
        var values = new Dictionary<string, StringValues>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in parameters)
        {
            values.TryGetValue(key, out var previous);
            values[key] = StringValues.Concat(previous, value);
        }
        return new QueryCollection(values);
    }

    private static Task<PageResponse<OutsourceItemResponse>> ListItemsAsync(
        IQueryCollection parameters, HttpContext context, OutsourceItemService service, CancellationToken ct)
    {
        var page = QueryValues.Page(parameters);
        return service.ListAsync(AccessService.GetCurrent(context), OutsourceItemQuery.From(parameters), page.Page, page.Size, page.Offset, ct);
    }

    private static async Task<IResult> ExportItemsAsync(
        IQueryCollection parameters, HttpContext context, OutsourceItemService service, CancellationToken ct)
    {
        var export = await service.ExportAsync(AccessService.GetCurrent(context), OutsourceItemQuery.From(parameters), ct);
        return Results.File(export.Content, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", export.FileName);
    }

    // Same as the Kestrel/IIS request cap: ~5000 rows with every MES column filled can pass 16 MiB of JSON.
    internal const long MesImportRequestLimitBytes = 64L * 1024 * 1024;

    internal static bool IsMesImport(HttpRequest request) =>
        HttpMethods.IsPost(request.Method) &&
        (string.Equals(request.Path.Value?.TrimEnd('/'), "/api/v1/outsource/mes-mirror/import", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(request.Path.Value?.TrimEnd('/'), "/api/v1/outsource/mes-mirror/import/preview", StringComparison.OrdinalIgnoreCase));
}
