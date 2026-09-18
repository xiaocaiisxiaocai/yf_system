using Yf.Api.Modules.Oem.Common;

namespace Yf.Api.Modules.Oem.Transfers;

/// <summary>The facts about a transfer that visibility depends on.</summary>
public sealed record TransferFacts(string Direction, ulong CompanyId, string Lifecycle, ulong? InternalSenderUserId, ulong? OemSenderAccountId);

/// <summary>
/// The facts about a caller: realm identity, internal permissions, and whether the
/// caller currently holds an activated (PENDING) approval task on the transfer, or
/// held any task on it historically.
/// </summary>
public sealed record ActorFacts(
    string Realm, ulong Id, ulong? CompanyId, bool CanView, bool CanDownload, bool CanCreate,
    bool HasActiveReviewTask, bool HasAnyTask)
{
    public static ActorFacts For(OemActor actor, IReadOnlySet<string> permissions, bool hasActiveReviewTask, bool hasAnyTask) => actor switch
    {
        OemAccountActor account => new(OemRealms.Oem, account.AccountId, account.CompanyId, false, false, false, false, false),
        _ => new(OemRealms.Internal, actor.Id, null,
            permissions.Contains(OemPermissions.TransferView), permissions.Contains(OemPermissions.FileDownload),
            permissions.Contains(OemPermissions.TransferCreate), hasActiveReviewTask, hasAnyTask),
    };
}

public enum ContentPurpose { None, Recipient, Sender, Review }

public sealed record TransferCapabilities(bool View, bool EditDraft, ContentPurpose ContentAccess)
{
    public bool CanReadContent => ContentAccess != ContentPurpose.None;
    public bool IsRecipient => ContentAccess == ContentPurpose.Recipient;
}

/// <summary>
/// Single source of truth for who may see a transfer and read its content (pure
/// function, unit-tested). File-level conditions — CLEAN, AVAILABLE, not past its
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

        // Drafts are private to the person who is writing them.
        if (transfer.Lifecycle == TransferLifecycle.Draft)
            return new(isActualSender, isActualSender, isActualSender ? ContentPurpose.Sender : ContentPurpose.None);

        bool view;
        if (outbound)
            // The vendor learns nothing about an outbound transfer until it is released.
            view = isOem ? ownCompany && transfer.Lifecycle == TransferLifecycle.Released
                : isActualSender || actor.CanView || actor.HasAnyTask;
        else
            view = isOem ? ownCompany : actor.CanView;
        if (!view) return new(false, false, ContentPurpose.None);
        if (closedWithoutRelease) return new(true, false, ContentPurpose.None);

        ContentPurpose content;
        if (transfer.Lifecycle == TransferLifecycle.Sealed)
        {
            // Before release only the actual sender and an activated reviewer may read files.
            content = isActualSender ? ContentPurpose.Sender
                : outbound && !isOem && actor.HasActiveReviewTask ? ContentPurpose.Review
                : ContentPurpose.None;
        }
        else
        {
            // Released.
            content = outbound
                ? isOem ? ContentPurpose.Recipient
                    : isActualSender && actor.CanView && actor.CanDownload ? ContentPurpose.Sender : ContentPurpose.None
                : isOem ? ContentPurpose.Sender
                    : actor.CanView && actor.CanDownload ? ContentPurpose.Recipient : ContentPurpose.None;
        }
        return new(true, false, content);
    }
}
