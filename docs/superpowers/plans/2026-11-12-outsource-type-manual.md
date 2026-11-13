# 外发类型改为人工填写 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 外发类型不再来自 IPMS，由生管在汇入时与厂商一起填写，汇入之后采购可以修改，并且每次都校验类型与厂商的对应关系。

**Architecture:** 从 `IpmsWorkOrder` 与同步流程中移除类型；汇入行新增「外发类型」；单笔与批量编辑新增 `typeId`；类型与厂商的校验统一走 `OutsourceReference.Match`。前端汇入模板和编辑、批量对话框同步增加类型。

**Tech Stack:** .NET 8、EF Core、xUnit v3；React 18 + Arco、Vitest；OpenAPI 契约流程（见根 `CLAUDE.md`）。

**Spec:** `docs/superpowers/specs/2026-11-12-ipms-mes-mirror-design.md` 的「对外协契约的连带变更」一节。

## Global Constraints

- 汇入模板列固定为：外发日期、工令、外发厂商、外发类型（四列，类型必填）。
- 类型输入接受 `OEMA` 或 `OEMA【进料+制造】`，不区分大小写（沿用 `OutsourceReference.Normalize`）；存入 `outsource_items.type_code` 的是字典里的规范代码（`type.Code`）。
- 只有**启用**的类型参与比对；停用的类型视同不在字典中。
- 类型不在字典：「外发类型不在字典中」；类型与厂商不符：「{类型标签} 不可配{厂商名}」（与现有文案一致）。
- 汇入之后采购可改类型：单笔 `PUT /outsource/items/{id}`、批量 `PUT /outsource/items/batch`，都需要 `outsource:manage` 且带版本检查，已结案不可改。
- 保存时对「修改后的类型与厂商组合」整体校验（同时改两者按新值），只要类型或厂商有变化就校验；两者都没变则不校验，历史上类型已停用的记录仍可改备注。
- 已发送的记录改类型后转为「有变更待通知」，并写入变更履历（字段键沿用 `typeCode`，标签「外发类型」）和审计 `OUTSOURCE_ITEM_UPDATE`。
- IPMS 同步不得再读取或改写类型；`IpmsWorkOrder` 不再含 `OutsourceTypeCode`。
- 开发阶段不保证旧数据兼容；`outsource_items.type_code` 列与其他表结构不变，无迁移。
- 契约与 OpenAPI：改动路由请求体后依次 `YF_UPDATE_OPENAPI=1 dotnet test .\tests\Yf.Api.Tests.csproj --filter OpenApiContractTests`、`npm run generate:api-types`，并同步 `tests/Contracts/api-v1.json`。

## Review Focus

1. 汇入文件的类型格式五花八门：`oema`、`OEMA【进料+制造】`、前后空格、全角括号、空白：空白报「请填写外发类型」；其余能规范化的接受，不能的报不在字典中。（Task 1）
2. 汇入后类型被停用：该记录仍可改备注、日期，但改厂商或类型时要求选一个启用的类型。（Task 3）
3. 批量修改混合类型：选中记录类型各异，批量改厂商时任一条与新厂商不符则整批拒绝并指出工令。（Task 3）
4. 批量只改类型：每条记录的现有厂商都必须在新类型的可用厂商内，否则整批拒绝。（Task 3）
5. 同步不会再把类型清成空：对已有类型的记录同步后类型保持不变。（Task 2）
6. 已发送记录改类型后下一次邮件把「外发类型」单元格标黄。（Task 3，邮件快照沿用 `type` 字段）

---

### Task 1: 汇入带外发类型

**Files:**
- Modify: `server_dotnet/Yf.Api/Modules/Outsource/OutsourceModels.cs`（`OutsourceImportRow` 增加 `string? TypeName`，放在 `VendorName` 之后）
- Modify: `server_dotnet/Yf.Api/Modules/Outsource/OutsourceItemService.cs`（`ImportHeaders`、`PlanImportAsync`）
- Test: `server_dotnet/tests/OutsourceItemTests.cs`

**Interfaces:**
- Produces: `OutsourceImportRow(int RowNumber, string? OutsourceDate, string? WorkOrderNo, string? VendorName, string? TypeName)`；`ImportHeaders = ["外发日期", "工令", "外发厂商", "外发类型"]`；`OutsourceImportPreviewRow.TypeLabel` 在通过类型校验的行上填写。

- [ ] **Step 1: 写失败测试**（沿用该文件的汇入测试夹具）：
  - `ImportRequiresAKnownTypeThatMatchesTheVendor`：同一批含 6 行：合法 `OEMA`、合法 `oema【进料+制造】`（小写、带中文）、空白类型（消息「请填写外发类型」）、字典外 `ZZZ`（「外发类型不在字典中」）、类型与厂商不符（`ODM-12345` 配鼎亚，消息含「不可配鼎亚」）、被停用的类型。断言各行状态与消息，仅前两行创建，创建的记录 `TypeCode == "OEMA"`（规范代码）。
  - `PreviewShowsTheTypeLabelForReadyRows`：预览中 READY 行的 `TypeLabel == "OEMA【进料+制造】"`。
  - `ImportNoLongerNeedsIpmsToProvideTheType`：IPMS 假数据不含类型，合法行仍能汇入。
- [ ] **Step 2: 运行确认失败**：`--filter OutsourceItemTests`，预期编译失败（`TypeName` 不存在）。
- [ ] **Step 3: 实现**：在 `PlanImportAsync` 的行解析处读取 `TypeName`；厂商校验之后、加入 `lookups` 之前，按 Global Constraints 校验类型（空白 → 无法汇入；`reference.Find` 为空 → 不在字典；`reference.Match(type, vendor.Id)` 不为 `Ok` → 不符）；通过的行把规范代码带到后面，创建记录时写 `item.TypeCode`（不再来自 IPMS）；预览填 `TypeLabel`。删除原来依据 `order.OutsourceTypeCode` 的两段校验。
- [ ] **Step 4: 运行测试确认通过**。
- [ ] **Step 5: 提交**：`git commit -m "Take the outsourcing type from the import file and check it against the vendor"`

### Task 2: IPMS 不再提供类型

**Files:**
- Modify: `server_dotnet/Yf.Api/Modules/Outsource/Ipms/IpmsClient.cs`（`IpmsWorkOrder`、`FakeIpmsClient`）
- Modify: `server_dotnet/Yf.Api/Modules/Outsource/Ipms/Mes/MesMirrorIpmsClient.cs`
- Modify: `server_dotnet/Yf.Api/Modules/Outsource/OutsourceItemService.cs`（`ApplyIpms`）
- Modify: `server_dotnet/Yf.Api/Modules/Outsource/OutsourceIpmsSyncService.cs`（`ApplyAsync` 的比较与更新列表）
- Test: `server_dotnet/tests/OutsourceSyncTests.cs`；让编译器指出并修正所有使用旧构造函数的测试：`MesMirrorIpmsClientTests.cs`、`OutsourceSettingsTests.cs`、`OutsourceItemTests.cs`、`OutsourceMailTests.cs`

**Interfaces:**
- Produces: `IpmsWorkOrder(string WorkOrderNo, string? Customer, string? MachineModel, string? MechanicalSection, string? MechanicalOwner, string? ElectricalOwner, DateOnly? WorkOrderIssuedOn, DateOnly? BomOn, DateOnly? DrawingReleasedOn, int? Quantity, DateOnly? DueDate, bool Closed)`（去掉 `OutsourceTypeCode`）。

- [ ] **Step 1: 写失败测试** `OutsourceSyncTests.SyncNeverChangesTheType`：汇入时该记录的类型由测试直接写库设为 `OEMA`，IPMS 假数据的交期变化；同步后读取记录，断言 `TypeCode == "OEMA"`，且变更履历里没有 `typeCode` 条目。
- [ ] **Step 2: 运行确认失败**：现有同步会用 IPMS 的 `OutsourceTypeCode` 覆盖类型。先让测试桩的工令带上字典外的类型 `XYZ`（此时 `IpmsWorkOrder` 仍含该字段），预期失败：同步后 `TypeCode` 变成 `XYZ`。
- [ ] **Step 3: 实现**：从 `IpmsWorkOrder`、`FakeFile`/`FakeRow`、`FakeIpmsClient` 去掉类型；`ApplyIpms` 不再写 `TypeCode`；同步的比较列表与 `ExecuteUpdateAsync` 中去掉 `typeCode`；`MesMirrorIpmsClient.Map` 去掉对应实参。修正所有测试里的 `IpmsWorkOrder` 构造与 Fake JSON。
- [ ] **Step 4: 运行** `dotnet test .\tests\Yf.Api.Tests.csproj --filter "FullyQualifiedName~Outsource|FullyQualifiedName~Mes"`，预期全部通过（汇入已在 Task 1 改为读文件里的类型，因此去掉 IPMS 类型不会再影响汇入）。
- [ ] **Step 5: 提交**：`git commit -m "Stop reading the outsourcing type from IPMS"`

### Task 3: 单笔与批量编辑可改外发类型

**Files:**
- Modify: `server_dotnet/Yf.Api/Modules/Outsource/OutsourceModels.cs`
- Modify: `server_dotnet/Yf.Api/Modules/Outsource/OutsourceItemService.cs`（`ParseChange`、`Change`、`ApplyAsync`、`UpdateAsync`、`BatchUpdateAsync`）
- Modify: `server_dotnet/Yf.Api/Modules/Outsource/OutsourceHistory.cs`（如 `typeCode` 的标签尚未登记，登记为「外发类型」）
- Modify: `server_dotnet/tests/Contracts/api-v1.json`、`tests/Contracts/openapi-v1.json`
- Test: `server_dotnet/tests/OutsourceItemTests.cs`

**Interfaces:**
- Produces: `OutsourceItemUpdate(string? OutsourceDate, ulong? VendorId, ulong? TypeId, string? Remark, string? Status, DateTime? ExpectedUpdatedAt)`；`OutsourceBatchUpdate(OutsourceItemVersion[]? Items, string? OutsourceDate, ulong? VendorId, ulong? TypeId, string? Remark, string? Status)`；`private sealed record Change(DateOnly? Date, ulong? VendorId, ulong? TypeId, string? Remark, string? Status)`。
- 单笔编辑把 `TypeId` 视为必填（与 `VendorId` 一致：请选择外发类型）；批量只在提供时修改。

- [ ] **Step 1: 写失败测试**：
  - `BuyerCanChangeTheTypeButNotToOneTheVendorCannotTake`：单笔改到字典内且厂商可配的类型成功，`TypeCode` 与响应 `TypeLabel` 更新，变更履历含 `typeCode`（来源 EDIT）；改到厂商不可配的类型返回 400，消息含「不可配」。
  - `ChangingTheTypeAndVendorTogetherIsCheckedAgainstTheNewPair`：同时改两者，新组合合法则成功（即使旧组合与新类型不配）。
  - `BatchTypeChangeIsRejectedWhenAnyItemsVendorCannotTakeIt`：批量只改类型，其中一条的厂商不在新类型范围 → 400，消息含该工令号，其余记录均未修改。
  - `SentItemsWaitForNoticeAfterATypeChange`：已发送记录改类型后 `SendStatus == "PENDING_NOTICE"`。
  - `ItemsWithADisabledTypeStillAcceptRemarkEdits`：类型被停用的记录仅改备注成功；同时改厂商则要求先选启用的类型，返回 400。
  - `ClosedItemsCannotChangeType`：已结案 → 409「已结案的记录不能修改」。
- [ ] **Step 2: 运行确认失败**，预期编译失败。
- [ ] **Step 3: 实现**：`ParseChange` 增加 `TypeId`；`ApplyAsync` 中解析 `TypeId` 对应的启用类型（不存在或停用 → 400「外发类型不存在或已停用」）；对每条锁定记录计算有效的（类型、厂商），只要类型或厂商相对原值有变化，就用 `reference.Match` 校验并按 Global Constraints 的文案报错（批量提示含工令号）；更新 `TypeCode = type.Code`，`AuditChange("typeCode", "外发类型", 旧标签, 新标签)`。
- [ ] **Step 4: 更新契约**：运行 OpenAPI 生成测试，`web/` 里 `npm run generate:api-types`。
- [ ] **Step 5: 运行** `--filter "FullyQualifiedName~Outsource|FullyQualifiedName~OpenApi|FullyQualifiedName~Contract"` 全部通过。
- [ ] **Step 6: 提交**：`git commit -m "Let purchasing change the outsourcing type with the vendor check"`

### Task 4: 前端汇入与编辑

**Files:**
- Modify: `web/src/outsource/pages/OutsourceListPage.tsx`（`ImportRow`、`IMPORT_HEADERS`、`prepareImport`、汇入提示、`EditDialog`、`BatchDialog`、`vendorOptions`）
- Test: `web/src/test/outsource/outsource-list.test.tsx`

**Interfaces:**
- Consumes: Task 3 重新生成的 `ApiResponses`/请求类型（`typeId`）。
- 汇入行：`{ rowNumber, outsourceDate, workOrderNo, vendorName, typeName }`，模板第四列 `外发类型`，`requiredHeaders` 含它。

- [ ] **Step 1: 写失败测试**（改现有用例并新增）：
  - 汇入模板与提交：上传含四列的表格，请求体每行带 `typeName`；汇入提示文案不再出现「其余字段由 IPMS 带出…外发类型」旧句，改为「填写外发日期、工令、外发厂商和外发类型，其余字段由 IPMS 带出」。
  - 编辑对话框：显示外发类型下拉（选项为全部启用类型），厂商下拉随所选类型过滤；类型改变后当前厂商不在新类型可用范围时清空并要求重新选择；提交体带 `typeId`。
  - 批量对话框：外发类型下拉「不修改」；仅在选择后提交 `typeId`。
- [ ] **Step 2: 运行确认失败**：`npx vitest run src/test/outsource/outsource-list.test.tsx`。
- [ ] **Step 3: 实现**：按上述接口修改；类型下拉标签沿用「代码【名称】」；`vendorOptions` 改为接收当前所选类型 id（而不是只看记录原类型）；移除「外发类型不在字典中，无法更换厂商」的禁用逻辑，改为：记录类型不在字典时类型下拉必须重新选择。
- [ ] **Step 4: 运行** `npm run lint`、`npx tsc -b`、`npx vitest run src/test/outsource`，预期全部通过。
- [ ] **Step 5: 提交**：`git commit -m "Add the outsourcing type to the import template and the edit dialogs"`

### Task 5: 契约文档与回归

**Files:**
- Modify: `server_dotnet/docs/外协清单契约-2026-11-09.md`（字段归属表、外发类型字典与防呆、汇入、IPMS 同步、采购编辑各节；「待 IPMS」里类型一项删除；实现进度新增本次说明）
- Modify: `docs/superpowers/specs/2026-11-12-ipms-mes-mirror-design.md`（把「连带变更」标为已实现）

- [ ] **Step 1: 更新文档**：类型归属改为「生管汇入时填写，采购可改」，防呆规则只剩汇入与修改两处校验，删除「同步后类型与厂商不符」「未知类型」中因 IPMS 导致的描述（类型被停用导致的「未知类型」仍保留并阻止发送）。
- [ ] **Step 2: 全量回归**：后端 `--filter "FullyQualifiedName~Outsource|FullyQualifiedName~Mes|FullyQualifiedName~OpenApi|FullyQualifiedName~Contract"`；前端 `npm run lint`、`npx tsc -b`、`npx vitest run`；隔离 HTTP 套件 `python scripts/test-isolated.py`（需 `YF_TEST_DATABASE_URL`）。预期全部通过，无跳过。
- [ ] **Step 3: 提交**：`git commit -m "Document that the outsourcing type is entered by staff"`

---

## Self-Review

- **Spec 覆盖**：汇入模板加类型（Task 1）；类型不再由 IPMS 提供、同步不改类型（Task 2）；采购可改类型并校验厂商（Task 3、4）；防呆文案与停用类型处理（Global Constraints、Task 1/3）；已发送改类型变更待通知、履历、审计（Task 3）；文档（Task 5）。
- **类型一致**：`TypeName`（汇入）与 `TypeId`（编辑）分属两个接口，前者是文本，后者是字典 id；`Change` 与 `OutsourceItemUpdate`/`OutsourceBatchUpdate` 字段顺序在 Task 3 中给出，前端 Task 4 消费其生成类型。
- **与镜像计划的关系**：本计划完成后才可把 `App:Ipms:Mode` 设为 `MesMirror`（镜像不提供类型）。Task 1 与镜像计划已落地的 `MesMirrorIpmsClient.Map` 有交叉，需同一次修改。
- **比例**：不写实现体，只给签名、测试名和断言要点。
