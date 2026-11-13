# IPMS（MES 监控）数据镜像 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 每天 03:00 把「迅得二期」MES 监控的数据抓进本库镜像表，并让外协清单的 `IIpmsClient` 从镜像表读取。

**Architecture:** 后台服务 `MesMirrorSyncService` 经 `IMesSource`（HTTP 实现 `MesHttpSource`）顺序翻页抓取，先写暂存表，校验通过后在一个事务内整表替换正式表；`MesMirrorIpmsClient : IIpmsClient` 只查本库。调度、租约方式沿用 `OutsourceIpmsSyncService`。

**Tech Stack:** .NET 8、EF Core 9/Pomelo MySQL、xUnit v3；测试用假 `IMesSource` 与假 `HttpMessageHandler`，不连真实 MES。

**Spec:** `docs/superpowers/specs/2026-11-12-ipms-mes-mirror-design.md`（外发类型改为人工填写属于另一份计划，不在本计划内）。

## Global Constraints

- 只调用 MES 的两个接口：登入 `POST /xyj-admin/r/login`、数据 `POST /xyj-twxd-mes/r/xdSalesOrder/queryByMesMonitor`，都在同一个 `MesBaseUrl`（含端口，如 `http://183.238.37.108:6001`）之下；不调用其它接口。
- 认证凭证是请求头 `token`（不是 Cookie）。登入请求体 JSON：`account`、`password`、`captcha:""`、`usertype:0`、`yzmState:0`、`suffix:""`。
- 数据请求体沿用页面的形状：`pageBean{page,pageSize,showTotal:true,summary:[]}`、`querys`（`productionStatus IN ["1","2","0"]` 与 `outMachineStatus IN ["0"]`）、`queryComplaint:{}`、`params:{}`、`selfDate:{}`、`sorter:[]`、`countFlag:false`、`dataAuthority:false`、`permissionsGroup:false`。响应 `data.rows`、`data.total`（**字符串**）。
- 每页默认 100 条（范围 1 到 200，不得更大）、顺序抓取、页间隔默认 2 秒、每页超时 180 秒、每页失败重试 2 次。
- 日抓取时间默认 `03:00`（业务时间 UTC+8，每个业务日一次）；`App:WorkerEnabled=false` 或未配置 `Mode=MesMirror` 时不运行。
- 抓满标准：去重后的工令数等于首页 `total`；且不少于上一轮成功笔数的 50%（上一轮为 0 时不检查）。不满足则整轮失败、保留旧数据。
- 账号、密码、token 不得出现在日志、审计、异常消息、审计 detail 中。
- 迁移按 CLAUDE.md：`dotnet ef migrations add`，同步 `SchemaShapeTests.ExpectedMigrations` 与 `MigrationRoundTripTests.Latest`（含回滚断言）；不手写 DDL。
- 外部调用不放在用户请求或业务事务中；整表替换事务内只做数据库操作。
- 测试遵守 CLAUDE.md：DB 测试用 `SchemaDatabaseScope`，需要 `YF_TEST_DATABASE_URL`（从 `Yf.Api/appsettings.Local.json` 的回环凭证组装，不打印密码）。

## Review Focus

1. MES 返回 `total` 为字符串、为空或缺失：按字符串解析；缺失视为失败，不当 0 处理。（Task 2）
2. 同一工令在多行重复，导致去重数小于 `total`：整轮失败并记录「重复工令」，不替换。（Task 4；首次实测时若真实数据确有重复，需回到用户处决定）
3. 抓取期间 MES 数据变动使翻页重叠或漏行：靠去重数校验发现，整轮失败；不自动重抓。（Task 4）
4. 抓取中 token 过期或被踢：重新登入一次并重试该页，再失败则整轮失败。（Task 2）
5. 响应不是 JSON（HTML 错误页）、`code` 不为 200、`rows` 为空数组但 `total>0`：视为失败。（Task 2、4）
6. 取消（站点停止）时租约必须释放，暂存表残留要在下一轮开始时清空。（Task 4）
7. 抓取失败那天外协清单继续使用昨天的镜像数据，不被清空。（Task 4）

---

### Task 1: 配置与模式 `MesMirror`

**Files:**
- Modify: `server_dotnet/Yf.Api/Modules/Outsource/Ipms/IpmsClient.cs`
- Modify: `server_dotnet/deploy/appsettings.example.json`
- Test: `server_dotnet/tests/MesMirrorOptionsTests.cs`（新建）

**Interfaces:**
- Produces: `IpmsOptions` 新增属性 `string MesBaseUrl`、`string MesAccount`、`string MesPassword`、`string MesSyncTime`（默认 `"03:00"`）、`int MesPageSize`（默认 100）、`int MesPageDelaySeconds`（默认 2）、`int MesPageTimeoutSeconds`（默认 180）、`int MesMinRatioPercent`（默认 50）；常量 `IpmsOptions.MesMirrorMode = "MesMirror"`（`internal const`）。

- [ ] **Step 1: 写失败测试** `MesMirrorOptionsTests`：
  - `ValidatesMesMirrorSettings`（Theory）：`MesBaseUrl` 为空、非绝对 URL、`MesAccount`/`MesPassword` 为空、`MesSyncTime` 非 `HH:mm`、`MesPageSize` 为 0 或 201、`MesPageDelaySeconds` 为 -1 或 61、`MesPageTimeoutSeconds` 为 9 或 601、`MesMinRatioPercent` 为 0 或 101，每项都让 `Validate()` 抛 `InvalidOperationException`；合法组合不抛。
  - `ExceptionMessagesNeverContainThePassword`：密码设为 `"s3cret-PW"`，对每个非法设置捕获异常，断言 `Message` 不含该密码。
  - `UnknownModeIsRejected`：`Mode="X"` 仍抛。
- [ ] **Step 2: 运行确认失败**：`dotnet test .\tests\Yf.Api.Tests.csproj --filter MesMirrorOptionsTests`，预期编译失败或测试失败。
- [ ] **Step 3: 实现**：在 `IpmsOptions` 增加上述属性；`Validate()` 接受 `Mode` 为空、`Fake`、`MesMirror`；`MesMirror` 时按上述范围校验，错误消息只写设置名，不写值。`IpmsClientFactory.Create` 暂不改（Task 5 接线）。`deploy/appsettings.example.json` 的 `App:Ipms` 补占位项（`Mode` 保持空，账号密码写 `"<private>"`）。
- [ ] **Step 4: 检查没有地方序列化 `AppOptions`**：`Grep` `ipms|Ipms` 在 `Yf.Api` 中的 JSON 输出与日志位置（尤其 `--check-development-readiness`、`--inspect-development-data`），确认不会输出 `MesPassword`；若有，改为不输出该属性。
- [ ] **Step 5: 运行测试确认通过**，同一命令，预期全部通过。
- [ ] **Step 6: 提交**：`git commit -m "Add MES mirror settings to the IPMS options"`

### Task 2: `IMesSource` 与 `MesHttpSource`

**Files:**
- Create: `server_dotnet/Yf.Api/Modules/Outsource/Ipms/Mes/IMesSource.cs`
- Create: `server_dotnet/Yf.Api/Modules/Outsource/Ipms/Mes/MesHttpSource.cs`
- Test: `server_dotnet/tests/MesHttpSourceTests.cs`（新建，无 DB）

**Interfaces:**
- Produces:
  - `public sealed record MesRow(string WorkOrderNo, string RawJson);`
  - `public sealed record MesPage(int Total, IReadOnlyList<MesRow> Rows);`
  - `public interface IMesSource { Task<MesPage> FetchPageAsync(int page, int pageSize, CancellationToken ct); }`（`page` 从 1 起；失败抛 `IpmsUnavailableException`，消息不含凭证）
  - `public sealed class MesHttpSource(HttpClient http, IpmsOptions options) : IMesSource`
- Consumes: Task 1 的 `IpmsOptions` 属性。

- [ ] **Step 1: 写失败测试**（假 `HttpMessageHandler`，按请求路径应答）：
  - `FetchesPageWithLoginTokenAndParsesStringTotal`：登入应答 `{"code":200,"data":{"token":"T1"}}`；数据应答 `{"code":200,"data":{"total":"1778","rows":[{"workOrder":"21074672",...}]}}`。断言：先登入一次；数据请求头含 `token: T1`；请求体 `pageBean.page/pageSize` 等于入参，`querys` 含 `productionStatus` 与 `outMachineStatus`；返回 `Total==1778`，`Rows[0].WorkOrderNo=="21074672"`，`RawJson` 是该行原样 JSON。
  - `ReusesTheTokenAcrossPages`：两页只登入一次。
  - `LogsInAgainOnceWhenTheTokenIsRejected`：第二页首次返回 HTTP 401（以及另一个用例：HTTP 200 但 JSON `code` 为 401），之后重新登入并重试成功；登入共 2 次。
  - `FailsWhenLoginIsRejectedTwice`：抛 `IpmsUnavailableException`。
  - `RejectsMalformedResponses`（Theory）：HTML 正文、`code` 为 500、缺 `data.total`、缺 `rows`，均抛 `IpmsUnavailableException`。
  - `ErrorsNeverContainTheCredentials`：账号 `acct-1`、密码 `s3cret-PW`，在以上各失败情形中断言 `Message`、`InnerException?.Message` 都不含它们和 token 值。
  - `HonoursThePageTimeout`：处理器永不返回，`MesPageTimeoutSeconds=10` 的替身（测试里用 10 秒下限或可注入的 `TimeSpan`，选后者更快）超时抛 `IpmsUnavailableException`。
- [ ] **Step 2: 运行确认失败**：`--filter MesHttpSourceTests`。
- [ ] **Step 3: 实现** `MesHttpSource`：惰性登入，用 `SemaphoreSlim` 串行化登入与 token 字段；token 提取函数 `ExtractToken(JsonElement)` 先找 `data.token` 再找 `token`（**这是对真实登入响应结构的假设，见 Task 6 的核对步骤**）；每页超时用 `CancellationTokenSource.CreateLinkedTokenSource` + `CancelAfter(options.MesPageTimeoutSeconds)`（构造函数提供 `TimeSpan? pageTimeoutOverride` 供测试）；`total` 用 `JsonElement` 兼容字符串与数字；原始行用 `GetRawText()`；工令取 `workOrder`，缺失或空白的行视为响应异常。所有失败统一包成 `IpmsUnavailableException("MES 暂时无法连接，请稍后再试")` 或更具体但不含凭证的消息，`inner` 不得带请求体。
- [ ] **Step 4: 运行测试确认通过**。
- [ ] **Step 5: 提交**：`git commit -m "Add the MES HTTP source with login, token reuse and re-login"`

### Task 3: 镜像表与迁移

**Files:**
- Modify: `server_dotnet/Yf.Api/Infrastructure/Entities/OutsourceEntities.cs`（或新建同目录 `MesMirrorEntities.cs`，倾向新建以保持文件聚焦）
- Modify: `server_dotnet/Yf.Api/Infrastructure/YfDbContext.cs`
- Create: 迁移 `AddMesMirror`（`dotnet ef migrations add AddMesMirror --project .\Yf.Api`，含 Designer 与快照更新）
- Modify: `server_dotnet/tests/SchemaShapeTests.cs`（`ExpectedMigrations`）、`server_dotnet/tests/MigrationRoundTripTests.cs`（`Latest` 与回滚断言）

**Interfaces:**
- Produces（实体，表名/列名 snake_case，utf8mb4_unicode_ci）：
  - `MesWorkOrder`：`string WorkOrderNo`（主键，varchar(64)）、`string BatchId`（char(36)）、`DateTime FetchedAt`（datetime(3)）、`string? CustomerName`（varchar(256)）、`string? DeviceModels`（varchar(256)）、`string RawJson`（longtext）→ 表 `mes_work_orders`。
  - `MesWorkOrderStaging`：同上列 → 表 `mes_work_orders_staging`。
  - `MesMirrorState`：`byte Id`（固定 1）、`DateTime? LeaseUntil`、`DateTime? LastStartedAt`、`DateTime? LastFinishedAt`、`DateTime? LastSuccessAt`、`string? LastError`（varchar(256)）、`string? LastTrigger`（varchar(16)）、`int LastCount`（成功时的笔数，默认 0）、`DateOnly? ScheduledFor`→ 表 `mes_mirror_state`。
  - `YfDbContext` 新增 `DbSet<MesWorkOrder> MesWorkOrders`、`DbSet<MesWorkOrderStaging> MesWorkOrdersStaging`、`DbSet<MesMirrorState> MesMirrorState`。

- [ ] **Step 1: 写失败测试**：在 `SchemaShapeTests` 把新迁移 id 加入 `ExpectedMigrations`（运行会因迁移尚不存在而失败）；`MigrationRoundTripTests.Latest` 先不动。
- [ ] **Step 2: 运行确认失败**：`--filter SchemaShapeTests`。
- [ ] **Step 3: 实现实体与配置**，生成迁移。迁移只新增三张表，不播种数据（状态行由服务按 `INSERT IGNORE` 创建，与外协一致）。
- [ ] **Step 4: 更新 `MigrationRoundTripTests.Latest` 为新迁移 id**，并补该测试里对「回滚到上一迁移后这三张表不存在」的断言（对照已有同类断言的写法）。
- [ ] **Step 5: `dotnet ef migrations has-pending-model-changes --project .\Yf.Api`** 应无差异；运行 `--filter "SchemaShapeTests|MigrationRoundTripTests"` 全部通过。
- [ ] **Step 6: 提交**：`git commit -m "Add the MES mirror tables"`

### Task 4: `MesMirrorSyncService`

**Files:**
- Create: `server_dotnet/Yf.Api/Modules/Outsource/Ipms/Mes/MesMirrorSyncService.cs`
- Test: `server_dotnet/tests/MesMirrorSyncTests.cs`（新建）

**Interfaces:**
- Consumes: Task 2 的 `IMesSource`、`MesPage`、`MesRow`；Task 3 的实体；Task 1 的 `IpmsOptions`（通过 `AppOptions.Ipms`）。
- Produces:
  - `public sealed class MesMirrorSyncService(IDbContextFactory<YfDbContext> dbFactory, AuditService audit, IMesSource source, AppOptions options, ILogger<MesMirrorSyncService> logger) : BackgroundService`
  - `internal sealed record MirrorOutcome(int Fetched, int Pages, string? Error);`
  - `internal Task<MirrorOutcome?> RunAsync(string trigger, DateOnly? scheduledFor, CancellationToken ct)`（租约被占用或当日已跑过返回 `null`）
  - 调度判断复用 `OutsourceIpmsSyncService.IsDue(options.Ipms.MesSyncTime, state.ScheduledFor, now, out day)`。
  - 审计 action `OUTSOURCE_MES_MIRROR`，detail 含 `trigger, fetched, pages, error`（不含凭证）。

- [ ] **Step 1: 写失败测试**（`SchemaDatabaseScope` + 假 `IMesSource`，`options.Ipms.MesPageSize` 设小值如 2、`MesPageDelaySeconds=0`）：
  - `ReplacesTheMirrorAfterAFullFetch`：先放入旧镜像 3 行；假源 5 行（3 页）；`RunAsync("SCHEDULED", day)` 后镜像恰为这 5 行，`RawJson` 原样，`BatchId` 一致；状态 `LastCount==5`、`LastSuccessAt` 非空、`LastError` 为空；暂存表为空。
  - `KeepsTheOldMirrorWhenAPageFails`：第 2 页抛 `IpmsUnavailableException`；旧镜像不变，状态 `LastError` 有值、`LastSuccessAt` 不变，暂存表被清空。
  - `RejectsAnIncompleteFetch`：首页 `Total=5`，实际只抓到 4 个不同工令（或有重复工令）；旧镜像不变，错误提示包含「笔数」或「重复」。
  - `RejectsASuddenDropBelowFiftyPercent`：上一轮 `LastCount=10`，本轮完整抓到 4 行（总数也是 4）；不替换，错误提示包含「骤降」。上一轮 `LastCount=0` 时同样 4 行则替换成功。
  - `ClearsStaleStagingRowsBeforeStarting`：暂存表预置残留行，成功后它们不在镜像中。
  - `OnlyOneRunHoldsTheLease`：把 `lease_until` 设为未来后 `RunAsync` 返回 `null`；同一业务日 `scheduledFor` 已记录时再次调用返回 `null`。
  - `ReleasesTheLeaseWhenCancelled`：取消令牌触发，租约释放（`lease_until IS NULL`）。
  - `AuditAndLogsNeverContainCredentials`：假源抛带账号/密码字样的异常消息时，状态 `LastError` 为通用文案，审计 detail 不含它们。
  - `DoesNotRunWhenWorkerDisabledOrUnconfigured`：对 `ExecuteAsync` 的启动判断：`WorkerEnabled=false` 或 `Mode!=MesMirror` 时直接返回，不触及数据库。
- [ ] **Step 2: 运行确认失败**：`--filter MesMirrorSyncTests`。
- [ ] **Step 3: 实现**：
  - 取租约/当日一次的 SQL 与 `OutsourceIpmsSyncService.RunAsync` 同形（表 `mes_mirror_state`，租约 `LeaseMinutes=60`，每页成功后续租）。
  - 流程：清空暂存表 → 循环 `FetchPageAsync(page, pageSize)`（首页记下 `Total`；页间隔 `Task.Delay`；失败重试 2 次由调用方负责——`MesHttpSource` 已含重登入，这里再按 `MesPageRetryCount=2` 对 `IpmsUnavailableException` 重试同一页）→ 每页批量写暂存表 → 直到累计行数达到 `Total` 或某页为空。
  - 校验：去重工令数 `==Total`；`LastCount>0` 时 `Total*100 >= LastCount*MesMinRatioPercent`；任一不满足设错误并跳过替换。
  - 替换：单事务 `DELETE mes_work_orders` + `INSERT … SELECT … FROM mes_work_orders_staging`（原生 SQL，属文档允许的原子边界），然后清空暂存表并更新状态 `LastCount/LastSuccessAt`。
  - `finally` 释放租约并写 `LastFinishedAt/LastError`（用 `CancellationToken.None`）；未预期异常只记通用文案「同步失败，请稍后重试」，日志只记异常类型与脱敏消息。
- [ ] **Step 4: 运行测试确认通过**。
- [ ] **Step 5: 提交**：`git commit -m "Add the MES mirror sync service with staged full replacement"`

### Task 5: `MesMirrorIpmsClient` 与接线

**Files:**
- Create: `server_dotnet/Yf.Api/Modules/Outsource/Ipms/Mes/MesMirrorIpmsClient.cs`
- Modify: `server_dotnet/Yf.Api/Modules/Outsource/Ipms/IpmsClient.cs`（`IpmsClientFactory`）
- Modify: `server_dotnet/Yf.Api/Modules/Outsource/OutsourceModule.cs`
- Test: `server_dotnet/tests/MesMirrorIpmsClientTests.cs`（新建）

**Interfaces:**
- Consumes: Task 3 的 `MesWorkOrder`；`IIpmsClient`、`IpmsWorkOrder`（现有）。
- Produces: `public sealed class MesMirrorIpmsClient(IDbContextFactory<YfDbContext> dbFactory) : IIpmsClient`，`IsConfigured == true`；`internal static IpmsWorkOrder? Map(string workOrderNo, string rawJson)`。

映射（**第一版只映射已确认的字段，其余为 null**）：`Customer←customerName`、`MachineModel←deviceModels`、`Quantity←deviceNum`（用户确认「数量」列；字段名待 Task 6 核对）、`DueDate←customerEndDate`（**未确认：页面有「计划交期」「客户交期」「原交期」三列，需用户确认用哪一列**，Task 6 之前先问清）、`Closed=false`（镜像只含未出机的工令；已完工工令从镜像消失，外协同步会把它们计入「IPMS 查无」，是否改为结案属于待决事项，见设计文档）。外发类型不由 MES 提供，`OutsourceTypeCode=null`。

- [ ] **Step 1: 写失败测试**：
  - `MapsTheConfirmedFields`：给定含 `customerName/deviceModels/deviceNum/customerEndDate` 的 JSON，得到对应的 `IpmsWorkOrder`；日期格式 `yyyy-MM-dd` 解析为 `DateOnly`；字段缺失或格式异常时为 `null`，不抛。
  - `LooksUpOnlyTheRequestedWorkOrders`（DB）：镜像 3 行，查 2 个存在 + 1 个不存在，结果只含存在的。
  - `LookupDoesNotCallTheNetwork`：构造时不注入任何 `IMesSource`，且查询在镜像为空时返回空字典而非抛错。
  - `FactoryBuildsTheMirrorClientForMesMirrorMode`：`IpmsClientFactory.Create` 在 `Mode=MesMirror` 时不再返回 `UnconfiguredIpmsClient`（工厂需要 `IDbContextFactory<YfDbContext>`，改为 `Create(AppOptions, IServiceProvider)` 或在 `AddOutsourceModule` 内用 `provider` 构造；选后者以不改变其它调用方）。
- [ ] **Step 2: 运行确认失败**：`--filter MesMirrorIpmsClientTests`。
- [ ] **Step 3: 实现**，并在 `AddOutsourceModule` 注册：`HttpClient`（`MesBaseUrl` 为 `BaseAddress`，`Timeout=Timeout.InfiniteTimeSpan`，页超时由源自己控制）、`IMesSource`→`MesHttpSource`、`MesMirrorSyncService`（并作为 `IHostedService`）；仅在 `Mode==MesMirror` 时注册后台服务与源，以免未配置时多出依赖。
- [ ] **Step 4: 运行测试确认通过**，再运行 `--filter "FullyQualifiedName~Outsource"` 确认外协既有测试没有被破坏。
- [ ] **Step 5: 提交**：`git commit -m "Read outsourcing IPMS data from the MES mirror"`

### Task 6: 文档与真实环境联调

**Files:**
- Modify: `server_dotnet/docs/外协清单契约-2026-11-09.md`（IPMS 同步一节：数据来源为 MES 镜像、每日 03:00 镜像 + 03:30 同步、配置项、失败行为）
- Modify: `docs/superpowers/specs/2026-11-12-ipms-mes-mirror-design.md`（把已验证项和实测耗时补进「已验证的事实」，待定项更新）
- Modify: `HANDOFF.md`（仅在确认了新的坑时）

- [ ] **Step 1: 向用户确认两件事**，答复后写入映射与文档：交期取 MES 的哪一列；是否要把「任务完成」状态也抓进来并据此给 `Closed` 赋值。
- [ ] **Step 2: 向用户索取专用只读账号的配置方式**：账号密码写入服务器外部配置文件（`YF_CONFIG_PATH` 指向的文件），不经过聊天、不进仓库。
- [ ] **Step 3: 核对登入响应结构**：用专用账号在本机做一次只读登入，**只确认**响应里 token 所在字段与过期时的响应形态，核对 Task 2 的假设（`data.token`、HTTP 401 或 JSON `code` 401）；不打印 token。若与假设不符，先改 `ExtractToken` 与对应测试，再继续。
- [ ] **Step 4: 真实联调**：本机把 `Mode=MesMirror`、`MesSyncTime` 设为当前时间后两分钟，启动后端，观察一轮：记录总页数、每页耗时、总耗时、笔数、是否通过「去重数等于 total」校验，把实测结果写入设计文档。若真实数据存在重复工令导致校验失败，停下并回到用户决策，不自行放宽校验。
- [ ] **Step 5: 全量回归**：`dotnet test .\tests\Yf.Api.Tests.csproj --filter "FullyQualifiedName~Outsource|FullyQualifiedName~Mes|FullyQualifiedName~Schema|FullyQualifiedName~Migration"`；再按 `iis-release` 流程的前置检查确认工作区状态。
- [ ] **Step 6: 提交**：`git commit -m "Document the MES mirror and record the first real run"`

---

## Self-Review

- **Spec 覆盖**：每日 03:00（Task 1、4）；每页 100 条/顺序/页间隔/超时/重试（Task 1、2、4）；暂存表加整表替换与失败保留旧数据（Task 3、4）；笔数校验与 50% 保护（Task 4）；整行原始 JSON 加少量列（Task 3）；`IIpmsClient` 读镜像（Task 5）；凭证不外泄（Task 1、2、4）；会话保持采用「过期重新登入」（Task 2）；外协同步 03:30 已另行完成；外发类型人工填写另有计划。
- **类型一致**：`IMesSource.FetchPageAsync(int page, int pageSize, CancellationToken)`、`MesPage(int Total, IReadOnlyList<MesRow>)`、`MesRow(string WorkOrderNo, string RawJson)`、`MirrorOutcome`、`MesMirrorState.LastCount` 在各任务中一致。
- **待用户确认的假设**：登入响应结构与过期响应形态（Task 6 Step 3）、交期取哪一列与是否抓全部状态（Step 1）、MES 工令是否可能重复（Step 4）。
- **比例**：本计划没有写出实现体，只给签名、测试名与断言；若执行者发现某步骤仍有两种合理写法，以 Global Constraints 与设计文档为准。
