# 取消子项目、全员可见与新建即开始 — 设计

日期：2026-11-09　状态：待审阅

## 1. 意图

- 项目只有一层，去掉“主项目 + 子项目”结构，减少一层操作和同步逻辑。
- 公司内部所有能登录的人员都能看到全部项目，便于互相查阅；但只有负责人能动自己的项目。
- 新建项目即进入进行中，直接带到上传页，省掉“建主项目 → 建子项目 → 开始”三步。
- 顺带消除缺陷：只建主项目、没有子项目时无处“开始”（主项目状态由子项目汇总、开始只在子项目上）。

## 2. 已确认的业务规则

### 2.1 可见与编辑
- 内部账号（有 `project:list`）可见全部项目：详情、文件、留言、下载、预览、动态、复制履历。厂商侧不变：只看自家厂商的项目，草稿规则随草稿状态一并消失。
- “自己的项目”= 当前负责人（`responsible_user_id`）。创建人即初始负责人；转交后原创建人不再有任何特权。
- 非负责人的内部账号对项目完全只读：不能改资料、终止/重新开始、上传、留言、删除文件、确认/退回资料、重开、中止他人上传会话。
- 负责人执行上述动作仍需原有 RBAC 权限点（`project:update`、`project:status`、`file:upload`、`message:create`、`file:delete` 等）；归属是附加条件，不替代权限点。资料确认/退回/重开维持“仅负责人、不需额外权限点”。
- 删除：系统管理员（`project:delete`）与项目所属课别/上级部门/事业部主管（沿用 `ProjectDeletionAuthorization`）。
- 转交负责人：当前负责人、系统管理员、所属组织主管；均需 `project:transfer`（主管与管理员沿用删除授权的判定口径）。

### 2.2 取消子项目
- 项目即现 `projects` 行；原主项目的公共资料全部落在项目自身。
- 文件、留言、截图、回执、动态、资料确认、到期提醒、复制履历继续按 `projects.id` 保存（基本不动）。
- “复制子项目”改为“复制项目”：任何可见项目都能复制（需 `project:create`），副本负责人为执行复制的人。

### 2.3 新建即开始
- 创建成功后项目状态直接为 `IN_PROGRESS`，前端跳到 `/projects/{id}?tab=files`。
- 创建事务内执行原“开始”的全部前提校验（厂商启用、至少一个启用且实际具备 `file:upload` 的厂商账号、负责人有效）；不满足返回原 `409 / 40901` 文案，**不落任何数据**。
- 创建时开启第一轮资料（`material_file_floor` = 0 或 null 语义等同），写状态历史（`START`）与审计，按“开始”开关给厂商联络人发开始邮件。
- 复制出的项目同样直接 `IN_PROGRESS`，前提不满足则复制任务失败并显示原文案。
- 草稿状态 `DRAFT` 退出模型。状态机：`IN_PROGRESS → COMPLETED`（资料确认 OK）、`COMPLETED → IN_PROGRESS`（重开）、`IN_PROGRESS → TERMINATED`（终止）、`TERMINATED → IN_PROGRESS`（重新开始）。
- 删除仅限 `TERMINATED` 项目（其余删除副作用沿用原子项目删除：连同文件、留言、上传记录、动态、状态履历；blob 进 GC；进行中上传阻挡；作为复制来源且副本存在时阻挡）。

## 3. 方案

| 方案 | 做法 | 取舍 |
|---|---|---|
| **A（已选）** | 保留 `projects`，删除 `project_groups` 及其附属表 | 协作数据本就挂在 `projects.id`，风险最小；删除主子同步与状态汇总；API/契约/测试改动大 |
| B | 两表保留，每个主项目固定一个隐藏子项目 | 改动最小但保留全部同步与双层权限复杂度，违背“移除掉” |
| C | 保留 `project_groups`，协作数据改挂主项目 | 外键全部迁移，改动和风险最大，无收益 |

选择 A：`projects` 已含全部公共资料快照（供应商、机型、料号、Robot 类型、负责人、课别、优先级、需求日期、工令号），删一层即可。

## 4. 设计要点（方案 A）

### 4.1 数据库（新 EF 迁移 `RemoveProjectGroups`）
- 新增 `project_robot_owners(project_id, contact_id, sort_no)`，结构对齐 `project_group_robot_owners`；迁移把每个项目所属主项目的 Robot 负责人复制过来。
- 删除 `projects.project_group_id`（含外键与索引）、`project_groups`、`project_group_robot_owners`、`project_group_work_orders`、`project_group_status_logs`。
- 项目名称唯一性从主项目移到项目（沿用原主项目名唯一规则：同名报 400/40901 与原文案一致）；需确认现有 `projects` 是否已有同名，开发库重建故迁移直接建唯一约束或沿用服务端校验（以原主项目实现为准，实现时核对）。
- 残留 `DRAFT` 行在迁移中改为 `TERMINATED`（开发库会重建，仅为迁移完整）。
- 退役权限点 `project:view_all`（删除角色授权行与权限行）。
- 复制任务表若引用主项目（按组列复制任务），改为按发起人/来源项目查询，删除对应列。
- 开发数据库按《开发数据初始化说明》重建，不做历史数据兼容。

### 4.2 后端
- 删除：`ProjectGroupService`、`ProjectGroupAccessService`、`ProjectGroupStatusService`、主子同步与主项目状态重算、`/project-groups*` 全部路由、`POST /project-groups/{id}/projects`、`GET /project-groups/{groupId}/copy-jobs`。
- 新增/改造路由（全部在 `/api/v1`）：
  - `GET /projects`：分页列表，迁移原主项目列表的筛选（关键字、状态、厂商、`materialState`、`overdue`），新增 `mine=true`（只看我负责的）。
  - `POST /projects`：创建并开始（原 `ProjectUpsertRequest` 去掉 `subprojectNames`）。
  - `PUT /projects/{id}`：编辑全部公共资料 + 名称说明，必须带 `expectedUpdatedAt`；已完成/已终止不可编辑；创建后不可换厂商（沿用原主项目规则）。
  - `PUT /projects/{id}/responsible`：转交，可带 `expectedResponsibleUserId`。
  - `PUT /projects/{id}/status`：只剩终止与重新开始。
  - `GET /project-copy-jobs`：当前用户发起的复制任务（替代按主项目列出）。
  - 其余 `/projects/{id}/...`（文件、留言、动态、资料确认、重开、复制、复制履历）保持路径。
- 访问控制：`ProjectAccessService.VisibleQueryAsync` 内部账号返回全部项目；新增统一的 `RequireOwnerAsync`（在锁定行上校验 `responsible_user_id == actor`），所有写入入口（编辑、状态、上传开始/分片/合并、留言、删文件、中止上传、资料确认、重开）在锁内调用；`CanDeleteFilesAsync`、`UploadActivity.CanAbort` 等能力判断加上归属条件。
- 响应：项目响应增加 `canManage`（当前用户是负责人且项目对其可写）供前端控制只读；`ProjectGroup*` 响应类型删除，主项目字段并入 `ProjectResponse`（含 Robot 负责人联络人、工令号列表、逾期等）。
- 通知与未读范围（**我的决定**，见 §6）：内部侧的协作通知、未读计数、工作台待办、邮件收件人只覆盖“我负责的项目”；浏览他人项目不产生未读或邮件。厂商侧不变。
- 实时推送：内部受众改为全部启用的、有 `project:list` 的内部账号（仅推送变化信号，客户端照常重新拉取）。
- 编辑公共资料不再“同步到未完成子项目”；重开不再“重新同步主项目资料”。
- 到期提醒、厂商撤回、资料轮次、文件限流、删除副作用逻辑不变。

### 4.3 前端
- 删除 `ProjectGroupDetail`、`SubprojectDock`、`SubprojectPane` 及相关上下文/布局文件与路由 `project-groups/:id`。
- `ProjectList` 改为直接列项目：列沿用主项目列表（去掉“子项目进度”），新增“只看我负责的”切换；“新建项目”表单去掉子项目字段，主项目字段原样保留；创建成功后跳 `/projects/{id}?tab=files`。
- `ProjectDetail` 成为唯一工作区：顶部展示公共资料、编辑、转交、终止/重新开始、复制、删除入口；`canManage=false` 时隐藏上传区、留言输入框、资料确认与状态按钮，只保留查看与下载。
- 工作台、协作通知中跳转主项目的链接改为 `/projects/{id}`。
- 文案统一为“项目”，去掉“主项目/子项目”字样。

### 4.4 契约、测试与文档
- 新契约 `server_dotnet/docs/项目协作契约-2026-11-09.md` 取代《主项目与子项目协作契约》（旧文件保留并在开头标注已被取代）；同步更新邮件提醒、复制履历、负责人转交、并发与错误响应等引用主子项目的契约段落、`server_dotnet/README.md` 删除表、根 `CLAUDE.md` 的 Projects 段。
- `tests/Contracts/api-v1.json`、`openapi-v1.json`、前端 `api-types.ts` 重新生成。
- 迁移相关：`SchemaShapeTests.ExpectedMigrations`、`MigrationRoundTripTests.Latest` 及回滚断言、`MailDeliveryTests`/`FilesRecoveryTests` 手写 schema。
- xUnit、Python HTTP 合约、浏览器步骤中所有主项目/子项目用例改写为单层项目；新增用例覆盖：全员可见、非负责人写入 403（含上传分片与留言）、新建即开始与前提失败不落数据、转交授权、仅已终止可删。

## 5. 非目标
- 不改 OEM 模块。
- 不改文件存储、blob、限流、邮件发送机制。
- 不做历史数据兼容与旧路由兼容层。
- 不新增依赖。

## 6. 我做的决定（请在审阅时确认）
1. **通知范围只覆盖自己负责的项目**：否则全员可见后每人都会收到所有项目的未读和邮件。
2. **转交负责人**：负责人本人、系统管理员、所属组织主管可转交（需 `project:transfer`）。否则全员可见后任何有 `project:transfer` 的人都能抢走别人的项目。
3. **复制**：任何人可复制任何可见项目成为自己的新项目（复制是新建，不是编辑他人项目）。
4. **新建即开始时发“开始”邮件**给厂商联络人，受原“开始”开关控制。
5. **项目列表新增“只看我负责的”筛选**。
6. `project:view_all` 退役。

## 7. 留给实施者
- 项目名唯一约束的具体落点（服务端校验 vs 数据库唯一索引），按原主项目实现保持行为一致。
- 详情页信息区的具体布局，沿用现有主项目详情头部样式。
- 测试改写的拆分顺序与粒度。
