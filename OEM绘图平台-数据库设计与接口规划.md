# OEM 绘图平台 - 数据库设计与接口规划

**版本**：V0.1（设计草案，尚未开发）
**更新日期**：2026-09-16
**前置文档**：[OEM绘图平台-需求文档.md](./OEM绘图平台-需求文档.md)

本文档只覆盖 OEM 平台新增的表和接口，技术约定（Dapper/MySqlConnector 参数化 SQL、Minimal API、camelCase JSON、Bearer + 刷新会话、`yf_schema_migrations` 版本化）沿用 `server_dotnet` 现有实现，不重复描述。

## 0. 与现有 schema 的关系

| 现有对象 | 处理方式 |
|---|---|
| `departments`（事业部/部门/课别三级） | 复用，新增 `leader_account_id` 列，见 §1.1 |
| `users`（内部+供应商单表登录） | 复用内部账号部分；OEM 外部账号不进这张表，见 §1.2 |
| `files` / `upload_sessions` | 复用分片上传、断点续传、合并校验的全部机制；OEM 一侧的文件挂靠对象从 `project_id` 换成 `oem_submission_id`，见 §1.4 |
| `email_outbox` / `audit_logs` | 复用，新增 OEM 相关 `event_type` / `action` 枚举值 |
| `permissions` / `roles` / `role_permissions` | 复用表结构，新增 `oem:*` 权限点，见 §3 |

## 1. 新增与变更表

### 1.1 组织架构变更

**`departments`** 新增列：

| 字段 | 类型 | 说明 |
|---|---|---|
| leader_account_id | BIGINT UNSIGNED NULL，FK→users(id) | 该组织节点（课别/部门）当前主管；不建强制外键约束（沿用本库对人员引用列的一贯做法），应用层校验目标账号为启用的内部账号 |

不新增变更历史表；主管变更直接覆盖，按需要写入 `audit_logs`（`action=DEPT_LEADER_CHANGE`）。

### 1.2 OEM 企业与账号

**`oem_companies`** — OEM 代工厂商主体（与 `suppliers` 平级，完全独立建表）

| 字段 | 类型 | 说明 |
|---|---|---|
| id | BIGINT UNSIGNED PK | |
| name | VARCHAR(128) NOT NULL | 厂商名称 |
| contact_name / contact_phone / contact_email | VARCHAR NULL | 联系方式 |
| remark | VARCHAR(512) NULL | |
| status | VARCHAR(16) NOT NULL DEFAULT 'ACTIVE' | ACTIVE / DISABLED |
| created_by / created_at / updated_at | | |

**`oem_accounts`** — OEM 厂商的登录账号，与内部 `users` 表物理隔离

| 字段 | 类型 | 说明 |
|---|---|---|
| id | BIGINT UNSIGNED PK | |
| employee_no | VARCHAR(64) NOT NULL UNIQUE | 登录账号标识（独立命名空间，不与 `users.employee_no` 冲突校验） |
| password_hash | VARCHAR(255) NOT NULL | Argon2id，复用现有密码策略 |
| real_name | VARCHAR(64) NOT NULL | |
| email | VARCHAR(128) NOT NULL | |
| oem_company_id | BIGINT UNSIGNED NOT NULL FK→oem_companies | 同一厂商下全部启用账号共享该厂商的提交范围，模式与供应商账号一致 |
| status | VARCHAR(16) NOT NULL DEFAULT 'ACTIVE' | ACTIVE / DISABLED |
| must_change_password | TINYINT(1) NOT NULL DEFAULT 1 | |
| failed_login_attempts / locked_until | | 复用现有登录限流字段设计 |
| created_by / created_at / updated_at | | |

登录、JWT 签发、刷新会话轮换沿用 Identity 模块现有机制，但签发的 token 带独立 `realm=oem` 声明；鉴权中间件按 `realm` 拒绝跨界访问 `/api/v1/oem/*` 与供应商协作平台路由的互相调用。

### 1.3 审批流

**`oem_flow_templates`** — 流程模板

| 字段 | 类型 | 说明 |
|---|---|---|
| id | BIGINT UNSIGNED PK | |
| name | VARCHAR(64) NOT NULL | |
| status | VARCHAR(16) NOT NULL DEFAULT 'ACTIVE' | ACTIVE / DISABLED；禁用后不可再发起新实例，已有实例不受影响 |
| created_by / created_at / updated_at | | |

**`oem_flow_template_nodes`** — 模板节点（线性，按 `sort_no` 排序）

| 字段 | 类型 | 说明 |
|---|---|---|
| id | BIGINT UNSIGNED PK | |
| template_id | BIGINT UNSIGNED NOT NULL FK | |
| sort_no | INT NOT NULL | 节点顺序 |
| approver_source | VARCHAR(32) NOT NULL | 当前只有 `SECTION_LEADER`（提交人所在课别主管）、`DEPARTMENT_LEADER`（提交人所在部门主管）两种，按提交人组织路径动态解析，不存具体审批人 |
| approval_mode | VARCHAR(16) NOT NULL DEFAULT 'SINGLE' | 预留：SINGLE（当前唯一支持）/ ALL（会签）/ ANY（或签），后两者暂不实现 |
| enabled | TINYINT(1) NOT NULL DEFAULT 1 | 关闭的节点执行时被彻底跳过 |
| created_at / updated_at | | |

索引：`KEY (template_id, sort_no)`。

**当前默认模板**：两个节点，节点1 `approver_source=SECTION_LEADER, enabled=1`；节点2 `approver_source=DEPARTMENT_LEADER, enabled=0`（默认关闭，见需求文档 §3.1.2）。

**`oem_flow_instances`** — 流程实例，绑定一次提交

| 字段 | 类型 | 说明 |
|---|---|---|
| id | BIGINT UNSIGNED PK | |
| template_id | BIGINT UNSIGNED NOT NULL FK | 发起时快照使用的模板；模板之后变更不影响已发起实例 |
| submission_id | BIGINT UNSIGNED NOT NULL FK→oem_submissions | |
| initiator_id | BIGINT UNSIGNED NOT NULL FK→oem_accounts | 发起人 |
| initiator_department_id | BIGINT UNSIGNED NOT NULL | 发起时快照的提交人课别，供审批人解析使用，不随组织架构后续调整变化 |
| status | VARCHAR(16) NOT NULL DEFAULT 'IN_PROGRESS' | IN_PROGRESS / COMPLETED / REJECTED |
| current_node_id | BIGINT UNSIGNED NULL FK→oem_flow_template_nodes | 当前所在的启用节点；全部启用节点通过后置空并置 COMPLETED |
| created_at / updated_at | | |

**`oem_flow_tasks`** — 每个节点产生的审批任务

| 字段 | 类型 | 说明 |
|---|---|---|
| id | BIGINT UNSIGNED PK | |
| instance_id | BIGINT UNSIGNED NOT NULL FK | |
| node_id | BIGINT UNSIGNED NOT NULL FK | |
| approver_id | BIGINT UNSIGNED NOT NULL FK→users | 按 `approver_source` 动态解析后固化的审批人，解析失败（该组织节点没有配置 `leader_account_id`）时任务创建失败，实例回退报错，不允许空审批人 |
| result | VARCHAR(16) NOT NULL DEFAULT 'PENDING' | PENDING / APPROVED / REJECTED |
| reason | VARCHAR(1024) NULL | 驳回原因 |
| decided_at | DATETIME(3) NULL | |
| created_at | DATETIME(3) | |

索引：`KEY (instance_id)`、`KEY (approver_id, result)`（用于"待我审批"列表）。

状态机（与需求文档 §3.1.4 一致）：

```
oem_flow_instances.status = IN_PROGRESS
  → 当前节点任务 APPROVED
      → 若存在下一个 enabled=1 的节点：current_node_id 前进，创建新任务
      → 否则：status=COMPLETED，current_node_id=NULL
  → 当前节点任务 REJECTED
      → status=REJECTED，流程终止；提交人需发起新的 oem_submissions + oem_flow_instances
```

### 1.4 提交对象与文件

**`oem_submissions`** — 提交单（审批流绑定的业务对象）

| 字段 | 类型 | 说明 |
|---|---|---|
| id | BIGINT UNSIGNED PK | |
| oem_company_id | BIGINT UNSIGNED NOT NULL FK | 归属的 OEM 厂商，用于数据范围过滤 |
| title | VARCHAR(128) NOT NULL | |
| description | VARCHAR(1024) NULL | |
| status | VARCHAR(16) NOT NULL DEFAULT 'DRAFT' | DRAFT / SUBMITTED / APPROVED / REJECTED（与关联的 `oem_flow_instances.status` 保持一致，冗余存放便于列表查询不必联表） |
| created_by | BIGINT UNSIGNED NOT NULL FK→oem_accounts | |
| created_at / updated_at | | |

已确认为通用文件+审批壳，不区分具体业务类型（需求文档 §6），因此不再规划额外分类/条件字段。

**`files`** 表新增可空列 `oem_submission_id BIGINT UNSIGNED NULL FK→oem_submissions`，与现有 `project_id` 互斥（应用层校验：一条文件记录只能属于其中一个）。分片上传接口新增 `oem/uploads/*` 前缀路由，复用 `UploadService`/`FileStorage` 的全部逻辑，只是把落库目标列从 `project_id` 换成 `oem_submission_id`，合并、校验、断点续传、命名锁、崩溃安全标记均不改动。

## 2. 系统参数调整

| 参数 | 当前值 | OEM 场景调整 |
|---|---|---|
| upload.max_file_size | 现有值 | 上调至满足几 GB 文件（具体上限待业务方给出文件规模预估后定） |
| upload.chunk_size | 现有值（256KB~64MB 范围内） | 建议调至 16~32MB，减少多 GB 文件的请求数 |

两个参数复用现有 `system_configs` 存储机制，不新增参数表；已确认 OEM 与供应商协作平台共用同一套全局参数，不单独拆分两套配置键。

## 3. 权限点

| code | 说明 |
|---|---|
| oem:company_manage | OEM 厂商增改禁用 |
| oem:account_manage | OEM 厂商账号管理 |
| oem:submission_create | 创建/编辑提交单（仅 OEM 账号） |
| oem:submission_view | 查看提交单（内部/OEM 账号按数据范围过滤） |
| oem:flow_template_manage | 流程模板设计（仅内部管理员） |
| oem:flow_approve | 审批任务处理（内部账号，且必须是该任务当前指派的审批人） |
| dept:leader_manage | 维护组织节点主管（复用现有组织管理权限体系，新增这一个点） |

OEM 账号只能绑定 `oem:submission_create`/`oem:submission_view` 一类的固定最小权限集，不能通过内部角色获得内部数据权限，机制与供应商账号一致。

## 4. 接口规划（`/api/v1/oem/*`）

### 4.1 认证

| 方法 | 路径 | 说明 |
|---|---|---|
| POST | /oem/auth/login | OEM 账号登录，签发带 `realm=oem` 声明的 token |
| POST | /oem/auth/refresh | 刷新会话轮换，机制同现有 Identity 模块 |
| POST | /oem/auth/logout | |
| PUT | /oem/auth/password | |

### 4.2 OEM 厂商与账号管理（内部管理员使用，权限：oem:company_manage / oem:account_manage）

| 方法 | 路径 | 说明 |
|---|---|---|
| GET / POST | /oem/companies | 列表 / 创建 |
| GET / PUT | /oem/companies/{id} | 详情 / 编辑 |
| PUT | /oem/companies/{id}/status | 启用禁用 |
| GET / POST | /oem/companies/{id}/accounts | 该厂商账号列表 / 新增账号 |
| PUT | /oem/accounts/{id}、/oem/accounts/{id}/status、/oem/accounts/{id}/password | 编辑、启用禁用、重置密码 |

### 4.3 组织节点主管（复用 Admin 模块，权限：dept:leader_manage）

| 方法 | 路径 | 说明 |
|---|---|---|
| PUT | /departments/{id}/leader | body `{ accountId }`，设置该组织节点主管；置空表示未指定 |

### 4.4 流程模板设计（内部管理员，权限：oem:flow_template_manage）

| 方法 | 路径 | 说明 |
|---|---|---|
| GET | /oem/flow-templates | 列表 |
| POST | /oem/flow-templates | 创建（含初始节点数组） |
| PUT | /oem/flow-templates/{id} | 改名、启用禁用 |
| PUT | /oem/flow-templates/{id}/nodes | 整组替换节点（顺序、审批人来源、启用开关） |

### 4.5 提交与审批

| 方法 | 路径 | 权限 | 说明 |
|---|---|---|---|
| GET | /oem/submissions | oem:submission_view | 分页；OEM 账号只见本厂商，内部按 `oem:submission_view` 全局查看 |
| POST | /oem/submissions | oem:submission_create | 创建草稿 |
| GET | /oem/submissions/{id} | 数据范围 | 详情，含关联流程实例当前状态 |
| POST | /oem/submissions/{id}/submit | oem:submission_create | 发起流程实例：按默认启用模板创建 `oem_flow_instances` + 首个启用节点的 `oem_flow_tasks`；要求至少一个 `AVAILABLE` 文件且无活跃上传（校验逻辑复用 Projects 模块 `RequireFileUploadAsync` 的模式） |
| GET | /oem/approvals/pending | oem:flow_approve | 当前用户待审批任务列表 |
| POST | /oem/approvals/{taskId}/approve | oem:flow_approve | 通过；仅该任务当前指派的审批人可操作 |
| POST | /oem/approvals/{taskId}/reject | oem:flow_approve | body `{ reason }`；驳回直接终止实例 |

### 4.6 文件

| 方法 | 路径 | 说明 |
|---|---|---|
| POST | /oem/uploads/init、PUT /oem/uploads/{sid}/chunks/{index}、GET /oem/uploads/{sid}、POST /oem/uploads/{sid}/merge、DELETE /oem/uploads/{sid} | 与现有 `/uploads/*` 协议一致，挂载目标从 `projectId` 换成 `submissionId` |
| GET | /oem/submissions/{id}/files | 分页 |
| GET | /files/{id}/download、/files/{id}/content | 复用现有下载/预览接口，权限校验按 `oem_submission_id` 分支解析数据范围 |

## 5. 已确认决策（补充）

提交单业务字段、上传参数共用范围、管理界面归属三项均已确认，见需求文档 §6。剩余两项不影响 schema 设计，留在需求文档 §7 跟踪：组织节点主管变更是否需要留痕、流程模板设计器的具体交互细节。

---

**文档结束**
