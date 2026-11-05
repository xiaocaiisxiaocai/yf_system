using Yf.Api.Infrastructure;
using Yf.Api.Modules.Identity;

namespace Yf.Api.Modules.Projects;

/// <summary>
/// A supplier account may withdraw its own file or message within the configured number of minutes
/// (system settings <c>withdraw.supplier_file_minutes</c> / <c>withdraw.supplier_message_minutes</c>,
/// 0 disables). The window is measured with the database clock from the moment the item was stored and
/// uses the setting in force when the withdrawal is attempted.
/// </summary>
internal static class SupplierWithdrawal
{
    /// <summary>When <paramref name="actor"/> may still withdraw an item it authored, or null when it may not.</summary>
    internal static DateTime? Deadline(CurrentUser actor, ulong authorId, DateTime createdAt, int minutes, DateTime now)
    {
        if (actor.UserType != UserTypes.Supplier || authorId != actor.Id || minutes <= 0) return null;
        var deadline = createdAt.AddMinutes(minutes);
        return deadline > now ? deadline : null;
    }

    /// <summary>Throws the user-facing reason when the window is closed (or withdrawal is disabled).</summary>
    internal static void RequireOpen(CurrentUser actor, ulong authorId, DateTime createdAt, int minutes, DateTime now, string what)
    {
        if (minutes <= 0) throw ApiException.Conflict($"当前不允许撤回{what}");
        if (Deadline(actor, authorId, createdAt, minutes, now) is null)
            throw ApiException.Conflict($"已超过 {minutes} 分钟撤回时限，不能撤回{what}");
    }
}
