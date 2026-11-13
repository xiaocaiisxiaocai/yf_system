using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MySqlConnector;
using Yf.Api.Infrastructure;
using Yf.Api.Infrastructure.Entities;
using Yf.Api.Modules.Admin;

namespace Yf.Api.Modules.Outsource;

internal static class OutsourceMailStatuses
{
    internal const string Queued = "QUEUED";
    internal const string Sending = "SENDING";
    internal const string Sent = "SENT";
    internal const string Partial = "PARTIAL";
    internal const string Failed = "FAILED";
}

/// <summary>
/// Vendor mails of the outsourcing list. Purchasing previews the mails (one per vendor, plus a withdrawal for a vendor an
/// item was taken from), then confirms with the preview's fingerprint; the server alone decides vendor recipients and
/// the default copy list. Confirmation freezes content and attachment, marks the items sent and queues the mails for
/// <see cref="OutsourceMailWorker"/>. See 外协清单契约 → 发送.
/// </summary>
public sealed class OutsourceMailService(IDbContextFactory<YfDbContext> dbFactory, AuditService audit, OutsourceMailSignal signal)
{
    internal const int MaximumItems = 500;
    internal const int MaximumExtraCcPerMail = 20;
    private static readonly JsonSerializerOptions SnapshotJson = new(JsonSerializerDefaults.Web);

    private sealed record Address(string Email, string? Name, ulong? UserId);

    private sealed record Draft(
        string Key, string Kind, ulong VendorId, string VendorName, Address[] To, Address[] DefaultCc,
        OutsourceItemResponse[] Items, Dictionary<ulong, IReadOnlySet<string>> Changed);

    private sealed record Plan(Draft[] Drafts, string Fingerprint, OutsourceItemResponse[] Items);

    // ---- preview and send ----

    public async Task<OutsourceMailPreviewResponse> PreviewAsync(CurrentUser actor, OutsourceMailPreviewRequest request, CancellationToken ct)
    {
        var ids = ValidateItems(request.ItemIds);
        await using var context = await dbFactory.CreateDbContextAsync(ct);
        await AuthorizeReadAsync(context, actor, OutsourcePermissions.Manage, ct);
        var rows = await context.OutsourceItems.AsNoTracking().Where(x => ids.Contains(x.Id)).ToArrayAsync(ct);
        var plan = await PlanAsync(context, ids, rows, ct);
        var day = await BusinessDayAsync(context, ct);
        return new(plan.Fingerprint, plan.Drafts.Select(draft => new OutsourceMailDraft(
            draft.Key, draft.Kind, draft.VendorId, draft.VendorName,
            OutsourceMailContent.Subject(draft.Kind, draft.VendorName, day, draft.Items.Length),
            draft.To.Select(Json).ToArray(), draft.DefaultCc.Select(Json).ToArray(),
            draft.Items.Select(x => x.Id).ToArray(), draft.Items.Select(x => x.WorkOrderNo).ToArray(),
            draft.Items.Select(x => x.Customer ?? "").Distinct(StringComparer.Ordinal).Count(),
            OutsourceMailContent.Html(draft.Kind, draft.VendorName, draft.Items, draft.Changed))).ToArray());
    }

    public async Task<OutsourceMailSendResponse> SendAsync(CurrentUser actor, OutsourceMailSendRequest request, CancellationToken ct)
    {
        var ids = ValidateItems(request.ItemIds);
        var requestKey = request.RequestKey?.Trim() ?? "";
        if (requestKey.Length is < 8 or > 64 || !requestKey.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))
            throw ApiException.BadRequest("requestKey 无效");
        if (string.IsNullOrWhiteSpace(request.Fingerprint)) throw ApiException.BadRequest("请先预览邮件");

        await using var context = await dbFactory.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        actor = await AuthorizeWriteAsync(context, actor, OutsourcePermissions.Manage, ct);
        // A repeated confirmation (double click, retried request) returns what the first one queued.
        if (await context.OutsourceMailBatches.AnyAsync(x => x.RequestKey == requestKey, ct))
        {
            await transaction.RollbackAsync(ct);
            return new(await MailsAsync(context, context.OutsourceMails.Where(x => x.BatchKey == requestKey), ct));
        }
        var rows = new List<OutsourceItem>(ids.Length);
        foreach (var id in ids.Order())
        {
            rows.Add(await context.OutsourceItems.FromSqlInterpolated($"SELECT * FROM outsource_items WHERE id={id} FOR UPDATE")
                .AsNoTracking().SingleOrDefaultAsync(ct) ?? throw ApiException.NotFound("外协记录不存在或已被删除"));
        }
        var plan = await PlanAsync(context, ids, rows, ct);
        if (!string.Equals(plan.Fingerprint, request.Fingerprint, StringComparison.Ordinal))
            throw ApiException.VersionConflict("资料或收件人已变动，请重新预览后再发送");
        var extra = await ResolveExtraCcAsync(context, plan, request.ExtraCc, ct);

        var day = await BusinessDayAsync(context, ct);
        var now = await DbClock.UtcNowAsync(context, ct, 3);
        context.OutsourceMailBatches.Add(new OutsourceMailBatch { RequestKey = requestKey, CreatedBy = actor.Id });
        await context.SaveChangesAsync(ct);
        var mails = new List<OutsourceMail>();
        foreach (var draft in plan.Drafts)
        {
            var notice = draft.Kind == OutsourceMailKinds.Notice;
            var mail = new OutsourceMail
            {
                BatchKey = requestKey, Kind = draft.Kind, VendorId = draft.VendorId, CreatedBy = actor.Id,
                Subject = OutsourceMailContent.Subject(draft.Kind, draft.VendorName, day, draft.Items.Length),
                BodyHtml = OutsourceMailContent.Html(draft.Kind, draft.VendorName, draft.Items, draft.Changed),
                BodyText = OutsourceMailContent.Text(draft.Kind, draft.VendorName, draft.Items),
                AttachmentName = notice ? OutsourceMailContent.AttachmentName(draft.VendorName, day) : null,
                Attachment = notice ? OutsourceWorkbook.Build(draft.Items, draft.Changed, forVendor: true) : null,
                Status = OutsourceMailStatuses.Queued, NextAttemptAt = now,
            };
            context.OutsourceMails.Add(mail);
            await context.SaveChangesAsync(ct);
            var recipients = draft.To.Select(x => Recipient(mail.Id, "TO", x, "VENDOR"))
                .Concat(draft.DefaultCc.Select(x => Recipient(mail.Id, "CC", x, "DEFAULT_CC")))
                .Concat(extra.GetValueOrDefault(draft.Key, []).Select(x => Recipient(mail.Id, "CC", x, "EXTRA")));
            context.OutsourceMailRecipients.AddRange(recipients);
            context.OutsourceMailItems.AddRange(draft.Items.Select(x => new OutsourceMailItem { MailId = mail.Id, ItemId = x.Id, WorkOrderNo = x.WorkOrderNo }));
            mails.Add(mail);
        }
        await context.SaveChangesAsync(ct);

        // Everything mailed as a notice is now "sent" to its current vendor, with what it showed.
        foreach (var item in plan.Drafts.Where(x => x.Kind == OutsourceMailKinds.Notice).SelectMany(x => x.Items))
        {
            var snapshot = JsonSerializer.Serialize(OutsourceWorkbook.Snapshot(item), SnapshotJson);
            await context.OutsourceItems.Where(x => x.Id == item.Id).ExecuteUpdateAsync(s => s
                .SetProperty(x => x.SendStatus, OutsourceSendStatuses.Sent)
                .SetProperty(x => x.NotifiedVendorId, item.VendorId)
                .SetProperty(x => x.LastSentSnapshot, snapshot)
                .SetProperty(x => x.LastSentAt, now), ct);
        }
        await audit.WriteAsync(context.Database.Connection(), context.Database.RequireTransaction(), actor.Id,
            "OUTSOURCE_MAIL_SEND", "outsource_mail", null, new
            {
                targetName = "外协邮件",
                mails = plan.Drafts.Select(x => new { x.Kind, vendor = x.VendorName, count = x.Items.Length }).ToArray(),
                extraCc = extra.Values.Sum(x => x.Length),
            }, null, ct);
        await transaction.CommitAsync(ct);
        signal.Wake();
        var mailIds = mails.Select(x => x.Id).ToArray();
        return new(await MailsAsync(context, context.OutsourceMails.Where(x => mailIds.Contains(x.Id)), ct));
    }

    private async Task<Plan> PlanAsync(YfDbContext context, ulong[] ids, IReadOnlyCollection<OutsourceItem> rows, CancellationToken ct)
    {
        if (rows.Count != ids.Length) throw ApiException.NotFound("外协记录不存在或已被删除");
        var reference = await OutsourceReference.LoadAsync(context, ct);
        var items = rows.OrderBy(x => x.Id).ToArray();
        var responses = items.ToDictionary(x => x.Id, reference.Json);
        var vendors = await context.OutsourceVendors.AsNoTracking().ToDictionaryAsync(x => x.Id, ct);
        var contacts = (await context.OutsourceVendorContacts.AsNoTracking().Where(x => x.Status == AccountStatuses.Active)
            .OrderBy(x => x.SortNo).ThenBy(x => x.Id).ToArrayAsync(ct)).ToLookup(x => x.VendorId);

        var problems = new List<string>();
        foreach (var item in items)
        {
            var response = responses[item.Id];
            if (item.ClosedAt is not null) problems.Add($"工令 {item.WorkOrderNo} 已完成");
            else if (response.MatchState == OutsourceMatchStates.UnknownType) problems.Add($"工令 {item.WorkOrderNo} 的外发类型不在字典中");
            else if (response.MatchState == OutsourceMatchStates.VendorMismatch) problems.Add($"工令 {item.WorkOrderNo} 的外发类型与厂商不符");
            else if (vendors[item.VendorId].Status != AccountStatuses.Active) problems.Add($"工令 {item.WorkOrderNo} 的厂商已停用");
        }
        var notices = items.GroupBy(x => x.VendorId).OrderBy(g => vendors[g.Key].SortNo).ThenBy(g => g.Key).ToArray();
        var withdrawals = items.Where(x => x.NotifiedVendorId is { } old && old != x.VendorId)
            .GroupBy(x => x.NotifiedVendorId!.Value).OrderBy(g => g.Key).ToArray();
        foreach (var vendorId in notices.Select(g => g.Key).Concat(withdrawals.Select(g => g.Key)).Distinct())
        {
            if (!contacts[vendorId].Any()) problems.Add($"厂商「{vendors[vendorId].Name}」没有启用的联络邮箱");
        }
        if (problems.Count > 0) throw ApiException.BadRequest(string.Join("；", problems.Take(10)) + (problems.Count > 10 ? $"……共 {problems.Count} 项" : ""));

        var defaultCc = (await context.OutsourceDefaultCc.AsNoTracking()
                .Join(context.Users, cc => cc.UserId, user => user.Id, (cc, user) => new { cc.SortNo, user })
                .Where(x => x.user.Status == AccountStatuses.Active && x.user.UserType == UserTypes.Internal && x.user.Email != "")
                .OrderBy(x => x.SortNo).ThenBy(x => x.user.Id)
                .Select(x => new { x.user.Id, x.user.Email, x.user.RealName }).ToArrayAsync(ct))
            .Select(x => new Address(x.Email, x.RealName, x.Id)).ToArray();
        Address[] To(ulong vendorId) => contacts[vendorId].Select(x => new Address(x.Email, x.Name, null)).ToArray();

        var drafts = new List<Draft>();
        foreach (var group in notices)
        {
            var groupItems = group.Select(x => responses[x.Id]).ToArray();
            var changed = group.ToDictionary(x => x.Id, x => x.NotifiedVendorId == x.VendorId && x.LastSentSnapshot is { } json
                ? OutsourceWorkbook.Changed(responses[x.Id], JsonSerializer.Deserialize<Dictionary<string, string?>>(json, SnapshotJson))
                : (IReadOnlySet<string>)new HashSet<string>(StringComparer.Ordinal));
            drafts.Add(new($"{OutsourceMailKinds.Notice}:{group.Key}", OutsourceMailKinds.Notice, group.Key, vendors[group.Key].Name,
                To(group.Key), defaultCc, groupItems, changed));
        }
        foreach (var group in withdrawals)
        {
            drafts.Add(new($"{OutsourceMailKinds.Withdraw}:{group.Key}", OutsourceMailKinds.Withdraw, group.Key, vendors[group.Key].Name,
                To(group.Key), defaultCc, group.Select(x => responses[x.Id]).ToArray(), []));
        }

        var fingerprint = new StringBuilder();
        foreach (var draft in drafts)
        {
            fingerprint.Append(draft.Key).Append('|').Append(draft.VendorName).Append('|')
                .AppendJoin(',', draft.To.Select(x => x.Email.ToLowerInvariant()).Order(StringComparer.Ordinal)).Append('|')
                .AppendJoin(',', draft.DefaultCc.Select(x => x.Email.ToLowerInvariant()).Order(StringComparer.Ordinal)).Append('|')
                .AppendJoin(',', draft.Items.Select(x => $"{x.Id}:{x.UpdatedAt.Ticks}")).Append('\n');
        }
        // Reference data shown in the mail (type labels) also feeds the fingerprint through the rendered values.
        foreach (var item in responses.Values) fingerprint.AppendJoin('\u001f', OutsourceWorkbook.Snapshot(item).Values).Append('\n');
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fingerprint.ToString()))).ToLowerInvariant();
        return new(drafts.ToArray(), hash, responses.Values.ToArray());
    }

    private static async Task<Dictionary<string, Address[]>> ResolveExtraCcAsync(
        YfDbContext context, Plan plan, OutsourceExtraCc[]? requested, CancellationToken ct)
    {
        var result = new Dictionary<string, Address[]>(StringComparer.Ordinal);
        foreach (var entry in requested ?? [])
        {
            if (entry is null) continue;
            var draft = plan.Drafts.SingleOrDefault(x => x.Key == entry.Key) ?? throw ApiException.BadRequest("抄送对应的邮件不存在，请重新预览");
            var userIds = (entry.UserIds ?? []).Distinct().ToArray();
            var emails = (entry.Emails ?? []).Select(x => x?.Trim() ?? "").Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            foreach (var email in emails) AdminValidation.Email(email);
            var users = await context.Users.AsNoTracking().Where(x => userIds.Contains(x.Id))
                .Select(x => new { x.Id, x.Email, x.RealName, x.UserType, x.Status }).ToArrayAsync(ct);
            if (users.Length != userIds.Length || users.Any(x => x.UserType != UserTypes.Internal || x.Status != AccountStatuses.Active || x.Email == ""))
                throw ApiException.BadRequest("抄送人员必须是填写了邮箱的启用内部账号");
            var existing = draft.To.Concat(draft.DefaultCc).Select(x => x.Email).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var addresses = users.Select(x => new Address(x.Email, x.RealName, x.Id))
                .Concat(emails.Select(x => new Address(x, null, null)))
                .Where(x => existing.Add(x.Email)).ToArray();
            if (addresses.Length > MaximumExtraCcPerMail) throw ApiException.BadRequest($"每封邮件最多另加 {MaximumExtraCcPerMail} 位抄送");
            result[draft.Key] = addresses;
        }
        return result;
    }

    // ---- records ----

    public async Task<PageResponse<OutsourceMailResponse>> ListAsync(
        CurrentUser actor, ulong? vendorId, string? status, ulong page, uint size, ulong offset, CancellationToken ct)
    {
        await using var context = await dbFactory.CreateDbContextAsync(ct);
        await AuthorizeReadAsync(context, actor, OutsourcePermissions.View, ct);
        var query = context.OutsourceMails.AsNoTracking();
        if (vendorId is { } vendor) query = query.Where(x => x.VendorId == vendor);
        if (!string.IsNullOrEmpty(status)) query = query.Where(x => x.Status == status);
        var total = (ulong)await query.LongCountAsync(ct);
        var mails = await MailsAsync(context, query.OrderByDescending(x => x.Id).Skip((int)Math.Min(offset, int.MaxValue)).Take((int)size), ct);
        return new(mails, total, page, size);
    }

    /// <summary>Queues a failed mail again with exactly the content it had.</summary>
    public async Task<OutsourceMailResponse> RetryAsync(CurrentUser actor, ulong id, CancellationToken ct)
    {
        await using var context = await dbFactory.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        actor = await AuthorizeWriteAsync(context, actor, OutsourcePermissions.Manage, ct);
        var updated = await context.OutsourceMails.Where(x => x.Id == id && x.Status == OutsourceMailStatuses.Failed)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, OutsourceMailStatuses.Queued).SetProperty(x => x.Attempts, 0)
                .SetProperty(x => x.NextAttemptAt, DateTime.UtcNow).SetProperty(x => x.LastError, (string?)null), ct);
        if (updated == 0)
        {
            if (!await context.OutsourceMails.AnyAsync(x => x.Id == id, ct)) throw ApiException.NotFound();
            throw ApiException.Conflict("只有发送失败的邮件可以重新发送");
        }
        await context.OutsourceMailRecipients.Where(x => x.MailId == id).ExecuteUpdateAsync(s => s.SetProperty(x => x.Rejected, false), ct);
        await audit.WriteAsync(context.Database.Connection(), context.Database.RequireTransaction(), actor.Id,
            "OUTSOURCE_MAIL_RETRY", "outsource_mail", id, new { targetName = "外协邮件" }, null, ct);
        await transaction.CommitAsync(ct);
        signal.Wake();
        return (await MailsAsync(context, context.OutsourceMails.Where(x => x.Id == id), ct)).Single();
    }

    public async Task<(byte[] Content, string Name)> AttachmentAsync(CurrentUser actor, ulong id, CancellationToken ct)
    {
        await using var context = await dbFactory.CreateDbContextAsync(ct);
        await AuthorizeReadAsync(context, actor, OutsourcePermissions.View, ct);
        var row = await context.OutsourceMails.AsNoTracking().Where(x => x.Id == id)
            .Select(x => new { x.Attachment, x.AttachmentName }).SingleOrDefaultAsync(ct);
        if (row?.Attachment is null || row.AttachmentName is null) throw ApiException.NotFound("该邮件没有附件");
        return (row.Attachment, row.AttachmentName);
    }

    private static async Task<OutsourceMailResponse[]> MailsAsync(YfDbContext context, IQueryable<OutsourceMail> query, CancellationToken ct)
    {
        var mails = await query.AsNoTracking().Select(x => new
        {
            x.Id, x.BatchKey, x.Kind, x.VendorId, x.Subject, x.Status, x.AttachmentName, x.Attempts, x.LastError,
            x.CreatedBy, x.CreatedAt, x.SentAt,
        }).ToArrayAsync(ct);
        var ids = mails.Select(x => x.Id).ToArray();
        var recipients = (await context.OutsourceMailRecipients.AsNoTracking().Where(x => ids.Contains(x.MailId))
            .OrderBy(x => x.Id).ToArrayAsync(ct)).ToLookup(x => x.MailId);
        var items = (await context.OutsourceMailItems.AsNoTracking().Where(x => ids.Contains(x.MailId))
            .OrderBy(x => x.ItemId).ToArrayAsync(ct)).ToLookup(x => x.MailId);
        var vendorIds = mails.Select(x => x.VendorId).Distinct().ToArray();
        var vendors = await context.OutsourceVendors.AsNoTracking().Where(x => vendorIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.Name, ct);
        var userIds = mails.Select(x => x.CreatedBy).Distinct().ToArray();
        var users = await context.Users.AsNoTracking().Where(x => userIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.RealName, ct);
        return mails.Select(x => new OutsourceMailResponse(
            x.Id, x.BatchKey, x.Kind, x.VendorId, vendors.GetValueOrDefault(x.VendorId) ?? "", x.Subject, x.Status,
            recipients[x.Id].Select(r => new OutsourceMailRecipientResponse(r.Kind, r.Email, r.Name, r.Source, r.Rejected)).ToArray(),
            items[x.Id].Select(i => i.WorkOrderNo).ToArray(), x.AttachmentName, x.Attempts, x.LastError,
            users.GetValueOrDefault(x.CreatedBy), DateTime.SpecifyKind(x.CreatedAt, DateTimeKind.Utc),
            x.SentAt is { } sent ? DateTime.SpecifyKind(sent, DateTimeKind.Utc) : null)).ToArray();
    }

    // ---- helpers ----

    private static OutsourceMailRecipient Recipient(ulong mailId, string kind, Address address, string source) => new()
    {
        MailId = mailId, Kind = kind, Email = address.Email, Name = address.Name is { Length: > 64 } name ? name[..64] : address.Name, Source = source,
    };

    private static OutsourceMailAddress Json(Address address) => new(address.Email, address.Name);

    private static async Task<DateOnly> BusinessDayAsync(YfDbContext context, CancellationToken ct) =>
        DateOnly.FromDateTime((await DbClock.UtcNowAsync(context, ct, 3)).AddHours(8));

    private static ulong[] ValidateItems(ulong[]? ids)
    {
        var distinct = (ids ?? []).Distinct().ToArray();
        if (distinct.Length is < 1 or > MaximumItems) throw ApiException.BadRequest($"请选择 1–{MaximumItems} 条记录");
        return distinct;
    }

    private static async Task AuthorizeReadAsync(YfDbContext context, CurrentUser actor, string permission, CancellationToken ct)
    {
        AccessService.RequireInternal(actor);
        await context.Database.OpenConnectionAsync(ct);
        var connection = context.Database.Connection();
        actor = await AccessService.ReadActorAsync(connection, null, actor, ct);
        AccessService.RequireInternal(actor);
        await AccessService.RequirePermissionAsync(connection, null, actor, permission, ct);
    }

    private static async Task<CurrentUser> AuthorizeWriteAsync(YfDbContext context, CurrentUser actor, string permission, CancellationToken ct)
    {
        AccessService.RequireInternal(actor);
        var connection = context.Database.Connection();
        var transaction = context.Database.RequireTransaction();
        actor = await AccessService.LockActorAsync(connection, transaction, actor, ct);
        AccessService.RequireInternal(actor);
        await AccessService.RequirePermissionAsync(connection, transaction, actor, permission, ct);
        return actor;
    }
}
