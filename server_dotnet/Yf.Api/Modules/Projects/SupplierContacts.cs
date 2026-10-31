using Microsoft.EntityFrameworkCore;
using MySqlConnector;
using Yf.Api.Infrastructure;

namespace Yf.Api.Modules.Projects;

public sealed record SupplierContactOption(ulong Id, string Name, bool Enabled);

/// <summary>The (id, name) a supplier message or upload is signed with; both null for internal senders.</summary>
internal readonly record struct ContactSignature(ulong? ContactId, string? Name);

/// <summary>
/// Read-side access to supplier contacts for project work: Robot-owner pickers (internal) and the signature
/// a shared supplier login attaches to messages and uploads. See 厂商联络人与签名契约.
/// </summary>
internal static class SupplierContacts
{
    /// <summary>
    /// Internal users with the project menu list a supplier's contacts to pick Robot owners. Disabled contacts are
    /// included so the picker can show selections made before they were disabled. Supplier users cannot list
    /// contacts: their typed signature is checked against this list, so it must not be handed to them.
    /// </summary>
    internal static async Task<SupplierContactOption[]> OptionsAsync(
        MySqlConnection conn, CurrentUser actor, ulong? supplierId, CancellationToken ct)
    {
        await using var tx = await AppDb.BeginTransactionAsync(conn, ct);
        var current = await AccessService.ReadActorAsync(conn, tx, actor, ct);
        await AccessService.RequirePermissionAsync(conn, tx, current, "project:list", ct);
        AccessService.RequireInternal(current);
        var target = supplierId ?? throw ApiException.BadRequest("请指定供应商");
        await using var db = EfDb.Use(conn, tx);
        var result = await db.SupplierContacts.AsNoTracking().Where(contact => contact.SupplierId == target)
            .OrderBy(contact => contact.SortNo).ThenBy(contact => contact.Id)
            .Select(contact => new SupplierContactOption(contact.Id, contact.Name, contact.Status == AccountStatuses.Active))
            .ToArrayAsync(ct);
        await tx.CommitAsync(ct);
        return result;
    }

    internal static string? NormalizeSignerName(string? value)
    {
        var name = value?.Trim();
        return string.IsNullOrEmpty(name) ? null : name;
    }

    /// <summary>
    /// Supplier messages and uploads carry a typed signature: the name of the contact who provided them. It is
    /// required whenever the supplier has an active contact and must exactly match one of those contacts
    /// (after trimming); suppliers without contacts keep working unsigned. Internal senders never sign.
    /// </summary>
    internal static async Task<ContactSignature> ResolveSignatureAsync(
        YfDbContext db, CurrentUser actor, string? signerName, CancellationToken ct)
    {
        if (actor.IsInternal || actor.SupplierId is not { } supplierId) return default;
        var name = NormalizeSignerName(signerName);
        var active = db.SupplierContacts.AsNoTracking()
            .Where(contact => contact.SupplierId == supplierId && contact.Status == AccountStatuses.Active);
        if (name is null)
        {
            if (await active.AnyAsync(ct)) throw ApiException.BadRequest("请填写签名（提供人姓名）");
            return default;
        }
        if (name.EnumerateRunes().Count() > 32) throw ApiException.BadRequest("签名不能超过 32 个字符");
        // The column collation compares case- and width-insensitively; store the registered spelling.
        var contact = await active.Where(item => item.Name == name)
            .Select(item => new { item.Id, item.Name }).SingleOrDefaultAsync(ct)
            ?? throw ApiException.BadRequest($"签名「{name}」不是该供应商登记的联络人，请核对姓名");
        return new(contact.Id, contact.Name);
    }
}
