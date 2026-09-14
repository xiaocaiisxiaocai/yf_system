using System.Text.Json;

namespace Yf.Api.Infrastructure;

public sealed record AuditChange(string Field, string Label, object? Before, object? After)
{
    public static AuditChange[] OnlyChanged(params AuditChange[] changes) => changes
        .Where(change => JsonSerializer.Serialize(change.Before, JsonSerializerOptions.Web)
            != JsonSerializer.Serialize(change.After, JsonSerializerOptions.Web))
        .ToArray();
}
