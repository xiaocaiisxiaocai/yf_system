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
        CancellationToken ct) =>
        (await AuthorizeProjectAsync([connection], projectId, ct))[(connection.UserId, connection.SessionId)];

    /// <summary>
    /// Authorizes every (user, session) among <paramref name="candidates"/> for one project using a
    /// fixed number of queries in a single transaction, instead of one transaction per session.
    /// Semantics match the per-request checks: an ended session, an inactive or must-change-password
    /// account, or a disabled supplier disconnects; a missing project:list grant, an unknown project or a
    /// project outside the actor's scope skips.
    /// </summary>
    internal async Task<IReadOnlyDictionary<(ulong UserId, string SessionId), ProjectRealtimeAuthorization>> AuthorizeProjectAsync(
        IReadOnlyCollection<RealtimeConnection> candidates,
        ulong projectId,
        CancellationToken ct)
    {
        var decisions = new Dictionary<(ulong UserId, string SessionId), ProjectRealtimeAuthorization>();
        var nowSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var pending = new List<(ulong UserId, string SessionId)>();
        foreach (var connection in candidates)
        {
            var key = (connection.UserId, connection.SessionId);
            if (decisions.ContainsKey(key)) continue;
            if (connection.AccessExpiresAt <= nowSeconds)
            {
                decisions[key] = ProjectRealtimeAuthorization.Disconnect;
                continue;
            }
            decisions[key] = ProjectRealtimeAuthorization.Skip;
            pending.Add(key);
        }
        if (pending.Count == 0) return decisions;

        var userIds = pending.Select(key => key.UserId).Distinct().ToArray();
        string[] viewCodes = ["project:list", "project:view_all"];
        await using var db = await database.OpenAsync(ct);
        await using var tx = await AppDb.BeginTransactionAsync(db, ct);
        await AccessService.LockBusinessAsync(db, tx, ct);
        await using var context = EfDb.Use(db, tx);
        var now = await context.Database.SqlQuery<DateTime>(
            $"SELECT UTC_TIMESTAMP(6) AS Value").SingleAsync(ct);
        var activeSessions = (await context.RefreshTokens
                .Where(token => Enumerable.Contains(userIds, token.UserId) && !token.Revoked && token.ExpiresAt > now)
                .Select(token => new { token.UserId, token.SessionId })
                .Distinct()
                .ToListAsync(ct))
            .Select(token => (token.UserId, token.SessionId))
            .ToHashSet();
        var users = await context.Users
            .Where(user => Enumerable.Contains(userIds, user.Id))
            .Select(user => new { user.Id, user.UserType, user.SupplierId, user.Status, user.MustChangePassword })
            .ToDictionaryAsync(user => user.Id, ct);
        var supplierIds = users.Values.Where(user => user.SupplierId is not null)
            .Select(user => user.SupplierId!.Value).Distinct().ToArray();
        var activeSuppliers = supplierIds.Length == 0
            ? []
            : (await context.Suppliers
                .Where(supplier => Enumerable.Contains(supplierIds, supplier.Id) && supplier.Status == "ACTIVE")
                .Select(supplier => supplier.Id)
                .ToListAsync(ct)).ToHashSet();
        var grants = (await (
                from userRole in context.UserRoles
                join role in context.Roles on userRole.RoleId equals role.Id
                join rolePermission in context.RolePermissions on role.Id equals rolePermission.RoleId
                join permission in context.Permissions on rolePermission.PermissionId equals permission.Id
                where Enumerable.Contains(userIds, userRole.UserId) && role.Status == "ACTIVE"
                      && Enumerable.Contains(viewCodes, permission.Code)
                select new { userRole.UserId, permission.Code })
            .Distinct()
            .ToListAsync(ct))
            .Select(grant => (grant.UserId, grant.Code))
            .ToHashSet();
        var project = await context.Projects.Where(item => item.Id == projectId)
            .Select(item => new { item.SupplierId, item.ResponsibleUserId })
            .SingleOrDefaultAsync(ct);
        await tx.CommitAsync(ct);

        foreach (var key in pending)
        {
            if (!activeSessions.Contains(key)
                || !users.TryGetValue(key.UserId, out var user)
                || user.Status != "ACTIVE"
                || user.MustChangePassword
                || (user.UserType == "SUPPLIER"
                    && (user.SupplierId is not ulong ownSupplier || !activeSuppliers.Contains(ownSupplier))))
            {
                decisions[key] = ProjectRealtimeAuthorization.Disconnect;
                continue;
            }
            if (project is null || !grants.Contains((key.UserId, "project:list"))) continue;
            var visible = user.UserType == "INTERNAL"
                ? grants.Contains((key.UserId, "project:view_all")) || project.ResponsibleUserId == key.UserId
                : user.SupplierId is ulong supplierId && supplierId == project.SupplierId
                  && activeSuppliers.Contains(supplierId);
            if (visible) decisions[key] = ProjectRealtimeAuthorization.Deliver;
        }
        return decisions;
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
    private readonly ConcurrentDictionary<PendingProjectChange, QueuedChange> queued = new();

    public Task PublishAsync(ulong projectId, string kind, CancellationToken ct = default)
    {
        if (!RealtimeChangeKinds.IsValid(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        var change = new PendingProjectChange(projectId, kind);
        var state = queued.GetOrAdd(change, static _ => new QueuedChange());
        var shouldQueue = false;
        lock (state)
        {
            if (!state.Enqueued) { state.Enqueued = true; shouldQueue = true; }
            else state.Dirty = true;
        }
        if (shouldQueue && !queue.Writer.TryWrite(change))
        {
            lock (state)
            {
                state.Enqueued = false;
                state.Dirty = false;
            }
            queued.TryRemove(change, out _);
        }
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
                finally { RequeueOrRelease(change, stoppingToken.IsCancellationRequested); }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private void RequeueOrRelease(PendingProjectChange change, bool stopping)
    {
        if (!queued.TryGetValue(change, out var state)) return;
        var requeue = false;
        lock (state)
        {
            if (!stopping && state.Dirty)
            {
                state.Dirty = false;
                requeue = true;
            }
            else
            {
                state.Enqueued = false;
                state.Dirty = false;
                queued.TryRemove(change, out _);
            }
        }
        if (requeue && !queue.Writer.TryWrite(change))
        {
            lock (state)
            {
                state.Enqueued = false;
                state.Dirty = false;
            }
            queued.TryRemove(change, out _);
        }
    }

    private sealed class QueuedChange
    {
        internal bool Enqueued;
        internal bool Dirty;
    }

    private async Task DispatchAsync(PendingProjectChange change, CancellationToken stoppingToken)
    {
        var payload = new ProjectChangedPayload(change.ProjectId, change.Kind);
        var nowSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var active = connections.Snapshot().Where(connection => connection.AccessExpiresAt > nowSeconds).ToArray();
        if (active.Length > 0)
        {
            IReadOnlyDictionary<(ulong UserId, string SessionId), ProjectRealtimeAuthorization> decisions;
            using (var authorizationDeadline = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken))
            {
                authorizationDeadline.CancelAfter(TimeSpan.FromSeconds(5));
                try
                {
                    decisions = await authorizer.AuthorizeProjectAsync(active, change.ProjectId, authorizationDeadline.Token);
                }
                catch (Exception error) when (error is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
                {
                    // Fail closed: nobody is notified from an unverified snapshot. Clients still
                    // converge through their own REST refetches and fallback polling.
                    logger.LogWarning(error, "Realtime authorization failed for project {ProjectId}", change.ProjectId);
                    decisions = new Dictionary<(ulong UserId, string SessionId), ProjectRealtimeAuthorization>();
                }
            }

            using var sendDeadline = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            sendDeadline.CancelAfter(TimeSpan.FromSeconds(5));
            var sends = new List<Task>();
            foreach (var connection in active)
            {
                if (!decisions.TryGetValue((connection.UserId, connection.SessionId), out var decision)) continue;
                if (decision == ProjectRealtimeAuthorization.Disconnect) connections.Disconnect(connection.ConnectionId);
                else if (decision == ProjectRealtimeAuthorization.Deliver)
                    sends.Add(SendAsync(connection.ConnectionId, payload, change.ProjectId, sendDeadline.Token, stoppingToken));
            }
            // One slow client must not delay delivery to everyone queued behind it.
            await Task.WhenAll(sends);
        }

        foreach (var expired in connections.Snapshot()
                     .Where(connection => connection.AccessExpiresAt <= DateTimeOffset.UtcNow.ToUnixTimeSeconds()))
            connections.Disconnect(expired.ConnectionId);
    }

    private async Task SendAsync(string connectionId, ProjectChangedPayload payload, ulong projectId,
        CancellationToken deadline, CancellationToken stoppingToken)
    {
        try
        {
            await hub.Clients.Client(connectionId).SendAsync(ProjectRealtimeHub.ProjectChangedEvent, payload, deadline);
        }
        catch (Exception error) when (error is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
        {
            logger.LogDebug(error, "Realtime delivery failed for project {ProjectId}", projectId);
        }
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
