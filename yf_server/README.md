# yf_server — 公司与供应商协作平台后端

Rust + Axum + SeaORM（Code-First）+ MySQL 5.7。表结构唯一事实源为 Rust 代码（`server/src/entity` + `migration/src`），禁止手工改库。

## 快速开始

```bash
# 1. 建库（MySQL 5.7，utf8mb4）
mysql -uroot -p -e "CREATE DATABASE IF NOT EXISTS yf_system DEFAULT CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;"

# 2. 复制 config.toml 为已忽略的 config.local.toml，填写本机配置
#    （也可用环境变量 YF_DATABASE_URL / YF_JWT_SECRET 覆盖敏感项）

# 3. 本地启动（config.local.toml 中 auto_migrate=true 时自动迁移）
cd yf_server
cargo run -p server

# 或手工执行迁移（先显式设置 DATABASE_URL；CLI 不读取 config.local.toml）
# 完整环境变量检查及恢复步骤见 DEPLOYMENT.md
cargo run -p migration -- up
```

- 健康检查：http://127.0.0.1:8080/health
- API 根路径：`/api/v1`
- SMTP 凭据可通过 `YF_SMTP_HOST`、`YF_SMTP_PORT`、`YF_SMTP_USERNAME`、`YF_SMTP_PASSWORD`、`YF_SMTP_FROM` 进程级注入，避免写入配置文件；邮件中的登录地址使用前端 `web.base_url`，部署时可通过 `YF_WEB_BASE_URL` 覆盖。
- 首次初始化会创建 `admin`，随机初始密码仅写入当次后端启动日志；首次登录强制改密。不要把初始密码写入源码、文档或长期日志。
- Windows/IIS 发布、迁移、备份与恢复流程见 [DEPLOYMENT.md](DEPLOYMENT.md)。

## 删除操作

物理删除由独立权限点控制：`project:delete`、`file:delete`、`supplier:delete`、`supplier:account_delete`、`user:delete`、`dept:delete`、`role:delete`、`log:delete` 和既有的 `message:delete_any`。升级时新增权限只授予系统管理员角色，之后可在角色页按职责分配；管理页访问和其他编辑操作仍使用各自的菜单与管理权限。后端会在删除事务内重新校验权限，防止并发撤权后继续执行。

| 对象 | 删除条件与保留规则 |
|---|---|
| 项目 | 仅草稿或已终止状态可删除，且不能有文件、留言或上传记录；锁定项目后重新校验，不级联删除业务内容 |
| 用户、供应商账号 | 无业务历史或引用；有关联记录时使用禁用，保留创建人、确认人和已读凭证 |
| 供应商 | 无项目且无账号；不级联删除账号 |
| 组织、角色 | 有下级组织或绑定用户时拒绝删除；内置角色可分配权限、禁用或删除，名称保持固定 |
| 操作日志 | 清理与 `AUDIT_LOG_DELETE` 留痕同事务，记录实际删除 ID 和数量；清理记录本身不可删除 |

数据库迁移 008/009 会删除电话及供应商联系字段，回退迁移只恢复空列，不恢复旧数据。组织固定为三级的迁移会拒绝超过三级的旧数据；需先明确组织调整方案，再重试迁移。

迁移 016 一次性将轮次审批替换为项目级审批：清空项目业务聚合及相关项目审计，删除轮次表、轮次外键列和轮次权限，保留账号、供应商、角色、系统参数及非项目审计。该迁移不提供旧业务数据兼容或回退。

删除回归测试使用 `python scripts/test-isolated.py delete_safety_`，脚本只允许本机 MySQL，创建并清理临时测试库，不使用业务库运行删除测试。

系统管理员角色绑定启用用户时必须保留用户管理和角色管理入口，避免管理员在权限分配时锁死系统；角色禁用和删除仍受绑定用户校验约束。

## 项目动态

项目详情的「项目动态」按时间展示业务进展，支持类型筛选与关联对象定位。活动随业务事务一起记录，与可清理的系统操作日志分开保存。历史补齐仅采用现存记录中的确切时间和关联关系，缺失历史不会推测补写；已删除留言的正文不会通过动态重新显示。

## 结构

```
yf_server/
├── config.toml           # 可提交的安全模板（不含密钥）
├── config.local.toml     # 本机配置（已忽略）
├── server/               # 主程序（Axum）
│   └── src/
│       ├── entity/       # SeaORM 实体（Code-First 事实源）
│       ├── migration 依赖 # 启动时 auto_migrate
│       ├── middleware/   # auth(JWT) / 权限点
│       ├── service/      # 业务逻辑与数据范围过滤
│       ├── handler/      # HTTP 路由和输入边界
│       ├── storage/      # 本地磁盘布局（同卷原子 rename）
│       └── notify/       # 邮件 outbox worker（M4 实现）
├── migration/            # SeaORM 迁移和幂等种子数据
└── scripts/backup.ps1    # MySQL + 文件存储备份与 SHA-256 清单
```

生产环境必须显式迁移、备份并完成真实业务验收，不能把本地构建或 `/health` 通过当作部署完成。
