# OEM 绘图平台 - 数据库设计与接口规划

**版本**：V0.2（设计草案，尚未开发）
**更新日期**：2026-09-18
**前置文档**：[OEM绘图平台-需求文档.md](./OEM绘图平台-需求文档.md)

本文档覆盖 OEM 平台新增的数据库对象、状态边界和接口。生产后端继续使用 ASP.NET Core 10、EF Core、Pomelo/MySqlConnector、camelCase JSON、Bearer + 刷新会话轮换和 EF Core 迁移；普通业务读写使用 EF Core，只有行锁、命名锁和其他已审查的原子边界使用参数化原生 SQL。

## 0. 与现有系统的关系

| 现有对象 | 处理方式 |
|---|---|
| `departments` | 复用，增加当前主管字段 |
| `users` | 只复用内部账号；OEM 外部账号进入独立表 |
| `files` / `upload_sessions` | 不复用业务表；复用分片、合并、哈希、路径和崩溃恢复代码 |
| `system_configs` | 复用表结构，但使用 `oem.*` 独立配置键 |
| `audit_logs` | 复用，增加 actor realm/account 快照字段以区分内部与 OEM 账号 |
| `email_outbox` | 复用发送队列，增加 recipient realm/account 快照字段；OEM 邮件不关联 `project_id` |
| `permissions` / `roles` / `role_permissions` | 复用结构，增加 OEM 内部权限点 |
| 现有 `App.StorageRoot` | 继续存放供应商协作文件并进入备份；OEM 文件使用独立且不备份的 `OemStorageRoot` |

现有 `files.project_id`、`files.uploader_id`、`upload_sessions.project_id` 和 `upload_sessions.uploader_id` 都绑定现有项目及用户表；OEM 账号又必须独立建表，因此不能只给现有表增加一个 `oem_transfer_id`。代码复用和表复用必须分开。

## 1. 新增与变更表

### 1.1 组织架构变更

`departments` 增加：

| 字段 | 类型 | 说明 |
|---|---|---|
| leader_account_id | BIGINT UNSIGNED NULL | 当前主管，应用层校验为启用内部账号 |

不建立级联删除；主管变化写入 `audit_logs`，action 为 `DEPT_LEADER_CHANGE`。被审批任务引用的历史人员不因主管变化而改写。

### 1.2 OEM 厂商与账号

**`oem_companies`**

| 字段 | 类型 | 说明 |
|---|---|---|
| id | BIGINT UNSIGNED PK | |
| name | VARCHAR(128) NOT NULL | 厂商名称 |
| contact_name / contact_phone / contact_email | VARCHAR NULL | 联系方式 |
| remark | VARCHAR(512) NULL | |
| status | VARCHAR(16) NOT NULL | `ACTIVE / DISABLED` |
| created_by / created_at / updated_at | | 内部创建人及时间 |

**`oem_accounts`**

| 字段 | 类型 | 说明 |
|---|---|---|
| id | BIGINT UNSIGNED PK | |
| employee_no | VARCHAR(64) NOT NULL UNIQUE | OEM 登录标识，独立命名空间 |
| password_hash | VARCHAR(255) NOT NULL | Argon2id，复用现有密码策略 |
| real_name | VARCHAR(64) NOT NULL | |
| email | VARCHAR(128) NOT NULL | |
| oem_company_id | BIGINT UNSIGNED NOT NULL FK | 所属厂商 |
| status | VARCHAR(16) NOT NULL | `ACTIVE / DISABLED` |
| must_change_password | TINYINT(1) NOT NULL | |
| failed_login_attempts / locked_until | | 复用现有限流语义 |
| created_by / created_at / updated_at | | |

OEM token 使用 `realm=oem`。刷新会话为 OEM 建立独立表或在统一会话表中加入不可伪造的 realm；无论采用哪种实现，OEM 会话都不能解析成现有内部/供应商用户。

### 1.3 删除策略模板

**`oem_retention_templates`**

| 字段 | 类型 | 说明 |
|---|---|---|
| id | BIGINT UNSIGNED PK | |
| name | VARCHAR(64) NOT NULL | 管理员可见名称 |
| mode | VARCHAR(32) NOT NULL | `KEEP / AFTER_RELEASE / AFTER_FIRST_RECEIPT / FIRST_RECEIPT_OR_DEADLINE` |
| release_ttl_minutes | INT UNSIGNED NULL | 从发布起的最长期限 |
| receipt_grace_minutes | INT UNSIGNED NULL | 首次接收后的宽限时间 |
| status | VARCHAR(16) NOT NULL | `ACTIVE / DISABLED` |
| created_by / created_at / updated_at | | |

校验规则：

- `KEEP`：两个时长均为空；
- `AFTER_RELEASE`：必须有 `release_ttl_minutes`；
- `AFTER_FIRST_RECEIPT`：必须有 `receipt_grace_minutes`；
- `FIRST_RECEIPT_OR_DEADLINE`：两个时长都必须有值；
- 已被传递单引用的模板不能物理删除，只能禁用；
- 发送时把模式和时长快照到传递单，后续模板修改不追溯。

### 1.4 文件传递单

**`oem_transfers`**

| 字段 | 类型 | 说明 |
|---|---|---|
| id | BIGINT UNSIGNED PK | |
| direction | VARCHAR(24) NOT NULL | `INTERNAL_TO_OEM / OEM_TO_INTERNAL` |
| oem_company_id | BIGINT UNSIGNED NOT NULL FK | 目标或发送方 OEM 厂商 |
| title | VARCHAR(128) NOT NULL | |
| description | VARCHAR(1024) NULL | |
| internal_sender_user_id | BIGINT UNSIGNED NULL FK→users | 公司出站发送人 |
| oem_sender_account_id | BIGINT UNSIGNED NULL FK→oem_accounts | OEM 入站发送人 |
| lifecycle_status | VARCHAR(24) NOT NULL | 见下方状态定义 |
| scan_status | VARCHAR(16) NOT NULL | `PENDING / SCANNING / CLEAN / INFECTED / ERROR / UNSCANNABLE` |
| approval_status | VARCHAR(16) NOT NULL | `NOT_REQUIRED / PENDING / APPROVED / REJECTED` |
| retention_template_id | BIGINT UNSIGNED NOT NULL FK | 选择的管理员模板 |
| retention_mode | VARCHAR(32) NOT NULL | 模板快照 |
| release_ttl_minutes | INT UNSIGNED NULL | 模板快照 |
| receipt_grace_minutes | INT UNSIGNED NULL | 模板快照 |
| manifest_sha256 | CHAR(64) NULL | 发送时对有序附件清单生成的摘要 |
| released_at | DATETIME(3) NULL | 接收方首次获得权限的时间 |
| expires_at | DATETIME(3) NULL | 发布时按模板计算的最晚时间 |
| created_at / updated_at | DATETIME(3) | |
| concurrency_version | BIGINT UNSIGNED NOT NULL | 乐观并发版本 |

发送人约束：

- `INTERNAL_TO_OEM`：`internal_sender_user_id` 必填，`oem_sender_account_id` 为空；
- `OEM_TO_INTERNAL`：`oem_sender_account_id` 必填，`internal_sender_user_id` 为空；
- MySQL 5.7 不依赖 CHECK 约束保证该规则，服务层和迁移回归都必须校验。

`lifecycle_status`：

- `DRAFT`：允许上传、移除附件和编辑标题说明；
- `SEALED`：已发送并冻结附件，等待扫描、审批或自动发布；
- `RELEASED`：接收方可下载；
- `REJECTED`：公司出站审批被驳回；
- `BLOCKED`：病毒命中、无法扫描或安全失败；
- `EXPIRED`：策略到期且所有剩余实体已进入清理；
- `PURGE_PENDING`：传递单内全部剩余文件正在删除；
- `PURGED`：所有文件实体已删除；
- `STORAGE_LOST`：数据库记录存在但实体文件非预期丢失。

接收范围不另建成员表：公司出站按 `oem_company_id` 匹配该厂商全部启用账号；OEM 入站按内部账号是否同时具备 `oem:transfer_view` 和 `oem:file_download` 动态判断。

不增加 parent/revision/version 等关联字段；修正文件创建新的独立传递单。

### 1.5 OEM 文件

**`oem_transfer_files`**

| 字段 | 类型 | 说明 |
|---|---|---|
| id | BIGINT UNSIGNED PK | |
| transfer_id | BIGINT UNSIGNED NOT NULL FK | 所属传递单 |
| uploaded_by_internal_user_id | BIGINT UNSIGNED NULL FK→users | 与方向互斥 |
| uploaded_by_oem_account_id | BIGINT UNSIGNED NULL FK→oem_accounts | 与方向互斥 |
| original_name | VARCHAR(255) NOT NULL | 仅展示，不能参与物理路径 |
| stored_name | VARCHAR(64) NOT NULL UNIQUE | 服务端 UUID 名称 |
| ext | VARCHAR(16) NOT NULL | 规范化扩展名 |
| mime_type | VARCHAR(128) NULL | 服务端识别结果 |
| size_bytes | BIGINT UNSIGNED NOT NULL | |
| md5 | CHAR(32) NULL | 客户端完整性校验，可选 |
| sha256 | CHAR(64) NOT NULL | 服务端合并时计算 |
| storage_path | VARCHAR(512) NOT NULL | 相对 `OemStorageRoot` |
| payload_status | VARCHAR(24) NOT NULL | `QUARANTINED / AVAILABLE / PURGE_PENDING / PURGED / STORAGE_LOST` |
| scan_status | VARCHAR(16) NOT NULL | 每文件扫描状态 |
| first_recipient_download_at | DATETIME(3) NULL | 首名接收方完整下载 |
| purge_due_at | DATETIME(3) NULL | 按模板计算的删除时间 |
| purge_claimed_at | DATETIME(3) NULL | 清理租约 |
| purged_at | DATETIME(3) NULL | 实体删除成功时间 |
| created_at / updated_at | DATETIME(3) | |

删除策略在每个文件上独立触发：一张传递单有多个文件时，接收方完成下载哪个文件，就只触发该文件的首次接收时间；未下载文件仍按发布期限或自身首次接收时间处理。传递单状态由其文件聚合。

### 1.6 OEM 上传会话

**`oem_upload_sessions`**

字段沿用现有分片协议所需的 session id、transfer id、发送方 realm/account、文件名、大小、MD5、分片大小、总分片数、临时目录、状态、结果文件 id、过期时间和更新时间。

状态沿用 `UPLOADING / MERGING / COMPLETED / EXPIRED / ABORTED`，但 `COMPLETED` 只表示合并并创建了隔离文件，不表示文件可下载。合并成功后文件必须是 `QUARANTINED + PENDING`，不能直接写成 `AVAILABLE`。

### 1.7 扫描任务与结果

**`oem_file_scan_jobs`**

| 字段 | 类型 | 说明 |
|---|---|---|
| id | BIGINT UNSIGNED PK | |
| file_id | BIGINT UNSIGNED NOT NULL FK | |
| status | VARCHAR(16) NOT NULL | `PENDING / RUNNING / CLEAN / INFECTED / ERROR / UNSCANNABLE` |
| attempt_count | INT NOT NULL | |
| lease_owner / lease_until | VARCHAR / DATETIME(3) NULL | 多实例安全认领 |
| engine_name / engine_version | VARCHAR NULL | |
| signature_version | VARCHAR(128) NULL | 病毒库版本 |
| threat_name | VARCHAR(255) NULL | 仅存检测名称，不存内容 |
| last_error | VARCHAR(1024) NULL | 脱敏错误 |
| started_at / completed_at / created_at | DATETIME(3) | |

扫描任务以 file id + SHA-256 绑定。文件内容、大小或哈希不一致时旧结果失效。普通管理员没有修改扫描结果或强制放行的接口。

压缩包扫描还必须执行文件数量、嵌套层级、解压后总量和压缩比限制；检测到加密 ZIP/RAR/7z 直接置为 `UNSCANNABLE`，整单 `BLOCKED`。

### 1.8 审批流

流程模板仍由 `oem_flow_templates` 和 `oem_flow_template_nodes` 表示，节点来源当前只允许 `SECTION_LEADER`、`DEPARTMENT_LEADER`，部门节点默认关闭。

**`oem_flow_instances`**

| 字段 | 类型 | 说明 |
|---|---|---|
| id | BIGINT UNSIGNED PK | |
| transfer_id | BIGINT UNSIGNED NOT NULL UNIQUE FK | 仅公司出站 |
| template_id | BIGINT UNSIGNED NOT NULL FK | 来源模板 |
| initiator_user_id | BIGINT UNSIGNED NOT NULL FK→users | 内部发送人，不再指向 OEM 账号 |
| initiator_section_id | BIGINT UNSIGNED NOT NULL | 发起时组织快照 |
| status | VARCHAR(16) NOT NULL | `IN_PROGRESS / COMPLETED / REJECTED` |
| current_sort_no | INT NULL | |
| created_at / updated_at | DATETIME(3) | |

**`oem_flow_tasks`**

除 instance id、approver user id、结果、原因和时间外，还要快照 `sort_no`、`approver_source` 和节点名称。实例创建时一次性解析所有已启用节点：

- 解析结果必须是启用内部账号；
- 审批人等于发送人时该节点无效，继续下一个已启用节点；
- 没有任何非本人的有效审批人时，整笔创建事务回滚；
- 模板或组织主管之后变化不改写已创建任务；
- 当前任务才能审批，重复或过期操作返回冲突。

审批实例只在传递单全部文件 `CLEAN` 后创建。审批通过与发布、通知入队在同一事务中完成。

### 1.9 下载会话与首次接收

**`oem_download_sessions`**

| 字段 | 类型 | 说明 |
|---|---|---|
| id | CHAR(36) PK | 逻辑下载会话 |
| file_id | BIGINT UNSIGNED NOT NULL FK | |
| actor_realm | VARCHAR(16) NOT NULL | `internal / oem` |
| internal_user_id | BIGINT UNSIGNED NULL | |
| oem_account_id | BIGINT UNSIGNED NULL | |
| recipient_side | TINYINT(1) NOT NULL | 是否为本传递接收方 |
| expected_size | BIGINT UNSIGNED NOT NULL | 会话创建时文件大小快照 |
| status | VARCHAR(16) NOT NULL | `STARTED / COMPLETED / ABORTED / EXPIRED` |
| created_at / completed_at / expires_at | DATETIME(3) | |

Range 请求需要在同一逻辑会话下累计不重叠的已完成区间；可使用独立 `oem_download_ranges` 保存区间并在事务中合并。只有覆盖完整的 `0..expected_size-1` 且响应正常结束才把会话标为 `COMPLETED`。仅请求部分 Range、断开连接或生成 URL 均不算完成。

首个 `recipient_side=1` 的完成会话使用条件更新写入文件：

```
first_recipient_download_at = completed_at
purge_due_at =
  AFTER_RELEASE                -> expires_at（发布时已计算）
  AFTER_FIRST_RECEIPT          -> completed_at + receipt_grace
  FIRST_RECEIPT_OR_DEADLINE    -> min(completed_at + receipt_grace, expires_at)
```

发送方、审批人和预览会话可写下载审计，但 `recipient_side=0`，不能触发清理。

### 1.10 审计与邮件队列兼容

`audit_logs` 增加或等价保存 `actor_realm`、`actor_account_id` 和账号标识快照，避免内部 user id 与 OEM account id 混淆。OEM 事件使用 `OEM_TRANSFER_*`、`OEM_SCAN_*`、`OEM_DOWNLOAD_*`、`OEM_PURGE_*` 等 action。

`email_outbox` 增加 `recipient_realm`、`recipient_account_id` 和可空 `oem_transfer_id`；OEM 收件人仍使用入队时快照的邮箱。发送前根据 realm 重新校验账号和厂商是否启用，失效则取消，不向被禁用账号发送新通知。

## 2. 状态与事务边界

### 2.1 公司 → OEM

```
DRAFT
  → 发送并冻结清单
SEALED + 扫描中
  → 全部 CLEAN
创建内部审批实例（PENDING）
  → APPROVED
RELEASED（OEM 厂商全部启用账号可下载）
  → 文件逐个首次接收或到期
PURGE_PENDING → PURGED
```

扫描失败进入 `BLOCKED`；审批驳回进入 `REJECTED`。两者都不得重新编辑后复用，修正后创建新传递单。

### 2.2 OEM → 公司

```
DRAFT
  → 发送并冻结清单
SEALED + 扫描中（approval_status=NOT_REQUIRED）
  → 全部 CLEAN
RELEASED（具备 view + download 权限的内部账号可下载）
  → 文件逐个首次接收或到期
PURGE_PENDING → PURGED
```

### 2.3 文件访问条件

接收方预览、下载和每个 Range 请求都重新判断：

```
transfer.lifecycle_status == RELEASED
&& file.payload_status == AVAILABLE
&& file.scan_status == CLEAN
&& 当前账号仍启用
&& 当前账号仍属于接收范围
&& 当前时间未超过策略有效期
```

发送方查看自己的附件、当前有效审批人审阅公司出站附件使用独立规则：文件必须已经 `CLEAN`，账号仍启用并且确为发送人或当前审批任务指派人；传递单可以处于 `DRAFT/SEALED`，但不能是 `BLOCKED/PURGED/STORAGE_LOST`。此类访问写审计且 `recipient_side=0`，不能触发首次接收。

任何前端隐藏、旧 token、已生成下载会话或历史通知都不能绕过实时判断。扫描完成前，发送方和审批人同样不能预览或下载。

### 2.4 物理删除事务

数据库事务不能与文件系统删除组成同一原子事务，因此采用可重试状态机：

1. 以条件更新认领到期文件并写 `PURGE_PENDING`、租约和审计；
2. 新下载立即被拒绝，已开始的活动下载持有短期租约，清理任务等待其结束；
3. 在 `OemStorageRoot` 边界内解析并删除实体文件；
4. 文件不存在时区分“已成功重复清理”和“非预期丢失”；只有已认领清理才可幂等完成；
5. 更新 `PURGED`、`purged_at` 并写审计；
6. 失败保留 `PURGE_PENDING`，按退避重试并告警。

## 3. 配置与独立存储

### 3.1 应用私有配置

外部 JSON 配置新增：

- `App:OemStorageRoot`：OEM 文件专用根目录；
- 扫描服务连接、超时和凭据等私有参数；凭据不得进入网页 `system_configs` 或日志。

启动时验证 `OemStorageRoot`：必须为绝对本地路径，不得与网站、程序、配置、现有 `StorageRoot`、备份目录互相包含，不允许重解析点；目录只授予应用池和受控扫描身份所需权限，禁止执行。

### 3.2 OEM 系统参数

继续使用 `system_configs` 表，但不再与供应商协作平台共用上传键：

| 配置键 | 说明 |
|---|---|
| `oem.upload.allowed_exts.internal_to_oem` | 公司出站格式白名单 |
| `oem.upload.allowed_exts.oem_to_internal` | OEM 入站格式白名单 |
| `oem.upload.max_file_size` | 单文件上限 |
| `oem.upload.max_transfer_size` | 单传递单总量 |
| `oem.upload.chunk_size` | 分片大小 |
| `oem.upload.max_concurrent_per_company` | 每厂商并发上传数 |
| `oem.upload.max_storage_per_company` | 每厂商在线占用上限 |
| `oem.scan.archive_max_entries` | 压缩包条目上限 |
| `oem.scan.archive_max_depth` | 嵌套层级 |
| `oem.scan.archive_max_expanded_bytes` | 解压后总量上限 |
| `oem.scan.blocked_retention_hours` | 病毒、不可扫描和最终失败文件的隔离保留时间 |
| `oem.transfer.draft_ttl_hours` | 长期未发送草稿的清理期限 |

这些参数由具备 `oem:file_policy_manage` 的内部管理员维护。加密压缩包拒绝规则不是可关闭开关。

### 3.3 不备份要求

现有维护脚本继续只复制 `App.StorageRoot`，不得把 `OemStorageRoot` 加入备份清单。配置文件会备份路径字符串，但不复制该路径中的文件。

部署和运维检查还必须证明操作系统备份、VSS、虚拟机/云盘快照、NAS 同步和第三方备份策略排除了 `OemStorageRoot`。应用代码只能保证自身备份脚本不复制，不能替代基础设施核对。

数据库恢复或应用启动后运行存储一致性检查：对本应存在且未删除的 OEM 文件核对安全路径、实体存在和大小；缺失时标记 `STORAGE_LOST` 并通知发送方。不得因数据库备份恢复而假设文件实体可恢复。

## 4. 权限点

### 4.1 内部账号

| code | 说明 |
|---|---|
| `oem:company_manage` | OEM 厂商管理 |
| `oem:account_manage` | OEM 账号管理 |
| `oem:transfer_create` | 创建公司出站传递单 |
| `oem:transfer_view` | 查看 OEM 传递单；也是 OEM 入站接收范围的一部分 |
| `oem:file_download` | 下载/预览 OEM 文件；与 view 同时存在才属于 OEM 入站接收方 |
| `oem:flow_approve` | 处理指派给本人的审批任务 |
| `oem:flow_template_manage` | 审批模板管理 |
| `oem:retention_template_manage` | 删除策略模板管理 |
| `oem:file_policy_manage` | 格式、大小、配额和压缩包限制管理 |
| `dept:leader_manage` | 维护组织主管 |

`oem:transfer_view` 不隐含公司/账号/模板管理；管理权限也不自动获得文件下载能力。

### 4.2 OEM 账号

OEM realm 使用固定能力：创建本厂商入站传递、查看本厂商收发传递、下载公司已发布给本厂商的文件、查看或下载自己厂商上传的文件。它不能绑定内部角色或获得跨厂商、审批、策略和管理权限。

## 5. 接口规划（`/api/v1/oem/*`）

### 5.1 认证

| 方法 | 路径 | 说明 |
|---|---|---|
| POST | `/oem/auth/login` | OEM 登录，签发 `realm=oem` token |
| POST | `/oem/auth/refresh` | OEM 刷新会话轮换 |
| POST | `/oem/auth/logout` | |
| PUT | `/oem/auth/password` | |

### 5.2 厂商、账号和组织主管

沿用原规划的 `/oem/companies`、`/oem/companies/{id}/accounts`、`/oem/accounts/{id}` 管理接口；内部 Admin 增加 `PUT /departments/{id}/leader`。所有写入在事务内重新校验权限和账号状态。

### 5.3 审批与删除策略模板

| 方法 | 路径 | 说明 |
|---|---|---|
| GET / POST | `/oem/flow-templates` | 审批模板列表/创建 |
| PUT | `/oem/flow-templates/{id}` | 名称、状态 |
| PUT | `/oem/flow-templates/{id}/nodes` | 整组替换未来实例节点 |
| GET / POST | `/oem/retention-templates` | 删除策略模板列表/创建 |
| PUT | `/oem/retention-templates/{id}` | 修改名称、时长和状态，仅影响未来发送 |

### 5.4 传递单

| 方法 | 路径 | 说明 |
|---|---|---|
| GET | `/oem/transfers` | 内部按 view 权限查看全部；OEM 只看本厂商 |
| POST | `/oem/transfers` | 创建草稿；方向从 realm 推导 |
| GET / PUT | `/oem/transfers/{id}` | 详情/编辑草稿 |
| DELETE | `/oem/transfers/{id}` | 仅发送人删除未发送草稿 |
| POST | `/oem/transfers/{id}/send` | 冻结清单并启动后续扫描/审批/自动发布 |
| GET | `/oem/transfers/{id}/files` | 文件列表及扫描、接收和清理状态 |

内部创建必须提交目标 `oemCompanyId`；OEM 创建时服务端使用账号所属厂商，忽略或拒绝客户端传入的其他厂商。发送要求至少一个合并完成的文件且没有活跃上传。

### 5.5 上传与扫描状态

| 方法 | 路径 | 说明 |
|---|---|---|
| POST | `/oem/transfers/{id}/uploads/init` | 初始化 OEM 独立上传会话 |
| PUT | `/oem/uploads/{sid}/chunks/{index}` | 上传分片 |
| GET | `/oem/uploads/{sid}` | 查询断点状态 |
| POST | `/oem/uploads/{sid}/merge` | 合并后创建隔离文件和扫描任务 |
| DELETE | `/oem/uploads/{sid}` | 放弃会话 |
| GET | `/oem/files/{id}/scan-status` | 返回脱敏扫描状态 |
| DELETE | `/oem/files/{id}` | 仅草稿发送方移除尚未冻结的文件 |

不提供扫描结果修改、病毒文件下载或强制发布接口。

### 5.6 审批

| 方法 | 路径 | 说明 |
|---|---|---|
| GET | `/oem/approvals/pending` | 当前内部用户待审批 |
| POST | `/oem/approvals/{taskId}/approve` | 仅当前指派人，携带期望任务/实例版本 |
| POST | `/oem/approvals/{taskId}/reject` | 必填原因，直接终止 |

### 5.7 预览、下载与回执

| 方法 | 路径 | 说明 |
|---|---|---|
| GET | `/oem/files/{id}/content` | 仅 CLEAN 且当前角色有权查看；预览不触发首次接收 |
| POST | `/oem/files/{id}/download-sessions` | 创建短时逻辑下载会话 |
| GET | `/oem/files/{id}/download` | 使用当前会话流式下载并支持 Range |
| GET | `/oem/files/{id}/download-status` | 当前用户查询会话完成状态 |

下载凭证使用同源 HttpOnly Cookie 或等价不进入 URL/日志的机制，绑定文件、账号、会话和短期有效期。每个 Range 请求都重新校验账号、厂商/权限、发布、扫描和过期状态。

## 6. 后台任务

至少需要三个独立、可多实例安全运行的任务：

1. **扫描 worker**：认领隔离文件、调用受控扫描器、记录结果并推动审批或发布；
2. **清理 worker**：计算并认领到期文件、废弃草稿和安全隔离文件，等待活动下载租约，物理删除并重试失败；
3. **一致性 worker/启动检查**：识别数据库引用但实体缺失的文件并置为 `STORAGE_LOST`。

worker 关闭或异常时文件保持不可用或待清理状态；健康/就绪信息应暴露扫描队列积压、扫描器不可用和持续清理失败，但不得泄漏文件名、磁盘路径或病毒内容。

## 7. 必要回归与验收

实现时至少覆盖：

- 两个 realm 不能跨业务线、跨 OEM 厂商或伪造方向；
- 公司出站在扫描和审批完成前 OEM 不可见、不可下载；
- OEM 入站无需人工审批，但扫描未通过时内部不可预览或下载；
- 上传人自审被拒绝；无合格审批人时不产生半成品实例；
- 发送后附件不能增删替换，模板变化不影响历史实例；
- 加密 ZIP/RAR/7z、病毒命中、扫描超时和不可扫描均失败关闭；
- 多 GB 文件使用分片和流式处理，不整文件进入应用内存；
- 任意一名合法接收方完整下载只触发一次首次接收；发送方、预览、部分 Range 和断线不触发；
- 多附件按文件独立记录首次接收和删除时间，不因下载其中一个而误删其他文件；
- 宽限期内可重试，`PURGE_PENDING` 后新下载被拒绝，物理删除失败可恢复重试；
- 病毒/不可扫描文件和长期未发送草稿按独立期限清理，不受发送方选择的保留模板控制；
- OEM 文件不进入现有离线备份及基础设施快照核对范围；
- 数据库恢复但文件缺失时进入 `STORAGE_LOST`，不显示可下载；
- 文件实体删除后，审批、扫描、下载和删除审计仍可查询且不含秘密。

## 8. 已确认决策与待填参数

核心业务方向、接收范围、审批边界、双向扫描、加密压缩包拒绝、管理员策略模板、首次接收触发、不建立版本和 OEM 文件不备份均已确认。

实施前只需补齐参数值：初始格式白名单、数 GB 的具体上限、并发和厂商配额、默认宽限时长、废弃草稿及隔离文件清理期限、扫描器产品与部署方式、病毒库更新频率、超时和重试次数。参数确认不得改变本文件定义的权限、失败关闭、独立存储和不备份边界。

---

**文档结束**
