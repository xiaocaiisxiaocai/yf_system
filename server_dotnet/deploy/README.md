# ASP.NET Core + React IIS 正式安装

本包在**目标 Windows 服务器**使用，不需要 Rust、Node.js 或源码。程序使用 .NET 10 x64 framework-dependent 发布；需要先安装 IIS、IIS Application Initialization、.NET 10 Hosting Bundle、MySQL，以及带私钥且主机名匹配的 HTTPS 证书。

## 准备

1. 将发布 ZIP 与 `.sha256` 一并复制到服务器，核对哈希后解压到独立临时目录。
2. 将 `appsettings.example.json` 复制到网站和发布包以外，例如 `D:\YfConfig\appsettings.Production.json`。填入数据库连接、随机 JWT 密钥、独立存储目录、实际 HTTPS 来源和 SMTP（可留空以禁用发送）。不要把机密放入 `wwwroot`。
3. 创建存储目录，例如 `D:\YfData\storage`。已有系统要使用与数据库匹配的原文件存储。数据库必须为迁移 `m20260911_000017_auth_session_families` 的结构。
4. 新库可使用本包初始化；已有库不执行此命令。先由 DBA 创建空库，再在包根执行：

```powershell
$env:YF_CONFIG_PATH = 'D:\YfConfig\appsettings.Production.json'
$secret = Read-Host '初始管理员密码' -AsSecureString
$credential = New-Object System.Net.NetworkCredential('', $secret)
$env:YF_BOOTSTRAP_PASSWORD = $credential.Password
try { dotnet .\Yf.Api.dll --initialize-database }
finally { Remove-Item Env:\YF_BOOTSTRAP_PASSWORD; $credential = $null; $secret = $null }
```

初始化只允许空库，创建 `admin` 并强制首次改密。不会自动创建或删除数据库，不会打印密码。启动不自动执行数据库迁移。业务账号运行时还需读写业务表；历史文件迁移清理队列未排空时，应用会先清理并删除该临时表。

## 安装新站点

在目标服务器管理员 PowerShell 中，从发布包根执行：

```powershell
.\install-iis.ps1 `
  -HostName 'yf.example.com' `
  -CertificateThumbprint '替换为LocalMachine-My证书指纹' `
  -ConfigPath 'D:\YfConfig\appsettings.Production.json' `
  -SiteRoot 'C:\inetpub\yf_system_dotnet'
```

脚本核对包文件 SHA-256、独立路径、证书、Hosting Bundle 和运行时，然后创建新应用池、新 HTTPS 站点，并授予最小的程序读、配置读和文件存储修改权限。前后端同站点、同来源，不需要 ARR、URL Rewrite 或 Rust Windows 服务。

脚本拒绝已存在站点/应用池和非空目标目录，不覆盖其他部署。它不自动开放防火墙、不修改 DNS，也不停止旧 Rust 服务。请按实际网络环境配置 DNS、443/TCP 与 HTTPS 证书，并在停写窗口切换入口。

如果配置文件父目录不允许应用池遍历，还需由管理员给对应应用池授予父目录“遍历文件夹”权限。不要给网站目录业务文件写权限，不要给 Everyone 配置读权限。

## 验收和升级

- 检查 `https://实际主机名/health` 返回 `status=ok, db=up`。
- 打开根页面，首次改密后检查菜单、权限、项目提交/确认，以及真实 PDF 预览和下载。
- 配置 SMTP 后检查管理员邮件状态与实际收件；后台邮件有持久队列、认领租约和重试。发送采用至少一次语义，极端断电可能重复，不能当作严格一次投递。
- 应用池使用 AlwaysRunning、无空闲退出和站点预加载；仍需监控应用池、数据库、磁盘和邮件失败。
- 正式升级先停写并备份数据库、独立存储、外部配置与旧程序目录；准备另一个空目录解压核验新包，再停止对应应用池，切换网站物理路径并恢复应用池。复制原部署生成的 `web.config` 外部配置环境项到新目录；禁止将开发机本地配置覆盖正式配置。
- 失败回退切换回旧程序路径/后端启动方式；若发生数据库迁移，必须按对应迁移的备份恢复计划处理。不要同时运行 Rust 与 .NET 写同一套数据库/存储来做生产 A/B 测试。

应用只公开 `wwwroot` 静态资源，配置、DLL 和数据库不作为静态文件暴露。部署脚本在开发阶段只做语法与包检查；开发机的构建/隔离测试不能证明目标服务器的 IIS、证书、网络或 SMTP 已验收。
