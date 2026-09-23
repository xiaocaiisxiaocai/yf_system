using System.Text.Json;

namespace Yf.Api.Infrastructure;

internal static class JsonDefaults
{
    public static JsonSerializerOptions Web { get; } = CreateWebOptions();

    private static JsonSerializerOptions CreateWebOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}
