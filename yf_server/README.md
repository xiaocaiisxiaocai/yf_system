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

物理删除同时要求启用的内部系统管理员身份和对应管理权限。系统管理员身份来自启用的内置角色，不能用用户名或可配置权限点代替。登录和个人资料响应的 `user.isSystemAdmin` 供前端控制入口，后端在删除事务内重新验证。

| 对象 | 删除条件与保留规则 |
|---|---|
| 项目 | 非进行中，且无轮次、文件、留言、上传记录；锁定项目后重新校验，不级联删除业务内容 |
| 用户、供应商账号 | 无业务历史或引用；有关联记录时使用禁用，保留创建人、确认人和已读凭证 |
| 供应商 | 无项目且无账号；不级联删除账号 |
| 组织、角色 | 保留子组织、用户引用和内置角色保护；不因管理员身份跳过关联校验 |
| 操作日志 | 清理与 `AUDIT_LOG_DELETE` 留痕同事务，记录实际删除 ID 和数量；清理记录本身不可删除 |

数据库迁移 008/009 会删除电话及供应商联系字段，回退迁移只恢复空列，不恢复旧数据。组织固定为三级的迁移会拒绝超过三级的旧数据；需先明确组织调整方案，再重试迁移。

删除回归测试使用 `python scripts/test-isolated.py delete_safety_`，脚本只允许本机 MySQL，创建并清理临时测试库，不使用业务库运行删除测试。

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
