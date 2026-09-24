namespace Yf.Api.Infrastructure;

/// <summary>Compatibility facade for empty-database initialization and startup validation.</summary>
public static class SchemaBootstrap
{
    public static Task InitializeEmptyAsync(AppDb db, CancellationToken ct = default) =>
        EfDatabaseLifecycle.InitializeEmptyAsync(db, ct);

    internal static Task InitializeEmptyAsync(AppDb db, string bootstrapPassword, CancellationToken ct = default) =>
        EfDatabaseLifecycle.InitializeEmptyAsync(db, bootstrapPassword, ct);

    public static Task ValidateAsync(AppDb db, CancellationToken ct = default) =>
        EfDatabaseLifecycle.ValidateReadyAsync(db, ct);
}
