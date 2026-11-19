using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yf.Api.Infrastructure;
using Yf.Api.Infrastructure.Entities;
using Yf.Api.Modules.Admin;

namespace Yf.Api.Modules.Outsource;

internal static class OutsourceFieldTypes
{
    internal const string Text = "TEXT";
    internal const string Boolean = "BOOLEAN";
    internal const string Date = "DATE";
    internal const string Number = "NUMBER";
    internal const string Select = "SELECT";
    internal static bool IsValid(string? value) => value is Text or Boolean or Date or Number or Select;
}

/// <summary>Who fills a column in internally (外协清单契约 → 栏位设定).</summary>
internal static class OutsourceEditorScopes
{
    /// <summary>Everyone who may edit the list (<c>outsource:manage</c>); the default, so a new role needs no field setup.</summary>
    internal const string All = "ALL";
    /// <summary>Only members of the field's editor roles (and system administrators).</summary>
    internal const string Roles = "ROLES";
    internal const string None = "NONE";
    internal static bool IsValid(string? value) => value is All or Roles or None;
}

/// <summary>
/// What kind of column a field is, which fixes what the settings page may change: IPMS and fixed columns are never
/// filled in by hand, process columns drive routing and row colours so only internal staff fill them in, and manual
/// and custom columns may also be opened to the vendor.
/// </summary>
internal static class OutsourceFieldCategories
{
    internal const string Fixed = "FIXED";
    internal const string Ipms = "IPMS";
    internal const string Process = "PROCESS";
    internal const string Manual = "MANUAL";
    internal const string Custom = "CUSTOM";
    internal static bool InternalEditable(string category) => category is Process or Manual or Custom;
    internal static bool VendorEditable(string category) => category is Manual or Custom;
}

/// <summary>A built-in column: its category, Excel width, how its cell text is read, and whether it is pinned left / always shown to vendors.</summary>
internal sealed record OutsourceBuiltInField(string Key, string Category, double Width, Func<OutsourceItemResponse, string?> Value,
    bool Pinned = false, bool AlwaysVendorVisible = false);

internal static class OutsourceBuiltInFields
{
    private static string? Date(DateOnly? value) => value?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>Every built-in business column (without the row number); labels and order live in <c>outsource_fields</c>.</summary>
    internal static readonly OutsourceBuiltInField[] All =
    [
        new("outsourceDate", OutsourceFieldCategories.Process, 12, x => Date(x.OutsourceDate), Pinned: true),
        new("status", OutsourceFieldCategories.Process, 8, x => OutsourceItemStatuses.Label(x.Status), Pinned: true, AlwaysVendorVisible: true),
        new("workOrderNo", OutsourceFieldCategories.Fixed, 12, x => x.WorkOrderNo, Pinned: true, AlwaysVendorVisible: true),
        new("customer", OutsourceFieldCategories.Ipms, 18, x => x.Customer),
        new("machineModel", OutsourceFieldCategories.Ipms, 14, x => x.MachineModel),
        new("type", OutsourceFieldCategories.Process, 24, x => x.TypeLabel ?? x.TypeCode),
        new("vendor", OutsourceFieldCategories.Process, 14, x => x.VendorName),
        new("mechanicalSection", OutsourceFieldCategories.Ipms, 14, x => x.MechanicalSection),
        new("mechanicalOwner", OutsourceFieldCategories.Ipms, 12, x => x.MechanicalOwner),
        new("electricalOwner", OutsourceFieldCategories.Ipms, 12, x => x.ElectricalOwner),
        new("workOrderIssuedOn", OutsourceFieldCategories.Ipms, 12, x => Date(x.WorkOrderIssuedOn)),
        new("bomOn", OutsourceFieldCategories.Manual, 12, x => Date(x.BomOn)),
        new("drawingReleasedOn", OutsourceFieldCategories.Manual, 12, x => Date(x.DrawingReleasedOn)),
        new("originalDueDate", OutsourceFieldCategories.Ipms, 12, x => Date(x.OriginalDueDate)),
        new("latestDueDate", OutsourceFieldCategories.Process, 12, x => Date(x.LatestDueDate)),
        new("quantity", OutsourceFieldCategories.Ipms, 8, x => x.Quantity?.ToString(CultureInfo.InvariantCulture)),
        new("remark", OutsourceFieldCategories.Manual, 24, x => x.Remark),
    ];

    internal static readonly IReadOnlyDictionary<string, OutsourceBuiltInField> ByKey = All.ToDictionary(x => x.Key, StringComparer.Ordinal);
}

/// <summary>Parsing and display of custom field values (stored as text, at most 500 characters).</summary>
internal static class OutsourceFieldValues
{
    internal const int MaximumTextLength = 500;

    internal static string? Display(string dataType, string? raw) => dataType == OutsourceFieldTypes.Boolean
        ? raw switch { "true" => "是", "false" => "否", _ => raw }
        : raw;

    /// <summary>The stored text of an input, or null to clear the value; throws a 400 naming the field when it is invalid.</summary>
    internal static string? Normalize(string dataType, IReadOnlyCollection<string> options, string label, string? input)
    {
        var value = input?.Trim();
        if (string.IsNullOrEmpty(value)) return null;
        switch (dataType)
        {
            case OutsourceFieldTypes.Boolean:
                return value switch
                {
                    "true" or "是" => "true",
                    "false" or "否" => "false",
                    _ => throw ApiException.BadRequest($"「{label}」只能填是或否"),
                };
            case OutsourceFieldTypes.Date:
                return DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                    ? date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                    : throw ApiException.BadRequest($"「{label}」日期格式不正确");
            case OutsourceFieldTypes.Number:
                if (value.Length > 32 || !decimal.TryParse(value, NumberStyles.Number & ~NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var number))
                    throw ApiException.BadRequest($"「{label}」必须是数字");
                return number.ToString(CultureInfo.InvariantCulture);
            case OutsourceFieldTypes.Select:
                return options.Contains(value, StringComparer.Ordinal) ? value : throw ApiException.BadRequest($"「{label}」只能从选项中选择");
            default:
                if (value.EnumerateRunes().Count() > MaximumTextLength) throw ApiException.BadRequest($"「{label}」最多 {MaximumTextLength} 个字符");
                return value;
        }
    }
}

/// <summary>
/// The columns of the outsourcing list as configured on the field settings page, loaded once per request: their order,
/// labels, who may fill each in, and which ones vendors see. The vendor columns drive the portal, the mail bodies,
/// the attachment and the "as last sent" snapshot, so a column hidden from vendors never leaves the company.
/// </summary>
internal sealed class OutsourceFieldLayout
{
    internal sealed record Column(OutsourceField Field, OutsourceBuiltInField? BuiltIn, IReadOnlySet<ulong> EditorRoles, string[] Options)
    {
        public string Key => Field.FieldKey;
        public string Label => Field.Label;
        public string Category => BuiltIn?.Category ?? OutsourceFieldCategories.Custom;
        public bool Active => Field.Status == AccountStatuses.Active;
        public bool Pinned => BuiltIn?.Pinned == true;
        public bool VendorVisible => BuiltIn?.AlwaysVendorVisible == true || Field.VendorVisible;
        public bool VendorEditable => Field.VendorEditable && VendorVisible && OutsourceFieldCategories.VendorEditable(Category);
        public string EditorScope => OutsourceFieldCategories.InternalEditable(Category) ? Field.EditorScope : OutsourceEditorScopes.None;

        public string? Value(OutsourceItemResponse item) => BuiltIn is { } builtIn
            ? builtIn.Value(item)
            : OutsourceFieldValues.Display(Field.DataType, item.CustomValues?.GetValueOrDefault(Key));

        public OutsourceWorkbook.Field AsWorkbookField() => new(Key, Label, BuiltIn?.Width ?? (Field.DataType == OutsourceFieldTypes.Text ? 20 : 12), Value);
    }

    private readonly Dictionary<string, Column> byKey;

    /// <summary>Every field, disabled ones included, in list order.</summary>
    public IReadOnlyList<Column> Everything { get; }
    /// <summary>The active columns in list order.</summary>
    public IReadOnlyList<Column> Columns { get; }
    /// <summary>The active columns vendors see, in list order.</summary>
    public IReadOnlyList<Column> VendorColumns { get; }

    private OutsourceFieldLayout(IReadOnlyList<Column> everything)
    {
        Everything = everything;
        Columns = everything.Where(x => x.Active).ToArray();
        VendorColumns = Columns.Where(x => x.VendorVisible).ToArray();
        byKey = everything.ToDictionary(x => x.Key, StringComparer.Ordinal);
    }

    public Column? Find(string key) => byKey.GetValueOrDefault(key);

    public string Label(string key) => Find(key)?.Label ?? OutsourceHistory.Label(key);

    public IReadOnlySet<string> VendorKeys => VendorColumns.Select(x => x.Key).ToHashSet(StringComparer.Ordinal);

    public OutsourceWorkbook.Field[] WorkbookFields(bool vendor) => (vendor ? VendorColumns : Columns).Select(x => x.AsWorkbookField()).ToArray();

    /// <summary>The mail body's summary columns that vendors may see, in the body's own order.</summary>
    public OutsourceWorkbook.Field[] MailBodyFields(IEnumerable<string> keys) => keys
        .Select(Find).OfType<Column>().Where(x => x.Active && x.VendorVisible).Select(x => x.AsWorkbookField()).ToArray();

    /// <summary>Whether <paramref name="editor"/> may fill <paramref name="column"/> in (the caller has already checked outsource:manage).</summary>
    public static bool CanEdit(Column column, OutsourceEditor editor)
    {
        if (!column.Active || !OutsourceFieldCategories.InternalEditable(column.Category)) return false;
        if (editor.IsSystemAdmin) return true;
        return column.EditorScope switch
        {
            OutsourceEditorScopes.All => true,
            OutsourceEditorScopes.Roles => column.EditorRoles.Overlaps(editor.RoleIds),
            _ => false,
        };
    }

    public static async Task<OutsourceFieldLayout> LoadAsync(YfDbContext context, CancellationToken ct)
    {
        var fields = await context.OutsourceFields.AsNoTracking().OrderBy(x => x.SortNo).ThenBy(x => x.Id).ToArrayAsync(ct);
        var editors = (await context.OutsourceFieldEditorRoles.AsNoTracking().ToArrayAsync(ct)).ToLookup(x => x.FieldId, x => x.RoleId);
        var columns = new List<Column>(fields.Length);
        foreach (var field in fields)
        {
            OutsourceBuiltInField? builtIn = null;
            // A built-in row whose key this version does not know is skipped rather than shown without a value.
            if (field.BuiltIn && !OutsourceBuiltInFields.ByKey.TryGetValue(field.FieldKey, out builtIn)) continue;
            columns.Add(new(field, builtIn, editors[field.Id].ToHashSet(), Options(field.Options)));
        }
        // Defensive: a built-in column always exists, even when its row is missing (it is then shown and internal).
        foreach (var missing in OutsourceBuiltInFields.All.Where(x => columns.All(column => column.Key != x.Key)))
        {
            columns.Add(new(new OutsourceField
            {
                FieldKey = missing.Key, BuiltIn = true, Label = OutsourceHistory.Label(missing.Key), DataType = OutsourceFieldTypes.Text,
                Status = AccountStatuses.Active, VendorVisible = true, EditorScope = OutsourceEditorScopes.All, SortNo = int.MaxValue,
            }, missing, new HashSet<ulong>(), []));
        }
        return new(columns);
    }

    internal static string[] Options(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<string[]>(json) ?? []; }
        catch (JsonException) { return []; }
    }
}

/// <summary>The roles of an internal editor, resolved once per write for the per-field checks.</summary>
internal sealed record OutsourceEditor(IReadOnlySet<ulong> RoleIds, bool IsSystemAdmin)
{
    internal static async Task<OutsourceEditor> LoadAsync(YfDbContext context, ulong userId, CancellationToken ct)
    {
        var roles = await context.UserRoles.AsNoTracking().Where(x => x.UserId == userId)
            .Join(context.Roles.Where(x => x.Status == AccountStatuses.Active), x => x.RoleId, x => x.Id, (_, role) => role)
            .Select(x => new { x.Id, x.IsBuiltIn, x.Name }).ToArrayAsync(ct);
        return new(roles.Select(x => x.Id).ToHashSet(),
            roles.Any(x => x.IsBuiltIn && x.Name == BuiltInRoleNames.SystemAdministrator));
    }
}

public sealed record OutsourceFieldColumnResponse(
    string Key, string Label, string DataType, string[] Options, string Category, bool Pinned, bool VendorVisible, bool VendorEditable,
    bool CanEdit);

/// <summary>The list's active columns in order, with what the current account may fill in.</summary>
public sealed record OutsourceFieldLayoutResponse(OutsourceFieldColumnResponse[] Fields);

public sealed record OutsourceFieldSettingResponse(
    ulong Id, string Key, bool BuiltIn, string Label, string DataType, string[] Options, string Category, int SortNo, string Status,
    bool Pinned, bool VendorVisible, bool VendorVisibleLocked, bool VendorEditable, bool VendorEditableAllowed,
    string EditorScope, bool EditorScopeAllowed, ulong[] EditorRoleIds, bool InUse, DateTime UpdatedAt);

public sealed record OutsourceFieldRoleOption(ulong Id, string Name);

public sealed record OutsourceFieldSettingsResponse(OutsourceFieldSettingResponse[] Fields, OutsourceFieldRoleOption[] Roles);

public sealed record OutsourceFieldCreate(string? Label, string? DataType, string[]? Options);

/// <summary>Each member left null is not changed. Label, type and options apply to custom fields only.</summary>
public sealed record OutsourceFieldUpdate(
    string? Label, string? DataType, string[]? Options, bool? VendorVisible, bool? VendorEditable, string? EditorScope, ulong[]? EditorRoleIds);

/// <summary>Every field that is not pinned to the left, in the new order.</summary>
public sealed record OutsourceFieldOrderRequest(ulong[]? FieldIds);

/// <summary>The field settings page (外协清单契约 → 栏位设定); every write needs <c>outsource:field_settings</c>.</summary>
public sealed class OutsourceFieldService(IDbContextFactory<YfDbContext> dbFactory, AuditService audit)
{
    internal const int MaximumCustomFields = 50;
    internal const int MaximumOptions = 50;

    /// <summary>The list layout for the current account (anyone who can view the list).</summary>
    public async Task<OutsourceFieldLayoutResponse> LayoutAsync(CurrentUser actor, CancellationToken ct)
    {
        await using var context = await dbFactory.CreateDbContextAsync(ct);
        await OutsourceItemService.AuthorizeReadAsync(context, actor, OutsourcePermissions.View, ct);
        var layout = await OutsourceFieldLayout.LoadAsync(context, ct);
        var canManage = await AccessService.HasPermissionAsync(context.Database.Connection(), null, actor.Id, OutsourcePermissions.Manage, ct);
        var editor = await OutsourceEditor.LoadAsync(context, actor.Id, ct);
        return new(layout.Columns.Select(column => new OutsourceFieldColumnResponse(
            column.Key, column.Label, column.Field.DataType, column.Options, column.Category, column.Pinned,
            column.VendorVisible, column.VendorEditable, canManage && OutsourceFieldLayout.CanEdit(column, editor))).ToArray());
    }

    public async Task<OutsourceFieldSettingsResponse> SettingsAsync(CurrentUser actor, CancellationToken ct)
    {
        await using var context = await dbFactory.CreateDbContextAsync(ct);
        await OutsourceItemService.AuthorizeReadAsync(context, actor, OutsourcePermissions.FieldSettings, ct);
        return await SettingsResponseAsync(context, ct);
    }

    public async Task<OutsourceFieldSettingsResponse> CreateAsync(CurrentUser actor, OutsourceFieldCreate request, CancellationToken ct)
    {
        var label = Label(request.Label);
        var dataType = OutsourceFieldTypes.IsValid(request.DataType) ? request.DataType! : throw ApiException.BadRequest("请选择栏位类型");
        var options = Options(dataType, request.Options);
        await using var context = await dbFactory.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        actor = await OutsourceItemService.AuthorizeWriteAsync(context, actor, OutsourcePermissions.FieldSettings, ct);
        var fields = await context.OutsourceFields.FromSqlRaw("SELECT * FROM outsource_fields FOR UPDATE").ToListAsync(ct);
        if (fields.Count(x => !x.BuiltIn) >= MaximumCustomFields) throw ApiException.BadRequest($"自定义栏位最多 {MaximumCustomFields} 个");
        EnsureUniqueLabel(fields, label, null);
        string key;
        do { key = "c" + Convert.ToHexString(RandomNumberGenerator.GetBytes(5)).ToLowerInvariant(); }
        while (fields.Any(x => x.FieldKey == key));
        // A new column starts as agreed: internal staff with edit rights fill it in, vendors do not see it.
        var field = new OutsourceField
        {
            FieldKey = key, BuiltIn = false, Label = label, DataType = dataType,
            Options = options.Length == 0 ? null : JsonSerializer.Serialize(options),
            SortNo = (fields.Count == 0 ? 0 : fields.Max(x => x.SortNo)) + 10, Status = AccountStatuses.Active,
            VendorVisible = false, VendorEditable = false, EditorScope = OutsourceEditorScopes.All, UpdatedBy = actor.Id,
        };
        context.OutsourceFields.Add(field);
        await context.SaveChangesAsync(ct);
        await audit.WriteAsync(context.Database.Connection(), context.Database.RequireTransaction(), actor.Id,
            "OUTSOURCE_FIELD_CREATE", "outsource_field", field.Id, new { targetName = label, dataType, options }, null, ct);
        await transaction.CommitAsync(ct);
        return await SettingsResponseAsync(context, ct);
    }

    public async Task<OutsourceFieldSettingsResponse> UpdateAsync(CurrentUser actor, ulong id, OutsourceFieldUpdate request, CancellationToken ct)
    {
        await using var context = await dbFactory.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        actor = await OutsourceItemService.AuthorizeWriteAsync(context, actor, OutsourcePermissions.FieldSettings, ct);
        var fields = await context.OutsourceFields.FromSqlRaw("SELECT * FROM outsource_fields FOR UPDATE").ToListAsync(ct);
        var field = fields.SingleOrDefault(x => x.Id == id) ?? throw ApiException.NotFound("栏位不存在");
        var builtIn = field.BuiltIn ? OutsourceBuiltInFields.ByKey.GetValueOrDefault(field.FieldKey) : null;
        var category = builtIn?.Category ?? OutsourceFieldCategories.Custom;
        var before = await DescribeAsync(context, field, ct);

        if (request.Label is not null || request.DataType is not null || request.Options is not null)
        {
            if (field.BuiltIn)
            {
                if ((request.Label is not null && request.Label.Trim() != field.Label)
                    || (request.DataType is not null && request.DataType != field.DataType) || request.Options is not null)
                    throw ApiException.BadRequest("系统栏位的名称和类型不能修改");
            }
            else
            {
                if (request.Label is not null)
                {
                    var label = Label(request.Label);
                    EnsureUniqueLabel(fields, label, field.Id);
                    field.Label = label;
                }
                var dataType = request.DataType ?? field.DataType;
                if (!OutsourceFieldTypes.IsValid(dataType)) throw ApiException.BadRequest("请选择栏位类型");
                var values = await context.OutsourceItemValues.Where(x => x.FieldId == field.Id).Select(x => x.Value).Distinct().ToArrayAsync(ct);
                if (dataType != field.DataType && values.Length > 0) throw ApiException.Conflict("栏位已有填写内容，不能更改类型");
                var options = request.Options is not null || dataType != field.DataType
                    ? Options(dataType, request.Options ?? OutsourceFieldLayout.Options(field.Options))
                    : OutsourceFieldLayout.Options(field.Options);
                if (dataType == OutsourceFieldTypes.Select && values.FirstOrDefault(x => !options.Contains(x, StringComparer.Ordinal)) is { } used)
                    throw ApiException.Conflict($"选项「{used}」已被使用，不能删除");
                field.DataType = dataType;
                field.Options = options.Length == 0 ? null : JsonSerializer.Serialize(options);
            }
        }
        if (request.VendorVisible is { } visible)
        {
            if (builtIn?.AlwaysVendorVisible == true && !visible) throw ApiException.BadRequest($"「{field.Label}」固定对厂商显示");
            field.VendorVisible = visible;
            // A column the vendor cannot see can never be filled in by the vendor.
            if (!visible) field.VendorEditable = false;
        }
        // Vendors filling columns in is reserved (外协清单契约 → 栏位设定): the column and the rules are kept, the switch is not offered yet.
        if (request.VendorEditable == true) throw ApiException.BadRequest("厂商填写栏位尚未开放");
        if (request.VendorEditable == false) field.VendorEditable = false;
        if (request.EditorScope is not null || request.EditorRoleIds is not null)
        {
            if (!OutsourceFieldCategories.InternalEditable(category)) throw ApiException.BadRequest($"「{field.Label}」不能由人工填写");
            var scope = request.EditorScope ?? field.EditorScope;
            if (!OutsourceEditorScopes.IsValid(scope)) throw ApiException.BadRequest("请选择内部填写范围");
            var roleIds = (request.EditorRoleIds ?? (await context.OutsourceFieldEditorRoles.Where(x => x.FieldId == field.Id)
                .Select(x => x.RoleId).ToArrayAsync(ct))).Distinct().ToArray();
            if (scope == OutsourceEditorScopes.Roles)
            {
                if (roleIds.Length == 0) throw ApiException.BadRequest("请至少选择一个可填写的角色");
                var known = await context.Roles.CountAsync(x => roleIds.Contains(x.Id) && x.Status == AccountStatuses.Active, ct);
                if (known != roleIds.Length) throw ApiException.BadRequest("所选角色不存在或已停用，请刷新后重新选择");
            }
            else roleIds = [];
            field.EditorScope = scope;
            await context.OutsourceFieldEditorRoles.Where(x => x.FieldId == field.Id).ExecuteDeleteAsync(ct);
            context.OutsourceFieldEditorRoles.AddRange(roleIds.Select(roleId => new OutsourceFieldEditorRole { FieldId = field.Id, RoleId = roleId }));
        }
        field.UpdatedBy = actor.Id;
        await context.SaveChangesAsync(ct);
        var after = await DescribeAsync(context, field, ct);
        var changes = AuditChange.OnlyChanged(before.Zip(after, (b, a) => new AuditChange(b.Field, b.Label, b.Before, a.Before)).ToArray());
        if (changes.Length > 0)
        {
            await audit.WriteAsync(context.Database.Connection(), context.Database.RequireTransaction(), actor.Id,
                "OUTSOURCE_FIELD_UPDATE", "outsource_field", field.Id, new { targetName = field.Label, changes }, null, ct);
        }
        await transaction.CommitAsync(ct);
        return await SettingsResponseAsync(context, ct);
    }

    public async Task<OutsourceFieldSettingsResponse> SetStatusAsync(CurrentUser actor, ulong id, string? status, CancellationToken ct)
    {
        if (status is not (AccountStatuses.Active or AccountStatuses.Disabled)) throw ApiException.BadRequest("状态只能是启用或停用");
        await using var context = await dbFactory.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        actor = await OutsourceItemService.AuthorizeWriteAsync(context, actor, OutsourcePermissions.FieldSettings, ct);
        var field = await context.OutsourceFields.FromSqlInterpolated($"SELECT * FROM outsource_fields WHERE id={id} FOR UPDATE")
            .SingleOrDefaultAsync(ct) ?? throw ApiException.NotFound("栏位不存在");
        if (field.BuiltIn) throw ApiException.BadRequest("系统栏位不能停用");
        if (field.Status != status)
        {
            var before = field.Status;
            field.Status = status;
            field.UpdatedBy = actor.Id;
            await context.SaveChangesAsync(ct);
            await audit.WriteAsync(context.Database.Connection(), context.Database.RequireTransaction(), actor.Id,
                "OUTSOURCE_FIELD_STATUS", "outsource_field", field.Id, new
                {
                    targetName = field.Label,
                    changes = new[] { new AuditChange("status", "状态", StatusLabel(before), StatusLabel(status)) },
                }, null, ct);
        }
        await transaction.CommitAsync(ct);
        return await SettingsResponseAsync(context, ct);
    }

    public async Task<OutsourceFieldSettingsResponse> SetOrderAsync(CurrentUser actor, OutsourceFieldOrderRequest request, CancellationToken ct)
    {
        var ids = request.FieldIds ?? throw ApiException.BadRequest("请提供栏位顺序");
        await using var context = await dbFactory.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        actor = await OutsourceItemService.AuthorizeWriteAsync(context, actor, OutsourcePermissions.FieldSettings, ct);
        var fields = await context.OutsourceFields.FromSqlRaw("SELECT * FROM outsource_fields FOR UPDATE").ToListAsync(ct);
        static bool IsPinned(OutsourceField field) => field.BuiltIn && OutsourceBuiltInFields.ByKey.TryGetValue(field.FieldKey, out var builtIn) && builtIn.Pinned;
        var movable = fields.Where(x => !IsPinned(x)).ToArray();
        if (ids.Length != movable.Length || ids.Distinct().Count() != ids.Length || !ids.ToHashSet().SetEquals(movable.Select(x => x.Id)))
            throw ApiException.VersionConflict("栏位已变动，请刷新后重新排序");
        // Pinned columns keep their place on the left; everything else follows in the given order.
        var start = fields.Where(IsPinned).Select(x => x.SortNo).DefaultIfEmpty(0).Max();
        var before = fields.OrderBy(x => x.SortNo).ThenBy(x => x.Id).Select(x => x.Label).ToArray();
        for (var index = 0; index < ids.Length; index++)
        {
            var field = movable.Single(x => x.Id == ids[index]);
            field.SortNo = start + 10 * (index + 1);
        }
        await context.SaveChangesAsync(ct);
        var after = fields.OrderBy(x => x.SortNo).ThenBy(x => x.Id).Select(x => x.Label).ToArray();
        if (!before.SequenceEqual(after))
        {
            await audit.WriteAsync(context.Database.Connection(), context.Database.RequireTransaction(), actor.Id,
                "OUTSOURCE_FIELD_ORDER", "outsource_field", null, new
                {
                    targetName = "外协栏位顺序",
                    changes = new[] { new AuditChange("order", "顺序", string.Join("、", before), string.Join("、", after)) },
                }, null, ct);
        }
        await transaction.CommitAsync(ct);
        return await SettingsResponseAsync(context, ct);
    }

    private static string StatusLabel(string status) => status == AccountStatuses.Active ? "启用" : "停用";

    private static async Task<OutsourceFieldSettingsResponse> SettingsResponseAsync(YfDbContext context, CancellationToken ct)
    {
        var layout = await OutsourceFieldLayout.LoadAsync(context, ct);
        var inUse = (await context.OutsourceItemValues.AsNoTracking().Select(x => x.FieldId).Distinct().ToArrayAsync(ct)).ToHashSet();
        var roles = await context.Roles.AsNoTracking().Where(x => x.Status == AccountStatuses.Active)
            .OrderBy(x => x.Id).Select(x => new OutsourceFieldRoleOption(x.Id, x.Name)).ToArrayAsync(ct);
        return new(layout.Everything.Where(x => x.Field.Id != 0).Select(column => new OutsourceFieldSettingResponse(
            column.Field.Id, column.Key, column.Field.BuiltIn, column.Label, column.Field.DataType, column.Options, column.Category,
            column.Field.SortNo, column.Field.Status, column.Pinned, column.VendorVisible, column.BuiltIn?.AlwaysVendorVisible == true,
            column.VendorEditable, OutsourceFieldCategories.VendorEditable(column.Category), column.EditorScope,
            OutsourceFieldCategories.InternalEditable(column.Category), column.EditorRoles.Order().ToArray(),
            inUse.Contains(column.Field.Id), DateTime.SpecifyKind(column.Field.UpdatedAt, DateTimeKind.Utc))).ToArray(), roles);
    }

    private sealed record Described(string Field, string Label, string? Before);

    /// <summary>The settings of one field as audit text, compared before and after an update.</summary>
    private static async Task<Described[]> DescribeAsync(YfDbContext context, OutsourceField field, CancellationToken ct)
    {
        var roleIds = await context.OutsourceFieldEditorRoles.Where(x => x.FieldId == field.Id).Select(x => x.RoleId).ToArrayAsync(ct);
        var roleNames = await context.Roles.Where(x => roleIds.Contains(x.Id)).OrderBy(x => x.Id).Select(x => x.Name).ToArrayAsync(ct);
        static string YesNo(bool value) => value ? "是" : "否";
        var scope = field.EditorScope switch
        {
            OutsourceEditorScopes.All => "所有有编辑权限的人员",
            OutsourceEditorScopes.Roles => "指定角色：" + string.Join("、", roleNames),
            _ => "内部不填写",
        };
        return
        [
            new("label", "名称", field.Label),
            new("dataType", "类型", field.DataType),
            new("options", "选项", string.Join("、", OutsourceFieldLayout.Options(field.Options))),
            new("vendorVisible", "厂商可见", YesNo(field.VendorVisible)),
            new("vendorEditable", "厂商填写", YesNo(field.VendorEditable)),
            new("editorScope", "内部填写", scope),
        ];
    }

    private static string Label(string? input)
    {
        var label = input?.Trim() ?? "";
        if (label.Length == 0) throw ApiException.BadRequest("请填写栏位名称");
        if (label.EnumerateRunes().Count() > 32) throw ApiException.BadRequest("栏位名称最多 32 个字符");
        return label;
    }

    private static void EnsureUniqueLabel(IEnumerable<OutsourceField> fields, string label, ulong? except)
    {
        if (fields.Any(x => x.Id != except && string.Equals(x.Label.Trim(), label, StringComparison.OrdinalIgnoreCase)))
            throw ApiException.Conflict($"栏位名称「{label}」已存在");
    }

    private static string[] Options(string dataType, string[]? input)
    {
        if (dataType != OutsourceFieldTypes.Select)
        {
            if (input is { Length: > 0 }) throw ApiException.BadRequest("只有下拉选项类型的栏位可以设定选项");
            return [];
        }
        var options = (input ?? []).Select(x => x?.Trim() ?? "").Where(x => x.Length > 0).ToArray();
        if (options.Length == 0) throw ApiException.BadRequest("请至少填写一个选项");
        if (options.Length > MaximumOptions) throw ApiException.BadRequest($"选项最多 {MaximumOptions} 个");
        if (options.Any(x => x.EnumerateRunes().Count() > 32)) throw ApiException.BadRequest("每个选项最多 32 个字符");
        if (options.Distinct(StringComparer.Ordinal).Count() != options.Length) throw ApiException.BadRequest("选项不能重复");
        return options;
    }
}
