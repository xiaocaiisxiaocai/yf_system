# MES 镜像手动抓取 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 在外协设定页的「IPMS 同步」分页手动抓取 MES 数据：抓全部或抓指定页、可指定每页条数、可中途停止，并在停止或抓指定页之后二次确认「放弃」还是「合并写入」。

**Architecture:** 在已实现的 `MesMirrorSyncService` 上增加一个进程内的「手动抓取任务」：后台任务按现有流程抓取到暂存表；停止或抓指定页后进入「等待确认」，由用户放弃（清空暂存）或合并（按工令更新或新增，不删除）。任务状态保存在内存，租约沿用 `mes_mirror_state`。前端在设定页展示进度、停止按钮和确认对话框。

**Tech Stack:** .NET 8、EF Core、xUnit v3；React 18 + Arco、Vitest；OpenAPI 契约流程。

**Spec:** `docs/superpowers/specs/2026-11-12-ipms-mes-mirror-design.md` 的「手动抓取」一节；基础实现见 `docs/superpowers/plans/2026-11-12-ipms-mes-mirror.md`（已完成 Task 1 到 5）。

## Global Constraints

- 同一时间只有一个抓取任务（定时或手动）：沿用 `mes_mirror_state` 租约；已有任务或等待确认的结果时再次启动返回 `409 / 40901`「已有抓取任务进行中或等待确认」。
- 手动请求：模式 `ALL`（抓全部）或 `PAGE`（抓指定页）；`pageSize` 1 到 200，缺省取 `App:Ipms:MesPageSize`；`PAGE` 必须带 `page`（≥ 1）。
- `ALL` 不中断地抓完并通过原有校验（去重数等于 `total`、不低于上次笔数的 50%）时，与定时抓取一样整表替换，无需确认。
- 停止、或 `PAGE` 抓取完成后，结果留在暂存表并进入等待确认；确认只有两个选择：放弃（清空暂存，镜像不变）、合并（`INSERT … ON DUPLICATE KEY UPDATE`，按工令更新或新增，不删除任何行）。
- 合并不改 `last_success_at`；合并后把 `last_count` 更新为镜像的实际笔数；写审计 `OUTSOURCE_MES_MIRROR`，detail 含 `trigger=MANUAL`、`mode`、`action`（`MERGE` 或 `DISCARD`）、笔数，不含凭证。
- 等待确认的结果占用租约（每次状态读取不续租）；租约过期（60 分钟）视为放弃，下一次任务开始时清空暂存。进程重启同样丢失内存状态，暂存在下次任务开始时清空。
- 只有 `outsource:settings` 权限可启动、停止、确认、查看进度。路由在 `/api/v1/outsource/mes-mirror/*`。
- 停止要尽快生效：取消当前页的请求（`CancellationToken`），不等待页超时；已完整写入暂存的页保留。
- 抓取、停止、确认都不得把凭证、token 写入日志、审计、响应。
- 不需要新迁移。路由与响应变更后依次 `YF_UPDATE_OPENAPI=1 dotnet test .\tests\Yf.Api.Tests.csproj --filter OpenApiContractTests`、`npm run generate:api-types`，并同步 `tests/Contracts/api-v1.json`。

## Review Focus

1. 点击停止时恰好在写页或整表替换事务中：整表替换一旦开始就不可中断，停止请求应等它完成后返回「已完成，无需确认」，不能留下半替换。（Task 1）
2. 停止时一页都没抓到：直接回到空闲，不要出现一个空的确认对话框。（Task 1）
3. 用户一直不确认或关闭页面：等待状态必须在状态查询里可见，且租约过期后自动失效，定时抓取不被永久阻塞。（Task 1）
4. `PAGE` 指定的页码超出范围（例如第 999 页）：MES 返回空数组，视为「该页没有数据」并回到空闲，不进入确认。（Task 1）
5. 合并时同一工令在镜像里已有旧数据：被新值覆盖，`batch_id`、`fetched_at` 更新；镜像里没被抓到的工令原样保留。（Task 1）
6. 两个人同时点开始或确认：第二个请求得到 409，不会重复合并。（Task 1）

---

### Task 1: 手动抓取引擎

**Files:**
- Modify: `server_dotnet/Yf.Api/Modules/Outsource/Ipms/Mes/MesMirrorSyncService.cs`
- Create: `server_dotnet/Yf.Api/Modules/Outsource/Ipms/Mes/MesMirrorModels.cs`
- Test: `server_dotnet/tests/MesMirrorManualTests.cs`（新建；沿用 `MesMirrorSyncTests` 的假源与夹具写法）

**Interfaces:**
- Produces（`MesMirrorModels.cs`）：
  - `public sealed record MesMirrorRunRequest(string? Mode, int? Page, int? PageSize);`
  - `public sealed record MesMirrorConfirmRequest(bool Apply);`
  - `public sealed record MesMirrorPreviewRow(string WorkOrderNo, string? CustomerName, string? DeviceModels);`
  - `public sealed record MesMirrorStatusResponse(string Phase, string? RunMode, int? Page, int PageSize, int Fetched, int Pages, int? Total, string? PendingReason, DateTime? StartedAt, DateTime? LastFinishedAt, DateTime? LastSuccessAt, string? LastError, string? LastTrigger, int LastCount, int MirrorCount, MesMirrorPreviewRow[] Preview);`
  - `Phase` 取值 `IDLE`、`RUNNING`、`STOPPING`、`AWAITING_CONFIRM`；`PendingReason` 取值 `STOPPED`、`PAGE_FETCHED`（仅等待确认时有值）；`Preview` 最多 50 行，取自暂存表，仅等待确认时填充。
- Produces（`MesMirrorSyncService` 新增公开方法，都需要 `outsource:settings`，校验方式与 `OutsourceIpmsSyncService.SetSyncTimeAsync` 一致）：
  - `Task<MesMirrorStatusResponse> StatusAsync(CurrentUser actor, CancellationToken ct)`
  - `Task<MesMirrorStatusResponse> StartAsync(CurrentUser actor, MesMirrorRunRequest request, CancellationToken ct)`：校验请求、取得租约、启动后台任务后立即返回当前状态。
  - `Task<MesMirrorStatusResponse> StopAsync(CurrentUser actor, CancellationToken ct)`：请求取消并等待任务落到稳定状态（空闲或等待确认，最长等 30 秒，超时则返回当前 `STOPPING`）。
  - `Task<MesMirrorStatusResponse> ConfirmAsync(CurrentUser actor, MesMirrorConfirmRequest request, CancellationToken ct)`。
- Consumes: 已有的 `IMesSource`、`RunAsync`（抓取与整表替换逻辑需要抽出可共用的「抓页到暂存」与「整表替换」两个私有方法，供定时与手动共用，行为不变）。

- [ ] **Step 1: 写失败测试**（每个都用 `SchemaDatabaseScope`，`MesPageSize=2`、`MesPageDelaySeconds=0`，权限用与 `OutsourceSyncTests` 相同的角色夹具）：
  - `PageRunWaitsForConfirmationAndMergeKeepsOtherRows`：镜像预置 `OLD1`、`OLD2`、`A3`（旧值 `customer_name='旧'`）；假源 5 行 `A1..A5`；`StartAsync(PAGE, page=2, pageSize=2)` → 状态 `AWAITING_CONFIRM`、`PendingReason=="PAGE_FETCHED"`、`Preview` 为 `A3`、`A4`；此时镜像不变；`ConfirmAsync(Apply=true)` → 镜像含 `OLD1`、`OLD2`、`A3`（已更新为新值）、`A4`；没有 `A1`、`A2`、`A5`；状态回到 `IDLE`；暂存表为空；`LastSuccessAt` 未变；`MirrorCount==4`。
  - `DiscardingKeepsTheMirrorAndClearsStaging`：同上场景 `Apply=false`，镜像与开始前完全相同，暂存为空，状态 `IDLE`。
  - `StoppingAFullRunAsksBeforeUpdatingAndMergeDeletesNothing`：假源第 1、2 页立即返回，第 3 页阻塞；`StartAsync(ALL, pageSize=2)`；等进度 `Pages==2`；`StopAsync` → `AWAITING_CONFIRM`、`PendingReason=="STOPPED"`、`Fetched==4`；确认合并后镜像是旧行加 4 个新行，没有删除任何旧行。
  - `StoppingBeforeAnyPageWasFetchedReturnsToIdle`：第 1 页阻塞时停止 → `IDLE`，无等待确认，暂存为空。
  - `CompletingAFullRunReplacesTheMirrorWithoutConfirmation`：`ALL` 抓满 5 行 → 自动整表替换，状态 `IDLE`，旧行被删除。
  - `OnlyOneRunAtATime`：运行中、等待确认中各再调用 `StartAsync` 都抛 `ApiException` 409；定时的 `RunAsync("SCHEDULED", day, ct)` 在这两种状态下返回 `null`。
  - `ExpiredPendingResultIsDiscardedByTheNextRun`：等待确认时把 `lease_until` 改成过去，再 `StartAsync` 成功，旧暂存行不在新结果里。
  - `PageBeyondTheLastPageReturnsToIdleWithoutAConfirmation`：`PAGE page=999` 假源返回空 → `IDLE`，`LastError` 为空，`Preview` 为空。
  - `ValidatesRequestsAndPermissions`（Theory）：`Mode` 未知、`PAGE` 缺 `page`、`page=0`、`pageSize=0` 或 201 → 400；只有 `outsource:view` 的账号调用四个方法都 403。
  - `ConfirmWithoutAPendingResultIsRejected`：空闲时确认 → 409「没有等待确认的抓取结果」。
  - `FailedManualRunRecordsTheErrorAndKeepsTheMirror`：第 2 页持续失败 → `IDLE`，`LastError` 有值，镜像不变。
- [ ] **Step 2: 运行确认失败**：`--filter MesMirrorManualTests`，预期编译失败（新类型与方法不存在）。
- [ ] **Step 3: 实现**：
  - 内存状态用一个私有类 `ManualRun`（`Phase`、`Mode`、`Page`、`PageSize`、`Fetched`、`Pages`、`Total`、`PendingReason`、`StartedAt`、`Cts`、`Task`），受同一把锁保护；`StartAsync` 先取 DB 租约（失败 → 409），再创建任务，`Task.Run` 在后台抓取，不持有请求线程。
  - `ALL`：沿用定时的流程，但抓页循环可被停止（取消只取消当前页请求，已写入暂存的页保留）；完整抓完且通过校验 → 整表替换并回到空闲；被停止且已有暂存行 → `AWAITING_CONFIRM(STOPPED)`；已有暂存行为零 → 空闲并释放租约。
  - `PAGE`：只抓指定一页；有行 → `AWAITING_CONFIRM(PAGE_FETCHED)`，无行 → 空闲。
  - 整表替换事务一旦开始不接受取消（用 `CancellationToken.None`），`StopAsync` 此时只等待完成。
  - `ConfirmAsync`：用同一把锁保证只有一个确认生效；合并用 `INSERT INTO mes_work_orders(...) SELECT ... FROM mes_work_orders_staging ON DUPLICATE KEY UPDATE batch_id=VALUES(batch_id), fetched_at=VALUES(fetched_at), customer_name=VALUES(customer_name), device_models=VALUES(device_models), raw_json=VALUES(raw_json)`；之后清空暂存、更新 `last_count` 为镜像实际笔数、释放租约，写审计。
  - `StatusAsync` 合并内存任务状态与 `mes_mirror_state`、镜像笔数；若内存是等待确认但租约已过期，则视为空闲。
- [ ] **Step 4: 运行测试确认通过**，再运行 `--filter "FullyQualifiedName~MesMirror"` 确认原有 14 个同步测试仍通过。
- [ ] **Step 5: 提交**：`git commit -m "Add manual MES mirror runs with stop and confirm"`

### Task 2: 路由与契约

**Files:**
- Modify: `server_dotnet/Yf.Api/Modules/Outsource/OutsourceModule.cs`
- Modify: `server_dotnet/tests/Contracts/api-v1.json`、`server_dotnet/tests/Contracts/openapi-v1.json`
- Modify: `web/src/api/generated/api-types.ts`（由生成命令产生）
- Test: `server_dotnet/tests/MesMirrorManualTests.cs`（增加路由级断言）或 `tests/Yf.Api.Tests` 里已有的 OpenAPI 契约测试

**Interfaces:**
- Produces 路由（都返回 `MesMirrorStatusResponse`，`CurrentUser` 来自 `AccessService.GetCurrent(c)`，路由 lambda 至少绑定一个服务参数，见根 `CLAUDE.md`）：
  - `GET /api/v1/outsource/mes-mirror/status`
  - `POST /api/v1/outsource/mes-mirror/run`（体：`MesMirrorRunRequest`）
  - `POST /api/v1/outsource/mes-mirror/stop`
  - `POST /api/v1/outsource/mes-mirror/confirm`（体：`MesMirrorConfirmRequest`）
- `MesMirrorSyncService` 已作为单例注册，路由直接注入它。

- [ ] **Step 1: 写失败测试**：运行 `OpenApiContractTests`，预期因契约中没有新路由而失败（以 `YF_UPDATE_OPENAPI` 未设置时的比较为红灯）。
- [ ] **Step 2: 实现路由**，然后 `YF_UPDATE_OPENAPI=1 dotnet test .\tests\Yf.Api.Tests.csproj --filter OpenApiContractTests`，手工更新 `tests/Contracts/api-v1.json` 的路由清单，`web/` 里 `npm run generate:api-types`。
- [ ] **Step 3: 运行** `--filter "FullyQualifiedName~OpenApi|FullyQualifiedName~Contract|FullyQualifiedName~MesMirror"` 全部通过，且前端 `src/test/migrated/core/api-contracts.test.ts` 通过。
- [ ] **Step 4: 提交**：`git commit -m "Expose the manual MES mirror run endpoints"`

### Task 2b: 页面可配置 MES 地址与测试连接（2026-11-12 追加）

用户要求：MES 地址要能在页面上更换（现在测外网，之后可能换内网）；token 由程序自己登入获取，不需要手动取，并在页面提供测试工具。

**Files:**
- Modify: `server_dotnet/Yf.Api/Infrastructure/Entities/MesMirrorEntities.cs`（`MesMirrorState` 增加 `string? BaseUrl`，列 `base_url` varchar(256) 可空）
- Create: 迁移 `AddMesMirrorBaseUrl`；Modify: `tests/SchemaShapeTests.cs`（`ExpectedMigrations`）、`tests/MigrationRoundTripTests.cs`（`Latest` 与回滚断言：回滚后无 `column|mes_mirror_state|base_url|`）
- Modify: `Yf.Api/Modules/Outsource/Ipms/Mes/IMesSource.cs`、`MesHttpSource.cs`、`UnconfiguredMesSource.cs`（工厂）、`MesMirrorSyncService.cs`、`MesMirrorModels.cs`、`OutsourceModule.cs`
- Modify: `tests/MesHttpSourceTests.cs`（构造方式随设置来源调整）、`tests/MesMirrorManualTests.cs`（新增用例）、`tests/Contracts/api-v1.json`、`tests/Contracts/openapi-v1.json`

**Interfaces:**
- `public interface IMesSettings { Task<string?> BaseUrlAsync(CancellationToken ct); }`：有效地址 = 页面保存的地址（`mes_mirror_state.base_url`），为空则取配置 `App:Ipms:MesBaseUrl`。实现 `DbMesSettings(IDbContextFactory<YfDbContext>, AppOptions)`。
- `MesHttpSource(HttpClient http, IpmsOptions options, IMesSettings settings, TimeSpan? pageTimeoutOverride = null)`：每次请求读取当前地址并用绝对 URL 发送；地址与上次使用的不同时丢弃旧 token 重新登入。
- `IMesSource` 增加 `Task<MesTestResult> TestAsync(string? baseUrl, CancellationToken ct)`：用给定地址（空则用当前有效地址）**全新登入**并抓第 1 页、1 条；不写任何数据；失败不抛异常，返回安全的提示（不含账号、密码、token）。`public sealed record MesTestResult(bool Ok, string Message, int? Total, int ElapsedMs);`
- 新增模型：`MesMirrorSettingsRequest(string? BaseUrl)`、`MesMirrorSettingsResponse(string BaseUrl, string? ConfigBaseUrl, bool Saved, bool AccountConfigured)`、`MesMirrorTestRequest(string? BaseUrl)`；服务方法 `SettingsAsync`、`SaveSettingsAsync`、`TestAsync`（`outsource:settings`）。
- 路由：`GET /api/v1/outsource/mes-mirror/settings`、`PUT /api/v1/outsource/mes-mirror/settings`、`POST /api/v1/outsource/mes-mirror/test`（返回 `MesTestResult`）。
- 约束：地址必须是绝对的 http 或 https，含主机，不含用户名密码与片段，长度 ≤ 256；保存时规范化（去掉末尾 `/`）；留空表示恢复使用配置值；保存写审计 `OUTSOURCE_MES_MIRROR_SETTINGS`（只写新旧地址，不写凭证）；运行中或等待确认时拒绝保存与测试（409）；`Mode` 不是 `MesMirror` 时测试返回「MES 数据镜像尚未启用」。
- 账号与密码仍只在外部配置文件，不入库、不在页面显示；页面只显示「账号已配置」与否。`MesBaseUrl` 配置改为可空（页面保存了地址即可），`IpmsOptions.Validate` 在 `Mode=MesMirror` 时仍要求账号与密码，地址若配置则必须合法。

- [ ] **Step 1: 写失败测试**：
  - `MesHttpSourceTests`：`UsesTheCurrentBaseUrlAndLogsInAgainWhenItChanges`（设置换成另一个地址后，下一次请求发往新地址且重新登入）；`TestAsyncLogsInFreshAndFetchesOneRowWithoutSecrets`（成功返回 `Ok`、`Total`；登入被拒、网络失败、返回非 JSON 各返回 `Ok=false` 与不含凭证的提示；不复用旧 token）。
  - `MesMirrorManualTests`：`SettingsFallBackToTheConfiguredUrlAndSaveNormalizes`（保存 `http://10.0.0.5:6001/` 后读取为 `http://10.0.0.5:6001`；留空后回到配置值；`AccountConfigured` 反映配置）；`RejectsInvalidUrls`（Theory：空白以外的非法值：`ftp://x`、`http://user:pw@x`、`not a url`、超长）；`SettingsAndTestAreRefusedWhileARunIsActive`；`OnlySettingsPermissionMaySaveOrTest`；`ChangingTheUrlWritesAnAuditWithoutCredentials`。
  - `MigrationRoundTripTests`：回滚后不含 `base_url` 列，最新架构含该列。
- [ ] **Step 2: 运行确认失败**（编译失败或断言失败）。
- [ ] **Step 3: 实现**：迁移与实体；`DbMesSettings`；`MesHttpSource` 改用绝对 URL 与地址变化时清 token；`TestAsync` 用独立的登入请求，不碰共享 token；服务方法与路由；OpenAPI 与 `api-v1.json` 更新，`npm run generate:api-types`。
- [ ] **Step 4: 运行** `--filter "FullyQualifiedName~MesHttpSource|FullyQualifiedName~MesMirror|FullyQualifiedName~SchemaShape|FullyQualifiedName~MigrationRoundTrip|FullyQualifiedName~OpenApi|FullyQualifiedName~ApiContract"` 全部通过。
- [ ] **Step 5: 提交**：`git commit -m "Let the MES address be set on the page and add a connection test"`

### Task 3: 设定页的手动抓取面板

**Files:**
- Modify: `web/src/outsource/pages/OutsourceSettingsPage.tsx`（「IPMS 同步」分页里新增「MES 数据镜像」区块）
- Create: `web/src/outsource/pages/MesMirrorPanel.tsx`（独立组件，避免该页面继续变大）
- Modify: `web/src/styles/arco-components.ts`（若引入新的 Arco 组件，用 `node scripts/generate-arco-styles.mjs` 重新生成）
- Test: `web/src/test/outsource/mes-mirror-panel.test.tsx`（新建）

**Interfaces:**
- Consumes: Task 2 生成的 `ApiResponses['GET /outsource/mes-mirror/status']` 等类型。
- Produces: `export default function MesMirrorPanel(): JSX.Element`，内部每 3 秒刷新一次状态，仅在 `Phase` 为 `RUNNING` 或 `STOPPING` 时轮询。

- [ ] **Step 1: 写失败测试**（`vi.mock` 掉 `http`，沿用该目录现有测试的写法）：
  - 空闲时显示最近一次结果、镜像笔数、「抓取全部」与「抓取指定页」，页码和每页条数输入有校验（页码 ≥ 1，每页 1 到 200）。
  - 点击「抓取全部」带每页条数发 `POST /outsource/mes-mirror/run {mode:'ALL', pageSize}`；运行中显示进度（已抓笔数/总数、页数）和「停止」按钮，并禁用开始按钮。
  - 点击「停止」后进入等待确认：弹出确认对话框，列出已抓笔数与预览工令表，两个按钮「放弃，保留现有数据」与「合并已抓部分」；两者分别发 `{apply:false}` 与 `{apply:true}`。
  - 「抓取指定页」完成后同样弹出确认对话框，文案区分「第 N 页」。
  - 页面加载时若状态已是 `AWAITING_CONFIRM`，直接显示确认对话框（用户中途关闭页面后回来也能处理）。
  - 「MES 地址」输入框与「保存」按钮（留空保存即恢复配置值）；「测试连接」显示成功（含总笔数、耗时）或失败原因，运行中或等待确认时两者禁用。
  - 设定页路由本身已要求 `outsource:settings`，不另做权限分支（执行时确认，原计划的「无权限看不到」用例由路由守卫覆盖）。
- [ ] **Step 2: 运行确认失败**：`npx vitest run src/test/outsource/mes-mirror-panel.test.tsx`。
- [ ] **Step 3: 实现**：组件文案简洁，不加多余说明文字；确认对话框只可通过两个按钮之一关闭（不提供遮罩点击或关闭图标，避免误丢状态），合并按钮用主色，放弃按钮为次级；表格用 `ResizableTable`，`tableId` 取 `outsource-mes-preview`。
- [ ] **Step 4: 运行** `npm run lint`、`npx tsc -b`、`npx vitest run src/test/outsource src/test/migrated/core`，预期全部通过。
- [ ] **Step 5: 提交**：`git commit -m "Add the manual MES mirror panel to the outsourcing settings"`

### Task 4: 文档与回归

**Files:**
- Modify: `server_dotnet/docs/外协清单契约-2026-11-09.md`（「MES 数据镜像」一节补手动抓取、停止、确认、权限、状态）
- Modify: `docs/superpowers/specs/2026-11-12-ipms-mes-mirror-design.md`

- [ ] **Step 1: 更新文档**，并把「我替用户做的决定」里手动抓取的 5 条在用户确认后标为已确认。
- [ ] **Step 2: 全量回归**：后端 `--filter "FullyQualifiedName~Outsource|FullyQualifiedName~Mes|FullyQualifiedName~OpenApi|FullyQualifiedName~Contract|FullyQualifiedName~Schema"`；前端 `npm run lint`、`npx tsc -b`、`npx vitest run`；隔离 HTTP 套件。预期全部通过，无跳过。
- [ ] **Step 3: 真实联调（需用户的专用账号）**：在设定页分别试「抓指定页」（第 1 页、100 条）与「抓取全部」后点「停止」，核对暂存、确认、合并后镜像的笔数，并记录每页耗时。不确认就不写库，因此可以先只试单页。
- [ ] **Step 4: 提交**：`git commit -m "Document manual MES mirror runs"`

---

## Self-Review

- **Spec 覆盖**：抓指定页与每页条数（Task 1、3）；手动触发（Task 1、2、3）；中途停止（Task 1、3）；停止与单页后二次确认放弃或合并（Task 1、3）；完整抓完自动替换（Task 1）；权限与审计（Global Constraints、Task 1）。
- **类型一致**：`MesMirrorStatusResponse`、`MesMirrorRunRequest`、`MesMirrorConfirmRequest`、`MesMirrorPreviewRow` 的字段在 Task 1 定义，Task 2 的路由与 Task 3 的前端按生成类型消费。
- **风险**：租约 60 分钟内未确认会阻塞定时抓取，这是设计里的选择；若实际常常忘记确认，可缩短租约，属于配置级调整。
- **比例**：不写实现体，只给签名、测试名与断言要点。
