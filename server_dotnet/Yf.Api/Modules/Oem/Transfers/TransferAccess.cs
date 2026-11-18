using Yf.Api.Modules.Oem.Common;

namespace Yf.Api.Modules.Oem.Transfers;

/// <summary>The facts about a transfer that visibility depends on.</summary>
public sealed record TransferFacts(
    string Direction,
    ulong CompanyId,
    string Lifecycle,
    ulong? InternalSenderUserId,
    ulong? OemSenderAccountId,
    bool ApprovalBlocked = false);

/// <summary>
/// The facts about a caller: realm identity, internal permissions, and whether the
/// caller currently holds an activated (PENDING) approval task on the transfer, or
/// held any task on it historically. <c>SeesAll</c> marks a system administrator; <c>IsNotified</c> marks someone the
/// vendor chose to notify about this inbound transfer; <c>InScope</c> marks a supervisor (organisation leader) whose
/// organisation, including everything below it, contains this transfer's internal sender or a notified person.
/// </summary>
public sealed record ActorFacts(
    string Realm, ulong Id, ulong? CompanyId, bool CanView, bool CanDownload, bool CanCreate,
    bool HasActiveReviewTask, bool HasAnyTask, bool CanRecover = false, bool SeesAll = false, bool IsNotified = false,
    bool InScope = false, bool CanUpdateDraft = false, bool CanDeleteDraft = false)
{
    public static ActorFacts For(OemActor actor, IReadOnlySet<string> permissions, bool hasActiveReviewTask, bool hasAnyTask,
        bool seesAll = false, bool isNotified = false, bool inScope = false) => actor switch
    {
        OemAccountActor account => new(OemRealms.Oem, account.AccountId, account.CompanyId, false, false, false, false, false),
        _ => new(OemRealms.Internal, actor.Id, null,
            permissions.Contains(OemPermissions.TransferView), permissions.Contains(OemPermissions.FileDownload),
            permissions.Contains(OemPermissions.TransferCreate), hasActiveReviewTask, hasAnyTask,
            permissions.Contains(OemPermissions.ApprovalRecover), seesAll, isNotified, inScope,
            // Draft writes are independent grants: update, delete (create holders keep it for retries) and send.
            permissions.Contains(OemPermissions.TransferUpdate),
            permissions.Contains(OemPermissions.TransferDelete) || permissions.Contains(OemPermissions.TransferCreate)),
    };

    /// <summary>View permission over this transfer although it is not the actor's own: administrators and supervisors in scope.</summary>
    public bool ViewsAll => CanView && (SeesAll || InScope);
}

public enum ContentPurpose { None, Recipient, Sender, Review }

public sealed record TransferCapabilities(
    bool View, bool EditDraft, ContentPurpose ContentAccess, bool RecoveryOnly = false,
    bool DeleteDraft = false, bool SendDraft = false)
{
    public bool CanReadContent => ContentAccess != ContentPurpose.None;
    public bool IsRecipient => ContentAccess == ContentPurpose.Recipient;
}

/// <summary>
/// Single source of truth for who may see a transfer and read its content (pure
/// function, unit-tested). File-level conditions — VALID, AVAILABLE, not past its
/// purge time — are checked separately by the delivery slice on every request.
/// </summary>
public static class TransferAccess
{
    public static TransferCapabilities Evaluate(TransferFacts transfer, ActorFacts actor)
    {
        var isOem = actor.Realm == OemRealms.Oem;
        var outbound = transfer.Direction == TransferDirections.InternalToOem;
        var ownCompany = isOem && actor.CompanyId == transfer.CompanyId;
        var isActualSender = isOem
            ? transfer.OemSenderAccountId == actor.Id
            : transfer.InternalSenderUserId == actor.Id;
        var closedWithoutRelease = transfer.Lifecycle is TransferLifecycle.Blocked or TransferLifecycle.Rejected
            or TransferLifecycle.Cancelled or TransferLifecycle.Abandoned;
        var recoveryView = !isOem && actor.CanRecover && outbound
            && transfer.Lifecycle == TransferLifecycle.Sealed && transfer.ApprovalBlocked;

        // Drafts are private to the person who is writing them; each draft write is gated by its own grant so the
        // detail view can never offer an action the corresponding endpoint refuses.
        if (transfer.Lifecycle == TransferLifecycle.Draft)
        {
            var canEdit = isActualSender && (isOem || actor.CanUpdateDraft);
            var canSend = isActualSender && (isOem || actor.CanCreate);
            var canDelete = isActualSender && (isOem || actor.CanDeleteDraft);
            return new(isActualSender, canEdit, isActualSender ? ContentPurpose.Sender : ContentPurpose.None,
                DeleteDraft: canDelete, SendDraft: canSend);
        }

        // Internal staff see their own transfers, the ones they approve and the inbound ones the vendor chose to notify
        // them about; only administrators (with view permission) see everyone's.
        bool view;
        if (outbound)
            // The vendor learns nothing about an outbound transfer until it is released.
            view = isOem ? ownCompany && transfer.Lifecycle == TransferLifecycle.Released
                : isActualSender || actor.ViewsAll || actor.HasAnyTask || recoveryView;
        else
            view = isOem ? ownCompany : actor.CanView && (actor.SeesAll || actor.IsNotified || actor.InScope);
        if (!view) return new(false, false, ContentPurpose.None);
        if (closedWithoutRelease) return new(true, false, ContentPurpose.None);

        ContentPurpose content;
        if (transfer.Lifecycle == TransferLifecycle.Sealed)
        {
            // Before release only the actual sender and the approvers (the current one and those of earlier
            // nodes) may read files; nothing spreads further until the transfer is released.
            content = isActualSender ? ContentPurpose.Sender
                : outbound && !isOem && (actor.HasActiveReviewTask || actor.HasAnyTask) ? ContentPurpose.Review
                : ContentPurpose.None;
        }
        else
        {
            // Released. On the company side what went out stays reviewable: the sender, every approver who
            // decided on it, and administrators holding view + download may read it. None of these is a vendor receipt.
            content = outbound
                ? isOem ? ContentPurpose.Recipient
                    : isActualSender ? ContentPurpose.Sender
                    : actor.HasAnyTask ? ContentPurpose.Review
                    : actor.ViewsAll && actor.CanDownload ? ContentPurpose.Sender : ContentPurpose.None
                : isOem ? ContentPurpose.Sender
                    : actor.CanView && actor.CanDownload ? ContentPurpose.Recipient : ContentPurpose.None;
        }
        var recoveryOnly = recoveryView && !isActualSender && !actor.ViewsAll && !actor.HasAnyTask;
        return new(true, false, content, recoveryOnly);
    }
}
