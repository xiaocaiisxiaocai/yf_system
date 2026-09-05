# Windows 部署、升级与恢复

本项目由两个运行边界组成：IIS 托管 `web/dist` 静态文件并把 `/api/*` 反向代理到仅监听本机的 Rust 服务；MySQL 和文件存储必须同时纳入备份。

## 1. 发布前门禁

```powershell
Set-Location D:\path\to\yf_system\yf_server
cargo test --locked -j 1 -p server --bin server
cargo build --locked --release -j 1

Set-Location ..\web
npm ci
npm run lint
npm run build
```

生产目录不要复制 `config.local.toml`。在服务器上单独创建该文件，或给服务进程设置 `YF_CONFIG`、`YF_DATABASE_URL`、`YF_JWT_SECRET`。JWT 密钥至少 32 字符；HTTPS 下 `cookie_secure = true`；数据库迁移应由升级步骤显式执行，日常启动使用 `auto_migrate = false`。

## 2. 备份

升级前先停止业务写入；仅在线执行时，数据库与文件快照之间可能存在时间差，不能视为严格一致的恢复点。

```powershell
$env:YF_BACKUP_DB_PASSWORD = Read-Host "MySQL 密码" -MaskInput
.\scripts\backup.ps1 `
  -BackupRoot 'E:\yf_backups' `
  -StorageRoot 'D:\yf_storage' `
  -DatabaseName 'yf_system' `
  -DatabaseUser 'yf_backup'
Remove-Item Env:\YF_BACKUP_DB_PASSWORD
```

脚本生成带时间戳的目录、`database.sql`、完整存储副本和 `SHA256SUMS.txt`。备份账号只需读取目标库、视图、触发器和例程所需权限。至少在隔离数据库和隔离存储目录做一次恢复演练后，才能把备份判定为可恢复。

## 3. 迁移和服务切换

1. 停止 WinSW（或现有服务管理器）中的后端服务，确认 `127.0.0.1:8080` 已无旧进程监听。
2. 完成上一步备份并记录旧版程序目录，保留其作为程序回退包。
3. 使用与生产相同的安全配置运行 `cargo run --locked -p migration -- up`，确认退出码为 0。
4. 替换 Rust release 程序和 IIS 静态目录，启动后端服务。
5. 依次验证 `GET /health`、登录、项目列表、文件上传/下载、留言、权限隔离和审计记录。

Rust 程序不是原生 Windows Service，需由 WinSW 等服务包装器托管，并把工作目录设置为后端发布目录；不要用开发用的 Vite 服务器承载生产前端。后端应保持 `127.0.0.1:8080`，外部只暴露 IIS HTTPS。

IIS 需要 URL Rewrite 和 ARR 反向代理：`/api/*` 原样代理到 `http://127.0.0.1:8080`，其他不存在的静态路径重写到 `/index.html`。同时设置请求体上限不小于系统允许的单个上传分片大小。

## 4. 回退与恢复

- 程序回退：停止服务，恢复旧版后端程序和旧版 `web/dist`，再启动并复验。数据库迁移默认按向前兼容设计，未经验证不要直接执行 `migration down`。
- 灾难恢复：在隔离环境创建空库，导入 `database.sql`，校验 `SHA256SUMS.txt` 后恢复 `storage`，再将配置指向隔离路径做登录、项目、文件下载和审计抽检。
- 数据库与存储必须来自同一停写窗口。若只能使用在线备份，恢复后需核对 `files.storage_path` 对应文件是否存在，并处理孤立文件或缺失文件。

## 5. 运行监控

- `/health` 返回非 200 时应告警；它会把数据库不可用报告为降级。
- 管理端“系统参数”显示存储使用率。达到 `storage.warn_percent` 后，后台每天最多给每位启用的系统管理员入队一封邮件；必须配置 SMTP 且管理员邮箱有效。
- 定期检查 `email_outbox` 中 `FAILED`、长时间 `SENDING` 和积压的 `PENDING`。
- 备份保留周期、异地副本和恢复演练频率应按业务数据保留要求确定，不能由应用自行猜测。
