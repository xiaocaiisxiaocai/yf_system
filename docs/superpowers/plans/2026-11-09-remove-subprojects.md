# 取消子项目、全员可见与新建即开始 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 项目只有一层（删除 `project_groups`），内部全员可见、仅负责人可写，新建即进行中并跳到上传页。

**Architecture:** 保留 `projects` 为唯一项目实体，主项目公共资料已在其上；删除主项目表、服务、路由与主子同步/状态汇总。访问层改为“内部可见全部 + 写入需负责人”，通知/未读范围收窄到负责的项目。

**Tech Stack:** ASP.NET Core 8 Minimal API、EF Core 9/Pomelo、MySQL；React 18 + Arco；xUnit v3、Python 合约、Playwright 浏览器步骤。

**Spec:** `docs/superpowers/specs/2026-11-09-remove-subprojects-design.md`

## Global Constraints

- 不新增依赖；.NET 8.0.x + EF 9.0.x；dotnet-ef 9.0.20。
- 迁移只经 `dotnet ef migrations add`，`YF_EF_DESIGN_CONNECTION` 指向一次性设计库。
- 所有写入沿用请求/写入模式：业务门控锁 → 重查 actor → 权限点 → 行锁内授权 → 审计 → 提交 → 实时推送。
- 归属判断在锁定行上做：`responsible_user_id == current.Id`；非负责人内部写入返回 `403`（`ApiException.Forbidden("仅项目负责人可以操作该项目")`）。
- 状态：`IN_PROGRESS` / `COMPLETED` / `TERMINATED`；`DRAFT` 不再产生。删除仅 `TERMINATED`（文案“仅已终止项目可以删除”）。
- 界面与文案统一“项目”，不出现“主项目/子项目”。
- 不改 OEM。不做旧路由兼容。

## Review Focus

1. 非负责人对上传**分片/合并**（不只开始上传）和留言图片的写入必须被拒 —— 加到 Task 3 测试。
2. 负责人转交后，原负责人立即失去写权限且旧的进行中上传会话不能再合并 —— Task 3 测试。
3. 新建时厂商前提失败不落任何行（项目、工令号、Robot 负责人、审计、邮件）—— Task 2 测试。
4. 内部非负责人访问他人项目不产生协作通知/未读/工作台待办 —— Task 4 测试。
5. 复制任务在厂商前提失效时失败且显示前提文案，不留下半个项目 —— Task 4 测试。

---

### Task 1: 迁移 `RemoveProjectGroups` 与实体

**Files:**
- Modify: `server_dotnet/Yf.Api/Infrastructure/Entities/ProjectEntities.cs`（删 `ProjectGroup*` 四个实体及配置；`Project` 删 `ProjectGroupId`，加 `CompletedAt`(`completed_at datetime(3)` null)；新增 `ProjectRobotOwner` 表 `project_robot_owners(project_id, contact_id, sort_no)`，PK(project_id, contact_id)，索引 `idx_project_robot_owners_contact`，FK project cascade / contact restrict）
- Modify: `Entities/ProjectCopyJobEntities.cs`（删 `ProjectGroupId` 列/外键；索引改为 `(RequestedBy, Status, CreatedAt, Id)` 名 `idx_project_copy_jobs_requester`）
- Modify: `Infrastructure/YfDbContext.cs`、`BootstrapSeedCatalog.cs`（删 `project:view_all`）、`DevelopmentDataReset.cs`
- Create: `Infrastructure/Migrations/<ts>_RemoveProjectGroups.cs`（生成后在 Up 开头手工插入数据步骤 SQL：复制 robot owners 到项目、`completed_at` 取所属主项目已完成时间、`DRAFT→TERMINATED`、删除 `project:view_all` 的 `role_permissions` 与 `permissions` 行）
- Test: `tests/SchemaShapeTests.cs`（ExpectedMigrations）、`tests/MigrationRoundTripTests.cs`（Latest + 回滚断言）、`tests/HandwrittenTestSchemas.cs`

- [ ] Step 1: 改实体/上下文/种子，`dotnet ef migrations add RemoveProjectGroups`，审阅 Up/Down，补数据 SQL。
- [ ] Step 2: `dotnet ef migrations has-pending-model-changes` → 无变更。
- [ ] Step 3: 更新三份 schema 测试；跑 `--filter "SchemaShapeTests|MigrationRoundTripTests"` → PASS（在 Task 2 编译通过后）。

### Task 2: 访问层与项目 CRUD（创建即开始）

**Files:**
- Modify: `Modules/Projects/ProjectAccessService.cs`
  - `VisibleQueryAsync`：内部返回 `db.Projects`。
  - `AuthorizeAsync`：内部只需 `project:list`。
  - Add `internal static void RequireOwner(CurrentUser current, ProjectAccess access)`：内部且 `access.ResponsibleUserId != current.Id` → 403。
  - `RequireViewForValidatedActorAsync(forUpdate)`：只锁 `projects` 行（删主项目锁）。
  - `RequireFileUploadAsync` / `RequireFileDeleteAsync` / `CanDeleteFilesAsync(…, ulong? responsibleUserId)`：内部加归属条件。
  - `UploadActivity.CanAbort`：内部分支要求 `isResponsibleUser`。
- Delete: `ProjectGroupService.cs`、`ProjectGroupAccessService.cs`、`ProjectGroupStatusService.cs`
- Modify: `ProjectService.cs` — 新增 `ListAsync(conn, actor, page, pageSize, keyword, status, supplierId, materialState, overdue, mine, ct) -> PageResponse<ProjectResponse>`、`CreateAsync(conn, actor, ProjectUpsertRequest, ip, ct) -> ProjectResponse`、`UpdateAsync(…, ProjectUpsertRequest, …) -> ProjectResponse`、`TransferAsync(…, ProjectTransferRequest, …) -> ProjectResponse`；删 `UpdateSubprojectAsync`；删除逻辑去掉主项目重算与兄弟推送；`SupplierOptionsAsync` 内部全部启用厂商。
  - Create：校验同原主项目创建 + `EnsureResponsibleUserCanActAsync` + `EnsureSupplierCanCollaborateAsync`；插入 `Status=IN_PROGRESS`，`MaterialFileFloor=null`；工令号、Robot 负责人；状态历史 `FromStatus=null, ToStatus=IN_PROGRESS, Action="START"`；审计 `PROJECT_CREATE` + `PROJECT_START`；`EnqueueStatusChangeAsync(…, "START", …)`；名称冲突文案 `项目名称已存在`。
  - Update：沿用原主项目编辑规则（`expectedUpdatedAt` 必填、已结束不可改、不可换厂商），直接更新项目行与工令号/Robot 负责人；在锁定行上 `RequireOwner`；需 `project:update`。
  - Transfer：需 `project:transfer`，且（当前负责人 或 `ProjectDeletionAuthorization.LoadScopeAsync(...).CanDelete(sectionId)`），否则 403；可带 `expectedResponsibleUserId`。
- Modify: `ProjectService.Workflow.cs` — 状态只剩 TERMINATE/RESTART（`ProjectWorkflowRules.ManagementTransition` 删 DRAFT 行）；`SetStatusAsync` 加 `RequireOwner`；`ApplyTransitionAsync` 删 groupStatus，`MATERIAL_APPROVE` 写 `CompletedAt=now`，离开 COMPLETED 清空；重开删除主项目同步，改 `RobotOwners.RebuildSnapshotAsync(db, projectId, ct)`。
- Modify: `ProjectService.MaterialReview.cs`、`ProjectWorkflowRules.cs`（`EnsureDeletable` 仅 TERMINATED）、`ProjectQueries.cs`（删 group join/字段，加 `CompletedAt`）、`RobotOwners.cs`（按项目 id，`RefreshSnapshotsAsync` 跳过 COMPLETED 项目）、`ProjectModels.cs`、`ProjectResponses.cs`（`ProjectResponse` 加 `RobotOwnerContactIds`、`CompletedAt`、`CanManage`；删 `ProjectGroupResponse/DetailResponse`、`SubprojectUpsertRequest/UpdateRequest`；`ProjectGroupTransferRequest`→`ProjectTransferRequest`；`ProjectUpsertRequest` 删 `SubprojectNames`）
- Modify: `ProjectsModule.cs` — 路由：`GET/POST /projects`、`PUT /projects/{id}`(ProjectUpsertRequest)、`PUT /projects/{id}/responsible`、`GET /project-copy-jobs`；删全部 `/project-groups*`。
- Modify: `Modules/Admin/{UserService,DepartmentService,RoleService,SupplierService}.cs` 中 `ProjectGroups` 引用改为 `Projects`（负责人停用保护覆盖 `IN_PROGRESS` 项目）。
- Test: 改写 `tests/ProjectGroupTests.cs` → `tests/ProjectLifecycleTests.cs`；`ProjectStartPrerequisiteTests.cs` 改为创建路径。

- [ ] Step 1: 写失败测试：`Create_starts_project_and_returns_in_progress`（status=IN_PROGRESS，状态历史 1 条 START，出 START 邮件 1 封）、`Create_rejects_when_supplier_has_no_uploader_and_writes_nothing`（409/40901 原文案；projects/work_orders/robot_owners/audit/outbox 计数不变）、`Internal_user_sees_every_project`、`Non_owner_update_status_transfer_are_forbidden`、`Supervisor_can_transfer`、`Only_terminated_project_can_be_deleted`。
- [ ] Step 2: 跑 `dotnet test .\tests\Yf.Api.Tests.csproj --filter ProjectLifecycleTests` → 编译失败/FAIL。
- [ ] Step 3: 实现上述文件。
- [ ] Step 4: 同上命令 → PASS；`dotnet build` 全解决方案 0 error。
- [ ] Step 5: Commit `Collapse projects to one level, start on create and restrict writes to the owner`。

### Task 3: 协作写入的归属校验

**Files:**
- Modify: `Modules/Files/*`（上传开始/分片/合并/中止/删除走 `RequireFileUploadAsync` / `RequireFileDeleteAsync` 已含归属）、`Modules/Projects/MessageService.cs`（发留言、留言图片、删除留言：内部需负责人；`message:delete_any` 仍需负责人）、`ProjectService.MaterialReview.cs`（已是负责人）。
- Test: `tests/OwnerWriteGuardTests.cs`（新）

- [ ] Step 1: 失败测试：非负责人内部账号 `upload init`、`PUT chunk`、`merge`、`POST messages`（含图片）、`DELETE file`、`DELETE message`、`abort session` 均 403；厂商账号仍可上传/留言；转交后原负责人上述操作 403 且其未完成会话合并 403。
- [ ] Step 2: 跑 `--filter OwnerWriteGuardTests` → FAIL。
- [ ] Step 3: 实现。
- [ ] Step 4: PASS。Commit `Require the responsible user for internal collaboration writes`。

### Task 4: 通知、未读、工作台、实时与复制

**Files:**
- Modify: `CollaborationService.cs`、`DashboardService.cs`、`MessageService.UnreadCountAsync` 调用方、`ProjectNotificationService.cs`（内部收件人 = 负责人；厂商侧不变）、`Realtime.cs`（内部受众 = 全部启用且有 `project:list` 的内部账号；删除 view_all/creator 分支）、`ProjectCopyService.cs` + `ProjectCopyWorker.cs`（副本无主项目；负责人=复制人，课别按复制人解析；提交时 `EnsureSupplierCanCollaborateAsync`，失败则任务 failed 并以该文案为错误；直接 IN_PROGRESS + START 状态历史；`ListJobsAsync` 按 `RequestedBy == current.Id`）、`ProjectDueReminderService.cs`（不变，确认无 group 引用）。
- Test: 更新 `UnreadWindowTests`、`DashboardConcurrencyTests`、`RealtimeSecurityTests`、`ProjectNotificationTests`、`ProjectCopyTests`、`BackgroundProjectCopyTests`；新增断言：非负责人内部账号 dashboard `pendingTasks=0`、协作通知为空；复制在厂商无上传者时任务 failed 且 error 为 `SupplierWithoutUploaderStartMessage`。

- [ ] Step 1: 写/改失败测试。
- [ ] Step 2: 跑相关 `--filter` → FAIL。
- [ ] Step 3: 实现。
- [ ] Step 4: PASS。Commit `Scope notifications to owned projects and copy into started projects`。

### Task 5: 全量后端测试与 OpenAPI 合约

- [ ] Step 1: 修复其余引用主项目的测试（`grep -rl "ProjectGroup\|project-groups" tests`）。
- [ ] Step 2: `YF_UPDATE_OPENAPI=1 dotnet test .\tests\Yf.Api.Tests.csproj --filter OpenApiContractTests`；更新 `tests/Contracts/api-v1.json`。
- [ ] Step 3: 全量 `dotnet test .\tests\Yf.Api.Tests.csproj`（设 `YF_TEST_DATABASE_URL`）→ failed/skipped 0。
- [ ] Step 4: Commit `Update backend tests and API contracts for single-level projects`。

### Task 6: 前端

**Files:**
- Delete: `web/src/pages/project/{ProjectGroupDetail.tsx,SubprojectDock.tsx,SubprojectDock.css,SubprojectPane.tsx,subprojectDockContext.ts,subprojectDockLayout.ts}` 及其测试 `web/src/test/{ProjectGroupDetail,SubprojectDock,SubprojectPane}.test.tsx`、`subprojectDockLayout.test.ts`。
- Modify: `App.tsx`（删 `project-groups/:id` 路由）、`api/types.ts`、`api/copyJobs.ts`（`GET /project-copy-jobs`）、`pages/project/ProjectList.tsx`（列项目；去“子项目进度”与子项目字段；新增“只看我负责的”开关→`mine=true`；创建成功 `navigate(\`/projects/${id}?tab=files\`)`；编辑/转交/删除按 `canManage`/`canDelete`）、`pages/project/ProjectDetail.tsx`（头部公共资料、编辑、转交、终止/重新开始、复制、删除；`canManage=false` 时隐藏上传、留言输入、资料确认、状态按钮）、`components/ProjectWorkflowPanel.tsx`、`FileTable.tsx`、`Dashboard.tsx`、`CollaborationNotifications.tsx`、`store/collaboration.ts`、`pages/system/auditLogDetails.ts`、`systemConfigModel.ts`（文案）。
- Test: 更新 `ProjectList.migrated.test.tsx`（创建后跳转断言 `/projects/9?tab=files`；无子项目字段）、`ProjectDetail.migrated.test.tsx`（`canManage=false` 不渲染上传与留言输入）、其余引用。

- [ ] Step 1: `npm run generate:api-types`。
- [ ] Step 2: 改测试 → `npm test` FAIL。
- [ ] Step 3: 实现。
- [ ] Step 4: `npm run lint && npm test && npm run build` → 全绿；`src/test/migrated/core/api-contracts.test.ts` 通过。
- [ ] Step 5: Commit `Show single-level projects, open uploads after create and hide writes for non-owners`。

### Task 7: Python HTTP 合约与浏览器步骤

- [ ] Step 1: 改写 `server_dotnet/scripts/test_*.py`、`upload_contract.py`、`browser/*.cjs` 中主子项目流程为单层（创建即进行中；非负责人只读检查）。
- [ ] Step 2: `python -m unittest …self-tests`（scripts 目录）→ OK。
- [ ] Step 3: `python .\scripts\test-isolated.py` → 全部通过。
- [ ] Step 4: 浏览器全套 `test-browser.py --output ..\.artifacts\tests\browser\browser-<new>` → 通过（需 PFX 环境；缺失则跑除 mail-notifications 外步骤并报告缺口）。
- [ ] Step 5: Commit `Update HTTP and browser acceptance for single-level projects`。

### Task 8: 契约与文档

- [ ] Step 1: 新建 `server_dotnet/docs/项目协作契约-2026-11-09.md`；旧《主项目与子项目协作契约》开头标注被取代；同步引用主子项目的契约段落、`server_dotnet/README.md` 删除表、根 `CLAUDE.md` Projects 段、`docs/开发数据初始化说明.md`（如涉及）。
- [ ] Step 2: `verify-all.ps1` → summary 全通过。
- [ ] Step 3: Commit `Document single-level project collaboration contract`。
