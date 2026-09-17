using System.Collections.Concurrent;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Yf.Api.Infrastructure;
using Yf.Api.Modules.Identity;

namespace Yf.Api.Modules.Projects;

internal static class RealtimeChangeKinds
{
    internal const string Messages = "messages";
    internal const string Receipts = "receipts";
    internal const string Activity = "activity";

    internal static bool IsValid(string kind) => kind is Messages or Receipts or Activity;
}

internal sealed record RealtimeConnection(
    string ConnectionId,
    ulong UserId,
    string SessionId,
    long AccessExpiresAt);

internal sealed class RealtimeConnectionRegistry : IDisposable
{
    private readonly ConcurrentDictionary<string, Entry> connections = new(StringComparer.Ordinal);

    internal IReadOnlyList<RealtimeConnection> Snapshot() =>
        connections.Values.Select(entry => entry.Connection).ToArray();

    internal bool Register(RealtimeConnection connection, Action abort)
    {
        Disconnect(connection.ConnectionId);
        if (connection.AccessExpiresAt <= DateTimeOffset.UtcNow.ToUnixTimeSeconds())
        {
            TryAbort(abort);
            return false;
        }

        var entry = new Entry(connection, abort);
        if (!connections.TryAdd(connection.ConnectionId, entry))
        {
            entry.Dispose();
            TryAbort(abort);
            return false;
        }
        entry.StartExpiry(() => Disconnect(connection.ConnectionId));
        return true;
    }

    internal void Remove(string connectionId)
    {
        if (connections.TryRemove(connectionId, out var entry)) entry.Dispose();
    }

    internal void Disconnect(string connectionId)
    {
        if (!connections.TryRemove(connectionId, out var entry)) return;
        entry.Dispose();
        TryAbort(entry.Abort);
    }

    public void Dispose()
    {
        foreach (var connectionId in connections.Keys) Disconnect(connectionId);
    }

    private static void TryAbort(Action abort)
    {
        try { abort(); } catch { }
    }

    private sealed class Entry(RealtimeConnection connection, Action abort) : IDisposable
    {
        private readonly object gate = new();
        private Timer? expiry;
        private bool disposed;
        internal RealtimeConnection Connection { get; } = connection;
        internal Action Abort { get; } = abort;

        internal void StartExpiry(Action expire)
        {
            var delay = DateTimeOffset.FromUnixTimeSeconds(Connection.AccessExpiresAt) - DateTimeOffset.UtcNow;
            lock (gate)
            {
                if (disposed) return;
                expiry = new Timer(_ => expire(), null, delay > TimeSpan.Zero ? delay : TimeSpan.Zero, Timeout.InfiniteTimeSpan);
            }
        }

        public void Dispose()
        {
            lock (gate)
            {
                disposed = true;
                expiry?.Dispose();
                expiry = null;
            }
        }
    }
}

internal enum ProjectRealtimeAuthorization
{
    Deliver,
    Skip,
    Disconnect,
}

internal sealed class ProjectRealtimeAuthorizer(AppDb database)
{
    internal async Task<bool> ValidateIdentityAsync(RealtimeConnection connection, CancellationToken ct)
    {
        if (connection.AccessExpiresAt <= DateTimeOffset.UtcNow.ToUnixTimeSeconds()) return false;
        await using var db = await database.OpenAsync(ct);
        await using var tx = await AppDb.BeginTransactionAsync(db, ct);
        if (!await HasActiveSessionAsync(db, tx, connection, ct)) return false;
        try
        {
            await AccessService.LockActorAsync(db, tx,
                new CurrentUser(connection.UserId, string.Empty, "INTERNAL", null), ct);
        }
        catch (ApiException)
        {
            return false;
        }
        await tx.CommitAsync(ct);
        return true;
    }

    internal async Task<ProjectRealtimeAuthorization> AuthorizeProjectAsync(
        RealtimeConnection connection,
        ulong projectId,
        CancellationToken ct)
    {
        if (connection.AccessExpiresAt <= DateTimeOffset.UtcNow.ToUnixTimeSeconds())
            return ProjectRealtimeAuthorization.Disconnect;

        await using var db = await database.OpenAsync(ct);
        await using var tx = await AppDb.BeginTransactionAsync(db, ct);
        if (!await HasActiveSessionAsync(db, tx, connection, ct))
            return ProjectRealtimeAuthorization.Disconnect;

        CurrentUser current;
        try
        {
            current = await AccessService.LockActorAsync(db, tx,
                new CurrentUser(connection.UserId, string.Empty, "INTERNAL", null), ct);
        }
        catch (ApiException)
        {
            return ProjectRealtimeAuthorization.Disconnect;
        }

        try
        {
            await ProjectAccessService.RequireViewForValidatedActorAsync(db, tx, current, projectId, false, ct);
        }
        catch (ApiException)
        {
            return ProjectRealtimeAuthorization.Skip;
        }
        await tx.CommitAsync(ct);
        return ProjectRealtimeAuthorization.Deliver;
    }

    private static async Task<bool> HasActiveSessionAsync(
        MySqlConnector.MySqlConnection db,
        MySqlConnector.MySqlTransaction tx,
        RealtimeConnection connection,
        CancellationToken ct)
    {
        await using var context = EfDb.Use(db, tx);
        var now = await context.Database.SqlQuery<DateTime>(
            $"SELECT UTC_TIMESTAMP(6) AS Value").SingleAsync(ct);
        return await context.RefreshTokens.AnyAsync(token =>
            token.UserId == connection.UserId
            && token.SessionId == connection.SessionId
            && !token.Revoked
            && token.ExpiresAt > now, ct);
    }
}

internal interface IProjectRealtimePublisher
{
    Task PublishAsync(ulong projectId, string kind, CancellationToken ct = default);
}

internal sealed record PendingProjectChange(ulong ProjectId, string Kind);

internal sealed class ProjectRealtimePublisher(
    IHubContext<ProjectRealtimeHub> hub,
    RealtimeConnectionRegistry connections,
    ProjectRealtimeAuthorizer authorizer,
    ILogger<ProjectRealtimePublisher> logger) : BackgroundService, IProjectRealtimePublisher
{
    private readonly Channel<PendingProjectChange> queue = Channel.CreateUnbounded<PendingProjectChange>(
        new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });

    public Task PublishAsync(ulong projectId, string kind, CancellationToken ct = default)
    {
        if (!RealtimeChangeKinds.IsValid(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        queue.Writer.TryWrite(new PendingProjectChange(projectId, kind));
        return Task.CompletedTask;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var change in queue.Reader.ReadAllAsync(stoppingToken))
            {
                try { await DispatchAsync(change, stoppingToken); }
                catch (Exception error) when (error is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
                {
                    logger.LogWarning(error, "Realtime dispatch failed for project {ProjectId}", change.ProjectId);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task DispatchAsync(PendingProjectChange change, CancellationToken stoppingToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        var payload = new ProjectChangedPayload(change.ProjectId, change.Kind);
        var active = connections.Snapshot()
            .Where(connection => connection.AccessExpiresAt > DateTimeOffset.UtcNow.ToUnixTimeSeconds())
            .GroupBy(connection => (connection.UserId, connection.SessionId));
        foreach (var group in active)
        {
            ProjectRealtimeAuthorization authorization;
            try
            {
                authorization = await authorizer.AuthorizeProjectAsync(group.First(), change.ProjectId, deadline.Token);
            }
            catch (Exception error) when (error is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning(error, "Realtime authorization failed for project {ProjectId}", change.ProjectId);
                if (deadline.IsCancellationRequested) break;
                continue;
            }
            if (authorization == ProjectRealtimeAuthorization.Disconnect)
            {
                foreach (var connection in group) connections.Disconnect(connection.ConnectionId);
                continue;
            }
            if (authorization == ProjectRealtimeAuthorization.Skip) continue;
            foreach (var connection in group)
            {
                try
                {
                    await hub.Clients.Client(connection.ConnectionId)
                        .SendAsync(ProjectRealtimeHub.ProjectChangedEvent, payload, deadline.Token);
                }
                catch (Exception error) when (error is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
                {
                    logger.LogDebug(error, "Realtime delivery failed for project {ProjectId}", change.ProjectId);
                }
            }
        }

        foreach (var expired in connections.Snapshot()
                     .Where(connection => connection.AccessExpiresAt <= DateTimeOffset.UtcNow.ToUnixTimeSeconds()))
            connections.Disconnect(expired.ConnectionId);
    }
}

internal sealed record ProjectChangedPayload(
    [property: JsonPropertyName("projectId")] ulong ProjectId,
    [property: JsonPropertyName("kind")] string Kind);

internal sealed class ProjectRealtimeHub(
    RealtimeConnectionRegistry connections,
    ProjectRealtimeAuthorizer authorizer) : Hub
{
    public const string Path = "/api/v1/collaboration/live";
    public const string ProjectChangedEvent = "ProjectChanged";

    public override async Task OnConnectedAsync()
    {
        var http = Context.GetHttpContext();
        CurrentUser? current = null;
        AccessClaims? claims = null;
        if (http is not null)
        {
            if (http.Items.TryGetValue(typeof(CurrentUser), out var currentValue))
                current = currentValue as CurrentUser;
            if (http.Items.TryGetValue(typeof(AccessClaims), out var claimsValue))
                claims = claimsValue as AccessClaims;
        }
        if (current is null || claims is null || current.Id != claims.UserId)
        {
            Context.Abort();
            throw new HubException("连接未获授权");
        }

        var connection = new RealtimeConnection(Context.ConnectionId, claims.UserId, claims.SessionId, claims.ExpiresAt);
        if (!await authorizer.ValidateIdentityAsync(connection, Context.ConnectionAborted))
        {
            Context.Abort();
            throw new HubException("连接未获授权");
        }
        var lifetime = Context.Features.Get<IConnectionLifetimeFeature>();
        Action abort = lifetime is null ? Context.Abort : lifetime.Abort;
        if (!connections.Register(connection, abort))
            throw new HubException("连接未获授权");
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        connections.Remove(Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }
}

public static class ProjectRealtimeEndpoints
{
    public static IEndpointRouteBuilder MapProjectRealtime(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapHub<ProjectRealtimeHub>(ProjectRealtimeHub.Path);
        return endpoints;
    }
}
