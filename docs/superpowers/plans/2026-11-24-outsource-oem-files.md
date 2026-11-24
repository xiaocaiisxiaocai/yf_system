# 外协清单 × ODM/OEMA 串接与「档案」栏位 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 传递单绑定外协清单的一个工令；外协清单新增「档案」栏（灯 + 文件清单 + 权限控制的预览/下载）；左侧业务切换外协清单在前。

**Architecture:** `oem_transfers` 加可空 `outsource_item_id`；发送时由工令决定厂商。「档案」数据由 ODM/OEMA 的传递单文件聚合而来，预览/下载复用现有 OEM 端点，在 `TransferAccess` 增加一种「参考」访问（持 `outsource:file_view`，不算收件）。

**Tech Stack:** ASP.NET Core 8 / EF Core 9 / MySQL；React 18 + Arco；xUnit + vitest。

**Spec:** `docs/superpowers/specs/2026-11-24-outsource-oem-files-design.md`

## Global Constraints

- 迁移用 `dotnet ef migrations add`（`YF_EF_DESIGN_CONNECTION` 指向一次性库），同时更新 `SchemaShapeTests.ExpectedMigrations`、`MigrationRoundTripTests.Latest`。
- 权限码 `outsource:file_view`，显示名「外协档案」，授予内置管理员；不回填其他角色。
- 文件字节只在 `App:OemStorageRoot`，不复制到外协模块；不改保留政策、审批、校验规则。
- 「参考」访问不得写 `FirstRecipientDownloadAt` / `FirstRecipientRealm` / `FirstRecipientId`。
- 路由或响应变更后：`YF_UPDATE_OPENAPI=1 dotnet test .\tests\Yf.Api.Tests.csproj --filter OpenApiContractTests`，再在 `web/` 执行 `npm run generate:api-types`；更新 `tests/Contracts/api-v1.json`。
- DB 测试只用窄 `--filter`；前端状态词用「启用/停用」。
- 「档案」栏不进导出、厂商邮件正文与附件、已发送快照（同 `ElectricalHandoffKey` 的做法：从 `MailColumns` 排除）。

## Review Focus

1. 持有 `outsource:file_view` 的账号（采购角色默认没有）下载文件后，该文件的保留政策不被提前触发（`FirstRecipientDownloadAt` 仍为空）。
2. 没有 `outsource:file_view` 的内部账号用任何方式（直接调 `/oem/files/{id}/content|download-sessions`）都取不到非自己可见传递单的内容。
3. 厂商 A 的账号不能把传递单绑到厂商 B 的工令，也读不到 B 的档案清单。
4. 草稿、审批中、校验失败的文件不使灯变绿；已清除的文件仍在清单里标「已清除」。
5. 工令的厂商被改后，旧传递单仍属原厂商，档案清单标示所属厂商；已有传递单的外协记录不能删除。

---

## File Structure

- 后端新增：`Modules/Outsource/OutsourceFiles.cs`（档案聚合与清单查询）、迁移 `AddOutsourceTransferLink`。
- 后端修改：`Infrastructure/Entities`（OemTransfer）、`Modules/Oem/Transfers/{OemTransferService,TransferAccess,OemTransferReader}.cs`、`Modules/Oem/Delivery/OemDeliveryService.cs`、`Modules/Oem/Common/OemResponseContracts.cs`、`Modules/Outsource/{OutsourceFields,OutsourceModels,OutsourceItemService,OutsourceModule,OutsourceVendorView}.cs`、`Modules/Oem/OemModule.cs`、`Infrastructure/BootstrapSeedCatalog.cs`。
- 前端：`layouts/AppRail.tsx`、`oem/pages/TransferListPage.tsx`（发送对话框，内部与厂商共用）、`oem/api/OemApi.ts`、新增 `outsource/components/ItemFilesPopover.tsx`、`outsource/pages/OutsourceListPage.tsx`、`oem/pages/PortalOutsourcePage.tsx`。

---

### Task 1: 业务切换顺序

**Files:** Modify `web/src/layouts/AppRail.tsx`；Test `web/src/test/module-navigation.test.tsx`（或现有 AppRail 测试）。

**Interfaces:** Produces: rail 顺序为 collaboration、outsource、oem。

- [ ] 写测试 `rail lists 外协清单 before ODM/OEMA`：同时具备三种业务权限时，`nav[aria-label=业务切换]` 内按钮顺序 = `Robot仿真协作`、`外协清单`、`ODM/OEMA 文件传递`。
- [ ] 跑测试确认失败。
- [ ] 在 `AppRail.tsx` 的 `lines` 中把 outsource 一项移到 oem 之前；`businessHomes` 的首页落点优先级若依赖顺序，保持原行为不变。
- [ ] 跑 `npx vitest run src/test/module-navigation.test.tsx` 通过。提交。

### Task 2: 数据结构与权限

**Files:** Modify `Infrastructure/Entities`（`OemTransfer.OutsourceItemId: ulong?`）、实体映射、`BootstrapSeedCatalog.cs`；Create 迁移 `AddOutsourceTransferLink`；Test `SchemaShapeTests.cs`、`MigrationRoundTripTests.cs`。

**Interfaces:** Produces: `oem_transfers.outsource_item_id` (bigint unsigned null, 索引，FK→`outsource_items.id` ON DELETE RESTRICT)；权限 `outsource:file_view`。

- [ ] 写迁移测试 `AddOutsourceTransferLinkAddsColumnAndPermission`：升级后列存在且可空；`permissions` 有 `outsource:file_view`，内置管理员角色已授予；回滚后列与权限移除；其他角色未被授予。
- [ ] 跑测试确认失败。
- [ ] 加实体属性与映射；`dotnet ef migrations add AddOutsourceTransferLink`；迁移里用「按 code 插入」的方式写权限（参照 `20261120064823_AddOutsourceDateImportPermission`），并加入 `BootstrapSeedCatalog`（避免重复键，见仓库 CLAUDE.md）。
- [ ] 更新 `ExpectedMigrations`、`Latest`；跑相关窄测试通过。提交。

### Task 3: 发送必须绑定工令

**Files:** Modify `OemTransferService.cs`（`TransferCreate` 加 `ulong? OutsourceItemId`；`CreateAsync`）、`OemResponseContracts.cs`（`OemTransferSummaryResponse` 加 `ulong? OutsourceItemId, string? WorkOrderNo`）、`OemTransferReader.cs`（关键字可搜工令；输出工令）；Test `tests/Oem/OemTransferOutsourceLinkTests.cs`。

**Interfaces:** Consumes: `OutsourceItem`（Id, VendorId, WorkOrderNo, ClosedAt）。Produces: `TransferCreate(string? Title = null, string? Description = null, ulong? OemCompanyId = null, ulong? OutsourceItemId = null)`。

- [ ] 写测试（每个一条断言）：
  - `InternalCreateDerivesVendorFromItem`：只给 `OutsourceItemId`，传递单 `OemCompanyId == item.VendorId`。
  - `InternalCreateRequiresItem`：不给 → 400「请选择外协清单中的工令」。
  - `InternalCreateRejectsCompletedItem`：已完成工令 → 400。
  - `InternalCreateRejectsConflictingCompany`：同时给了不同的 `OemCompanyId` → 400。
  - `VendorCreateRejectsOtherCompanyItem`：厂商账号选别家工令 → 400；选本公司工令成功。
  - `ListSearchMatchesWorkOrder`：关键字搜工令返回该单。
- [ ] 跑测试确认失败。
- [ ] 实现：创建时校验工令存在、未完成、（内部）厂商启用且有可接收账号（沿用 `OemRecipientPolicy.RequireEnabledAccountAsync`）；内部发送忽略调用方给的厂商，仅在两者不一致时报错。
- [ ] 跑窄测试通过。提交。

### Task 4: 「参考」访问（不算收件）

**Files:** Modify `TransferAccess.cs`（`ContentPurpose` 加 `Reference`；`ActorFacts` 加 `HasOutsourceFileAccess`）、`OemDeliveryService.cs`（参考访问的会话 `RecipientSide=false`，用途记 `REFERENCE`）；Test `tests/Oem/OemReferenceAccessTests.cs`、扩充现有 `TransferAccess` 纯函数测试。

**Interfaces:** Produces: `ContentPurpose.Reference`；规则：内部账号持 `outsource:file_view`、传递单 `OutsourceItemId` 非空、已送出（RELEASED）、文件可用 → `Reference`。

- [ ] 写测试：
  - `ReferenceCanPreviewAndDownloadReleasedFileOfBoundTransfer`。
  - `ReferenceDownloadDoesNotMarkFirstReceipt`：下载后 `FirstRecipientDownloadAt` 仍为 null，保留到期时间不变。
  - `NoPermissionGetsNothing`：无 `outsource:file_view` 且非其可见范围 → 403。
  - `UnboundOrDraftTransferIsNotReferenceReadable`：未绑工令或草稿/审批中 → 403。
- [ ] 跑测试确认失败。
- [ ] 实现 `TransferAccess.Evaluate` 的分支；`OemDeliveryService` 对 `Reference` 的 `RecipientSide=false`、审计用途 `REFERENCE`；确保 `capabilities.IsRecipient` 为 false。
- [ ] 跑窄测试通过。提交。

### Task 5: 档案聚合、清单接口、栏位

**Files:** Create `Modules/Outsource/OutsourceFiles.cs`；Modify `OutsourceFields.cs`（新内置栏位 `files`，不进 `MailColumns`）、`OutsourceModels.cs`（`OutsourceItemResponse` 加 `int FileCount, int PurgedFileCount`）、`OutsourceItemService.cs`（列表聚合；删除守卫）、`OutsourceModule.cs`、`OemModule.cs`、`OutsourceVendorView.cs`；迁移同 Task 2 或新迁移加 `outsource_fields` 行（`files`，vendor_visible=1，editor NONE，排在 `workOrderNo` 之后）；Test `tests/OutsourceFilesTests.cs`。

**Interfaces:** Produces: `record OutsourceItemFile(ulong FileId, ulong TransferId, string Name, ulong SizeBytes, string Direction, string VendorName, DateTime SentAt, string State)`（`State` ∈ `AVAILABLE|PROCESSING|PURGED`）；`GET /api/v1/outsource/items/{id}/files`（内部，任何内部账号可见清单，附 `canOpen` = 持 `outsource:file_view`）；`GET /api/v1/oem/outsource-items/{id}/files`（厂商，仅本公司）。

- [ ] 写测试：
  - `FileCountCountsOnlyReleasedAvailableFiles` / `PurgedFilesStayListedAsPurged` / `FailedValidationFilesNotListed` / `DraftAndPendingApprovalFilesNotCounted`。
  - `ListAggregatesInOneQuery`（页内多条工令，查询次数不随条数增长）。
  - `VendorSeesOnlyOwnCompanyFiles`。
  - `ItemWithTransfersCannotBeDeleted`（409，消息「已有往来文件，不能删除」）。
  - `FilesColumnExcludedFromMailExportAndSnapshot`。
- [ ] 跑测试确认失败。
- [ ] 实现聚合：`OutsourceFiles.CountsAsync(YfDbContext, IReadOnlyCollection<ulong> itemIds, ulong? companyId, CancellationToken)` 返回每工令 `(FileCount, PurgedFileCount)`；`OutsourceFiles.ListAsync(...)` 返回清单。
- [ ] 重新生成 OpenAPI 与前端类型；跑窄测试通过。提交。

### Task 6: 发送对话框选工令

**Files:** Modify `web/src/oem/pages/TransferListPage.tsx`（对话框）、`oem/api/OemApi.ts`（`createTransfer` 带 `outsourceItemId`；新增 `sendableItems()`）；后端新增 `GET /api/v1/oem/sendable-items`（内部：未完成、厂商启用且有可接收账号的工令，附不可选原因；厂商：仅本公司）；Test `web/src/test/oem/oem-routes.test.tsx` 或新文件。

**Interfaces:** Produces: `OemApi.sendableItems(): Promise<{ id: number; workOrderNo: string; customer: string | null; vendorName: string; disabledReason: string | null }[]>`。

- [ ] 写测试：
  - `internal send dialog has no vendor select and requires picking a work order`。
  - `picked work order shows its vendor read-only`、`unavailable items are disabled with the reason`。
  - `vendor send dialog lists only own items`。
- [ ] 跑测试确认失败。
- [ ] 实现：移除「目标 ODM/OEMA 厂商」下拉及其加载错误区块，换成可搜索的工令选择（工令号 + 客户 + 厂商）；提交时带 `outsourceItemId`。
- [ ] 窄测试通过。提交。

### Task 7: 「档案」栏与清单

**Files:** Create `web/src/outsource/components/ItemFilesPopover.tsx`；Modify `OutsourceListPage.tsx`（`files` 栏渲染：灯 + 数量，点开清单）、`PortalOutsourcePage.tsx`（同栏，厂商入口）、`oem/api/OemApi.ts`（`itemFiles(id)`）；Test `web/src/test/outsource/outsource-files.test.tsx`。

**Interfaces:** Consumes: Task 5 的清单接口、`OemFilePreview`、`OemApi.startDownload`（现有）。

- [ ] 写测试：
  - `light is gray without files and green with available files`。
  - `purged files are listed with 已清除 and have no buttons`。
  - `viewer without outsource:file_view sees names but no preview or download buttons`。
  - `viewer with outsource:file_view can preview and download`（点击调用现有预览/下载）。
  - `vendor portal shows the column and only own files`。
- [ ] 跑测试确认失败。
- [ ] 实现 `ItemFilesPopover`（点击格子时再加载清单，按钮显隐由 `canOpen` 决定）；栏位用 `ResizableTable` 既有机制；灯样式放在 `OutsourceListPage.css`。
- [ ] 跑 `npx vitest run src/test/outsource src/test/oem`、`npx tsc --noEmit`、`npm run lint` 通过。提交。

### Task 8: 文档与真实运行验证

**Files:** Modify `docs/外协清单契约-2026-11-09.md`（新增「档案与传递单绑定」一节）、`Modules/Oem/CLAUDE.md`、`OEM当前集成与部署-2026-10-05.md`、根 `CLAUDE.md` 的 Outsource 段。

- [ ] 更新上述文档，写明：绑定规则、「参考」访问不算收件、权限 `outsource:file_view`、已清除显示、删除守卫。
- [ ] `restart-dev.ps1` 后用浏览器逐项验证：内部发送选工令、厂商发送选工令、档案灯与清单、有/无权限的按钮差异、采购角色默认只看到文件名、无预览下载按钮，勾选外协档案权限的角色下载后传递单保留时间不变、清除后标「已清除」。
- [ ] 跑 Task 2–5 相关窄测试一轮；开 PR，等 CI。

---

## Self-Review

- 规格覆盖：导航(T1)、绑定与厂商推导(T3)、厂商发送(T3/T6)、档案栏与灯(T5/T7)、权限与无权限只看文件名(T4/T5/T7)、已清除显示(T5/T7)、不进导出邮件快照(T5)、删除守卫(T5)、文档(T8)。无缺口。
- 类型一致：`OutsourceItemId`、`FileCount`、`PurgedFileCount`、`OutsourceItemFile`、`ContentPurpose.Reference`、`outsource:file_view` 全文一致。
- 比例：计划约为规格的 1.5 倍，代码块只含签名与断言。
