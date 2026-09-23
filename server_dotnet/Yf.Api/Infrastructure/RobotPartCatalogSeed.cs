using System.Reflection;
using System.Text.Json;

namespace Yf.Api.Infrastructure;

internal static class RobotPartCatalogSeed
{
    internal sealed record Item(string SupplierName, string PartNumber, string Model, int SourceRow);

    internal static Item[] Load()
    {
        var assembly = typeof(RobotPartCatalogSeed).Assembly;
        var resource = assembly.GetManifestResourceNames().SingleOrDefault(name =>
            name.EndsWith("Data.robot-parts-20260923.json", StringComparison.Ordinal))
            ?? throw new InvalidOperationException("Embedded Robot part catalog is missing.");
        using var stream = assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException("Embedded Robot part catalog cannot be opened.");
        var document = JsonSerializer.Deserialize<CatalogDocument>(stream, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        }) ?? throw new InvalidOperationException("Embedded Robot part catalog is invalid.");
        var parts = document.Parts ?? [];
        if (parts.Length != 27
            || parts.Any(item => string.IsNullOrWhiteSpace(item.SupplierName)
                || string.IsNullOrWhiteSpace(item.PartNumber) || string.IsNullOrWhiteSpace(item.Model))
            || parts.Select(item => item.SupplierName).Distinct(StringComparer.Ordinal).Count() != 7
            || parts.Select(item => (item.SupplierName, item.PartNumber)).Distinct().Count() != parts.Length)
            throw new InvalidOperationException("Embedded Robot part catalog does not match the approved 7-brand/27-part snapshot.");
        return parts;
    }

    private sealed class CatalogDocument
    {
        public Item[]? Parts { get; init; }
    }
}
