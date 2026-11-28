using System.IO.Compression;
using System.Net;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using Yf.Api.Tests.Oem;

namespace Yf.Api.Tests;

/// <summary>
/// End-to-end HTTP, EF Code-First, RBAC, vendor isolation, anti-leak, global phone linkage,
/// and merged-cell Excel import/export tests for the unified contact matrix.
/// </summary>
public sealed class OutsourceContactTests
{
    private static readonly JsonSerializerOptions UnescapedJsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static string UnescapedJson(JsonNode node) => node.ToJsonString(UnescapedJsonOptions);

    [Fact(Timeout = 180_000)]
    public async Task EmptyStateAndStrictPermissionsEnforceReadOnlyVsManageVsSettings()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await OemTestHost.StartAsync(ct);
        var admin = await host.LoginAdminAsync(ct);

        await host.CreateInternalUserAsync("ct_reader", "Reader#2026", ["outsource"], null, ct);
        await host.CreateInternalUserAsync("ct_manager", "Manager#2026", ["outsource", "outsource:contact_manage"], null, ct);
        await host.CreateInternalUserAsync("ct_config", "Config#2026", ["outsource", "outsource:contact_settings"], null, ct);

        var reader = await host.LoginInternalAsync("ct_reader", "Reader#2026", ct);
        var manager = await host.LoginInternalAsync("ct_manager", "Manager#2026", ct);
        var config = await host.LoginInternalAsync("ct_config", "Config#2026", ct);

        // 1. Fresh DB starts with ZERO built-in columns and ZERO rows.
        var initial = await reader.GetAsync("/api/v1/outsource/contacts", ct).Ok();
        Assert.Empty(initial["columns"]!.AsArray());
        Assert.Empty(initial["rows"]!.AsArray());
        Assert.False(initial["canManage"]!.GetValue<bool>());
        Assert.False(initial["canConfigureColumns"]!.GetValue<bool>());

        // 0-column guards
        await manager.PostAsync("/api/v1/outsource/contacts/rows", new { }, ct).Status(HttpStatusCode.BadRequest);
        await manager.GetAsync("/api/v1/outsource/contacts/template", ct).Status(HttpStatusCode.BadRequest);
        await reader.GetAsync("/api/v1/outsource/contacts/export", ct).Status(HttpStatusCode.BadRequest);

        // 2. Strict RBAC: reader cannot edit anything or configure columns; manager cannot configure columns; config cannot manage rows/people/import.
        await reader.PostAsync("/api/v1/outsource/contacts/columns", new { title = "列A", dataType = "TEXT" }, ct).Status(HttpStatusCode.Forbidden);
        await manager.GetAsync("/api/v1/outsource/contacts/columns", ct).Status(HttpStatusCode.Forbidden);
        await manager.PostAsync("/api/v1/outsource/contacts/columns", new { title = "列A", dataType = "TEXT" }, ct).Status(HttpStatusCode.Forbidden);

        // Create two dynamic columns with config role, test IsGroupColumn toggle & deleting any column (including group column).
        var colGroup = await config.PostAsync("/api/v1/outsource/contacts/columns",
            new { title = "归属端", dataType = "TEXT", autoMerge = true, vendorVisible = true, isGroupColumn = true }, ct).Ok();
        var colTempGroup = await config.PostAsync("/api/v1/outsource/contacts/columns",
            new { title = "临时分组列", dataType = "TEXT", autoMerge = false, vendorVisible = true, isGroupColumn = true }, ct).Ok();

        // Setting isGroupColumn=true on colTempGroup cleared it on colGroup.
        var colsAfterToggle = (await config.GetAsync("/api/v1/outsource/contacts/columns", ct).Ok()).AsArray();
        Assert.False(colsAfterToggle.Single(c => c!.Id() == colGroup.Id())!["isGroupColumn"]!.GetValue<bool>());
        Assert.True(colsAfterToggle.Single(c => c!.Id() == colTempGroup.Id())!["isGroupColumn"]!.GetValue<bool>());

        // Even a group column can be deleted freely.
        await config.DeleteAsync($"/api/v1/outsource/contacts/columns/{colTempGroup.Id()}", ct).Ok();
        await config.PutAsync($"/api/v1/outsource/contacts/columns/{colGroup.Id()}", new { isGroupColumn = true }, ct).Ok();

        // Now test reader & config role 403 rejections against row/person/import endpoints.
        await reader.PostAsync("/api/v1/outsource/contacts/rows", new { }, ct).Status(HttpStatusCode.Forbidden);
        await reader.GetAsync("/api/v1/outsource/contacts/template", ct).Status(HttpStatusCode.Forbidden);
        await reader.GetAsync("/api/v1/outsource/contacts/user-options", ct).Status(HttpStatusCode.Forbidden);
        await reader.PostAsync("/api/v1/outsource/contacts/people", new { scopeType = "INTERNAL", name = "禁止", phone = "13800000001" }, ct).Status(HttpStatusCode.Forbidden);

        await config.PostAsync("/api/v1/outsource/contacts/rows", new { }, ct).Status(HttpStatusCode.Forbidden);
        await config.PostAsync("/api/v1/outsource/contacts/people", new { scopeType = "INTERNAL", name = "禁止", phone = "13800000001" }, ct).Status(HttpStatusCode.Forbidden);
        await config.PostAsync("/api/v1/outsource/contacts/import/preview", new { mode = "APPEND", rows = Array.Empty<object>() }, ct).Status(HttpStatusCode.Forbidden);

        // Manager creates a row and reader can view & export cleanly, but still cannot modify.
        var createdRow = await manager.PostAsync("/api/v1/outsource/contacts/rows", new { }, ct).Ok();
        var rowId = createdRow.Id();
        var version = createdRow["version"]!.GetValue<uint>();

        await reader.PutAsync("/api/v1/outsource/contacts/cells", new
        {
            columnId = colGroup.Id(),
            rows = new[] { new { rowId, expectedVersion = version } },
            textValue = "非法修改",
        }, ct).Status(HttpStatusCode.Forbidden);
        await reader.PutAsync("/api/v1/outsource/contacts/rows/visibility", new
        {
            rows = new[] { new { rowId, expectedVersion = version } },
            visibility = "ALL_VENDORS",
        }, ct).Status(HttpStatusCode.Forbidden);
        await reader.DeleteAsync($"/api/v1/outsource/contacts/rows/{rowId}?expectedVersion={version}", ct).Status(HttpStatusCode.Forbidden);

        // Reader's read & export endpoints succeed.
        var readerView = await reader.GetAsync("/api/v1/outsource/contacts", ct).Ok();
        Assert.Single(readerView["rows"]!.AsArray());
        Assert.False(readerView["canManage"]!.GetValue<bool>());
        Assert.False(readerView["canConfigureColumns"]!.GetValue<bool>());
        using var exportResp = await reader.Http.GetAsync("/api/v1/outsource/contacts/export", ct);
        Assert.Equal(HttpStatusCode.OK, exportResp.StatusCode);

        // Admin retains full powers.
        var adminView = await admin.GetAsync("/api/v1/outsource/contacts", ct).Ok();
        Assert.True(adminView["canManage"]!.GetValue<bool>());
        Assert.True(adminView["canConfigureColumns"]!.GetValue<bool>());
    }

    [Fact(Timeout = 180_000)]
    public async Task MatrixWorkflowEnforcesVendorIsolationAntiLeakPhoneSyncConcurrencyAndExcel()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await OemTestHost.StartAsync(ct);
        var admin = await host.LoginAdminAsync(ct);

        var vendorA = await host.CreateVendorAsync(admin, "外包厂商A", "cm_va", ct);
        var vendorB = await host.CreateVendorAsync(admin, "外包厂商B", "cm_vb", ct);
        var vendorC = await host.CreateVendorAsync(admin, "外包厂商C", "cm_vc", ct);
        var companyA = await host.CompanyIdOfAsync("cm_va", ct);
        var companyB = await host.CompanyIdOfAsync("cm_vb", ct);
        var companyC = await host.CompanyIdOfAsync("cm_vc", ct);

        // Configure 5 dynamic columns covering TEXT, MULTILINE, PERSON (COMBINED & SPLIT), vendorVisible=false, IsGroupColumn.
        var cGroup = await admin.PostAsync("/api/v1/outsource/contacts/columns",
            new { title = "归属端", dataType = "TEXT", autoMerge = true, vendorVisible = true, isGroupColumn = true }, ct).Ok();
        var cUnit = await admin.PostAsync("/api/v1/outsource/contacts/columns",
            new { title = "对应课别", dataType = "TEXT", autoMerge = true, vendorVisible = true }, ct).Ok();
        var cEng = await admin.PostAsync("/api/v1/outsource/contacts/columns",
            new { title = "当值工程师", dataType = "PERSON", personDisplay = "COMBINED", autoMerge = false, vendorVisible = true }, ct).Ok();
        var cMgr = await admin.PostAsync("/api/v1/outsource/contacts/columns",
            new { title = "课别主管", dataType = "PERSON", personDisplay = "SPLIT", phoneTitle = "主管电话", autoMerge = true, vendorVisible = true }, ct).Ok();
        var cNote = await admin.PostAsync("/api/v1/outsource/contacts/columns",
            new { title = "内部备注", dataType = "TEXT", autoMerge = false, vendorVisible = false }, ct).Ok();

        // Create people in master table.
        var pMgrMech = await admin.PostAsync("/api/v1/outsource/contacts/people",
            new { scopeType = "INTERNAL", name = "主管甲", phone = "13800000001", titleHint = "机构主管" }, ct).Ok();
        var pMgrElec = await admin.PostAsync("/api/v1/outsource/contacts/people",
            new { scopeType = "INTERNAL", name = "主管乙", phone = "13800000002", titleHint = "电控主管" }, ct).Ok();
        var pEng1 = await admin.PostAsync("/api/v1/outsource/contacts/people",
            new { scopeType = "INTERNAL", name = "工程师1", phone = "13800000011" }, ct).Ok();
        var pEng2 = await admin.PostAsync("/api/v1/outsource/contacts/people",
            new { scopeType = "INTERNAL", name = "工程师2", phone = "13800000012" }, ct).Ok();
        var pVendorAPerson = await admin.PostAsync("/api/v1/outsource/contacts/people",
            new { scopeType = "VENDOR", vendorId = companyA, name = "厂商A联系人", phone = "13900000001" }, ct).Ok();

        // Duplicate person check
        await admin.PostAsync("/api/v1/outsource/contacts/people",
            new { scopeType = "INTERNAL", name = "工程师1", phone = "13899999999" }, ct).Status(HttpStatusCode.Conflict);

        // Create 3 rows:
        // Row 1: 研发端 / 机构一课 -> ALL_VENDORS (visible to A, B, C)
        // Row 2: 研发端 / 电控工令 -> SPECIFIC_VENDORS [A] (工程师1)
        // Row 3: 研发端 / 电控工令 -> SPECIFIC_VENDORS [B] (工程师2)
        // Row 4: 外包厂商端 / 项目PM -> SPECIFIC_VENDORS [A] (厂商A联系人)
        var r1 = await admin.PostAsync("/api/v1/outsource/contacts/rows", new { }, ct).Ok();
        var r2 = await admin.PostAsync("/api/v1/outsource/contacts/rows", new { }, ct).Ok();
        var r3 = await admin.PostAsync("/api/v1/outsource/contacts/rows", new { }, ct).Ok();
        var r4 = await admin.PostAsync("/api/v1/outsource/contacts/rows", new { }, ct).Ok();

        // Batch set cGroup = "研发端" on rows 1..3 in one merged-block edit!
        await admin.PutAsync("/api/v1/outsource/contacts/cells", new
        {
            columnId = cGroup.Id(),
            rows = new[]
            {
                new { rowId = r1.Id(), expectedVersion = r1["version"]!.GetValue<uint>() },
                new { rowId = r2.Id(), expectedVersion = r2["version"]!.GetValue<uint>() },
                new { rowId = r3.Id(), expectedVersion = r3["version"]!.GetValue<uint>() },
            },
            textValue = "研发端",
        }, ct).Ok();

        // Stale expectedVersion returns 409/40903!
        await admin.PutAsync("/api/v1/outsource/contacts/cells", new
        {
            columnId = cUnit.Id(),
            rows = new[] { new { rowId = r1.Id(), expectedVersion = 1u } }, // version is now 2
            textValue = "过期版本",
        }, ct).Status(HttpStatusCode.Conflict, 40903);

        // Refresh versions from GET /
        var matrix = await admin.GetAsync("/api/v1/outsource/contacts", ct).Ok();
        uint Ver(ulong id) => matrix["rows"]!.AsArray().Single(x => x!.Id() == id)!["version"]!.GetValue<uint>();

        await admin.PutAsync("/api/v1/outsource/contacts/cells", new
        {
            columnId = cUnit.Id(),
            rows = new[] { new { rowId = r1.Id(), expectedVersion = Ver(r1.Id()) } },
            textValue = "机构一课",
        }, ct).Ok();
        await admin.PutAsync("/api/v1/outsource/contacts/cells", new
        {
            columnId = cUnit.Id(),
            rows = new[]
            {
                new { rowId = r2.Id(), expectedVersion = Ver(r2.Id()) },
                new { rowId = r3.Id(), expectedVersion = Ver(r3.Id()) },
            },
            textValue = "电控工令",
        }, ct).Ok();
        await admin.PutAsync("/api/v1/outsource/contacts/cells", new
        {
            columnId = cGroup.Id(),
            rows = new[] { new { rowId = r4.Id(), expectedVersion = Ver(r4.Id()) } },
            textValue = "外包厂商端",
        }, ct).Ok();

        matrix = await admin.GetAsync("/api/v1/outsource/contacts", ct).Ok();
        await admin.PutAsync("/api/v1/outsource/contacts/cells", new
        {
            columnId = cUnit.Id(),
            rows = new[] { new { rowId = r4.Id(), expectedVersion = Ver(r4.Id()) } },
            textValue = "项目PM",
        }, ct).Ok();

        // Populate people & internal note (each cell edit increments row version, so refresh between edits on the same row)
        matrix = await admin.GetAsync("/api/v1/outsource/contacts", ct).Ok();
        await admin.PutAsync("/api/v1/outsource/contacts/cells", new
        {
            columnId = cMgr.Id(),
            rows = new[] { new { rowId = r1.Id(), expectedVersion = Ver(r1.Id()) } },
            people = new[] { new { personId = pMgrMech.Id(), prefixLabel = "" } },
        }, ct).Ok();
        await admin.PutAsync("/api/v1/outsource/contacts/cells", new
        {
            columnId = cMgr.Id(),
            rows = new[]
            {
                new { rowId = r2.Id(), expectedVersion = Ver(r2.Id()) },
                new { rowId = r3.Id(), expectedVersion = Ver(r3.Id()) },
            },
            people = new[] { new { personId = pMgrElec.Id(), prefixLabel = "" } },
        }, ct).Ok();

        matrix = await admin.GetAsync("/api/v1/outsource/contacts", ct).Ok();
        await admin.PutAsync("/api/v1/outsource/contacts/cells", new
        {
            columnId = cEng.Id(),
            rows = new[] { new { rowId = r2.Id(), expectedVersion = Ver(r2.Id()) } },
            people = new[] { new { personId = pEng1.Id(), prefixLabel = "工令设计" } },
        }, ct).Ok();
        await admin.PutAsync("/api/v1/outsource/contacts/cells", new
        {
            columnId = cEng.Id(),
            rows = new[] { new { rowId = r3.Id(), expectedVersion = Ver(r3.Id()) } },
            people = new[] { new { personId = pEng2.Id(), prefixLabel = "工令设计" } },
        }, ct).Ok();
        await admin.PutAsync("/api/v1/outsource/contacts/cells", new
        {
            columnId = cNote.Id(),
            rows = new[] { new { rowId = r1.Id(), expectedVersion = Ver(r1.Id()) } },
            textValue = "内部保密工艺说明",
        }, ct).Ok();

        // Set visibility on r1..r4
        matrix = await admin.GetAsync("/api/v1/outsource/contacts", ct).Ok();
        await admin.PutAsync("/api/v1/outsource/contacts/rows/visibility", new
        {
            rows = new[] { new { rowId = r1.Id(), expectedVersion = Ver(r1.Id()) } },
            visibility = "ALL_VENDORS",
        }, ct).Ok();
        await admin.PutAsync("/api/v1/outsource/contacts/rows/visibility", new
        {
            rows = new[] { new { rowId = r2.Id(), expectedVersion = Ver(r2.Id()) } },
            visibility = "SPECIFIC_VENDORS",
            vendorIds = new[] { companyA },
        }, ct).Ok();
        await admin.PutAsync("/api/v1/outsource/contacts/rows/visibility", new
        {
            rows = new[] { new { rowId = r3.Id(), expectedVersion = Ver(r3.Id()) } },
            visibility = "SPECIFIC_VENDORS",
            vendorIds = new[] { companyB },
        }, ct).Ok();

        // Anti-leak check 1: r4 is still INTERNAL_ONLY -> assigning pVendorAPerson succeeds;
        // then widening r4 to ALL_VENDORS or [companyA, companyB] MUST FAIL with 400!
        matrix = await admin.GetAsync("/api/v1/outsource/contacts", ct).Ok();
        await admin.PutAsync("/api/v1/outsource/contacts/cells", new
        {
            columnId = cEng.Id(),
            rows = new[] { new { rowId = r4.Id(), expectedVersion = Ver(r4.Id()) } },
            people = new[] { new { personId = pVendorAPerson.Id(), prefixLabel = "厂方PM" } },
        }, ct).Ok();

        matrix = await admin.GetAsync("/api/v1/outsource/contacts", ct).Ok();
        await admin.PutAsync("/api/v1/outsource/contacts/rows/visibility", new
        {
            rows = new[] { new { rowId = r4.Id(), expectedVersion = Ver(r4.Id()) } },
            visibility = "ALL_VENDORS",
        }, ct).Status(HttpStatusCode.BadRequest);
        await admin.PutAsync("/api/v1/outsource/contacts/rows/visibility", new
        {
            rows = new[] { new { rowId = r4.Id(), expectedVersion = Ver(r4.Id()) } },
            visibility = "SPECIFIC_VENDORS",
            vendorIds = new[] { companyA, companyB },
        }, ct).Status(HttpStatusCode.BadRequest);

        // Setting r4 to ONLY [companyA] succeeds!
        await admin.PutAsync("/api/v1/outsource/contacts/rows/visibility", new
        {
            rows = new[] { new { rowId = r4.Id(), expectedVersion = Ver(r4.Id()) } },
            visibility = "SPECIFIC_VENDORS",
            vendorIds = new[] { companyA },
        }, ct).Ok();

        // Anti-leak check 2: cannot assign vendorA's person onto r1 (ALL_VENDORS)!
        matrix = await admin.GetAsync("/api/v1/outsource/contacts", ct).Ok();
        await admin.PutAsync("/api/v1/outsource/contacts/cells", new
        {
            columnId = cEng.Id(),
            rows = new[] { new { rowId = r1.Id(), expectedVersion = Ver(r1.Id()) } },
            people = new[] { new { personId = pVendorAPerson.Id(), prefixLabel = "" } },
        }, ct).Status(HttpStatusCode.BadRequest);

        // Vendor portal isolation checks
        var portalA = await vendorA.GetAsync("/api/v1/oem/contacts", ct).Ok();
        var portalACols = portalA["columns"]!.AsArray().Select(c => c!["colKey"]!.GetValue<string>()).ToArray();
        Assert.DoesNotContain(cNote["colKey"]!.GetValue<string>(), portalACols);
        var portalARows = portalA["rows"]!.AsArray();
        Assert.Equal(3, portalARows.Count); // r1, r2, r4
        var portalAJson = UnescapedJson(portalA);
        Assert.DoesNotContain("工程师2", portalAJson, StringComparison.Ordinal);
        Assert.DoesNotContain("内部保密工艺说明", portalAJson, StringComparison.Ordinal);
        Assert.DoesNotContain("vendorIds", portalAJson, StringComparison.Ordinal);
        Assert.DoesNotContain("employeeNo", portalAJson, StringComparison.Ordinal);

        // Vendor C (mech only) sees ONLY r1 — zero电控, zero外包厂商端!
        var portalC = await vendorC.GetAsync("/api/v1/oem/contacts", ct).Ok();
        var portalCRows = portalC["rows"]!.AsArray();
        Assert.Single(portalCRows);
        Assert.Equal("机构一课", portalCRows[0]!["values"]![cUnit["colKey"]!.GetValue<string>()]!.GetValue<string>());
        Assert.DoesNotContain("主管乙", UnescapedJson(portalC), StringComparison.Ordinal);

        // Internal staff cannot call vendor portal contact endpoints
        await admin.GetAsync("/api/v1/oem/contacts", ct).Status(HttpStatusCode.Forbidden);
        await admin.GetAsync("/api/v1/oem/contacts/export", ct).Status(HttpStatusCode.Forbidden);

        // Global phone linkage + sync check across internal and portal
        await admin.PutAsync($"/api/v1/outsource/contacts/people/{pEng1.Id()}",
            new { scopeType = "INTERNAL", name = "工程师1", phone = "13899990001" }, ct).Ok();
        var portalAAfterPhone = await vendorA.GetAsync("/api/v1/oem/contacts", ct).Ok();
        Assert.Contains("13899990001", UnescapedJson(portalAAfterPhone), StringComparison.Ordinal);

        // Person references & handover
        var refs = (await admin.GetAsync($"/api/v1/outsource/contacts/people/{pEng1.Id()}/references", ct).Ok()).AsArray();
        Assert.Single(refs);
        await admin.PostAsync($"/api/v1/outsource/contacts/people/{pEng1.Id()}/handover",
            new { targetPersonId = pEng2.Id() }, ct).Ok();
        var portalAAfterHandover = await vendorA.GetAsync("/api/v1/oem/contacts", ct).Ok();
        Assert.Contains("工程师2", UnescapedJson(portalAAfterHandover), StringComparison.Ordinal);
        Assert.DoesNotContain("工程师1", UnescapedJson(portalAAfterHandover), StringComparison.Ordinal);

        // Disabled person disappears from vendor portal while staying in internal view
        await admin.PutAsync($"/api/v1/outsource/contacts/people/{pEng2.Id()}/status", new { status = "DISABLED" }, ct).Ok();
        var portalAAfterDisable = await vendorA.GetAsync("/api/v1/oem/contacts", ct).Ok();
        Assert.DoesNotContain("工程师2", UnescapedJson(portalAAfterDisable), StringComparison.Ordinal);
        var internalAfterDisable = await admin.GetAsync("/api/v1/outsource/contacts", ct).Ok();
        Assert.Contains("工程师2", UnescapedJson(internalAfterDisable), StringComparison.Ordinal);

        // DeleteCompanyAsync refuses deleting companyA while referenced by contact person/row
        await admin.DeleteAsync($"/api/v1/oem/companies/{companyA}", ct).Status(HttpStatusCode.BadRequest);

        // Excel export produces valid OOXML with <mergeCells>, inlineStr, and zero formula cells
        using var internalExport = await admin.Http.GetAsync("/api/v1/outsource/contacts/export", ct);
        var internalSheet = await SheetXmlAsync(internalExport, ct);
        Assert.Contains("<mergeCells", internalSheet, StringComparison.Ordinal);
        Assert.Contains("<mergeCell ref=\"A2:A4\"/>", internalSheet, StringComparison.Ordinal); // 3 研发端 rows merged in column A!
        Assert.Contains("t=\"inlineStr\"", internalSheet, StringComparison.Ordinal);
        Assert.DoesNotContain("<f>", internalSheet, StringComparison.Ordinal);

        using var vendorExport = await vendorA.Http.GetAsync("/api/v1/oem/contacts/export", ct);
        var vendorSheet = await SheetXmlAsync(vendorExport, ct);
        Assert.Contains("<mergeCells", vendorSheet, StringComparison.Ordinal);
        Assert.DoesNotContain("内部保密工艺说明", vendorSheet, StringComparison.Ordinal);

        // Excel import preview + confirm: sample skip, phone update guard, REPLACE_GROUPS, anti-leak block
        var gKey = cGroup["colKey"]!.GetValue<string>();
        var uKey = cUnit["colKey"]!.GetValue<string>();
        var eKey = cEng["colKey"]!.GetValue<string>();
        var mKey = cMgr["colKey"]!.GetValue<string>();

        var importInput = new
        {
            mode = "REPLACE_GROUPS",
            rows = new object[]
            {
                new
                {
                    rowNumber = 2,
                    visibilityText = "全部外包（可填：全部外包 / 仅内部 / 厂商A、厂商B）",
                    cells = new[] { new { colKey = gKey, textValue = "示例分组（导入时自动忽略本范例行）", phonePairText = (string?)null } },
                },
                new
                {
                    rowNumber = 3,
                    visibilityText = "外包厂商C",
                    cells = new object[]
                    {
                        new { colKey = gKey, textValue = "研发端", phonePairText = (string?)null },
                        new { colKey = uKey, textValue = "机构一课", phonePairText = (string?)null },
                        new { colKey = eKey, textValue = "导入前缀：工程师1 13877778888", phonePairText = (string?)null },
                        new { colKey = mKey, textValue = "主管甲", phonePairText = "13800000001" },
                    },
                },
            },
        };
        var previewPhoneChange = await admin.PostAsync("/api/v1/outsource/contacts/import/preview", importInput, ct).Ok();
        var previewVersion = previewPhoneChange["previewVersion"]!.GetValue<string>();
        Assert.Equal(1, previewPhoneChange["ready"]!.GetValue<int>());
        Assert.Equal(1, previewPhoneChange["skipped"]!.GetValue<int>());
        Assert.Equal(1, previewPhoneChange["phoneChangeCount"]!.GetValue<int>());

        Assert.Equal(3, previewPhoneChange["replaceCount"]!.GetValue<int>());
        // Missing confirmation must fail even when the preview still matches.
        await admin.PostAsync("/api/v1/outsource/contacts/import", new
        {
            importInput.mode, importInput.rows, previewVersion, confirmPhoneUpdates = false,
        }, ct).Status(HttpStatusCode.BadRequest);

        var importResult = await admin.PostAsync("/api/v1/outsource/contacts/import", new
        {
            importInput.mode, importInput.rows, previewVersion, confirmPhoneUpdates = true,
        }, ct).Ok();
        Assert.Equal(1, importResult["inserted"]!.GetValue<int>());
        Assert.Equal(3, importResult["replaced"]!.GetValue<int>());
        Assert.Equal(1, importResult["updatedPhones"]!.GetValue<int>());

        var afterImport = await admin.GetAsync("/api/v1/outsource/contacts", ct).Ok();
        Assert.Equal(2, afterImport["rows"]!.AsArray().Count); // 1 new 研发端 row + preserved 外包厂商端 r4!

        // Anti-leak during import: trying to import vendorA's person for vendorB must be INVALID!
        var leakImportPreview = await admin.PostAsync("/api/v1/outsource/contacts/import/preview", new
        {
            mode = "APPEND",
            rows = new object[]
            {
                new
                {
                    rowNumber = 2,
                    visibilityText = "外包厂商B",
                    cells = new object[]
                    {
                        new { colKey = gKey, textValue = "外包厂商端" },
                        new { colKey = eKey, textValue = "厂商A联系人 13900000001" },
                    },
                },
            },
        }, ct).Ok();
        // Because row targets companyB, vendorA's person isn't matched as vendorB; if marked explicitly via personOverrides to vendorA, it is blocked!
        var leakOverridePreview = await admin.PostAsync("/api/v1/outsource/contacts/import/preview", new
        {
            mode = "APPEND",
            rows = new object[]
            {
                new
                {
                    rowNumber = 2,
                    visibilityText = "外包厂商B",
                    cells = new object[]
                    {
                        new { colKey = gKey, textValue = "外包厂商端" },
                        new { colKey = eKey, textValue = "新厂商人员 13900000099" },
                    },
                },
            },
            personOverrides = new[]
            {
                new { matchKey = $"V:{companyB}\0新厂商人员\013900000099", vendorId = (ulong?)companyA },
            },
        }, ct).Ok();
        Assert.Equal(1, leakOverridePreview["invalid"]!.GetValue<int>());
    }

    private static async Task<string> SheetXmlAsync(HttpResponseMessage response, CancellationToken ct)
    {
        response.EnsureSuccessStatusCode();
        using var zip = new ZipArchive(new MemoryStream(await response.Content.ReadAsByteArrayAsync(ct)));
        using var reader = new StreamReader(zip.GetEntry("xl/worksheets/sheet1.xml")!.Open());
        return await reader.ReadToEndAsync(ct);
    }
}
