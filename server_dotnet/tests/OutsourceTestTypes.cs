using Yf.Api.Infrastructure;
using Yf.Api.Modules.Outsource;

namespace Yf.Api.Tests;

internal static class OutsourceTestTypes
{
    public static Task<OutsourceTypeResponse> UpdateTypeSettingsAsync(this OutsourceSettingsService service,
        CurrentUser actor, OutsourceTypeResponse type, CancellationToken ct, ulong[]? vendorIds = null, string? status = null) =>
        service.UpdateTypeAsync(actor, type.Id, new(type.Code, type.Name, type.SortNo, VendorIds: vendorIds, Status: status), ct);
}
