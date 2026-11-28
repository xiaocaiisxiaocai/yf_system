using System.Data;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MySqlConnector;
using Yf.Api.Infrastructure;
using Yf.Api.Infrastructure.Entities;
using Yf.Api.Modules.Admin;
using Yf.Api.Modules.Identity;
using Yf.Api.Modules.Oem.Data;

namespace Yf.Api.Modules.Outsource;

public sealed class OutsourceContactService(IDbContextFactory<YfDbContext> dbFactory, AuditService audit)
{
    internal const int MaximumImportRows = 2000;
    private const int MaximumTextLength = 2000;
    private const int MaximumPeoplePerCell = 20;
    private const string VersionConflictMessage = "联络表数据已被他人修改，请刷新后重试";
    private const string ImportPreviewConflictMessage = "导入预览已失效，请重新预览并确认";

    private static async Task<MySqlNamedLock> LockContactWritesAsync(
        YfDbContext context, CurrentUser actor, CancellationToken ct)
    {
        AccessService.RequireInternal(actor);
        // Serialize writes to this matrix, including mutations across rows and the people master.
        // The caller holds the lease through commit or rollback on the same open connection.
        await context.Database.OpenConnectionAsync(ct);
        var connection = context.Database.Connection();
        return await MySqlNamedLock.TryAcquireAsync(connection, MySqlNamedLock.Name("contact-matrix", connection.Database), 10, ct)
            ?? throw ApiException.Conflict("联络表正在更新，请稍后重试");
    }

    // =========================================================================
    // 1. READ MATRIX, EXPORT & TEMPLATE
    // =========================================================================

    public async Task<OutsourceContactMatrixResponse> GetMatrixAsync(
        CurrentUser actor, string? group, string? keyword, ulong? vendorId, CancellationToken ct)
    {
        await using var context = await dbFactory.CreateDbContextAsync(ct);
        await OutsourceItemService.AuthorizeReadAsync(context, actor, null, ct);
        var codes = await AccessService.PermissionCodesAsync(context.Database.Connection(), null, actor.Id, ct);
        var canManage = codes.Contains(OutsourcePermissions.ContactManage, StringComparer.Ordinal);
        var canConfigure = codes.Contains(OutsourcePermissions.ContactSettings, StringComparer.Ordinal);
        return await LoadMatrixAsync(context, group, keyword, vendorId, canManage, canConfigure, ct);
    }

    public async Task<OutsourceExport> ExportAsync(
        CurrentUser actor, string? group, string? keyword, ulong? vendorId, CancellationToken ct)
    {
        await using var context = await dbFactory.CreateDbContextAsync(ct);
        await OutsourceItemService.AuthorizeReadAsync(context, actor, null, ct);
        var matrix = await LoadMatrixAsync(context, group, keyword, vendorId, false, false, ct);
        if (matrix.Columns.Length == 0)
            throw ApiException.BadRequest("请先配置至少一列后再导出");
        var vendorNames = matrix.Vendors.ToDictionary(v => v.Id, v => v.Name);
        var bytes = OutsourceContactGrid.BuildInternalWorkbook(matrix.Columns, matrix.Rows, vendorNames);
        var today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(8)).ToString("yyyyMMdd");
        return new(bytes, $"对接联络表_{today}.xlsx");
    }

    public async Task<OutsourceExport> TemplateAsync(CurrentUser actor, CancellationToken ct)
    {
        await using var context = await dbFactory.CreateDbContextAsync(ct);
        await OutsourceItemService.AuthorizeReadAsync(context, actor, OutsourcePermissions.ContactManage, ct);
        var columns = await LoadActiveColumnsAsync(context, ct);
        if (columns.Length == 0)
            throw ApiException.BadRequest("请先配置至少一列后再下载导入模板");
        var bytes = OutsourceContactGrid.BuildTemplateWorkbook(columns);
        return new(bytes, "对接联络表导入模板.xlsx");
    }

    // =========================================================================
    // 2. COLUMN SETTINGS (outsource:contact_settings)
    // =========================================================================

    public async Task<OutsourceContactColumnResponse[]> ListColumnsAsync(CurrentUser actor, CancellationToken ct)
    {
        await using var context = await dbFactory.CreateDbContextAsync(ct);
        await OutsourceItemService.AuthorizeReadAsync(context, actor, OutsourcePermissions.ContactSettings, ct);
        return await LoadActiveColumnsAsync(context, ct);
    }

    public async Task<OutsourceContactColumnResponse> CreateColumnAsync(
        CurrentUser actor, OutsourceContactColumnCreate request, CancellationToken ct)
    {
        var title = ValidateTitle(request.Title);
        var dataType = ValidateDataType(request.DataType);
        var personDisplay = dataType == ContactColumnTypes.Person
            ? ValidatePersonDisplay(request.PersonDisplay ?? ContactPersonDisplays.Combined)
            : ContactPersonDisplays.Combined;
        var phoneTitle = personDisplay == ContactPersonDisplays.Split ? NormalizePhoneTitle(request.PhoneTitle) : null;
        var widthHint = NormalizeWidthHint(request.WidthHint);

        await using var context = await dbFactory.CreateDbContextAsync(ct);
        await using var writeLock = await LockContactWritesAsync(context, actor, ct);
        await using var tx = await context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        actor = await OutsourceItemService.AuthorizeWriteAsync(context, actor, OutsourcePermissions.ContactSettings, ct);

        var columns = await context.OutsourceContactColumns
            .FromSqlRaw("SELECT * FROM outsource_contact_columns FOR UPDATE")
            .ToListAsync(ct);
        if (columns.Any(c => c.Status == AccountStatuses.Active && string.Equals(c.Title, title, StringComparison.OrdinalIgnoreCase)))
            throw ApiException.Conflict($"列标题「{title}」已存在");

        if (request.IsGroupColumn)
        {
            foreach (var existing in columns.Where(c => c.IsGroupColumn)) existing.IsGroupColumn = false;
        }

        var maxSort = columns.Where(c => c.Status == AccountStatuses.Active).Select(c => c.SortNo).DefaultIfEmpty(0).Max();
        var now = await DbClock.UtcNowAsync(context, ct, 3);
        var entity = new OutsourceContactColumn
        {
            ColKey = "col_" + Guid.NewGuid().ToString("N")[..8],
            Title = title,
            DataType = dataType,
            PersonDisplay = personDisplay,
            PhoneTitle = phoneTitle,
            AutoMerge = request.AutoMerge,
            VendorVisible = request.VendorVisible,
            IsGroupColumn = request.IsGroupColumn,
            SortNo = maxSort + 10,
            WidthHint = widthHint,
            Status = AccountStatuses.Active,
            CreatedAt = now,
            UpdatedAt = now,
        };
        context.OutsourceContactColumns.Add(entity);
        await context.SaveChangesAsync(ct);
        await WriteAuditAsync(context, actor.Id, "OUTSOURCE_CONTACT_COLUMN", "outsource_contact_column", entity.Id,
            new { targetName = entity.Title, operation = "CREATE", entity.DataType, entity.IsGroupColumn }, ct);
        await tx.CommitAsync(ct);
        return ToColumnResponse(entity);
    }

    public async Task<OutsourceContactColumnResponse> UpdateColumnAsync(
        CurrentUser actor, ulong id, OutsourceContactColumnUpdate request, CancellationToken ct)
    {
        await using var context = await dbFactory.CreateDbContextAsync(ct);
        await using var writeLock = await LockContactWritesAsync(context, actor, ct);
        await using var tx = await context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        actor = await OutsourceItemService.AuthorizeWriteAsync(context, actor, OutsourcePermissions.ContactSettings, ct);

        var columns = await context.OutsourceContactColumns
            .FromSqlRaw("SELECT * FROM outsource_contact_columns FOR UPDATE")
            .ToListAsync(ct);
        var column = columns.SingleOrDefault(c => c.Id == id && c.Status == AccountStatuses.Active)
            ?? throw ApiException.NotFound("列不存在或已被删除");

        var changes = new List<AuditChange>();
        if (request.Title is not null)
        {
            var title = ValidateTitle(request.Title);
            if (columns.Any(c => c.Id != id && c.Status == AccountStatuses.Active && string.Equals(c.Title, title, StringComparison.OrdinalIgnoreCase)))
                throw ApiException.Conflict($"列标题「{title}」已存在");
            if (column.Title != title)
            {
                changes.Add(new("title", "列标题", column.Title, title));
                column.Title = title;
            }
        }

        if (request.DataType is not null)
        {
            var nextType = ValidateDataType(request.DataType);
            if (nextType != column.DataType)
            {
                var switchingBetweenTextAndPerson =
                    (column.DataType == ContactColumnTypes.Person) != (nextType == ContactColumnTypes.Person);
                if (switchingBetweenTextAndPerson)
                {
                    var hasValues = await context.OutsourceContactRowValues.AnyAsync(v => v.ColumnId == id && v.TextValue != "", ct);
                    var hasPeople = await context.OutsourceContactRowPeople.AnyAsync(p => p.ColumnId == id, ct);
                    if (hasValues || hasPeople)
                        throw ApiException.Conflict("该列已有填写内容，不能在文本与人员类型间切换");
                }
                changes.Add(new("dataType", "列类型", column.DataType, nextType));
                column.DataType = nextType;
            }
        }

        if (column.DataType == ContactColumnTypes.Person)
        {
            if (request.PersonDisplay is not null)
            {
                var display = ValidatePersonDisplay(request.PersonDisplay);
                if (column.PersonDisplay != display)
                {
                    changes.Add(new("personDisplay", "人员显示", column.PersonDisplay, display));
                    column.PersonDisplay = display;
                }
            }
            if (column.PersonDisplay == ContactPersonDisplays.Split && request.PhoneTitle is not null)
            {
                var pt = NormalizePhoneTitle(request.PhoneTitle);
                if (column.PhoneTitle != pt)
                {
                    changes.Add(new("phoneTitle", "电话列标题", column.PhoneTitle ?? "", pt ?? ""));
                    column.PhoneTitle = pt;
                }
            }
            else if (column.PersonDisplay != ContactPersonDisplays.Split)
            {
                column.PhoneTitle = null;
            }
        }
        else
        {
            column.PersonDisplay = ContactPersonDisplays.Combined;
            column.PhoneTitle = null;
        }

        if (request.AutoMerge is { } autoMerge && column.AutoMerge != autoMerge)
        {
            changes.Add(new("autoMerge", "自动合并", column.AutoMerge ? "是" : "否", autoMerge ? "是" : "否"));
            column.AutoMerge = autoMerge;
        }
        if (request.VendorVisible is { } vendorVisible && column.VendorVisible != vendorVisible)
        {
            changes.Add(new("vendorVisible", "外包可见", column.VendorVisible ? "是" : "否", vendorVisible ? "是" : "否"));
            column.VendorVisible = vendorVisible;
        }
        if (request.IsGroupColumn is { } isGroup && column.IsGroupColumn != isGroup)
        {
            if (isGroup)
            {
                foreach (var other in columns.Where(c => c.Id != id && c.IsGroupColumn))
                    other.IsGroupColumn = false;
            }
            changes.Add(new("isGroupColumn", "分组列", column.IsGroupColumn ? "是" : "否", isGroup ? "是" : "否"));
            column.IsGroupColumn = isGroup;
        }
        if (request.WidthHint is not null)
            column.WidthHint = NormalizeWidthHint(request.WidthHint);

        column.UpdatedAt = await DbClock.UtcNowAsync(context, ct, 3);
        await context.SaveChangesAsync(ct);
        if (changes.Count > 0)
        {
            await WriteAuditAsync(context, actor.Id, "OUTSOURCE_CONTACT_COLUMN", "outsource_contact_column", column.Id,
                new { targetName = column.Title, operation = "UPDATE", changes }, ct);
        }
        await tx.CommitAsync(ct);
        return ToColumnResponse(column);
    }

    public async Task<OutsourceContactColumnResponse[]> ReorderColumnsAsync(
        CurrentUser actor, OutsourceContactColumnOrderRequest request, CancellationToken ct)
    {
        var ids = request.ColumnIds ?? throw ApiException.BadRequest("请提供列顺序");
        await using var context = await dbFactory.CreateDbContextAsync(ct);
        await using var writeLock = await LockContactWritesAsync(context, actor, ct);
        await using var tx = await context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        actor = await OutsourceItemService.AuthorizeWriteAsync(context, actor, OutsourcePermissions.ContactSettings, ct);

        var columns = await context.OutsourceContactColumns
            .FromSqlRaw("SELECT * FROM outsource_contact_columns FOR UPDATE")
            .ToListAsync(ct);
        var active = columns.Where(c => c.Status == AccountStatuses.Active).ToArray();
        if (ids.Length != active.Length || ids.Distinct().Count() != ids.Length || !ids.ToHashSet().SetEquals(active.Select(c => c.Id)))
            throw ApiException.VersionConflict("列定义已变化，请刷新后重试");

        var now = await DbClock.UtcNowAsync(context, ct, 3);
        for (var i = 0; i < ids.Length; i++)
        {
            var col = active.Single(c => c.Id == ids[i]);
            col.SortNo = (i + 1) * 10;
            col.UpdatedAt = now;
        }
        await context.SaveChangesAsync(ct);
        await WriteAuditAsync(context, actor.Id, "OUTSOURCE_CONTACT_COLUMN", "outsource_contact_column", null,
            new { targetName = "联络表列顺序", operation = "REORDER", count = ids.Length }, ct);
        await tx.CommitAsync(ct);
        return active.OrderBy(c => c.SortNo).ThenBy(c => c.Id).Select(ToColumnResponse).ToArray();
    }

    public async Task<EmptyResponse> DeleteColumnAsync(CurrentUser actor, ulong id, CancellationToken ct)
    {
        await using var context = await dbFactory.CreateDbContextAsync(ct);
        await using var writeLock = await LockContactWritesAsync(context, actor, ct);
        await using var tx = await context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        actor = await OutsourceItemService.AuthorizeWriteAsync(context, actor, OutsourcePermissions.ContactSettings, ct);

        var column = await context.OutsourceContactColumns
            .FromSqlInterpolated($"SELECT * FROM outsource_contact_columns WHERE id={id} FOR UPDATE")
            .SingleOrDefaultAsync(ct) ?? throw ApiException.NotFound("列不存在或已被删除");

        await context.OutsourceContactRowValues.Where(x => x.ColumnId == id).ExecuteDeleteAsync(ct);
        await context.OutsourceContactRowPeople.Where(x => x.ColumnId == id).ExecuteDeleteAsync(ct);
        await context.OutsourceContactColumns.Where(x => x.Id == id).ExecuteDeleteAsync(ct);
        await WriteAuditAsync(context, actor.Id, "OUTSOURCE_CONTACT_COLUMN", "outsource_contact_column", id,
            new { targetName = column.Title, operation = "DELETE" }, ct);
        await tx.CommitAsync(ct);
        return EmptyResponse.Instance;
    }

    // =========================================================================
    // 3. ROW & CELL MANAGEMENT (outsource:contact_manage)
    // =========================================================================

    public async Task<OutsourceContactRowResponse> CreateRowAsync(
        CurrentUser actor, OutsourceContactRowCreate request, CancellationToken ct)
    {
        await using var context = await dbFactory.CreateDbContextAsync(ct);
        await using var writeLock = await LockContactWritesAsync(context, actor, ct);
        await using var tx = await context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        actor = await OutsourceItemService.AuthorizeWriteAsync(context, actor, OutsourcePermissions.ContactManage, ct);

        var activeCols = await LoadActiveColumnEntitiesLockedAsync(context, ct);
        if (activeCols.Length == 0)
            throw ApiException.BadRequest("请先配置至少一列后再新增行");

        var rows = await context.OutsourceContactRows
            .FromSqlRaw("SELECT * FROM outsource_contact_rows ORDER BY sort_no, id FOR UPDATE")
            .ToListAsync(ct);

        var now = await DbClock.UtcNowAsync(context, ct, 3);
        var entity = new OutsourceContactRow
        {
            SortNo = 0,
            Visibility = ContactRowVisibilities.InternalOnly,
            Version = 1,
            CreatedBy = actor.Id,
            UpdatedBy = actor.Id,
            CreatedAt = now,
            UpdatedAt = now,
        };

        if (request.InsertAfterRowId is { } afterId)
        {
            var idx = rows.FindIndex(r => r.Id == afterId);
            if (idx < 0) throw ApiException.NotFound("指定的参考行不存在");
            rows.Insert(idx + 1, entity);
        }
        else
        {
            rows.Add(entity);
        }

        for (var i = 0; i < rows.Count; i++) rows[i].SortNo = (i + 1) * 10;
        context.OutsourceContactRows.Add(entity);
        await context.SaveChangesAsync(ct);

        await WriteAuditAsync(context, actor.Id, "OUTSOURCE_CONTACT_ROW_EDIT", "outsource_contact_row", entity.Id,
            new { targetName = $"第 {rows.IndexOf(entity) + 1} 行", operation = "CREATE" }, ct);
        await tx.CommitAsync(ct);
        return await LoadSingleRowResponseAsync(context, entity.Id, ct);
    }

    public async Task<OutsourceContactRowResponse> DuplicateRowAsync(CurrentUser actor, ulong id, CancellationToken ct)
    {
        await using var context = await dbFactory.CreateDbContextAsync(ct);
        await using var writeLock = await LockContactWritesAsync(context, actor, ct);
        await using var tx = await context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        actor = await OutsourceItemService.AuthorizeWriteAsync(context, actor, OutsourcePermissions.ContactManage, ct);

        var rows = await context.OutsourceContactRows
            .FromSqlRaw("SELECT * FROM outsource_contact_rows ORDER BY sort_no, id FOR UPDATE")
            .ToListAsync(ct);
        var idx = rows.FindIndex(r => r.Id == id);
        if (idx < 0) throw ApiException.NotFound("行不存在或已被删除");
        var source = rows[idx];

        var activeColIds = (await LoadActiveColumnEntitiesLockedAsync(context, ct)).Select(c => c.Id).ToHashSet();
        var vendorIds = await context.OutsourceContactRowVendors.Where(x => x.RowId == id).Select(x => x.VendorId).ToArrayAsync(ct);
        var values = await context.OutsourceContactRowValues.Where(x => x.RowId == id && activeColIds.Contains(x.ColumnId)).ToArrayAsync(ct);
        var people = await context.OutsourceContactRowPeople.Where(x => x.RowId == id && activeColIds.Contains(x.ColumnId)).ToArrayAsync(ct);

        var now = await DbClock.UtcNowAsync(context, ct, 3);
        var copy = new OutsourceContactRow
        {
            SortNo = 0,
            Visibility = source.Visibility,
            Version = 1,
            CreatedBy = actor.Id,
            UpdatedBy = actor.Id,
            CreatedAt = now,
            UpdatedAt = now,
        };
        rows.Insert(idx + 1, copy);
        for (var i = 0; i < rows.Count; i++) rows[i].SortNo = (i + 1) * 10;
        context.OutsourceContactRows.Add(copy);
        await context.SaveChangesAsync(ct);

        if (vendorIds.Length > 0)
            context.OutsourceContactRowVendors.AddRange(vendorIds.Select(vid => new OutsourceContactRowVendor { RowId = copy.Id, VendorId = vid }));
        if (values.Length > 0)
            context.OutsourceContactRowValues.AddRange(values.Select(v => new OutsourceContactRowValue { RowId = copy.Id, ColumnId = v.ColumnId, TextValue = v.TextValue }));
        if (people.Length > 0)
            context.OutsourceContactRowPeople.AddRange(people.Select(p => new OutsourceContactRowPerson { RowId = copy.Id, ColumnId = p.ColumnId, PersonId = p.PersonId, PrefixLabel = p.PrefixLabel, SortNo = p.SortNo }));

        await context.SaveChangesAsync(ct);
        await WriteAuditAsync(context, actor.Id, "OUTSOURCE_CONTACT_ROW_EDIT", "outsource_contact_row", copy.Id,
            new { targetName = $"第 {idx + 2} 行", operation = "DUPLICATE", sourceRowId = id }, ct);
        await tx.CommitAsync(ct);
        return await LoadSingleRowResponseAsync(context, copy.Id, ct);
    }

    public async Task<EmptyResponse> ReorderRowsAsync(
        CurrentUser actor, OutsourceContactRowOrderRequest request, CancellationToken ct)
    {
        var ids = request.RowIds ?? throw ApiException.BadRequest("请提供行顺序");
        await using var context = await dbFactory.CreateDbContextAsync(ct);
        await using var writeLock = await LockContactWritesAsync(context, actor, ct);
        await using var tx = await context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        actor = await OutsourceItemService.AuthorizeWriteAsync(context, actor, OutsourcePermissions.ContactManage, ct);

        var rows = await context.OutsourceContactRows
            .FromSqlRaw("SELECT * FROM outsource_contact_rows FOR UPDATE")
            .ToListAsync(ct);
        if (ids.Length != rows.Count || ids.Distinct().Count() != ids.Length || !ids.ToHashSet().SetEquals(rows.Select(r => r.Id)))
            throw ApiException.VersionConflict(VersionConflictMessage);

        var now = await DbClock.UtcNowAsync(context, ct, 3);
        for (var i = 0; i < ids.Length; i++)
        {
            var row = rows.Single(r => r.Id == ids[i]);
            row.SortNo = (i + 1) * 10;
            row.UpdatedBy = actor.Id;
            row.UpdatedAt = now;
        }
        await context.SaveChangesAsync(ct);
        await WriteAuditAsync(context, actor.Id, "OUTSOURCE_CONTACT_ROW_EDIT", "outsource_contact_row", null,
            new { targetName = "联络表行顺序", operation = "REORDER", count = ids.Length }, ct);
        await tx.CommitAsync(ct);
        return EmptyResponse.Instance;
    }

    public async Task<EmptyResponse> DeleteRowAsync(CurrentUser actor, ulong id, uint expectedVersion, CancellationToken ct)
    {
        await using var context = await dbFactory.CreateDbContextAsync(ct);
        await using var writeLock = await LockContactWritesAsync(context, actor, ct);
        await using var tx = await context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        actor = await OutsourceItemService.AuthorizeWriteAsync(context, actor, OutsourcePermissions.ContactManage, ct);

        var row = await context.OutsourceContactRows
            .FromSqlInterpolated($"SELECT * FROM outsource_contact_rows WHERE id={id} FOR UPDATE")
            .SingleOrDefaultAsync(ct) ?? throw ApiException.NotFound("行不存在或已被删除");
        if (row.Version != expectedVersion) throw ApiException.VersionConflict(VersionConflictMessage);

        await context.OutsourceContactRows.Where(x => x.Id == id).ExecuteDeleteAsync(ct);
        await WriteAuditAsync(context, actor.Id, "OUTSOURCE_CONTACT_ROW_EDIT", "outsource_contact_row", id,
            new { targetName = $"联络表行 #{id}", operation = "DELETE" }, ct);
        await tx.CommitAsync(ct);
        return EmptyResponse.Instance;
    }

    public async Task<OutsourceContactRowResponse[]> UpdateCellsAsync(
        CurrentUser actor, OutsourceContactCellsUpdateRequest request, CancellationToken ct)
    {
        var rowVersions = ValidateRowVersions(request.Rows);
        await using var context = await dbFactory.CreateDbContextAsync(ct);
        await using var writeLock = await LockContactWritesAsync(context, actor, ct);
        await using var tx = await context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        actor = await OutsourceItemService.AuthorizeWriteAsync(context, actor, OutsourcePermissions.ContactManage, ct);

        var column = await context.OutsourceContactColumns
            .FromSqlInterpolated($"SELECT * FROM outsource_contact_columns WHERE id={request.ColumnId} FOR UPDATE")
            .SingleOrDefaultAsync(ct);
        if (column is null || column.Status != AccountStatuses.Active)
            throw ApiException.NotFound("目标列不存在或已停用");

        var lockedRows = await LockAndVerifyRowsAsync(context, rowVersions, ct);
        var now = await DbClock.UtcNowAsync(context, ct, 3);

        if (column.DataType is ContactColumnTypes.Text or ContactColumnTypes.Multiline)
        {
            var text = (request.TextValue ?? "").Trim();
            if (text.Length > MaximumTextLength)
                throw ApiException.BadRequest($"「{column.Title}」最多 {MaximumTextLength} 个字符");

            var rowIds = lockedRows.Select(r => r.Id).ToArray();
            await context.OutsourceContactRowValues
                .Where(v => rowIds.Contains(v.RowId) && v.ColumnId == column.Id)
                .ExecuteDeleteAsync(ct);

            if (text.Length > 0)
            {
                context.OutsourceContactRowValues.AddRange(rowIds.Select(rid => new OutsourceContactRowValue
                {
                    RowId = rid,
                    ColumnId = column.Id,
                    TextValue = text,
                }));
            }
        }
        else if (column.DataType == ContactColumnTypes.Person)
        {
            var inputs = request.People ?? [];
            if (inputs.Length > MaximumPeoplePerCell)
                throw ApiException.BadRequest($"单个格位最多选择 {MaximumPeoplePerCell} 位联系人");
            if (inputs.Select(x => x.PersonId).Distinct().Count() != inputs.Length)
                throw ApiException.BadRequest("同一单元格不能重复添加同一位联系人");

            var personIds = inputs.Select(x => x.PersonId).ToArray();
            var peopleById = personIds.Length == 0
                ? new Dictionary<ulong, OutsourceContactPerson>()
                : await context.OutsourceContactPeople.AsNoTracking()
                    .Where(p => personIds.Contains(p.Id))
                    .ToDictionaryAsync(p => p.Id, ct);
            if (peopleById.Count != personIds.Length)
                throw ApiException.BadRequest("部分所选联系人不存在，请刷新后重试");

            var rowIds = lockedRows.Select(r => r.Id).ToArray();
            var rowVendors = await context.OutsourceContactRowVendors.AsNoTracking()
                .Where(rv => rowIds.Contains(rv.RowId))
                .ToArrayAsync(ct);
            var vendorNames = await LoadVendorNameMapAsync(context, ct);
            var assignedEntities = personIds.Select(pid => peopleById[pid]).ToArray();

            foreach (var row in lockedRows)
            {
                var vids = rowVendors.Where(rv => rv.RowId == row.Id).Select(rv => rv.VendorId).ToArray();
                OutsourceContactGrid.EnsureRowAntiLeak(row.Visibility, vids, assignedEntities, vendorNames);
            }

            await context.OutsourceContactRowPeople
                .Where(p => rowIds.Contains(p.RowId) && p.ColumnId == column.Id)
                .ExecuteDeleteAsync(ct);

            var entries = new List<OutsourceContactRowPerson>(rowIds.Length * inputs.Length);
            foreach (var rid in rowIds)
            {
                for (var i = 0; i < inputs.Length; i++)
                {
                    entries.Add(new OutsourceContactRowPerson
                    {
                        RowId = rid,
                        ColumnId = column.Id,
                        PersonId = inputs[i].PersonId,
                        PrefixLabel = ValidateOptionalPrefix(inputs[i].PrefixLabel),
                        SortNo = (i + 1) * 10,
                    });
                }
            }
            if (entries.Count > 0) context.OutsourceContactRowPeople.AddRange(entries);
        }

        foreach (var row in lockedRows)
        {
            row.Version += 1;
            row.UpdatedBy = actor.Id;
            row.UpdatedAt = now;
        }
        await context.SaveChangesAsync(ct);

        await WriteAuditAsync(context, actor.Id, "OUTSOURCE_CONTACT_ROW_EDIT", "outsource_contact_row",
            lockedRows.Length == 1 ? lockedRows[0].Id : null,
            new { targetName = column.Title, operation = "CELL_EDIT", rowCount = lockedRows.Length }, ct);
        await tx.CommitAsync(ct);
        return await LoadRowResponsesByIdsAsync(context, lockedRows.Select(r => r.Id).ToArray(), ct);
    }

    public async Task<OutsourceContactRowResponse[]> UpdateVisibilityAsync(
        CurrentUser actor, OutsourceContactVisibilityUpdateRequest request, CancellationToken ct)
    {
        var rowVersions = ValidateRowVersions(request.Rows);
        var visibility = request.Visibility?.Trim() ?? "";
        if (!ContactRowVisibilities.IsValid(visibility))
            throw ApiException.BadRequest("请选择合法的可见范围");
        var vendorIds = (request.VendorIds ?? []).Distinct().Order().ToArray();
        if (visibility == ContactRowVisibilities.SpecificVendors && vendorIds.Length == 0)
            throw ApiException.BadRequest("选择“仅指定外包可见”时，请至少勾选一家外包厂商");
        if (visibility != ContactRowVisibilities.SpecificVendors) vendorIds = [];

        await using var context = await dbFactory.CreateDbContextAsync(ct);
        await using var writeLock = await LockContactWritesAsync(context, actor, ct);
        await using var tx = await context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        actor = await OutsourceItemService.AuthorizeWriteAsync(context, actor, OutsourcePermissions.ContactManage, ct);

        var vendorNames = await LoadVendorNameMapAsync(context, ct);
        if (vendorIds.Length > 0)
        {
            var activeVendorIds = await context.OemCompanies.AsNoTracking()
                .Where(c => vendorIds.Contains(c.Id) && c.Status == AccountStatuses.Active)
                .Select(c => c.Id)
                .ToArrayAsync(ct);
            if (activeVendorIds.Length != vendorIds.Length)
                throw ApiException.BadRequest("所选外包厂商不存在或已停用，请刷新后重试");
        }

        var lockedRows = await LockAndVerifyRowsAsync(context, rowVersions, ct);
        var rowIds = lockedRows.Select(r => r.Id).ToArray();
        var rowPeople = await context.OutsourceContactRowPeople.AsNoTracking()
            .Where(rp => rowIds.Contains(rp.RowId))
            .Join(context.OutsourceContactPeople.AsNoTracking(), rp => rp.PersonId, p => p.Id, (rp, p) => new { rp.RowId, Person = p })
            .ToArrayAsync(ct);

        foreach (var row in lockedRows)
        {
            var assigned = rowPeople.Where(x => x.RowId == row.Id).Select(x => x.Person).ToArray();
            OutsourceContactGrid.EnsureRowAntiLeak(visibility, vendorIds, assigned, vendorNames);
        }

        var now = await DbClock.UtcNowAsync(context, ct, 3);
        await context.OutsourceContactRowVendors.Where(rv => rowIds.Contains(rv.RowId)).ExecuteDeleteAsync(ct);
        if (vendorIds.Length > 0)
        {
            context.OutsourceContactRowVendors.AddRange(
                from rid in rowIds
                from vid in vendorIds
                select new OutsourceContactRowVendor { RowId = rid, VendorId = vid });
        }

        foreach (var row in lockedRows)
        {
            row.Visibility = visibility;
            row.Version += 1;
            row.UpdatedBy = actor.Id;
            row.UpdatedAt = now;
        }
        await context.SaveChangesAsync(ct);

        await WriteAuditAsync(context, actor.Id, "OUTSOURCE_CONTACT_VISIBILITY", "outsource_contact_row",
            lockedRows.Length == 1 ? lockedRows[0].Id : null,
            new { targetName = "联络表可见范围", rowCount = lockedRows.Length, visibility, vendorIds }, ct);
        await tx.CommitAsync(ct);
        return await LoadRowResponsesByIdsAsync(context, rowIds, ct);
    }

    // =========================================================================
    // 4. PEOPLE MASTER & GLOBAL PHONE LINKAGE
    // =========================================================================

    public async Task<OutsourceContactPeopleResponse> ListPeopleAsync(
        CurrentUser actor, string? scopeType, ulong? vendorId, string? keyword, string? status, CancellationToken ct)
    {
        await using var context = await dbFactory.CreateDbContextAsync(ct);
        await OutsourceItemService.AuthorizeReadAsync(context, actor, null, ct);

        var query = context.OutsourceContactPeople.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(scopeType)) query = query.Where(p => p.ScopeType == scopeType.Trim());
        if (vendorId is { } vid) query = query.Where(p => p.VendorId == vid);
        if (!string.IsNullOrWhiteSpace(status)) query = query.Where(p => p.Status == status.Trim());
        var kw = keyword?.Trim() ?? "";
        if (kw.Length > 0)
        {
            var pattern = QueryValues.ContainsPattern(kw);
            query = query.Where(p =>
                EF.Functions.Like(p.Name, pattern, QueryValues.LikeEscape)
                || EF.Functions.Like(p.Phone, pattern, QueryValues.LikeEscape)
                || (p.TitleHint != null && EF.Functions.Like(p.TitleHint, pattern, QueryValues.LikeEscape)));
        }

        var people = await query.OrderBy(p => p.ScopeType).ThenBy(p => p.Name).ThenBy(p => p.Id).ToArrayAsync(ct);
        var vendors = await context.OemCompanies.AsNoTracking()
            .OrderBy(v => v.Name)
            .Select(v => new OutsourceContactVendorOption(v.Id, v.Name, v.Status))
            .ToArrayAsync(ct);
        var vendorNames = vendors.ToDictionary(v => v.Id, v => v.Name);

        var userIds = people.Where(p => p.UserId.HasValue).Select(p => p.UserId!.Value).Distinct().ToArray();
        var users = userIds.Length == 0
            ? new Dictionary<ulong, (string EmployeeNo, string RealName)>()
            : await context.Users.AsNoTracking()
                .Where(u => userIds.Contains(u.Id))
                .ToDictionaryAsync(u => u.Id, u => (u.EmployeeNo, u.RealName), ct);

        var items = people.Select(p =>
        {
            var vName = p.VendorId.HasValue ? vendorNames.GetValueOrDefault(p.VendorId.Value) : null;
            var uInfo = p.UserId.HasValue && users.TryGetValue(p.UserId.Value, out var u) ? u : default;
            return new OutsourceContactPersonResponse(
                p.Id, p.ScopeType, p.VendorId, vName, p.UserId, uInfo.EmployeeNo, uInfo.RealName,
                p.Name, p.Phone, p.Email, p.TitleHint, p.Status, p.UpdatedAt);
        }).ToArray();

        return new(items, vendors);
    }

    public async Task<OutsourceContactUserOption[]> ListUserOptionsAsync(CurrentUser actor, string? keyword, CancellationToken ct)
    {
        await using var context = await dbFactory.CreateDbContextAsync(ct);
        await OutsourceItemService.AuthorizeReadAsync(context, actor, OutsourcePermissions.ContactManage, ct);
        var query = context.Users.AsNoTracking()
            .Where(u => u.UserType == UserTypes.Internal && u.Status == AccountStatuses.Active);
        var kw = keyword?.Trim() ?? "";
        if (kw.Length > 0)
        {
            var pattern = QueryValues.ContainsPattern(kw);
            query = query.Where(u => EF.Functions.Like(u.RealName, pattern, QueryValues.LikeEscape)
                || EF.Functions.Like(u.EmployeeNo, pattern, QueryValues.LikeEscape));
        }
        return await query.OrderBy(u => u.RealName).ThenBy(u => u.Id).Take(200)
            .Select(u => new OutsourceContactUserOption(u.Id, u.EmployeeNo, u.RealName, u.Email))
            .ToArrayAsync(ct);
    }

    public async Task<OutsourceContactPersonResponse> CreatePersonAsync(
        CurrentUser actor, OutsourceContactPersonUpsert request, CancellationToken ct)
    {
        var (scopeType, vendorId, userId, name, phone, email, titleHint) = ValidatePersonUpsert(request);
        await using var context = await dbFactory.CreateDbContextAsync(ct);
        await using var writeLock = await LockContactWritesAsync(context, actor, ct);
        await using var tx = await context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        actor = await OutsourceItemService.AuthorizeWriteAsync(context, actor, OutsourcePermissions.ContactManage, ct);

        await ValidatePersonForeignKeysAndDuplicatesAsync(context, null, scopeType, vendorId, userId, name, phone, ct);
        var now = await DbClock.UtcNowAsync(context, ct, 3);
        var entity = new OutsourceContactPerson
        {
            ScopeType = scopeType,
            VendorId = vendorId,
            UserId = userId,
            Name = name,
            Phone = phone,
            Email = email,
            TitleHint = titleHint,
            Status = AccountStatuses.Active,
            UpdatedBy = actor.Id,
            CreatedAt = now,
            UpdatedAt = now,
        };
        context.OutsourceContactPeople.Add(entity);
        await SavePersonChangesAsync(context, ct);
        await WriteAuditAsync(context, actor.Id, "OUTSOURCE_CONTACT_PERSON", "outsource_contact_person", entity.Id,
            new { targetName = entity.Name, operation = "CREATE", entity.ScopeType, entity.VendorId, entity.Phone }, ct);
        await tx.CommitAsync(ct);
        return await LoadPersonResponseAsync(context, entity.Id, ct);
    }

    public async Task<OutsourceContactPersonResponse> UpdatePersonAsync(
        CurrentUser actor, ulong id, OutsourceContactPersonUpsert request, CancellationToken ct)
    {
        var (scopeType, vendorId, userId, name, phone, email, titleHint) = ValidatePersonUpsert(request);
        await using var context = await dbFactory.CreateDbContextAsync(ct);
        await using var writeLock = await LockContactWritesAsync(context, actor, ct);
        await using var tx = await context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        actor = await OutsourceItemService.AuthorizeWriteAsync(context, actor, OutsourcePermissions.ContactManage, ct);

        var person = await context.OutsourceContactPeople
            .FromSqlInterpolated($"SELECT * FROM outsource_contact_people WHERE id={id} FOR UPDATE")
            .SingleOrDefaultAsync(ct) ?? throw ApiException.NotFound("人员记录不存在");

        await ValidatePersonForeignKeysAndDuplicatesAsync(context, id, scopeType, vendorId, userId, name, phone, ct);

        if (person.ScopeType != scopeType || person.VendorId != vendorId)
        {
            var vendorNames = await LoadVendorNameMapAsync(context, ct);
            var candidate = new OutsourceContactPerson { Id = id, Name = name, ScopeType = scopeType, VendorId = vendorId };
            var refRowIds = await context.OutsourceContactRowPeople.AsNoTracking()
                .Where(rp => rp.PersonId == id)
                .Select(rp => rp.RowId)
                .Distinct()
                .ToArrayAsync(ct);
            if (refRowIds.Length > 0)
            {
                var rows = await context.OutsourceContactRows
                    .FromSqlRaw($"SELECT * FROM outsource_contact_rows WHERE id IN ({string.Join(",", refRowIds.Order())}) FOR UPDATE")
                    .ToArrayAsync(ct);
                var rowVendors = await context.OutsourceContactRowVendors.AsNoTracking()
                    .Where(rv => refRowIds.Contains(rv.RowId))
                    .ToArrayAsync(ct);
                foreach (var r in rows)
                {
                    var vids = rowVendors.Where(rv => rv.RowId == r.Id).Select(rv => rv.VendorId).ToArray();
                    OutsourceContactGrid.EnsureRowAntiLeak(r.Visibility, vids, [candidate], vendorNames);
                }
            }
        }

        var changes = AuditChange.OnlyChanged(
            new AuditChange("name", "姓名", person.Name, name),
            new AuditChange("phone", "电话", person.Phone, phone),
            new AuditChange("scopeType", "归属类型", person.ScopeType, scopeType),
            new AuditChange("titleHint", "备注", person.TitleHint ?? "", titleHint ?? ""));

        person.ScopeType = scopeType;
        person.VendorId = vendorId;
        person.UserId = userId;
        person.Name = name;
        person.Phone = phone;
        person.Email = email;
        person.TitleHint = titleHint;
        person.UpdatedBy = actor.Id;
        person.UpdatedAt = await DbClock.UtcNowAsync(context, ct, 3);
        await SavePersonChangesAsync(context, ct);

        if (changes.Length > 0)
        {
            await WriteAuditAsync(context, actor.Id, "OUTSOURCE_CONTACT_PERSON", "outsource_contact_person", id,
                new { targetName = person.Name, operation = "UPDATE", changes }, ct);
        }
        await tx.CommitAsync(ct);
        return await LoadPersonResponseAsync(context, id, ct);
    }

    public async Task<OutsourceContactPersonResponse> SetPersonStatusAsync(
        CurrentUser actor, ulong id, string? status, CancellationToken ct)
    {
        if (status is not (AccountStatuses.Active or AccountStatuses.Disabled))
            throw ApiException.BadRequest("状态只能是启用或停用");

        await using var context = await dbFactory.CreateDbContextAsync(ct);
        await using var writeLock = await LockContactWritesAsync(context, actor, ct);
        await using var tx = await context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        actor = await OutsourceItemService.AuthorizeWriteAsync(context, actor, OutsourcePermissions.ContactManage, ct);

        var person = await context.OutsourceContactPeople
            .FromSqlInterpolated($"SELECT * FROM outsource_contact_people WHERE id={id} FOR UPDATE")
            .SingleOrDefaultAsync(ct) ?? throw ApiException.NotFound("人员记录不存在");

        if (person.Status != status)
        {
            var before = person.Status;
            person.Status = status;
            person.UpdatedBy = actor.Id;
            person.UpdatedAt = await DbClock.UtcNowAsync(context, ct, 3);
            await context.SaveChangesAsync(ct);
            await WriteAuditAsync(context, actor.Id, "OUTSOURCE_CONTACT_PERSON", "outsource_contact_person", id,
                new
                {
                    targetName = person.Name,
                    operation = "STATUS",
                    changes = new[] { new AuditChange("status", "状态", before == AccountStatuses.Active ? "启用" : "停用", status == AccountStatuses.Active ? "启用" : "停用") },
                }, ct);
        }
        await tx.CommitAsync(ct);
        return await LoadPersonResponseAsync(context, id, ct);
    }

    public async Task<OutsourceContactPersonReferenceResponse[]> GetPersonReferencesAsync(
        CurrentUser actor, ulong id, CancellationToken ct)
    {
        await using var context = await dbFactory.CreateDbContextAsync(ct);
        await OutsourceItemService.AuthorizeReadAsync(context, actor, null, ct);

        var rows = await context.OutsourceContactRows.AsNoTracking()
            .OrderBy(r => r.SortNo).ThenBy(r => r.Id)
            .Select(r => r.Id)
            .ToArrayAsync(ct);
        var rowIndexMap = rows.Select((rid, idx) => (rid, RowNumber: idx + 1)).ToDictionary(x => x.rid, x => x.RowNumber);

        var columns = await LoadActiveColumnsAsync(context, ct);
        var groupCol = columns.FirstOrDefault(c => c.IsGroupColumn)
            ?? columns.FirstOrDefault(c => c.DataType is ContactColumnTypes.Text or ContactColumnTypes.Multiline);
        var colTitleMap = columns.ToDictionary(c => c.Id, c => c.Title);

        var refs = await context.OutsourceContactRowPeople.AsNoTracking()
            .Where(rp => rp.PersonId == id)
            .ToArrayAsync(ct);
        if (refs.Length == 0) return [];

        var refRowIds = refs.Select(r => r.RowId).Distinct().ToArray();
        var groupValues = groupCol is null
            ? new Dictionary<ulong, string>()
            : await context.OutsourceContactRowValues.AsNoTracking()
                .Where(v => refRowIds.Contains(v.RowId) && v.ColumnId == groupCol.Id)
                .ToDictionaryAsync(v => v.RowId, v => v.TextValue, ct);

        return refs
            .OrderBy(r => rowIndexMap.GetValueOrDefault(r.RowId, int.MaxValue))
            .ThenBy(r => r.SortNo)
            .Select(r => new OutsourceContactPersonReferenceResponse(
                r.RowId,
                rowIndexMap.GetValueOrDefault(r.RowId, 0),
                groupValues.GetValueOrDefault(r.RowId),
                r.ColumnId,
                colTitleMap.GetValueOrDefault(r.ColumnId) ?? $"列#{r.ColumnId}",
                r.PrefixLabel))
            .ToArray();
    }

    public async Task<OutsourceContactHandoverResponse> HandoverPersonAsync(
        CurrentUser actor, ulong sourcePersonId, OutsourceContactHandoverRequest request, CancellationToken ct)
    {
        if (request.TargetPersonId == 0 || request.TargetPersonId == sourcePersonId)
            throw ApiException.BadRequest("请选择不同的接替人员");

        await using var context = await dbFactory.CreateDbContextAsync(ct);
        await using var writeLock = await LockContactWritesAsync(context, actor, ct);
        await using var tx = await context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        actor = await OutsourceItemService.AuthorizeWriteAsync(context, actor, OutsourcePermissions.ContactManage, ct);

        var firstId = Math.Min(sourcePersonId, request.TargetPersonId);
        var secondId = Math.Max(sourcePersonId, request.TargetPersonId);
        var lockedPeople = await context.OutsourceContactPeople
            .FromSqlInterpolated($"SELECT * FROM outsource_contact_people WHERE id IN ({firstId}, {secondId}) ORDER BY id FOR UPDATE")
            .ToListAsync(ct);
        var source = lockedPeople.SingleOrDefault(p => p.Id == sourcePersonId) ?? throw ApiException.NotFound("原人员记录不存在");
        var target = lockedPeople.SingleOrDefault(p => p.Id == request.TargetPersonId) ?? throw ApiException.NotFound("接替人员不存在");
        if (target.Status != AccountStatuses.Active)
            throw ApiException.BadRequest("接替人员已停用，请选择启用状态的人员");
        if (source.ScopeType != target.ScopeType || source.VendorId != target.VendorId)
            throw ApiException.BadRequest("接替人员必须与原人员属于同一内部/外包厂商归属");

        var filterRowIds = request.RowIds is { Length: > 0 } ? request.RowIds.Distinct().ToArray() : null;
        var entriesQuery = context.OutsourceContactRowPeople.Where(rp => rp.PersonId == sourcePersonId);
        if (filterRowIds is not null) entriesQuery = entriesQuery.Where(rp => filterRowIds.Contains(rp.RowId));
        var entries = await entriesQuery.ToListAsync(ct);
        if (entries.Count == 0) return new(0);

        var affectedRowIds = entries.Select(e => e.RowId).Distinct().Order().ToArray();
        var affectedRows = await context.OutsourceContactRows
            .FromSqlRaw($"SELECT * FROM outsource_contact_rows WHERE id IN ({string.Join(",", affectedRowIds)}) FOR UPDATE")
            .ToArrayAsync(ct);
        var existingTargetCells = (await context.OutsourceContactRowPeople.AsNoTracking()
            .Where(rp => affectedRowIds.Contains(rp.RowId) && rp.PersonId == request.TargetPersonId)
            .Select(rp => new { rp.RowId, rp.ColumnId })
            .ToArrayAsync(ct))
            .Select(x => (x.RowId, x.ColumnId))
            .ToHashSet();

        var vendorNames = await LoadVendorNameMapAsync(context, ct);
        var rowVendors = await context.OutsourceContactRowVendors.AsNoTracking()
            .Where(rv => affectedRowIds.Contains(rv.RowId))
            .ToArrayAsync(ct);
        foreach (var row in affectedRows)
        {
            var vids = rowVendors.Where(rv => rv.RowId == row.Id).Select(rv => rv.VendorId).ToArray();
            OutsourceContactGrid.EnsureRowAntiLeak(row.Visibility, vids, [target], vendorNames);
        }

        foreach (var entry in entries)
        {
            if (existingTargetCells.Contains((entry.RowId, entry.ColumnId)))
            {
                context.OutsourceContactRowPeople.Remove(entry);
            }
            else
            {
                entry.PersonId = target.Id;
                existingTargetCells.Add((entry.RowId, entry.ColumnId));
            }
        }

        var now = await DbClock.UtcNowAsync(context, ct, 3);
        foreach (var row in affectedRows)
        {
            row.Version += 1;
            row.UpdatedBy = actor.Id;
            row.UpdatedAt = now;
        }
        await context.SaveChangesAsync(ct);

        await WriteAuditAsync(context, actor.Id, "OUTSOURCE_CONTACT_HANDOVER", "outsource_contact_person", sourcePersonId,
            new { targetName = $"{source.Name} -> {target.Name}", replacedCount = entries.Count, affectedRows = affectedRows.Length }, ct);
        await tx.CommitAsync(ct);
        return new(entries.Count);
    }

    // =========================================================================
    // 5. EXCEL IMPORT (PREVIEW & CONFIRM)
    // =========================================================================

    public async Task<OutsourceContactImportPreviewResponse> PreviewImportAsync(
        CurrentUser actor, OutsourceContactImportPreviewRequest request, CancellationToken ct)
    {
        await using var context = await dbFactory.CreateDbContextAsync(ct);
        await OutsourceItemService.AuthorizeReadAsync(context, actor, OutsourcePermissions.ContactManage, ct);
        await using var snapshot = await context.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        var plan = await PlanImportAsync(context, request.Mode, request.Rows, request.PersonOverrides, ct);
        return plan.Preview;
    }

    public async Task<OutsourceContactImportResponse> ImportAsync(
        CurrentUser actor, OutsourceContactImportRequest request, CancellationToken ct)
    {
        await using var context = await dbFactory.CreateDbContextAsync(ct);
        await using var writeLock = await LockContactWritesAsync(context, actor, ct);
        await using var tx = await context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        actor = await OutsourceItemService.AuthorizeWriteAsync(context, actor, OutsourcePermissions.ContactManage, ct);

        await context.OutsourceContactColumns.FromSqlRaw("SELECT * FROM outsource_contact_columns FOR UPDATE").LoadAsync(ct);
        await context.OutsourceContactRows.FromSqlRaw("SELECT * FROM outsource_contact_rows FOR UPDATE").LoadAsync(ct);
        await context.OutsourceContactPeople.FromSqlRaw("SELECT * FROM outsource_contact_people FOR UPDATE").LoadAsync(ct);

        var plan = await PlanImportAsync(context, request.Mode, request.Rows, request.PersonOverrides, ct);
        if (!string.Equals(request.PreviewVersion, plan.Preview.PreviewVersion, StringComparison.Ordinal))
            throw ApiException.VersionConflict(ImportPreviewConflictMessage);
        if (plan.Preview.Invalid > 0)
        {
            var first = plan.Preview.Rows.First(r => r.Status == "INVALID");
            throw ApiException.BadRequest($"第 {first.RowNumber} 行校验失败：{first.Message}");
        }
        if (plan.ReadyRows.Count == 0)
            throw ApiException.BadRequest("没有可导入的有效数据行");
        if (plan.Preview.PhoneChangeCount > 0 && !request.ConfirmPhoneUpdates)
            throw ApiException.BadRequest("存在同名人员的电话变更，请勾选确认同步更新电话后再导入");

        var now = await DbClock.UtcNowAsync(context, ct, 3);
        var createdPeople = new Dictionary<string, OutsourceContactPerson>(StringComparer.Ordinal);
        var peopleByIdentity = new Dictionary<ImportPersonIdentity, OutsourceContactPerson>();
        var updatedPhones = 0;

        foreach (var pPlan in plan.Preview.PeoplePlans)
        {
            if (pPlan.Action == "CREATE")
            {
                var identity = ImportPersonIdentity.From(pPlan.ScopeType, pPlan.VendorId, pPlan.Name);
                if (!peopleByIdentity.TryGetValue(identity, out var person))
                {
                    person = new OutsourceContactPerson
                    {
                        ScopeType = pPlan.ScopeType,
                        VendorId = pPlan.VendorId,
                        Name = pPlan.Name,
                        Phone = pPlan.Phone,
                        Status = AccountStatuses.Active,
                        UpdatedBy = actor.Id,
                        CreatedAt = now,
                        UpdatedAt = now,
                    };
                    peopleByIdentity.Add(identity, person);
                    context.OutsourceContactPeople.Add(person);
                }
                createdPeople[pPlan.MatchKey] = person;
            }
            else if (pPlan.Action == "UPDATE_PHONE" && pPlan.ExistingPersonId is { } existingId)
            {
                var existing = await context.OutsourceContactPeople.SingleAsync(p => p.Id == existingId, ct);
                if (existing.Phone != pPlan.Phone)
                {
                    existing.Phone = pPlan.Phone;
                    existing.UpdatedBy = actor.Id;
                    existing.UpdatedAt = now;
                    updatedPhones++;
                }
            }
        }
        await SavePersonChangesAsync(context, ct);

        var mode = plan.Preview.Mode;
        var replaceIds = plan.ReplacedRowIds;
        var replaced = replaceIds.Length == 0 ? 0 : await context.OutsourceContactRows
            .Where(r => replaceIds.Contains(r.Id)).ExecuteDeleteAsync(ct);

        var maxSort = await context.OutsourceContactRows.Select(r => (int?)r.SortNo).MaxAsync(ct) ?? 0;
        var insertedRows = new List<(PlannedImportRow Plan, OutsourceContactRow Entity)>(plan.ReadyRows.Count);
        foreach (var rPlan in plan.ReadyRows)
        {
            maxSort += 10;
            var entity = new OutsourceContactRow
            {
                SortNo = maxSort,
                Visibility = rPlan.Visibility,
                Version = 1,
                CreatedBy = actor.Id,
                UpdatedBy = actor.Id,
                CreatedAt = now,
                UpdatedAt = now,
            };
            context.OutsourceContactRows.Add(entity);
            insertedRows.Add((rPlan, entity));
        }
        await context.SaveChangesAsync(ct);

        foreach (var (rPlan, entity) in insertedRows)
        {
            if (rPlan.VendorIds.Length > 0)
            {
                context.OutsourceContactRowVendors.AddRange(rPlan.VendorIds.Select(vid =>
                    new OutsourceContactRowVendor { RowId = entity.Id, VendorId = vid }));
            }
            foreach (var (colId, val) in rPlan.Values)
            {
                if (string.IsNullOrEmpty(val)) continue;
                context.OutsourceContactRowValues.Add(new OutsourceContactRowValue
                {
                    RowId = entity.Id,
                    ColumnId = colId,
                    TextValue = val,
                });
            }
            foreach (var (colId, tokens) in rPlan.PersonTokens)
            {
                var seenPersonIds = new HashSet<ulong>();
                var sortNo = 0;
                foreach (var token in tokens)
                {
                    var personId = token.ResolvedPersonId
                        ?? createdPeople[token.MatchKey].Id;
                    if (!seenPersonIds.Add(personId)) continue;
                    sortNo += 10;
                    context.OutsourceContactRowPeople.Add(new OutsourceContactRowPerson
                    {
                        RowId = entity.Id,
                        ColumnId = colId,
                        PersonId = personId,
                        PrefixLabel = token.PrefixLabel,
                        SortNo = sortNo,
                    });
                }
            }
        }
        await context.SaveChangesAsync(ct);

        await WriteAuditAsync(context, actor.Id, "OUTSOURCE_CONTACT_IMPORT", "outsource_contact_row", null,
            new
            {
                targetName = "对接联络表 Excel 导入",
                mode,
                inserted = insertedRows.Count,
                replaced,
                createdPeople = peopleByIdentity.Count,
                updatedPhones,
            }, ct);
        await tx.CommitAsync(ct);
        return new(insertedRows.Count, replaced, peopleByIdentity.Count, updatedPhones, plan.Preview.Skipped);
    }

    // =========================================================================
    // 6. PRIVATE HELPERS
    // =========================================================================

    private sealed record PlannedPersonToken(string MatchKey, string? PrefixLabel, ulong? ResolvedPersonId);

    private sealed record PlannedImportRow(
        string Visibility,
        ulong[] VendorIds,
        Dictionary<ulong, string> Values,
        Dictionary<ulong, List<PlannedPersonToken>> PersonTokens);

    private sealed record ImportPlanResult(
        OutsourceContactImportPreviewResponse Preview,
        List<PlannedImportRow> ReadyRows,
        ulong[] ReplacedRowIds);

    private readonly record struct ImportPersonIdentity(string ScopeType, ulong? VendorId, string Name)
    {
        public static ImportPersonIdentity From(string scopeType, ulong? vendorId, string name) =>
            new(scopeType, vendorId, name.Trim().ToUpperInvariant());
    }

    private sealed record ImportReplacement(ulong Id, uint Version, int SortNo, string? GroupValue);

    private static async Task<ImportReplacement[]> LoadImportReplacementsAsync(
        YfDbContext context, string mode, OutsourceContactColumnResponse? groupColumn,
        IReadOnlyList<PlannedImportRow> rows, CancellationToken ct)
    {
        var rowQuery = context.OutsourceContactRows.AsNoTracking();
        if (mode == ContactImportModes.ReplaceAll)
        {
            return await rowQuery.OrderBy(r => r.Id)
                .Select(r => new ImportReplacement(r.Id, r.Version, r.SortNo, null)).ToArrayAsync(ct);
        }
        if (mode != ContactImportModes.ReplaceGroups || groupColumn is null) return [];
        var groups = rows.Select(r => r.Values.GetValueOrDefault(groupColumn.Id)?.Trim())
            .OfType<string>().Where(g => g.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (groups.Count == 0) return [];
        var candidates = await (from row in rowQuery
                                join value in context.OutsourceContactRowValues.AsNoTracking() on row.Id equals value.RowId
                                where value.ColumnId == groupColumn.Id
                                orderby row.Id
                                select new ImportReplacement(row.Id, row.Version, row.SortNo, value.TextValue)).ToArrayAsync(ct);
        return candidates.Where(r => groups.Contains(r.GroupValue!.Trim())).ToArray();
    }

    private async Task<ImportPlanResult> PlanImportAsync(
        YfDbContext context,
        string? rawMode,
        OutsourceContactImportRowInput[]? inputRows,
        OutsourceContactImportPersonOverride[]? personOverrides,
        CancellationToken ct)
    {
        var mode = string.IsNullOrWhiteSpace(rawMode) ? ContactImportModes.Append : rawMode.Trim();
        if (!ContactImportModes.IsValid(mode))
            throw ApiException.BadRequest("不支持的导入模式");
        if (inputRows is null || inputRows.Length is < 1 or > MaximumImportRows || inputRows.Any(r => r is null))
            throw ApiException.BadRequest($"导入行数需为 1–{MaximumImportRows} 行");

        var columns = await LoadActiveColumnsAsync(context, ct);
        if (columns.Length == 0)
            throw ApiException.BadRequest("请先配置至少一列后再导入");
        var groupCol = columns.FirstOrDefault(c => c.IsGroupColumn);
        if (mode == ContactImportModes.ReplaceGroups && groupCol is null)
            throw ApiException.BadRequest("当前未设置分组列，不能使用按分组覆盖模式");

        var colByKey = columns.ToDictionary(c => c.ColKey, StringComparer.Ordinal);
        var vendors = await context.OemCompanies.AsNoTracking()
            .Select(v => new ImportVendorLookup(v.Id, v.Name, v.Status))
            .ToArrayAsync(ct);
        var vendorByName = vendors
            .GroupBy(v => v.Name.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var vendorNames = vendors.ToDictionary(v => v.Id, v => v.Name);

        var existingPeople = await context.OutsourceContactPeople.AsNoTracking().OrderBy(p => p.Id).ToArrayAsync(ct);
        var overrides = (personOverrides ?? [])
            .Where(o => !string.IsNullOrWhiteSpace(o.MatchKey))
            .ToDictionary(o => o.MatchKey, o => o.VendorId, StringComparer.Ordinal);

        var previewRows = new List<OutsourceContactImportPreviewRow>(inputRows.Length);
        var readyRows = new List<PlannedImportRow>();
        var peoplePlans = new Dictionary<string, OutsourceContactImportPersonPlan>(StringComparer.Ordinal);
        var filePhoneByIdentity = new Dictionary<ImportPersonIdentity, string>();
        var detectedPhoneChanges = new HashSet<ulong>();

        for (var i = 0; i < inputRows.Length; i++)
        {
            var input = inputRows[i];
            var rowNum = input.RowNumber >= 2 ? input.RowNumber : i + 2;
            var cellsByKey = (input.Cells ?? [])
                .Where(c => !string.IsNullOrWhiteSpace(c.ColKey))
                .GroupBy(c => c.ColKey.Trim(), StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Last(), StringComparer.Ordinal);

            if (IsSampleRow(input.VisibilityText, cellsByKey))
            {
                previewRows.Add(new(rowNum, "SKIPPED", "已忽略范例行", ContactRowVisibilities.InternalOnly, [], null, 0));
                continue;
            }

            var rowErrors = new List<string>();
            var (visError, visibility, vendorIds, resolvedVendorNames) = ParseVisibility(input.VisibilityText, vendorByName);
            if (visError is not null) rowErrors.Add(visError);

            var values = new Dictionary<ulong, string>();
            var personTokensByCol = new Dictionary<ulong, List<PlannedPersonToken>>();
            var rowPeopleEntities = new List<OutsourceContactPerson>();
            var resolvedPeopleCount = 0;

            foreach (var col in columns)
            {
                cellsByKey.TryGetValue(col.ColKey, out var cell);
                if (col.DataType is ContactColumnTypes.Text or ContactColumnTypes.Multiline)
                {
                    var text = (cell?.TextValue ?? "").Trim();
                    if (text.Length > MaximumTextLength)
                    {
                        rowErrors.Add($"「{col.Title}」超过 {MaximumTextLength} 字符");
                    }
                    else if (text.Length > 0)
                    {
                        values[col.Id] = text;
                    }
                }
                else if (col.DataType == ContactColumnTypes.Person)
                {
                    var (parseErr, tokens) = OutsourceContactGrid.ParsePersonCell(
                        col.PersonDisplay, cell?.TextValue, cell?.PhonePairText);
                    if (parseErr is not null)
                    {
                        rowErrors.Add($"「{col.Title}」{parseErr}");
                        continue;
                    }
                    if (tokens.Count > MaximumPeoplePerCell)
                    {
                        rowErrors.Add($"「{col.Title}」人数超过上限 {MaximumPeoplePerCell}");
                        continue;
                    }
                    var plannedList = new List<PlannedPersonToken>(tokens.Count);
                    foreach (var token in tokens)
                    {
                        if (token.Name.Length > 64) { rowErrors.Add($"姓名「{token.Name}」超过 64 字符"); continue; }
                        if (token.Phone.Length > 32) { rowErrors.Add($"电话「{token.Phone}」超过 32 字符"); continue; }

                        var singleVendorId = visibility == ContactRowVisibilities.SpecificVendors && vendorIds.Length == 1
                            ? (ulong?)vendorIds[0]
                            : null;
                        var matchKey = BuildImportPersonMatchKey(singleVendorId, token.Name, token.Phone);

                        var matchedVendorExact = singleVendorId is { } svId
                            ? existingPeople.FirstOrDefault(p =>
                                p.ScopeType == ContactPersonScopes.Vendor
                                && p.VendorId == svId
                                && string.Equals(p.Name, token.Name, StringComparison.OrdinalIgnoreCase))
                            : null;

                        var useVendorScope = false;
                        ulong? targetVendorId = null;
                        if (overrides.TryGetValue(matchKey, out var ovVid))
                        {
                            if (ovVid is { } vId)
                            {
                                if (!vendorNames.ContainsKey(vId))
                                    rowErrors.Add($"人员「{token.Name}」指定的外包厂商不存在");
                                useVendorScope = true;
                                targetVendorId = vId;
                            }
                        }
                        else if (matchedVendorExact is not null)
                        {
                            useVendorScope = true;
                            targetVendorId = matchedVendorExact.VendorId;
                        }

                        var targetScope = useVendorScope ? ContactPersonScopes.Vendor : ContactPersonScopes.Internal;
                        var idKey = ImportPersonIdentity.From(targetScope, targetVendorId, token.Name);
                        if (filePhoneByIdentity.TryGetValue(idKey, out var seenPhone)
                            && !string.Equals(seenPhone, token.Phone, StringComparison.Ordinal))
                        {
                            rowErrors.Add($"文件内多行出现同名人员「{token.Name}」但电话不一致（{seenPhone} / {token.Phone}）");
                            continue;
                        }
                        filePhoneByIdentity[idKey] = token.Phone;

                        var matched = existingPeople.FirstOrDefault(p =>
                            p.ScopeType == targetScope
                            && p.VendorId == targetVendorId
                            && string.Equals(p.Name, token.Name, StringComparison.OrdinalIgnoreCase));

                        if (matched is not null)
                        {
                            var personAction = string.Equals(matched.Phone, token.Phone, StringComparison.Ordinal)
                                ? "REUSE"
                                : "UPDATE_PHONE";
                            if (personAction == "UPDATE_PHONE") detectedPhoneChanges.Add(matched.Id);

                            if (!peoplePlans.ContainsKey(matchKey))
                            {
                                peoplePlans[matchKey] = new OutsourceContactImportPersonPlan(
                                    matchKey, matched.Name, token.Phone, matched.ScopeType, matched.VendorId,
                                    matched.VendorId.HasValue ? vendorNames.GetValueOrDefault(matched.VendorId.Value) : null,
                                    personAction, personAction == "UPDATE_PHONE" ? matched.Phone : null, matched.Id,
                                    singleVendorId.HasValue, singleVendorId,
                                    singleVendorId.HasValue ? vendorNames.GetValueOrDefault(singleVendorId.Value) : null);
                            }
                            rowPeopleEntities.Add(matched);
                            plannedList.Add(new(matchKey, token.PrefixLabel, matched.Id));
                        }
                        else
                        {
                            // Check duplicate phone under same scope/vendor for a different name
                            var samePhoneOther = existingPeople.FirstOrDefault(p =>
                                p.ScopeType == targetScope
                                && p.VendorId == targetVendorId
                                && string.Equals(p.Phone, token.Phone, StringComparison.Ordinal)
                                && !string.Equals(p.Name, token.Name, StringComparison.OrdinalIgnoreCase));
                            if (samePhoneOther is not null)
                            {
                                rowErrors.Add($"电话「{token.Phone}」已被同归属的「{samePhoneOther.Name}」占用，请确认姓名");
                                continue;
                            }

                            if (!peoplePlans.TryGetValue(matchKey, out var existingPlan))
                            {
                                peoplePlans[matchKey] = new OutsourceContactImportPersonPlan(
                                    matchKey, token.Name, token.Phone, targetScope, targetVendorId,
                                    targetVendorId.HasValue ? vendorNames.GetValueOrDefault(targetVendorId.Value) : null,
                                    "CREATE", null, null,
                                    singleVendorId.HasValue, singleVendorId,
                                    singleVendorId.HasValue ? vendorNames.GetValueOrDefault(singleVendorId.Value) : null);
                            }
                            else if (existingPlan.ScopeType != targetScope || existingPlan.VendorId != targetVendorId)
                            {
                                rowErrors.Add($"新增人员「{token.Name} ({token.Phone})」在多行中归属冲突");
                                continue;
                            }

                            rowPeopleEntities.Add(new OutsourceContactPerson
                            {
                                Name = token.Name,
                                Phone = token.Phone,
                                ScopeType = targetScope,
                                VendorId = targetVendorId,
                            });
                            plannedList.Add(new(matchKey, token.PrefixLabel, null));
                        }
                        resolvedPeopleCount++;
                    }
                    if (plannedList.Count > 0) personTokensByCol[col.Id] = plannedList;
                }
            }

            if (rowErrors.Count == 0)
            {
                try
                {
                    OutsourceContactGrid.EnsureRowAntiLeak(visibility, vendorIds, rowPeopleEntities, vendorNames);
                }
                catch (ApiException ex)
                {
                    rowErrors.Add(ex.Message);
                }
            }

            var groupVal = groupCol is not null ? values.GetValueOrDefault(groupCol.Id) : null;
            if (rowErrors.Count > 0)
            {
                previewRows.Add(new(rowNum, "INVALID", string.Join("；", rowErrors), visibility, resolvedVendorNames, groupVal, resolvedPeopleCount));
            }
            else
            {
                previewRows.Add(new(rowNum, "READY", null, visibility, resolvedVendorNames, groupVal, resolvedPeopleCount));
                readyRows.Add(new(visibility, vendorIds, values, personTokensByCol));
            }
        }

        var replacements = await LoadImportReplacementsAsync(context, mode, groupCol, readyRows, ct);
        // A committed append also invalidates its preview, so a lost response cannot replay the same append.
        var appendAfter = mode == ContactImportModes.Append
            ? await context.OutsourceContactRows.AsNoTracking().OrderByDescending(r => r.SortNo)
                .ThenByDescending(r => r.Id).Select(r => (ulong?)r.Id).FirstOrDefaultAsync(ct)
            : null;
        var plans = peoplePlans.Values.OrderBy(p => p.Action, StringComparer.Ordinal)
            .ThenBy(p => p.Name, StringComparer.Ordinal).ThenBy(p => p.MatchKey, StringComparer.Ordinal).ToArray();
        // Bind confirmation to the input, column definitions, resolved people and exact rows to replace.
        var version = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            mode,
            inputRows,
            overrides = overrides.OrderBy(o => o.Key, StringComparer.Ordinal).ToArray(),
            columns,
            previewRows,
            readyRows,
            plans,
            replacements,
            appendAfter,
        })));
        var preview = new OutsourceContactImportPreviewResponse(
            mode,
            previewRows.Count(r => r.Status == "READY"),
            previewRows.Count(r => r.Status == "SKIPPED"),
            previewRows.Count(r => r.Status == "INVALID"),
            [.. previewRows],
            plans,
            detectedPhoneChanges.Count,
            replacements.Length,
            version);

        return new(preview, readyRows, replacements.Select(r => r.Id).ToArray());
    }

    private static bool IsSampleRow(string? visibilityText, IReadOnlyDictionary<string, OutsourceContactImportCellInput> cellsByKey)
    {
        if (!string.IsNullOrWhiteSpace(visibilityText) && visibilityText.Trim().StartsWith("全部外包（可填：", StringComparison.Ordinal))
            return true;
        return cellsByKey.Values.Any(c =>
            string.Equals(c.TextValue?.Trim(), "示例分组（导入时自动忽略本范例行）", StringComparison.Ordinal));
    }

    private sealed record ImportVendorLookup(ulong Id, string Name, string Status);

    private static (string? Error, string Visibility, ulong[] VendorIds, string[] VendorNames) ParseVisibility(
        string? raw,
        IReadOnlyDictionary<string, ImportVendorLookup> vendorByName)
    {
        var text = (raw ?? "").Trim();
        if (text.Length == 0 || text is "仅内部" or "仅内部可见" or "内部" or ContactRowVisibilities.InternalOnly)
            return (null, ContactRowVisibilities.InternalOnly, [], []);
        if (text is "全部外包" or "全部外包可见" or "全部" or ContactRowVisibilities.AllVendors)
            return (null, ContactRowVisibilities.AllVendors, [], []);

        var tokens = text.Split(['、', ',', '，', ';', '；', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0)
            return (null, ContactRowVisibilities.InternalOnly, [], []);

        var ids = new List<ulong>(tokens.Length);
        var names = new List<string>(tokens.Length);
        foreach (var token in tokens)
        {
            if (!vendorByName.TryGetValue(token, out var vendor))
                return ($"未知的外包厂商「{token}」", ContactRowVisibilities.SpecificVendors, [], []);
            if (vendor.Status != AccountStatuses.Active)
                return ($"外包厂商「{vendor.Name}」已停用", ContactRowVisibilities.SpecificVendors, [], []);
            if (!ids.Contains(vendor.Id))
            {
                ids.Add(vendor.Id);
                names.Add(vendor.Name);
            }
        }
        return (null, ContactRowVisibilities.SpecificVendors, ids.Order().ToArray(), [.. names]);
    }

    private static string BuildImportPersonMatchKey(ulong? singleVendorId, string name, string phone) =>
        $"{(singleVendorId.HasValue ? $"V:{singleVendorId.Value}" : "I")}\u0000{name.Trim()}\u0000{phone.Trim()}";

    private async Task<OutsourceContactMatrixResponse> LoadMatrixAsync(
        YfDbContext context,
        string? groupFilter,
        string? keywordFilter,
        ulong? vendorIdFilter,
        bool canManage,
        bool canConfigure,
        CancellationToken ct)
    {
        var columns = await LoadActiveColumnsAsync(context, ct);
        var vendors = await context.OemCompanies.AsNoTracking()
            .OrderBy(v => v.Name)
            .Select(v => new OutsourceContactVendorOption(v.Id, v.Name, v.Status))
            .ToArrayAsync(ct);

        var rows = await LoadRowResponsesInternalAsync(context, columns, vendors, null, ct);

        var groupCol = columns.FirstOrDefault(c => c.IsGroupColumn);
        IEnumerable<OutsourceContactRowResponse> filtered = rows;
        if (!string.IsNullOrWhiteSpace(groupFilter) && groupCol is not null)
        {
            var g = groupFilter.Trim();
            filtered = filtered.Where(r => string.Equals(r.Values.GetValueOrDefault(groupCol.ColKey)?.Trim(), g, StringComparison.OrdinalIgnoreCase));
        }
        if (vendorIdFilter is { } vid)
        {
            filtered = filtered.Where(r => r.Visibility == ContactRowVisibilities.AllVendors
                || (r.Visibility == ContactRowVisibilities.SpecificVendors && r.VendorIds.Contains(vid)));
        }
        if (!string.IsNullOrWhiteSpace(keywordFilter))
        {
            var kw = keywordFilter.Trim();
            var vendorNameMap = vendors.ToDictionary(v => v.Id, v => v.Name);
            filtered = filtered.Where(r => RowMatchesKeyword(r, kw, vendorNameMap));
        }

        return new(columns, filtered.ToArray(), vendors, canManage, canConfigure);
    }

    private static bool RowMatchesKeyword(
        OutsourceContactRowResponse row, string keyword, IReadOnlyDictionary<ulong, string> vendorNames)
    {
        if (row.Values.Values.Any(v => v.Contains(keyword, StringComparison.OrdinalIgnoreCase))) return true;
        if (row.People.Values.SelectMany(x => x).Any(p =>
            p.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase)
            || p.Phone.Contains(keyword, StringComparison.OrdinalIgnoreCase)
            || (p.PrefixLabel?.Contains(keyword, StringComparison.OrdinalIgnoreCase) ?? false)))
            return true;
        return row.VendorIds.Any(id => vendorNames.GetValueOrDefault(id)?.Contains(keyword, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    private async Task<OutsourceContactRowResponse> LoadSingleRowResponseAsync(
        YfDbContext context, ulong rowId, CancellationToken ct) =>
        (await LoadRowResponsesByIdsAsync(context, [rowId], ct)).Single();

    private async Task<OutsourceContactRowResponse[]> LoadRowResponsesByIdsAsync(
        YfDbContext context, ulong[] rowIds, CancellationToken ct)
    {
        var columns = await LoadActiveColumnsAsync(context, ct);
        var vendors = await context.OemCompanies.AsNoTracking()
            .Select(v => new OutsourceContactVendorOption(v.Id, v.Name, v.Status))
            .ToArrayAsync(ct);
        return await LoadRowResponsesInternalAsync(context, columns, vendors, rowIds, ct);
    }

    private static async Task<OutsourceContactRowResponse[]> LoadRowResponsesInternalAsync(
        YfDbContext context,
        OutsourceContactColumnResponse[] columns,
        OutsourceContactVendorOption[] vendors,
        ulong[]? onlyRowIds,
        CancellationToken ct)
    {
        var rowsQuery = context.OutsourceContactRows.AsNoTracking();
        if (onlyRowIds is not null) rowsQuery = rowsQuery.Where(r => onlyRowIds.Contains(r.Id));
        var rows = await rowsQuery.OrderBy(r => r.SortNo).ThenBy(r => r.Id).ToArrayAsync(ct);
        if (rows.Length == 0) return [];

        var rowIds = rows.Select(r => r.Id).ToArray();
        var colKeyById = columns.ToDictionary(c => c.Id, c => c.ColKey);
        var activeColIds = colKeyById.Keys.ToArray();
        var vendorNameById = vendors.ToDictionary(v => v.Id, v => v.Name);

        var rowVendors = await context.OutsourceContactRowVendors.AsNoTracking()
            .Where(rv => rowIds.Contains(rv.RowId))
            .ToArrayAsync(ct);
        var rowValues = activeColIds.Length == 0
            ? []
            : await context.OutsourceContactRowValues.AsNoTracking()
                .Where(rv => rowIds.Contains(rv.RowId) && activeColIds.Contains(rv.ColumnId))
                .ToArrayAsync(ct);
        var rowPeople = activeColIds.Length == 0
            ? []
            : await context.OutsourceContactRowPeople.AsNoTracking()
                .Where(rp => rowIds.Contains(rp.RowId) && activeColIds.Contains(rp.ColumnId))
                .Join(context.OutsourceContactPeople.AsNoTracking(), rp => rp.PersonId, p => p.Id, (rp, p) => new { rp, p })
                .OrderBy(x => x.rp.SortNo).ThenBy(x => x.rp.Id)
                .ToArrayAsync(ct);

        var vendorLookup = rowVendors.GroupBy(x => x.RowId).ToDictionary(g => g.Key, g => g.Select(x => x.VendorId).Order().ToArray());
        var valueLookup = rowValues.GroupBy(x => x.RowId).ToDictionary(
            g => g.Key,
            g => (IReadOnlyDictionary<string, string>)g.ToDictionary(x => colKeyById[x.ColumnId], x => x.TextValue, StringComparer.Ordinal));
        var peopleLookup = rowPeople.GroupBy(x => x.rp.RowId).ToDictionary(
            g => g.Key,
            g => (IReadOnlyDictionary<string, OutsourceContactCellPersonResponse[]>)g
                .GroupBy(x => colKeyById[x.rp.ColumnId], StringComparer.Ordinal)
                .ToDictionary(
                    cg => cg.Key,
                    cg => cg.Select(x => new OutsourceContactCellPersonResponse(
                        x.p.Id,
                        x.rp.PrefixLabel,
                        x.p.Name,
                        x.p.Phone,
                        x.p.Email,
                        x.p.ScopeType,
                        x.p.VendorId,
                        x.p.VendorId.HasValue ? vendorNameById.GetValueOrDefault(x.p.VendorId.Value) : null,
                        x.p.Status)).ToArray(),
                    StringComparer.Ordinal));

        var emptyValues = new Dictionary<string, string>(StringComparer.Ordinal);
        var emptyPeople = new Dictionary<string, OutsourceContactCellPersonResponse[]>(StringComparer.Ordinal);

        return rows.Select(r => new OutsourceContactRowResponse(
            r.Id,
            r.SortNo,
            r.Visibility,
            r.Version,
            vendorLookup.GetValueOrDefault(r.Id) ?? [],
            valueLookup.GetValueOrDefault(r.Id) ?? emptyValues,
            peopleLookup.GetValueOrDefault(r.Id) ?? emptyPeople,
            r.UpdatedAt)).ToArray();
    }

    private static async Task<OutsourceContactColumnResponse[]> LoadActiveColumnsAsync(YfDbContext context, CancellationToken ct) =>
        await context.OutsourceContactColumns.AsNoTracking()
            .Where(c => c.Status == AccountStatuses.Active)
            .OrderBy(c => c.SortNo).ThenBy(c => c.Id)
            .Select(c => ToColumnResponse(c))
            .ToArrayAsync(ct);

    private static Task<OutsourceContactColumn[]> LoadActiveColumnEntitiesLockedAsync(YfDbContext context, CancellationToken ct) =>
        context.OutsourceContactColumns
            .FromSqlRaw("SELECT * FROM outsource_contact_columns WHERE status='ACTIVE' ORDER BY sort_no, id FOR UPDATE")
            .ToArrayAsync(ct);

    private static async Task<OutsourceContactRow[]> LockAndVerifyRowsAsync(
        YfDbContext context, OutsourceContactRowVersion[] rowVersions, CancellationToken ct)
    {
        var ids = rowVersions.Select(r => r.RowId).Distinct().Order().ToArray();
        var rows = await context.OutsourceContactRows
            .FromSqlRaw($"SELECT * FROM outsource_contact_rows WHERE id IN ({string.Join(",", ids)}) FOR UPDATE")
            .ToArrayAsync(ct);
        if (rows.Length != ids.Length)
            throw ApiException.NotFound("部分目标行不存在或已被删除，请刷新后重试");

        var expectedById = rowVersions.ToDictionary(r => r.RowId, r => r.ExpectedVersion);
        foreach (var row in rows)
        {
            if (row.Version != expectedById[row.Id])
                throw ApiException.VersionConflict(VersionConflictMessage);
        }
        return rows.OrderBy(r => r.SortNo).ThenBy(r => r.Id).ToArray();
    }

    private static OutsourceContactRowVersion[] ValidateRowVersions(OutsourceContactRowVersion[]? rows)
    {
        if (rows is null || rows.Length == 0 || rows.Length > 500)
            throw ApiException.BadRequest("请提供 1–500 条有效行数据");
        if (rows.Select(r => r.RowId).Distinct().Count() != rows.Length || rows.Any(r => r.RowId == 0))
            throw ApiException.BadRequest("行参数重复或无效");
        return rows;
    }

    private async Task<OutsourceContactPersonResponse> LoadPersonResponseAsync(YfDbContext context, ulong personId, CancellationToken ct)
    {
        var p = await context.OutsourceContactPeople.AsNoTracking().SingleAsync(x => x.Id == personId, ct);
        string? vendorName = p.VendorId.HasValue
            ? await context.OemCompanies.AsNoTracking().Where(v => v.Id == p.VendorId.Value).Select(v => v.Name).SingleOrDefaultAsync(ct)
            : null;
        (string EmployeeNo, string RealName)? user = p.UserId.HasValue
            ? await context.Users.AsNoTracking().Where(u => u.Id == p.UserId.Value)
                .Select(u => ValueTuple.Create(u.EmployeeNo, u.RealName)).SingleOrDefaultAsync(ct)
            : null;
        return new OutsourceContactPersonResponse(
            p.Id, p.ScopeType, p.VendorId, vendorName, p.UserId,
            user?.EmployeeNo, user?.RealName, p.Name, p.Phone, p.Email, p.TitleHint, p.Status, p.UpdatedAt);
    }

    private static async Task ValidatePersonForeignKeysAndDuplicatesAsync(
        YfDbContext context,
        ulong? excludeId,
        string scopeType,
        ulong? vendorId,
        ulong? userId,
        string name,
        string phone,
        CancellationToken ct)
    {
        if (scopeType == ContactPersonScopes.Vendor)
        {
            var vendor = await context.OemCompanies.AsNoTracking().SingleOrDefaultAsync(v => v.Id == vendorId!.Value, ct);
            if (vendor is null || vendor.Status != AccountStatuses.Active)
                throw ApiException.BadRequest("归属外包厂商不存在或已停用");
        }
        if (userId is { } uid)
        {
            var user = await context.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == uid, ct);
            if (user is null || user.UserType != UserTypes.Internal || user.Status != AccountStatuses.Active)
                throw ApiException.BadRequest("关联的内部账号不存在或已停用");
            var usedByOther = await context.OutsourceContactPeople.AnyAsync(p => p.UserId == uid && p.Id != excludeId, ct);
            if (usedByOther) throw ApiException.Conflict("该内部账号已关联其他人员主档");
        }

        var existing = await context.OutsourceContactPeople.AsNoTracking()
            .Where(p => p.ScopeType == scopeType && p.VendorId == vendorId && p.Id != excludeId)
            .ToArrayAsync(ct);
        if (existing.Any(p => string.Equals(p.Phone, phone, StringComparison.Ordinal)
            || string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            throw ApiException.Conflict("同归属下已存在同姓名或同电话的人员档案，请直接复用以保持电话联动");
        }
    }

    private static async Task SavePersonChangesAsync(YfDbContext context, CancellationToken ct)
    {
        try { await context.SaveChangesAsync(ct); }
        catch (DbUpdateException error) when (error.InnerException is MySqlException { Number: 1062 })
        {
            throw ApiException.Conflict("人员档案存在重复关联，请刷新后重试");
        }
    }

    private static (string ScopeType, ulong? VendorId, ulong? UserId, string Name, string Phone, string? Email, string? TitleHint)
        ValidatePersonUpsert(OutsourceContactPersonUpsert request)
    {
        var scopeType = request.ScopeType?.Trim() ?? "";
        if (!ContactPersonScopes.IsValid(scopeType)) throw ApiException.BadRequest("请选择人员归属（内部或外包厂商）");

        ulong? vendorId = null;
        ulong? userId = null;
        if (scopeType == ContactPersonScopes.Vendor)
        {
            vendorId = request.VendorId is > 0 ? request.VendorId : throw ApiException.BadRequest("外包厂商人员必须指定归属厂商");
            if (request.UserId is not null) throw ApiException.BadRequest("外包厂商人员不能绑定内部系统账号");
        }
        else
        {
            if (request.VendorId is not null) throw ApiException.BadRequest("内部人员不能指定外包厂商");
            userId = request.UserId is > 0 ? request.UserId : null;
        }

        var name = request.Name?.Trim() ?? "";
        if (name.Length == 0 || name.EnumerateRunes().Count() > 64)
            throw ApiException.BadRequest("姓名不能为空且不超过 64 个字符");
        var phone = request.Phone?.Trim() ?? "";
        if (phone.Length == 0 || phone.EnumerateRunes().Count() > 32)
            throw ApiException.BadRequest("联系电话不能为空且不超过 32 个字符");
        AdminValidation.OptionalEmail(request.Email);
        var email = string.IsNullOrWhiteSpace(request.Email) ? null : request.Email.Trim();
        var titleHint = string.IsNullOrWhiteSpace(request.TitleHint) ? null : request.TitleHint.Trim();
        if (titleHint is not null && titleHint.EnumerateRunes().Count() > 128)
            throw ApiException.BadRequest("备注最多 128 个字符");
        return (scopeType, vendorId, userId, name, phone, email, titleHint);
    }

    private static string ValidateTitle(string? value)
    {
        var title = value?.Trim() ?? "";
        if (title.Length == 0 || title.EnumerateRunes().Count() > 64)
            throw ApiException.BadRequest("列标题不能为空且不超过 64 个字符");
        return title;
    }

    private static string ValidateDataType(string? value)
    {
        var type = value?.Trim() ?? "";
        return ContactColumnTypes.IsValid(type) ? type : throw ApiException.BadRequest("请选择合法的列类型");
    }

    private static string ValidatePersonDisplay(string? value)
    {
        var display = value?.Trim() ?? "";
        return ContactPersonDisplays.IsValid(display) ? display : throw ApiException.BadRequest("请选择合法的人员显示方式");
    }

    private static string? NormalizePhoneTitle(string? value)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return null;
        if (trimmed.EnumerateRunes().Count() > 64) throw ApiException.BadRequest("电话列标题不能超过 64 个字符");
        return trimmed;
    }

    private static double NormalizeWidthHint(double? value)
    {
        var width = value ?? 18d;
        return Math.Clamp(width, 8d, 60d);
    }

    private static string? ValidateOptionalPrefix(string? value)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return null;
        if (trimmed.EnumerateRunes().Count() > 32) throw ApiException.BadRequest("角色前缀不能超过 32 个字符");
        return trimmed;
    }

    private static Task<Dictionary<ulong, string>> LoadVendorNameMapAsync(YfDbContext context, CancellationToken ct) =>
        context.OemCompanies.AsNoTracking().ToDictionaryAsync(v => v.Id, v => v.Name, ct);

    private static OutsourceContactColumnResponse ToColumnResponse(OutsourceContactColumn c) => new(
        c.Id, c.ColKey, c.Title, c.DataType, c.PersonDisplay, c.PhoneTitle,
        c.AutoMerge, c.VendorVisible, c.IsGroupColumn, c.SortNo, c.WidthHint, c.Status);

    private Task WriteAuditAsync(YfDbContext context, ulong actorId, string action, string targetType, ulong? targetId, object detail, CancellationToken ct) =>
        audit.WriteAsync(context.Database.Connection(), context.Database.RequireTransaction(), actorId,
            action, targetType, targetId, detail, null, ct);
}
