# ASP.NET Core + React IIS 正式安装

本包在**目标 Windows 服务器**使用，不需要 Rust、Node.js 或源码。程序使用 .NET 10 x64 framework-dependent 发布；需要先安装 IIS、IIS Application Initialization、.NET 10 Hosting Bundle、MySQL，以及带私钥且主机名匹配的 HTTPS 证书。

实时协作使用 SignalR，建议在目标服务器的 IIS 角色服务中启用 **WebSocket Protocol**（Windows Server 功能名 `Web-WebSockets`）。反向代理也需允许 WebSocket Upgrade；Hub 路径为 `/api/v1/collaboration/live`，前后端保持同源。未启用 WebSocket 时 SignalR 可尝试其他传输，连接失败时前端恢复轮询。不要记录 Hub 的 `access_token` 查询参数，IIS 日志应移除 URI Query（`cs-uri-query`）字段或配置等效的脱敏日志；它用于浏览器的 WebSocket/SSE 握手，包含短期访问令牌。

## 准备

1. 将 `deloy` 下生成的整个版本文件夹复制到服务器的独立临时目录，保留其中 `manifest.json`，安装脚本会据此检查全部文件。默认不生成 ZIP；如果打包时显式使用了 `-CreateArchive`，则将 ZIP 与 `.sha256` 一并复制到服务器，核对哈希后解压。
2. 发布时已自动为 `appsettings.example.json` 填入默认数据库连接和 JWT 密钥。将它复制到网站、发布包和业务存储目录以外，例如 `D:\YfConfig\appsettings.Production.json`，再核对实际服务器的数据库地址/库名、独立存储目录和 HTTPS 访问地址；SMTP 可留空以禁用发送。不要把机密放入 `wwwroot` 或应用池可写目录。已有站点升级时继续使用原来的外部配置，不要用新示例覆盖已有 JWT 密钥。
3. 创建存储目录，例如 `D:\YfData\storage`。当前开发阶段不接管旧手写 schema 或旧数据；切换到本版本时创建新的空数据库和空存储目录。后续只有带完整 `__EFMigrationsHistory` 的 EF 管理数据库可以原地升级。
4. 先由 DBA 创建空库，再在包根执行：

```powershell
$env:YF_CONFIG_PATH = 'D:\YfConfig\appsettings.Production.json'
$secret = Read-Host '初始管理员密码' -AsSecureString
$credential = New-Object System.Net.NetworkCredential('', $secret)
$env:YF_BOOTSTRAP_PASSWORD = $credential.Password
try { dotnet .\Yf.Api.dll --initialize-database }
finally { Remove-Item Env:\YF_BOOTSTRAP_PASSWORD; $credential = $null; $secret = $null }
```

初始化只允许空库，通过 EF Core `InitialCreate` 建表，创建 `admin`、系统管理员权限、默认优先级和系统参数，并强制首次改密。不会自动创建或删除数据库，也不会打印密码。启动只读核对 EF 迁移历史，不自动执行 DDL。后续 EF 模型升级需停写和备份，再运行 `dotnet .\Yf.Api.dll --migrate-database`；该命令只接受已有且非空的 EF 历史，支持重复执行，并由数据库锁防止同时迁移。

## ClamAV 扫描服务

发布包自带官方 ClamAV 1.4.6 LTS Windows x64 便携 ZIP，不需要安装 GUI
杀毒软件。`install-iis.ps1` 在生产配置选择 `App:OemScanner:Engine=ClamAV`
时，先调用 `install-clamav.ps1`，将程序安装到独立程序目录，将病毒库、
日志和临时文件放入独立持久目录。默认路径分别为
`C:\Program Files\YfSystem\ClamAV` 和
`C:\ProgramData\YfSystem\ClamAV`，均不在 IIS 网站或 `wwwroot` 内。

ClamD 只监听 `127.0.0.1:3310`，应用配置必须使用：

```json
{
  "App": {
    "OemScanner": {
      "Engine": "ClamAV",
      "ClamAv": {
        "Host": "127.0.0.1",
        "Port": 3310,
        "ConnectTimeoutSeconds": 5,
        "MaxStreamBytes": 1073741824
      }
    }
  }
}
```

发布包内置 `main.cvd`、`daily.cvd` 和 `bytecode.cvd` 官方签名库快照，
首次安装无需联网下载。`clamav-database.ps1` 使用包内官方 `sigtool` 验签，
核对 `clamav/database-manifest.json` 中的哈希、大小、版本和 UTC 构建时间，
复制到独立数据目录后再次校验；缺失、损坏或额外签名文件均不能初始化。
安装随后创建自动启动的独立 `clamd` 和 `freshclam` Windows 服务。
FreshClam 配置为每天检查 12 次并在更新后通知 ClamD 重载。需要立即更新时，以管理员身份在发布包
根运行：

```powershell
.\update-clamav.ps1 -InstallRoot 'C:\Program Files\YfSystem\ClamAV'
```

安装脚本拒绝既有 `clamd`/`freshclam` 服务、已占用的 3310 端口、既有程序
或数据目录；不会覆盖服务或目录，也不会结束未知进程。服务没有可见桌面
窗口。ClamD 的 `INSTREAM`、单文件上限均为 1 GiB，扫描展开总量上限为
1536 MiB、递归深度 16；超过限制和加密归档/文档都按告警处理，不能解释为
扫描正常。Windows 版 ClamAV 的单文件能力约 2 GiB，本系统明确将业务流上限
固定为 1 GiB；20 GiB 文件必须在进入扫描前拒绝，不能静默跳过后返回 clean。

离线首次安装直接使用包内已验签快照，不调用 FreshClam 下载；后续更新服务
暂时离线或启动失败不会回滚已加载快照的 ClamD。恢复联网后执行上述更新命令，
成功更新会启动此前停止的 FreshClam 服务，继续自动更新。
内置库是打包时的快照，不会因安装或复制而变“新”；若启用“病毒库过期时暂停
放行”，超过管理员设置的年龄仍会暂停放行，不能用内置库绕过该门禁。
不要把业务上传文件、配置密钥或客户数据放入 ClamAV 程序/病毒库目录。

构建机默认通过 FreshClam 更新缓存中的完整 CVD 后打包。需要从已有已验证库
制作离线包时，在 `publish-iis.ps1` 增加 `-ClamAvDatabaseSnapshotDirectory <目录>`；
该目录需含三份完整 `.cvd`，不能用增量 `.cld` 或自定义签名替代。
`-UseExistingClamAvCacheOnly` 会禁止下载，使用缓存里的完整 CVD 或显式指定的快照。
构建和安装均重新执行官方数字签名校验，不仅依赖本地生成的清单。

`clamav/PROVENANCE.json` 记录 GitHub Releases API 发布的固定 SHA-256。
发布包还保留官方 detached signature、Talos 公钥、上游许可证/依赖说明和匹配
的完整源代码归档 `clamav-1.4.6.tar.gz`。本机存在 GnuPG 时准备脚本会额外验签；
没有 GnuPG 时仍强制核对固定上游 SHA-256，并在来源清单中明确记录未执行 GPG。

ClamAV 程序和持久数据目录关闭 ACL 继承，仅允许 `SYSTEM` 与本机
`Administrators` 完全控制，避免宽松父目录中的普通用户替换服务程序、配置或
病毒库。`-UseExistingClamAv` 也会只读复核这些 ACL；不符合时拒绝复用。

## 安装新站点

在目标服务器管理员 PowerShell 中，从发布包根执行：

```powershell
.\install-iis.ps1 `
  -HostName 'yf.example.com' `
  -CertificateThumbprint '替换为LocalMachine-My证书指纹' `
  -ConfigPath 'D:\YfConfig\appsettings.Production.json' `
  -SiteRoot 'C:\inetpub\yf_system_dotnet'
```

脚本核对包文件 SHA-256、程序包/站点/配置/存储独立路径、证书、Hosting Bundle 和运行时，然后创建新应用池、新 HTTPS 站点。它关闭配置文件 ACL 继承，只保留当前管理员、SYSTEM、Administrators 完全控制和应用池身份只读；业务存储只给应用池修改，程序目录只读。前后端同站点、同来源，不需要 ARR、URL Rewrite 或 Rust Windows 服务。

如果 ClamAV 已由本包成功安装，但随后 IIS 站点创建失败，修正 IIS 前置条件后
按原命令重跑并显式增加 `-UseExistingClamAv`：

```powershell
.\install-iis.ps1 `
  -HostName 'yf.example.com' `
  -CertificateThumbprint '替换为LocalMachine-My证书指纹' `
  -ConfigPath 'D:\YfConfig\appsettings.Production.json' `
  -SiteRoot 'C:\inetpub\yf_system_dotnet' `
  -UseExistingClamAv
```

该开关只复用已经运行的服务，不安装、重配、启停或覆盖它们。脚本会重新核对
ClamAV 1.4.6 官方程序哈希、程序和配置路径、回环地址/端口/扫描限制、三个可信
病毒库、两个服务状态、ClamD 服务 PID 对 3310 监听的归属，以及 PING/VERSION。
ClamD 必须运行；离线时允许已验证的 FreshClam 服务暂时停止，并提示恢复联网后更新。
任一项不一致即失败。首次部署或尚未成功安装 ClamAV 时不要使用此开关。

脚本拒绝已存在站点/应用池和非空目标目录，不覆盖其他部署。它不自动开放防火墙、不修改 DNS，也不停止现有后端服务。请按实际网络环境配置 DNS、443/TCP 与 HTTPS 证书，并在停写窗口切换入口。

如果配置文件父目录不允许应用池遍历，还需由管理员给对应应用池授予父目录“遍历文件夹”权限；父目录本身不能给应用池或广泛主体写入/删除子项权限，否则仍可能替换配置文件。不要给网站目录业务文件写权限，不要给 Everyone、Users 或 Authenticated Users 配置读权限。组织确需额外备份/运维主体读取配置时，安装后由管理员按最小权限显式添加，并重新核对应用池仍然只有读取权限。

## 正式服务器备份、升级与恢复

以下命令必须在**目标服务器的管理员 Windows PowerShell 5.1** 中执行，并保持 `maintain-iis.ps1` 与同一发布包中的 `maintenance-common.ps1` 位于同一目录。示例站点名为安装脚本默认值 `YfSystemDotNet`；如果安装时使用了其他名称，三种操作都必须传入该实际名称。

维护只支持由独立应用池承载、没有子应用的现有 IIS 站点。应用池必须使用 `ApplicationPoolIdentity` 且不加载用户 profile。站点必须使用外部 JSON 作为唯一主配置，由 `web.config` 中唯一的 `YF_CONFIG_PATH` 指向该文件，并以 in-process 的 `dotnet .\Yf.Api.dll` 标准形式启动。执行前移除站点、应用池、应用池默认值、机器和当前 PowerShell 中的 `App__*` / `App:*` 高优先级覆盖；维护脚本会拒绝这些覆盖和继承的额外 `YF_CONFIG_PATH`，防止备份、迁移或健康检查连接到另一套资源。正式配置的 `WebBaseUrl` 必须是实际 HTTPS 来源，`CookieSecure` 必须为 `true`。

服务器需安装与目标 MySQL 兼容的 5.7 或更高版本 `mysql.exe`、`mysqldump.exe` 客户端；若不在 `PATH`，按下例传绝对路径。脚本只检查客户端可执行文件存在，版本和服务器兼容性需在维护窗口前确认。所有目录必须是互不包含的本地绝对路径，不能经过 junction/symlink 等重解析点；备份目录、新程序目录和恢复存储目录必须不存在或为空。

数据库位于 `localhost`、`127.0.0.1` 或 `::1` 时，为兼容隔离测试和同机维护，连接串可以继续使用 `None`、`Disabled`、`Preferred` 等现有 `SSL Mode`。任何非回环数据库都必须设置 `SSL Mode=VerifyFull`，维护脚本会传给 `mysql`/`mysqldump` 为 `VERIFY_IDENTITY`，拒绝 `Preferred`、`Required` 和 `VerifyCA`，避免加密降级或只验 CA 不验主机名。私有 CA 可在连接串中使用 `CACertificateFile`、`CA Certificate File`、`SslCa` 或 `SSL CA` 指向网站、存储和包目录之外的本地绝对只读文件；脚本核对文件存在且无重解析点，并把它传为 `ssl-ca`。客户端证书/私钥仍不由维护脚本接管，需单独配置受控的备份客户端。

### Backup

从当前站点生成同一 `SiteName` 的离线备份：

```powershell
.\maintain-iis.ps1 `
  -Action Backup `
  -SiteName 'YfSystemDotNet' `
  -BackupDirectory 'E:\YfBackups\2026-09-11-before-upgrade' `
  -MySqlDump 'C:\Program Files\MySQL\MySQL Server 8.0\bin\mysqldump.exe'
```

备份包含当前程序、独立存储、外部 JSON 配置、数据库表、触发器、存储过程和事件，并用清单记录文件 SHA-256。它同时包含数据库凭据、JWT/SMTP 等密钥、业务数据和上传文件；应放在网站目录之外受限且加密的备份介质上，限制管理员/备份账号访问，不提交源码库，不通过普通文件共享长期暴露，并按保留策略安全清除。远程数据库必须先满足上面的 `VerifyFull` 证书身份验证要求，不能依靠网络边界代替传输加密。

### Upgrade

升级前准备已核对清单的新发布包和一个新的空程序目录。命令会先在应用池停止期间完成同样的离线备份，再复制新包、按需迁移并切换现有站点物理路径：

```powershell
.\maintain-iis.ps1 `
  -Action Upgrade `
  -SiteName 'YfSystemDotNet' `
  -BackupDirectory 'E:\YfBackups\2026-09-11-before-upgrade' `
  -PackageRoot 'D:\Packages\YfDotNet-NEW' `
  -NewSiteRoot 'C:\inetpub\yf_system_dotnet_20260911' `
  -MySqlDump 'C:\Program Files\MySQL\MySQL Server 8.0\bin\mysqldump.exe' `
  -MigrateDatabase
```

只有该版本确实要求 schema 升级时才传 `-MigrateDatabase`；否则省略。升级继续使用当前数据库、存储和外部配置。不要直接覆盖当前程序目录，也不要同时让新旧版本写同一套资源。

### Restore

恢复只接受清单中 `siteName` 与当前 `-SiteName` 相同的备份。先由 DBA 创建**不同于当前数据库名**的新数据库及专用账号，并确认目标 schema 中表、routines 和 events 均为零；不要把原库清空后复用。再准备新的空程序目录、新的空存储目录和新的外部 JSON 配置。新配置必须指向这些新资源，保持当前站点完全相同的 HTTPS `WebBaseUrl`，并设置 `CookieSecure=true`。

```powershell
.\maintain-iis.ps1 `
  -Action Restore `
  -SiteName 'YfSystemDotNet' `
  -BackupDirectory 'E:\YfBackups\2026-09-11-before-upgrade' `
  -NewSiteRoot 'C:\inetpub\yf_system_dotnet_restore_20260911' `
  -RestoreConfigPath 'D:\YfConfig\appsettings.Restored.json' `
  -MySql 'C:\Program Files\MySQL\MySQL Server 8.0\bin\mysql.exe'
```

Restore 使用备份内的程序和 schema，不接受 `-MigrateDatabase`。若导入失败，新数据库可能只写入了一部分；保留失败现场供 DBA 判断，换另一套全新的空目标重试。成功恢复后如需升级，再使用与目标版本对应的 `Upgrade` 流程。

三种操作都会先停止该站点的专属应用池并等待其工作进程全部退出。脚本只管理这个命名站点的专属池，不能排除其他 IIS 站点、Windows 服务、计划任务、命令行工具或远程实例继续写同一数据库和存储；管理员必须在维护窗口前识别并停止所有外部写入者。原池本来在运行时，成功后脚本才会启动它，并从配置中的同一 HTTPS 来源检查 `/health` 的 `status=ok, db=up`；任何步骤或健康检查失败都会让应用池保持停止。脚本不会删除旧数据库、旧存储或旧程序目录，也不会自动把站点切回旧路径；排查完成前不要手工启动应用池。旧资源确认不再需要前，按成套回退边界保留。

`Restore` 维护的是现有 IIS 站点，不会在裸机上创建 IIS、证书、绑定或应用池。灾难恢复到新机器时，先安装 IIS、Hosting Bundle、证书和 MySQL 客户端，使用**备份所对应版本**的发布包创建与备份同名的专属站点，例如：

```powershell
.\install-iis.ps1 `
  -PackageRoot 'D:\Packages\YfDotNet-BACKUP-VERSION' `
  -HostName 'yf.example.com' `
  -CertificateThumbprint '替换为LocalMachine-My证书指纹' `
  -ConfigPath 'D:\YfConfig\appsettings.DisasterStaging.json' `
  -SiteName 'YfSystemDotNet' `
  -AppPoolName 'YfSystemDotNet' `
  -SiteRoot 'C:\inetpub\yf_system_dotnet_staging'
```

这一步使用的临时外部配置、程序目录、数据库和存储仍须与最终恢复目标分开，并保持站点不对外服务。随后用同版本 `maintain-iis.ps1 -Action Restore` 恢复到另一套新空数据库、存储、程序目录和外部配置。这是分阶段恢复流程，不是一键全新服务器恢复。

## 验收

- 检查 `https://实际主机名/health` 返回 `status=ok, db=up`。
- 打开根页面，首次改密后检查菜单、权限、项目提交/确认，以及真实 PDF 预览和下载。
- 配置 SMTP 后检查管理员邮件状态与实际收件；后台邮件有持久队列、认领租约和重试。发送采用至少一次语义，极端断电可能重复，不能当作严格一次投递。
- 应用池使用 AlwaysRunning、无空闲退出和站点预加载；仍需监控应用池、数据库、磁盘和邮件失败。
- 每次备份、升级或恢复后检查受保护备份的清单和日志；升级/恢复还要完成浏览器业务验收。若执行过数据库迁移，回退必须按该版本的数据库备份计划成套处理。

应用只公开 `wwwroot` 静态资源，配置、DLL 和数据库不作为静态文件暴露。部署脚本在开发阶段只做语法与包检查；当前开发机没有创建或操作真实 IIS 站点，仅可读取 Microsoft.Web.Administration 默认配置。开发机的构建/隔离测试不能证明目标服务器的 IIS 站点、专属应用池、证书、网络或 SMTP 已验收。
