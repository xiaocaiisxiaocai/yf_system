# OEM 绘图平台 - 数据库设计与接口规划

**版本**：V0.3（设计草案，异常与恢复契约已补充，尚未开发）
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

OEM token 使用 realm=oem，刷新会话与内部/供应商会话隔离，不能解析成现有用户。

V0.3 选定独立 oem_refresh_tokens 表：id、account_id（FK→oem_accounts）、session_id、token_hash（唯一）、expires_at、revoked、轮换关联及创建/撤销时间；复用轮换和重放撤销算法，Cookie 名称/路径独立。现有 LoginRateLimiter 是内存 IP 与 IP+登录名限流，复用时账号桶增加 realm，登录名规范化与账号查找一致，总 IP 桶可共享。数据库失败次数分别锁定 users / oem_accounts 的具体账号行，不按裸登录名共用计数。

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
| approval_status | 派生字段，不独立写入 | NOT_REQUIRED / WAITING_SCAN / PENDING / APPROVAL_BLOCKED / APPROVED / REJECTED / CANCELLED，由审批实例映射 |
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
- `BLOCKED`：病毒、不可扫描或耗尽重试后的最终安全失败；
- `CANCELLED`：管理员带原因终止尚未发布的传递。

传递单仅持久化业务状态。扫描汇总、部分/全部删除及缺失数量按 §2.5 派生，不用文件状态覆盖业务结果；移除单据层级 EXPIRED、PURGE_PENDING、PURGED、STORAGE_LOST 状态及独立 scan_status 列。

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
| payload_status | VARCHAR(24) NOT NULL | QUARANTINED / PROMOTING / AVAILABLE / PURGE_PENDING / PURGED / STORAGE_LOST / MISSING_UNVERIFIED |
| scan_status | VARCHAR(16) NOT NULL | 每文件扫描状态 |
| first_recipient_download_at | DATETIME(3) NULL | 首名接收方完整下载 |
| purge_due_at | DATETIME(3) NULL | 按模板计算的删除时间 |
| purge_claimed_at | DATETIME(3) NULL | 清理租约 |
| purge_lease_owner / purge_lease_until | VARCHAR(64) / DATETIME(3) NULL | 清理所有者和截止时间 |
| purge_attempt_count / purge_next_attempt_at | INT / DATETIME(3) NULL | 清理重试 |
| purge_reason / purge_last_error | VARCHAR(64) / VARCHAR(1024) NULL | 原因及脱敏错误 |
| file_uuid | CHAR(36) NOT NULL UNIQUE | 不重复的实体标识，防数据库恢复后数值 ID 重用 |
| concurrency_version | BIGINT UNSIGNED NOT NULL | 条件更新及 fencing |
| purged_at | DATETIME(3) NULL | 实体删除成功时间 |
| created_at / updated_at | DATETIME(3) | |

删除按文件独立触发。未下载文件仍按发布期限或自身首次接收时间处理；页面汇总文件状态，不改变传递单历史业务结果。

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
| file_sha256 / file_size_bytes | CHAR(64) / BIGINT UNSIGNED NOT NULL | 扫描绑定的哈希及大小快照 |
| next_attempt_at / concurrency_version | DATETIME(3) NULL / BIGINT UNSIGNED | 退避与旧扫描 worker fencing |
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

模板表 oem_flow_templates 包含 id、name、status、concurrency_version、created_by、created_at、updated_at；节点表 oem_flow_template_nodes 包含 id、template_id、sort_no、name、approver_source、enabled、approval_mode（V1 仅 SINGLE）。唯一索引 (template_id, sort_no)，引用的模板不得物理删除。节点来源仅 SECTION_LEADER、DEPARTMENT_LEADER，部门节点默认关闭。发送快照用版本校验防止混读。

**`oem_flow_instances`**

| 字段 | 类型 | 说明 |
|---|---|---|
| id | BIGINT UNSIGNED PK | |
| transfer_id | BIGINT UNSIGNED NOT NULL UNIQUE FK | 仅公司出站 |
| template_id | BIGINT UNSIGNED NOT NULL FK | 来源模板 |
| initiator_user_id | BIGINT UNSIGNED NOT NULL FK→users | 内部发送人，不再指向 OEM 账号 |
| initiator_section_id | BIGINT UNSIGNED NOT NULL | 发起时组织快照 |
| status | VARCHAR(24) NOT NULL | WAITING_SCAN / IN_PROGRESS / APPROVAL_BLOCKED / COMPLETED / REJECTED / CANCELLED |
| template_snapshot | JSON NOT NULL | 发送时完整模板、组织路径、节点和解析结果 |
| blocked_reason | VARCHAR(128) NULL | 审批阻断原因 |
| concurrency_version | BIGINT UNSIGNED NOT NULL | 乐观并发版本 |
| current_sort_no | INT NULL | |
| created_at / updated_at | DATETIME(3) | |

**`oem_flow_tasks`**

任务字段：id、instance_id、sort_no、node_name、approver_source、approver_user_id、status（WAITING/PENDING/APPROVED/REJECTED/SUPERSEDED/CANCELLED）、reason、decided_at、created_at、concurrency_version、replaces_task_id。发送事务内解析全部启用节点并创建 WAITING 任务：

- 每个节点必须解析为启用、有审批权限且非发送人的内部账号，任一失败整个发送事务回滚、保留草稿，不得跳过；
- 发起人必须归属有效课别；默认模板下课别主管自发起会被阻断，部门节点关闭时不自动启用，备用路由待业务确认；
- 模板或组织主管之后变化不改写已创建任务；
- 当前任务才能审批，重复或过期操作返回冲突。

发送成功已有 WAITING_SCAN 实例；全部文件 CLEAN 且移动完成后才激活为 IN_PROGRESS，首个任务置 PENDING。激活和每个节点处理时重验快照人员资格；失效置 APPROVAL_BLOCKED，通知管理员。账号禁用/撤权时主动阻断，由巡检兜底。业务版本、快照和活跃上传校验均包含在发送事务中；重复 send 使用版本/唯一 transfer_id 返回已有结果，不重复建实例。

oem:approval_recover 可对异常未完成任务重新指派或终止未发布单据。必须填写原因；新人合格且非发送人，旧任务置 SUPERSEDED，新任务用 replaces_task_id 关联；记录操作者、原/新审批人和原因。已完成节点不变，每节点仅一条有效任务，改派和审批通过行锁及版本校验互斥。不得覆盖旧 approver 字段抹除历史。终止将实例与未完成任务置 CANCELLED，按安全清理期限处理附件，管理员不能直接代审批通过。

重新指派后重新校验全部未完成节点；扫描未完成继续 WAITING_SCAN，扫描完成且无失效节点才恢复 IN_PROGRESS，否则仍 APPROVAL_BLOCKED。扫描最终失败将等待实例及任务取消。审批通过、发布、期限初始化与通知入队在同一事务中完成。

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
  KEEP                        -> null（仅记录首次接收，不自动删除）
  AFTER_RELEASE                -> expires_at（发布时已计算）
  AFTER_FIRST_RECEIPT          -> completed_at + receipt_grace
  FIRST_RECEIPT_OR_DEADLINE    -> min(completed_at + receipt_grace, expires_at)
```

发送方、审批人和预览会话可写下载审计，但 `recipient_side=0`，不能触发清理。

发布事务内为全部文件初始化 purge_due_at：KEEP、AFTER_FIRST_RECEIPT 为空；AFTER_RELEASE、FIRST_RECEIPT_OR_DEADLINE 均设为 expires_at。首次接收后组合策略取较早时间，重复回执不能延长。使用数据库 UTC 时间，单条件模板禁止填写无关时长，有效时长必须为正且有上限。纯首次接收模式无人下载时不自动到期，界面明示；绝对期限可以截短宽限期。

下载会话另存 file_uuid、file_sha256、purpose（DOWNLOAD/PREVIEW/REVIEW）、last_progress_at、absolute_deadline、concurrency_version。recipient_side 由身份、方向和 purpose 推导且每次请求重验，不接受客户端声明；不能跨账号或跨会话汇总字节。

oem_download_ranges：id、session_id、start_offset、end_offset（闭区间），索引 (session_id, start_offset)。每次只接受单段合法 Range，完成响应后事务锁定会话，将重叠/相邻区间取并集，不累加重复字节。限制合并后区间数、请求频率和并行数；超过限制拒绝新的请求，不丢弃已有证据或误报完成。HEAD、304、416、部分发送失败均不计完成；完整 GET 也必须完成响应才记全区间。V1 拒绝零字节文件，避免空区间语义。

活动请求建立 oem_download_leases：id、session_id、owner、started_at、last_progress_at、lease_until、hard_deadline、status。请求准入与清理认领锁定同一文件，避免检查与打开之间穿透；租约有 fencing，失去租约立即停止读取。截止时间取会话绝对期限、请求最长时长、到期后排空期限的最早值，心跳不可延长硬截止。应用多实例以数据库租约协调，删除遇到仍被占用的文件应重试，不能冒报 PURGED。

可参考现有 MediaGrantService 的签名及 HttpOnly Cookie 机制，但 OEM 凭证须使用独立用途类型和 Cookie 名称，额外绑定 realm、账号、登录会话、文件 UUID、逻辑下载会话 ID、purpose、有效期。短期凭证续签仍受逻辑会话硬截止限制，不能沿用媒体的五分钟有效期作为数 GB 下载的完整生命周期。

### 1.10 审计与邮件队列兼容

`audit_logs` 增加或等价保存 `actor_realm`、`actor_account_id` 和账号标识快照，避免内部 user id 与 OEM account id 混淆。OEM 事件使用 `OEM_TRANSFER_*`、`OEM_SCAN_*`、`OEM_DOWNLOAD_*`、`OEM_PURGE_*` 等 action。

`email_outbox` 增加 `recipient_realm`、`recipient_account_id` 和可空 `oem_transfer_id`；OEM 收件人仍使用入队时快照的邮箱。发送前根据 realm 重新校验账号和厂商是否启用，失效则取消，不向被禁用账号发送新通知。

还须按事件类型重验状态与资格：发布邮件检查 RELEASED、查看/下载权限；审批邮件检查当前任务；异常邮件检查发送人或异常处置资格。所有邮件检查渠道规则，不能仅检查启用状态。内部入站范围为所有厂商，不按组织过滤；发布事件站内可见，邮件对象/渠道须显式配置，不能从 view 权限直接推导全员群发。未配置默认不群发邮件；审批只通知当前处理人，阻断通知发送人和异常处置管理员，安全告警不向未获得文件访问权的接收方暴露内容。发布通知以 transfer+event+recipient realm/id 去重并在发布事务中入队。

## 2. 状态与事务边界

### 2.1 公司 → OEM

```
DRAFT
  → 发送并冻结清单
SEALED + 扫描中
  → 全部 CLEAN
激活发送时已建实例（失效则 APPROVAL_BLOCKED）
  → APPROVED
RELEASED（OEM 厂商全部启用账号可下载）
  → 文件逐个首次接收或到期
文件 PURGE_PENDING → PURGED；传递单保留 RELEASED
```

扫描暂时故障保持 SEALED、有限重试；最终失败才 BLOCKED。审批驳回为 REJECTED，终止为 CANCELLED。终态文件修正须新建传递单。

### 2.2 OEM → 公司

```
DRAFT
  → 发送并冻结清单
SEALED + 扫描中（approval_status=NOT_REQUIRED）
  → 全部 CLEAN
RELEASED（具备 view + download 权限的内部账号可下载）
  → 文件逐个首次接收或到期
文件 PURGE_PENDING → PURGED；传递单保留 RELEASED
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

发送侧及审批审阅同样要求 CLEAN、payload_status=AVAILABLE、未到 purge_due_at、账号/厂商启用且具备对应权限。DRAFT/SEALED 内容只供实际发送人及已激活任务的审批人；RELEASED 后 OEM 同厂商共享发送侧查看，内部发送人仍需 view+download。BLOCKED/REJECTED/CANCELLED 禁止内容访问。发送侧和审批审阅 recipient_side=0，仅实际草稿发送人可修改附件。

任何前端隐藏、旧 token、已生成下载会话或历史通知都不能绕过实时判断。扫描完成前，发送方和审批人同样不能预览或下载。

### 2.4 物理删除事务

数据库事务不能与文件系统删除组成同一原子事务，因此采用可重试状态机：

1. 以条件更新认领到期文件并写 `PURGE_PENDING`、租约和审计；
2. 新下载立即被拒绝，已开始下载限时排空，到达空闲/绝对/排空截止时间即取消请求、释放句柄；
3. 在 `OemStorageRoot` 边界内解析并删除实体文件；
4. 按 §2.6 校验持久操作凭证；仅数据库认领记录加实体缺失不足以证明正常删除；
5. 更新 `PURGED`、`purged_at` 并写审计；
6. 失败保留 `PURGE_PENDING`，按退避重试并告警。

### 2.5 汇总规则与发布互斥

扫描任务使用 PENDING/RUNNING/CLEAN/INFECTED/ERROR/UNSCANNABLE；文件对外状态将 RUNNING 映射为 SCANNING，重试中的 ERROR 不代表终态失败。单据扫描汇总只读派生：存在 INFECTED 优先，其次 UNSCANNABLE，再次耗尽重试的 ERROR；否则存在运行任务为 SCANNING、存在待执行/重试任务为 PENDING，非空附件全部 CLEAN 才为 CLEAN。列表返回各状态数量和失败原因，不靠一个总状态掩盖其他问题。

发布必须在文件集已冻结、无活跃上传、所有文件 CLEAN 且 AVAILABLE、发送账号/厂商有效，以及公司出站审批完成时通过条件更新执行一次。草稿扫描完成不会自动发送；SEALED 时若所有扫描早已完成，send 后可立即推进。扫描完成与 send 竞争由 transfer 行锁和版本检查串行化，不能漏推进或重复发布。

文件 payload_status 是存储事实，业务 lifecycle_status 是流程事实。一个文件进入 PURGE_PENDING/PURGED，不改变 RELEASED；其他未到各自 purge_due_at 的 AVAILABLE 文件继续可下载。详情返回 available_count、purge_pending_count、purged_count、missing_count，全部文件删除也保留 RELEASED 及历史审批结果，并显示“全部文件已清理”。到期接口按文件 purge_due_at 拒绝访问，不等待清理 worker 扫描。发送侧也遵循该规则。

### 2.6 移动恢复与独立操作凭证

oem_file_operations 表：operation_uuid（PK）、file_uuid、file_sha256、size_bytes、kind（PROMOTE/PURGE）、source_path、target_path、status（PREPARED/RUNNING/COMPLETED/FAILED）、lease_owner、lease_until、concurrency_version、attempt_count、next_attempt_at、reason、created_at、completed_at。路径为 OEM 根目录内的服务端相对路径。移动与清理共享文件级锁和 fencing，旧 worker 不得在租约丢失后更新结果。

PROMOTE 先提交 PREPARED 和确定的两处路径，文件置 PROMOTING，再执行同卷不覆盖重命名，最后提交路径和 AVAILABLE。启动/恢复先处理操作再判缺失：只有源存在则继续移动；只有目标存在且大小/哈希一致则补提交；两者都存在则验证后受控去重，不覆盖不同内容；两者都不存在且无删除证据则分类缺失。后台校验与活动移动互斥，扫描结果只能绑定实际哈希一致的文件。

另设 App:OemOperationJournalRoot，保存不含任何文件内容的追加式操作凭证，独立于数据库恢复范围；记录 operation_uuid、file_uuid、哈希、大小、操作类型、意图/完成阶段、原因、UTC 时间与完整性校验。凭证需受限 ACL、持久刷新和备份/保留策略，不能被恢复旧业务数据库的流程回滚。保留时间不得短于允许恢复的最旧数据库备份，凭证不可用或损坏时停止新的物理删除并告警。

PURGE 在删除前持久写入意图，删除成功后持久写入完成凭证，再提交数据库 PURGED。崩溃后的状态判断：完成凭证可重放正常删除；仅意图且实体仍在可重试；仅意图且实体不在只能说明结果不确定，不把意图当完成证据。即使加入日志，也不能把文件系统与日志的非原子窗口描述为完全可判定。

恢复旧数据库后先扫描凭证、核对文件 UUID 与哈希并重放完成操作；已有完成删除凭证的文件不得重新发布。没有证据区分备份后正常删除与异常缺失时置 MISSING_UNVERIFIED，只有明确异常证据才置 STORAGE_LOST。凭证和存储一起损坏时仍可能无法还原原因，这是不备份文件前提下的明确局限。恢复入口在核对结束前不开放 OEM 内容接口。

凭证只留操作元数据，不留原文件、解压内容、分片或可恢复文件密钥。逻辑删除不承诺介质级擦除，也不能撤回接收方已保存副本。

## 3. 配置与独立存储


### 3.1 应用私有配置

外部 JSON 配置新增：

- `App:OemStorageRoot`：OEM 文件专用根目录；
- `App:OemOperationJournalRoot`：独立操作凭证目录，与文件存储和业务数据库恢复目标分离；
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
| `oem.scan.archive_max_ratio` | 压缩比上限 |
| `oem.upload.session_ttl_hours` | 上传会话绝对有效期 |
| `oem.download.max_ranges_per_session` | 合并后区间数量上限 |
| `oem.download.max_parallel_per_session` | 每会话并行请求上限 |
| `oem.download.max_requests_per_minute` | 每账号下载请求速率 |
| `oem.download.session_ttl_minutes` | 逻辑会话绝对寿命 |
| `oem.download.idle_timeout_seconds` | 无进展取消期限 |
| `oem.download.max_duration_minutes` | 单请求最长时长 |
| `oem.download.purge_drain_minutes` | 到期后活动请求排空期限 |
| `oem.scan.blocked_retention_hours` | 病毒、不可扫描和最终失败文件的隔离保留时间 |
| `oem.transfer.draft_ttl_hours` | 长期未发送草稿的清理期限 |

这些参数由具备 `oem:file_policy_manage` 的内部管理员维护。加密压缩包拒绝规则不是可关闭开关。

max_storage_per_company 统计关联该厂商的双向文件，包括草稿、隔离、可用、待物理删除及未完成上传预留额度。init 在事务内原子预留，merge 转移而不重复计费，实际删除完成/放弃上传后才释放；并发上传不可超售。合并、扫描解压和打包另有全局物理空间预留预算，磁盘不足时拒绝新任务，不挪用业务额度充当真实磁盘余量。MISSING_UNVERIFIED 的额度经对账确认后调整，不自动作为正常删除释放。

### 3.3 不备份要求

现有维护脚本继续只复制 `App.StorageRoot`，不得把 `OemStorageRoot` 加入备份清单。配置文件会备份路径字符串，但不复制该路径中的文件。

部署和运维检查还必须证明操作系统备份、VSS、虚拟机/云盘快照、NAS 同步和第三方备份策略排除了 `OemStorageRoot`。应用代码只能保证自身备份脚本不复制，不能替代基础设施核对。

数据库恢复或启动时先按 §2.6 恢复移动任务并重放独立操作凭证，再检查实体。不把历史正常删除一律标 STORAGE_LOST；证据不足使用 MISSING_UNVERIFIED。OEM 文件不备份不等于不持久保存操作证据。

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
| `oem:approval_recover` | 异常任务改派与终止未发布传递，不允许代审批通过 |
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

下表路径相对于 /api/v1；写入事务内重新校验权限、状态和期望版本。

| 方法 | 路径 | 权限/用途 |
|---|---|---|
| GET / POST | /oem/companies | oem:company_manage，列表/创建 |
| GET / PUT | /oem/companies/{id} | oem:company_manage，详情/编辑 |
| PUT | /oem/companies/{id}/status | oem:company_manage，启用/禁用 |
| GET / POST | /oem/companies/{id}/accounts | oem:account_manage，列表/创建 |
| GET / PUT | /oem/accounts/{id} | oem:account_manage，详情/编辑 |
| PUT | /oem/accounts/{id}/status | oem:account_manage，启用/禁用 |
| PUT | /oem/accounts/{id}/password | oem:account_manage，重置并撤销会话 |
| PUT | /departments/{id}/leader | dept:leader_manage，设置/清空主管 |
| GET / PUT | /oem/file-policies | oem:file_policy_manage，参数管理 |

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

列表/详情/通知的数据范围同样按方向和阶段过滤：OEM 只看本厂商发送侧单据及公司已发布给本厂商的单据，不能看到公司未发布草稿、扫描或审批详情。内部 view 提供元数据访问，内容还需 download 或当前审批任务资格；发送人的草稿维护不因此授权接收全部入站内容。同厂商其他员工在发布后共享发送侧文件，未发布草稿仍仅实际发送人维护和读取。存在历史引用的厂商/账号禁用而非物理删除。

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
| POST | /oem/approvals/{taskId}/reassign | oem:approval_recover；异常未完成任务，原因、新审批人和任务/实例版本必填 |
| POST | /oem/transfers/{id}/cancel | oem:approval_recover；未发布传递，原因及传递/实例版本必填 |

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
3. **一致性 worker/启动检查**：先恢复移动/删除操作，再按证据分类缺失文件，避免移动窗口误判。

worker 异常时新待扫描文件保持不可用，到期文件由请求时限阻断，已发布且仍有效文件不因清理 worker 停止而一律失效。内部运维就绪检查暴露扫描积压、扫描器不可用、凭证故障和清理失败，不在公共健康响应泄漏文件名、路径或病毒内容。

## 7. 必要回归与验收

实现时至少覆盖：

- 两个 realm 不能跨业务线、跨 OEM 厂商或伪造方向；
- 公司出站在扫描和审批完成前 OEM 不可见、不可下载；
- OEM 入站无需人工审批，但扫描未通过时内部不可预览或下载；
- 上传人自审、缺少课别及任一启用节点解析失败时发送回滚；模板/组织变更不改变发送快照；
- 审批人禁用/撤权可阻断、改派或终止；改派与迟到审批竞争只允许一次有效决议；
- 发送后附件不能增删替换，模板变化不影响历史实例；
- 加密 ZIP/RAR/7z、病毒命中、扫描超时和不可扫描均失败关闭；
- 多 GB 文件使用分片和流式处理，不整文件进入应用内存；
- 任意一名合法接收方完整下载只触发一次首次接收；发送方、预览、部分 Range 和断线不触发；
- 多附件按文件独立记录首次接收和删除时间，不因下载其中一个而误删其他文件；
- 宽限期内可重试，`PURGE_PENDING` 后新下载被拒绝，物理删除失败可恢复重试；
- 病毒/不可扫描文件和长期未发送草稿按独立期限清理，不受发送方选择的保留模板控制；
- OEM 文件不进入现有离线备份及基础设施快照核对范围；
- 数据库恢复后，正常删除由凭证重放，已证实丢失与原因不明分别呈现且禁止下载；
- 文件实体删除后，审批、扫描、下载和删除审计仍可查询且不含秘密。

还须以故障注入验证：移动前/后及数据库提交前后崩溃可幂等恢复；删除意图/完成凭证与旧库恢复的不同组合正确分类；扫描暂时错误能重试、最终错误才阻断；组合策略无人下载也到期；部分删除不影响其他文件；超量/重叠 Range、不同账号零散区间、HEAD、断线不误报收件；慢客户端被绝对截止取消；并发配额预留不超售；跨 realm 同名账号不共享账号限流桶；通知发送前撤权能够取消。

## 8. 已确认决策与待填参数

核心业务方向、接收范围、审批边界、双向扫描、加密压缩包拒绝、管理员策略模板、首次接收触发、不建立版本和 OEM 文件不备份均已确认。

实施前补齐初始白名单、文件/空间/并发上限、宽限及各类租约时长、隔离清理期限、扫描器及更新/超时/重试参数。主管本人和非课别人员的备用路由、邮件收件规则仍需业务确认，确认前采用明确阻断及不自动群发的默认行为。操作凭证部署/保留期限须覆盖数据库恢复窗口，不能承诺所有证据一起丢失后仍可还原原因。

---

**文档结束**
