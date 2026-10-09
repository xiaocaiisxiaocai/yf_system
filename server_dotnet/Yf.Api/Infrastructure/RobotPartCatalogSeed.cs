using System.Text.Json;

namespace Yf.Api.Infrastructure;

/// <summary>
/// The approved Robot catalog, as the migrations leave it: the 六軸 parts (AddRobotPartCatalog) minus the
/// parts SeedArmCatalogAndRobotTypes retires, plus its 三(四)轴 / SCARA四軸 / 蜘蛛手 parts and preset Robot types.
/// </summary>
internal static class RobotPartCatalogSeed
{
    internal sealed record Item(string SupplierName, string PartNumber, string Model, int SourceRow);

    internal sealed record RetiredPart(string SupplierName, string PartNumber);

    internal sealed record RobotType(string Name, int SortNo);

    /// <summary>A part with the remark its supplier gets when the snapshot that introduces it creates it.</summary>
    internal sealed record SeedPart(string SupplierName, string PartNumber, string Model, string SupplierRemark);

    internal sealed record Catalog(SeedPart[] Parts, RobotType[] RobotTypes);

    internal const int SupplierCount = 6;
    internal const int PartCount = 40;
    internal const int RobotTypeCount = 5;

    internal static Catalog Load()
    {
        var sixAxis = Read("Data.robot-parts-20260923.json");
        var arms = Read("Data.robot-catalog-20261008.json");
        Check(sixAxis, 27, 7, 0, "7-brand/27-part 六軸");
        Check(arms, 16, 5, RobotTypeCount, "5-brand/16-part arm");
        var retired = (arms.RemovedSixAxisParts ?? []).Select(item => (item.SupplierName, item.PartNumber)).ToHashSet();
        if (retired.Count != 3 || !retired.All(item => sixAxis.Parts!.Any(part => (part.SupplierName, part.PartNumber) == item)))
            throw new InvalidOperationException("Embedded Robot catalog retires parts that are not in the 六軸 snapshot.");
        SeedPart[] parts =
        [
            .. sixAxis.Parts!.Where(item => !retired.Contains((item.SupplierName, item.PartNumber)))
                .Select(item => new SeedPart(item.SupplierName, item.PartNumber, item.Model,
                    "Robot 料号目录初始化（2026-09-23）")),
            .. arms.Parts!.Select(item => new SeedPart(item.SupplierName, item.PartNumber, item.Model,
                "Robot 料号目录初始化（2026-10-08）")),
        ];
        if (parts.Length != PartCount
            || parts.Select(item => item.SupplierName).Distinct(StringComparer.Ordinal).Count() != SupplierCount
            || parts.Select(item => (item.SupplierName, item.PartNumber)).Distinct().Count() != parts.Length)
            throw new InvalidOperationException("Embedded Robot part catalogs do not match the approved 6-brand/40-part catalog.");
        return new(parts, arms.RobotTypes!);
    }

    private static CatalogDocument Read(string suffix)
    {
        var assembly = typeof(RobotPartCatalogSeed).Assembly;
        var resource = assembly.GetManifestResourceNames().SingleOrDefault(name =>
            name.EndsWith(suffix, StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"Embedded Robot catalog {suffix} is missing.");
        using var stream = assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"Embedded Robot catalog {suffix} cannot be opened.");
        return JsonSerializer.Deserialize<CatalogDocument>(stream, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        }) ?? throw new InvalidOperationException($"Embedded Robot catalog {suffix} is invalid.");
    }

    private static void Check(CatalogDocument document, int partCount, int supplierCount, int robotTypeCount, string label)
    {
        var parts = document.Parts ?? [];
        var robotTypes = document.RobotTypes ?? [];
        if (parts.Length != partCount
            || parts.Any(item => string.IsNullOrWhiteSpace(item.SupplierName)
                || string.IsNullOrWhiteSpace(item.PartNumber) || string.IsNullOrWhiteSpace(item.Model))
            || parts.Select(item => item.SupplierName).Distinct(StringComparer.Ordinal).Count() != supplierCount
            || parts.Select(item => (item.SupplierName, item.PartNumber)).Distinct().Count() != parts.Length
            || robotTypes.Length != robotTypeCount
            || robotTypes.Any(type => string.IsNullOrWhiteSpace(type.Name))
            || robotTypes.Select(type => type.Name).Distinct(StringComparer.Ordinal).Count() != robotTypes.Length)
            throw new InvalidOperationException($"Embedded Robot catalog does not match the approved {label} snapshot.");
        document.Parts = parts;
        document.RobotTypes = robotTypes;
    }

    private sealed class CatalogDocument
    {
        public Item[]? Parts { get; set; }
        public RetiredPart[]? RemovedSixAxisParts { get; set; }
        public RobotType[]? RobotTypes { get; set; }
    }
}
