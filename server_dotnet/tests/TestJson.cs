using System.Text.Json;

namespace Yf.Api.Tests;

/// <summary>Shared, cached serializer options (CA1869) for test assertions and evidence files.</summary>
internal static class TestJson
{
    /// <summary>Same settings as ASP.NET Core's default Minimal API JSON (camelCase, case-insensitive).</summary>
    public static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    public static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };
}
