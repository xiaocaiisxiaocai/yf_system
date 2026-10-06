namespace Yf.Api.Modules.Oem.Common;

/// <summary>
/// Something that happened to an OEM transfer. Services raise events on the unit of
/// work; <see cref="OemEventDispatcher"/> hands them to every <see cref="IOemEventHandler"/>
/// inside the same transaction just before commit, so side effects such as queued
/// notification mail commit or roll back together with the business change.
/// </summary>
public interface IOemEvent
{
    ulong TransferId { get; }
}

public sealed record TransferReleasedEvent(ulong TransferId) : IOemEvent;

/// <summary>Approval tasks became actionable (node activation or reassignment).</summary>
public sealed record ApprovalTasksActivatedEvent(ulong TransferId, IReadOnlyList<ulong> TaskIds) : IOemEvent;

public sealed record ApprovalBlockedEvent(ulong TransferId, string Reason) : IOemEvent;

public sealed record ApprovalCompletedEvent(ulong TransferId) : IOemEvent;

/// <summary>The transfer ended without release (rejected, cancelled or file-validation-blocked).</summary>
public sealed record TransferClosedEvent(ulong TransferId, string Lifecycle, string? Reason) : IOemEvent;

/// <summary>A draft attachment failed validation (the sender must remove it before sending).</summary>
public sealed record DraftFileValidationFailedEvent(ulong TransferId, ulong FileId, string ValidationStatus) : IOemEvent;

public sealed record FirstReceiptEvent(ulong TransferId, ulong FileId) : IOemEvent;

public sealed record FilePurgedEvent(ulong TransferId, ulong FileId) : IOemEvent;

public sealed record FilePurgeFailingEvent(ulong TransferId, ulong FileId, int Attempts) : IOemEvent;

public sealed record FileMissingEvent(ulong TransferId, ulong FileId, string PayloadStatus) : IOemEvent;

public interface IOemEventHandler
{
    Task HandleAsync(OemUnitOfWork uow, IOemEvent domainEvent, CancellationToken ct);
}

public sealed class OemEventDispatcher(IEnumerable<IOemEventHandler> handlers)
{
    private readonly IOemEventHandler[] handlers = handlers.ToArray();

    /// <summary>Runs handlers for all raised events (including events raised by handlers) and then commits.</summary>
    internal async Task CommitAsync(OemUnitOfWork uow, CancellationToken ct)
    {
        for (var round = 0; round < 10; round++)
        {
            var events = uow.TakeEvents();
            if (events.Count == 0)
            {
                await uow.CommitAsync(ct);
                return;
            }
            foreach (var domainEvent in events)
            foreach (var handler in handlers)
                await handler.HandleAsync(uow, domainEvent, ct);
        }
        throw new InvalidOperationException("OEM domain events did not settle.");
    }
}
