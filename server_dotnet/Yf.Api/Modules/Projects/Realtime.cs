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
    internal const string Project = "project";

    internal static bool IsValid(string kind) => kind is Messages or Receipts or Activity or Project;
}

internal sealed record RealtimeConnection(
    string ConnectionId,
    ulong UserId,
    string SessionId,
    long AccessExpiresAt,
    string UserType = UserTypes.Internal,
    ulong? SupplierId = null);

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

/// <summary>
/// Who may be told that a project changed. <paramref name="FormerInternalUserIds"/> lists internal accounts that
/// could see the project immediately before the change but may no longer (a former owner after an ownership
/// transfer): they receive the change-kind signal so their open workspace refetches and learns it lost access.
/// The signal carries no data, so this reveals nothing they did not already see.
/// </summary>
internal sealed record ProjectRealtimeAudience(
    ulong ProjectId,
    ulong SupplierId,
    ulong? ResponsibleUserId,
    IReadOnlySet<ulong> ViewAllUserIds,
    IReadOnlySet<ulong> SupplierUserIds,
    ulong? GroupCreatorId,
    IReadOnlySet<ulong>? FormerInternalUserIds = null);

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
            await AccessService.ReadActorAsync(db, tx,
                new CurrentUser(connection.UserId, string.Empty, connection.UserType, connection.SupplierId), ct);
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
        var audience = await ResolveAudienceAsync(projectId, candidates, ct);
        if (audience is null) return InitialDecisions(candidates);
        return await AuthorizeProjectAsync(candidates, audience, ct);
    }

    /// <summary>
    /// Everyone who could currently see the project, read from the database rather than from the identity
    /// cached when a connection was opened: the owner, the main project's creator, internal view-all grantees and the accounts that
    /// belong to the project's supplier right now. It only narrows candidates; delivery is re-authorized.
    /// </summary>
    internal Task<ProjectRealtimeAudience?> ResolveAudienceAsync(ulong projectId, CancellationToken ct) =>
        ResolveAudienceAsync(projectId, null, ct);

    internal async Task<ProjectRealtimeAudience?> ResolveAudienceAsync(
        ulong projectId,
        IReadOnlyCollection<RealtimeConnection>? candidates,
        CancellationToken ct)
    {
        await using var db = await database.OpenAsync(ct);
        await using var context = EfDb.Use(db);
        var project = await context.Projects.Where(item => item.Id == projectId)
            .Select(item => new
            {
                item.SupplierId,
                item.ResponsibleUserId,
                GroupCreatorId = context.ProjectGroups.Where(group => group.Id == item.ProjectGroupId)
                    .Select(group => (ulong?)group.CreatedBy).SingleOrDefault(),
            })
            .SingleOrDefaultAsync(ct);
        if (project is null) return null;
        var candidateUserIds = candidates?.Select(connection => connection.UserId).Distinct().ToArray();
        var usersWithViewAll = AccessService.UsersWithPermission(context, "project:view_all");
        var viewAllQuery = context.Users.Where(user => user.Status == AccountStatuses.Active
                && usersWithViewAll.Contains(user.Id))
            .Select(user => user.Id);
        var supplierQuery = context.Users.Where(user => user.SupplierId == project.SupplierId
            && user.Status == AccountStatuses.Active);
        if (candidateUserIds is { Length: > 0 })
        {
            viewAllQuery = viewAllQuery.Where(id => Enumerable.Contains(candidateUserIds, id));
            supplierQuery = supplierQuery.Where(user => Enumerable.Contains(candidateUserIds, user.Id));
        }
        else if (candidates is not null)
        {
            return new(projectId, project.SupplierId, project.ResponsibleUserId, new HashSet<ulong>(),
                new HashSet<ulong>(), project.GroupCreatorId);
        }
        var viewAll = await viewAllQuery.ToArrayAsync(ct);
        var supplierUsers = await supplierQuery
            .Select(user => user.Id).ToArrayAsync(ct);
        return new(projectId, project.SupplierId, project.ResponsibleUserId, viewAll.ToHashSet(), supplierUsers.ToHashSet(),
            project.GroupCreatorId);
    }

    internal static RealtimeConnection[] SelectCandidates(
        IEnumerable<RealtimeConnection> connections, ProjectRealtimeAudience audience) =>
        connections.Where(connection => connection.UserId == audience.ResponsibleUserId
                || connection.UserId == audience.GroupCreatorId
                || audience.ViewAllUserIds.Contains(connection.UserId)
                || audience.SupplierUserIds.Contains(connection.UserId)
                || IsFormerInternalViewer(audience, connection.UserId))
            .ToArray();

    private static bool IsFormerInternalViewer(ProjectRealtimeAudience audience, ulong userId) =>
        audience.FormerInternalUserIds is { } former && former.Contains(userId);

    /// <summary>
    /// Snapshots the current audience of every listed project inside the caller's transaction, so a change that
    /// narrows visibility (ownership transfer, deletion) can still reach the people who saw the project before it.
    /// </summary>
    internal static async Task<IReadOnlyList<ProjectRealtimeAudience>> SnapshotAudiencesAsync(
        YfDbContext db, IReadOnlyCollection<ulong> projectIds, CancellationToken ct)
    {
        if (projectIds.Count == 0) return [];
        var ids = projectIds.Distinct().ToArray();
        var projects = await db.Projects.Where(project => Enumerable.Contains(ids, project.Id))
            .OrderBy(project => project.Id)
            .Select(project => new
            {
                project.Id,
                project.SupplierId,
                project.ResponsibleUserId,
                GroupCreatorId = db.ProjectGroups.Where(group => group.Id == project.ProjectGroupId)
                    .Select(group => (ulong?)group.CreatedBy).SingleOrDefault(),
            }).ToArrayAsync(ct);
        if (projects.Length == 0) return [];
        var viewAll = (await db.Users.Where(user => user.Status == AccountStatuses.Active
                && AccessService.UsersWithPermission(db, "project:view_all").Contains(user.Id))
            .Select(user => user.Id).ToArrayAsync(ct)).ToHashSet();
        var supplierIds = projects.Select(project => project.SupplierId).Distinct().ToArray();
        var supplierUsers = (await db.Users.Where(user => user.SupplierId != null
                && Enumerable.Contains(supplierIds, user.SupplierId.Value)
                && user.Status == AccountStatuses.Active)
            .Select(user => new { user.Id, SupplierId = user.SupplierId!.Value }).ToArrayAsync(ct))
            .ToLookup(user => user.SupplierId, user => user.Id);
        return projects.Select(project => new ProjectRealtimeAudience(project.Id, project.SupplierId,
                project.ResponsibleUserId, viewAll, supplierUsers[project.SupplierId].ToHashSet(), project.GroupCreatorId))
            .ToArray();
    }

    internal async Task<IReadOnlyDictionary<(ulong UserId, string SessionId), ProjectRealtimeAuthorization>> AuthorizeProjectAsync(
        IReadOnlyCollection<RealtimeConnection> candidates,
        ProjectRealtimeAudience audience,
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
        var sessionIds = pending.Select(key => key.SessionId).Distinct().ToArray();
        await using var db = await database.OpenAsync(ct);
        await using var context = EfDb.Use(db);
        var facts = await (
            from token in context.RefreshTokens
            join user in context.Users on token.UserId equals user.Id
            where Enumerable.Contains(userIds, token.UserId)
                  && Enumerable.Contains(sessionIds, token.SessionId)
            select new RealtimeAuthorizationFact
            {
                UserId = token.UserId,
                SessionId = token.SessionId,
                SessionActive = !token.Revoked && token.ExpiresAt > DateTime.UtcNow
                    && token.SessionExpiresAt > DateTime.UtcNow,
                UserType = user.UserType,
                SupplierId = user.SupplierId,
                UserActive = user.Status == AccountStatuses.Active && !user.MustChangePassword,
                SupplierActive = user.UserType != UserTypes.Supplier
                    || user.SupplierId != null && context.Suppliers.Any(supplier =>
                        supplier.Id == user.SupplierId && supplier.Status == AccountStatuses.Active),
                HasProjectList = AccessService.UsersWithPermission(context, "project:list").Contains(user.Id),
                HasViewAll = AccessService.UsersWithPermission(context, "project:view_all").Contains(user.Id),
                ProjectSupplierId = (ulong?)audience.SupplierId,
                ProjectResponsibleUserId = audience.ResponsibleUserId,
                ProjectGroupCreatorId = audience.GroupCreatorId,
            }).ToArrayAsync(ct);
        var factsBySession = facts.GroupBy(fact => (fact.UserId, fact.SessionId))
            .ToDictionary(group => group.Key, group => group.ToArray());

        foreach (var key in pending)
        {
            if (!factsBySession.TryGetValue(key, out var sessionFacts)
                || !sessionFacts.Any(fact => fact.SessionActive)
                || !sessionFacts[0].UserActive
                || !sessionFacts[0].SupplierActive)
            {
                decisions[key] = ProjectRealtimeAuthorization.Disconnect;
                continue;
            }
            var fact = sessionFacts[0];
            if (!fact.HasProjectList || fact.ProjectSupplierId is null) continue;
            var visible = fact.UserType == UserTypes.Internal
                ? fact.HasViewAll || fact.ProjectResponsibleUserId == key.UserId || fact.ProjectGroupCreatorId == key.UserId
                    || IsFormerInternalViewer(audience, key.UserId)
                : fact.SupplierId is ulong supplierId && supplierId == fact.ProjectSupplierId;
            if (visible) decisions[key] = ProjectRealtimeAuthorization.Deliver;
        }
        return decisions;
    }

    private static IReadOnlyDictionary<(ulong UserId, string SessionId), ProjectRealtimeAuthorization> InitialDecisions(
        IReadOnlyCollection<RealtimeConnection> candidates)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return candidates.GroupBy(connection => (connection.UserId, connection.SessionId))
            .ToDictionary(group => group.Key, group => group.First().AccessExpiresAt <= now
                ? ProjectRealtimeAuthorization.Disconnect
                : ProjectRealtimeAuthorization.Skip);
    }

    private static async Task<bool> HasActiveSessionAsync(
        MySqlConnector.MySqlConnection db,
        MySqlConnector.MySqlTransaction tx,
        RealtimeConnection connection,
        CancellationToken ct)
    {
        await using var context = EfDb.Use(db, tx);
        var now = await DbClock.UtcNowAsync(context, ct);
        return await context.RefreshTokens.AnyAsync(token =>
            token.UserId == connection.UserId
            && token.SessionId == connection.SessionId
            && !token.Revoked
            && token.ExpiresAt > now
            && token.SessionExpiresAt > now, ct);
    }

    private sealed class RealtimeAuthorizationFact
    {
        public ulong UserId { get; init; }
        public string SessionId { get; init; } = string.Empty;
        public bool SessionActive { get; init; }
        public string UserType { get; init; } = string.Empty;
        public ulong? SupplierId { get; init; }
        public bool UserActive { get; init; }
        public bool SupplierActive { get; init; }
        public bool HasProjectList { get; init; }
        public bool HasViewAll { get; init; }
        public ulong? ProjectSupplierId { get; init; }
        public ulong? ProjectResponsibleUserId { get; init; }
        public ulong? ProjectGroupCreatorId { get; init; }
    }
}

internal interface IProjectRealtimePublisher
{
    Task PublishAsync(ulong projectId, string kind, CancellationToken ct = default);

    Task PublishAsync(ProjectRealtimeAudience audience, string kind, CancellationToken ct = default) =>
        PublishAsync(audience.ProjectId, kind, ct);
}

internal sealed record PendingProjectChange(ulong ProjectId, string Kind);

internal sealed class ProjectRealtimePublisher(
    IHubContext<ProjectRealtimeHub> hub,
    RealtimeConnectionRegistry connections,
    ProjectRealtimeAuthorizer authorizer,
    ILogger<ProjectRealtimePublisher> logger) : BackgroundService, IProjectRealtimePublisher
{
    private readonly Channel<QueuedDispatch> queue = Channel.CreateUnbounded<QueuedDispatch>(
        new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });
    private readonly ConcurrentDictionary<PendingProjectChange, QueuedChange> queued = new();

    public Task PublishAsync(ulong projectId, string kind, CancellationToken ct = default)
        => Enqueue(new(projectId, kind), null);

    public Task PublishAsync(ProjectRealtimeAudience audience, string kind, CancellationToken ct = default)
        => Enqueue(new(audience.ProjectId, kind), audience);

    private Task Enqueue(PendingProjectChange change, ProjectRealtimeAudience? audience)
    {
        if (!RealtimeChangeKinds.IsValid(change.Kind))
            throw new ArgumentOutOfRangeException(nameof(change), change.Kind, "Unsupported realtime change kind.");
        while (true)
        {
            var state = queued.GetOrAdd(change, static _ => new QueuedChange());
            var shouldQueue = false;
            lock (state)
            {
                if (!queued.TryGetValue(change, out var current) || !ReferenceEquals(current, state)) continue;
                if (audience is not null) state.Audience = audience;
                if (!state.Enqueued) { state.Enqueued = true; shouldQueue = true; }
                else state.Dirty = true;
            }
            if (!shouldQueue || queue.Writer.TryWrite(new(change, state))) break;
            lock (state)
            {
                state.Enqueued = false;
                state.Dirty = false;
                RemoveState(change, state);
            }
            break;
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// Changes for different projects dispatch concurrently, so one project's slow authorization or send
    /// does not delay every other project. A given (project, kind) is never dispatched twice at once: it
    /// stays marked as enqueued until <see cref="RequeueOrRelease"/>, and publishes meanwhile only mark it dirty.
    /// </summary>
    internal const int MaximumConcurrentDispatches = 4;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var slots = new SemaphoreSlim(MaximumConcurrentDispatches);
        var running = new ConcurrentDictionary<Task, byte>();
        try
        {
            await foreach (var dispatch in queue.Reader.ReadAllAsync(stoppingToken))
            {
                await slots.WaitAsync(stoppingToken);
                var runningDispatch = DispatchAndReleaseAsync(dispatch, slots, stoppingToken);
                running.TryAdd(runningDispatch, 0);
                _ = runningDispatch.ContinueWith(completed => running.TryRemove(completed, out _),
                    CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            // Let in-flight sends observe cancellation and finish before the semaphore is disposed.
            await Task.WhenAll(running.Keys);
        }
    }

    private async Task DispatchAndReleaseAsync(QueuedDispatch dispatch, SemaphoreSlim slots, CancellationToken stoppingToken)
    {
        await Task.Yield();
        try { await DispatchAsync(dispatch, stoppingToken); }
        catch (Exception error) when (error is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
        {
            logger.LogWarning(error, "Realtime dispatch failed for project {ProjectId}", dispatch.Change.ProjectId);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            RequeueOrRelease(dispatch, stoppingToken.IsCancellationRequested);
            slots.Release();
        }
    }

    private void RequeueOrRelease(QueuedDispatch dispatch, bool stopping)
    {
        var change = dispatch.Change;
        var state = dispatch.State;
        var requeue = false;
        lock (state)
        {
            if (!queued.TryGetValue(change, out var current) || !ReferenceEquals(current, state)) return;
            if (!stopping && state.Dirty)
            {
                state.Dirty = false;
                requeue = true;
            }
            else
            {
                state.Enqueued = false;
                state.Dirty = false;
                RemoveState(change, state);
            }
        }
        if (requeue && !queue.Writer.TryWrite(dispatch))
        {
            lock (state)
            {
                state.Enqueued = false;
                state.Dirty = false;
                RemoveState(change, state);
            }
        }
    }

    private void RemoveState(PendingProjectChange change, QueuedChange state) =>
        ((ICollection<KeyValuePair<PendingProjectChange, QueuedChange>>)queued)
        .Remove(new(change, state));

    private sealed record QueuedDispatch(PendingProjectChange Change, QueuedChange State);

    private sealed class QueuedChange
    {
        internal bool Enqueued;
        internal bool Dirty;
        internal ProjectRealtimeAudience? Audience;
    }

    private async Task DispatchAsync(QueuedDispatch dispatch, CancellationToken stoppingToken)
    {
        var change = dispatch.Change;
        ProjectRealtimeAudience? suppliedAudience;
        lock (dispatch.State) suppliedAudience = dispatch.State.Audience;
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
                    var audience = suppliedAudience ?? await authorizer.ResolveAudienceAsync(
                        change.ProjectId, active, authorizationDeadline.Token);
                    active = audience is null ? [] : ProjectRealtimeAuthorizer.SelectCandidates(active, audience);
                    decisions = active.Length == 0 || audience is null
                        ? new Dictionary<(ulong UserId, string SessionId), ProjectRealtimeAuthorization>()
                        : await authorizer.AuthorizeProjectAsync(active, audience, authorizationDeadline.Token);
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

        var connection = new RealtimeConnection(Context.ConnectionId, claims.UserId, claims.SessionId,
            claims.ExpiresAt, current.UserType, current.SupplierId);
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
