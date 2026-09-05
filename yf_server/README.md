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

# 或手工执行迁移
cargo run -p migration -- up
```

- 健康检查：http://127.0.0.1:8080/health
- API 根路径：`/api/v1`
- SMTP 凭据可通过 `YF_SMTP_HOST`、`YF_SMTP_PORT`、`YF_SMTP_USERNAME`、`YF_SMTP_PASSWORD`、`YF_SMTP_FROM` 进程级注入，避免写入配置文件；邮件中的登录地址使用前端 `web.base_url`，部署时可通过 `YF_WEB_BASE_URL` 覆盖。
- 首次初始化会创建 `admin`，随机初始密码仅写入当次后端启动日志；首次登录强制改密。不要把初始密码写入源码、文档或长期日志。
- Windows/IIS 发布、迁移、备份与恢复流程见 [DEPLOYMENT.md](DEPLOYMENT.md)。

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
