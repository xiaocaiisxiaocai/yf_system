# ASP.NET Core + React IIS 正式安装

本包在**目标 Windows 服务器**使用，不需要 Rust、Node.js 或源码。程序使用 .NET 8 x64 framework-dependent 发布；需要先安装 IIS、IIS Application Initialization、.NET 8 Hosting Bundle 和 MySQL。正式对外使用应配置 HTTPS 证书；现有开发站点若暂用 HTTP，发布配置中的 `WebBaseUrl` 必须与实际地址一致且 `CookieSecure=false`。

实时协作使用 SignalR，建议在目标服务器的 IIS 角色服务中启用 **WebSocket Protocol**（Windows Server 功能名 `Web-WebSockets`）。反向代理也需允许 WebSocket Upgrade；Hub 路径为 `/api/v1/collaboration/live`，前后端保持同源。未启用 WebSocket 时 SignalR 可尝试其他传输，连接失败时前端恢复轮询。不要记录 Hub 的 `access_token` 查询参数，IIS 日志应移除 URI Query（`cs-uri-query`）字段或配置等效的脱敏日志；它用于浏览器的 WebSocket/SSE 握手，包含短期访问令牌。

当前版本只支持**一个 API 进程、一个部署副本**。SignalR 连接注册表、实时事件队列/去重和原生下载 grant 都在进程内存中：IIS 应用池必须保持 `processModel.maxProcesses=1`，不得启用 web garden，也不得让同一业务环境同时运行第二个 IIS 站点、Windows 服务、容器或服务器副本。负载均衡粘性会话不能补齐跨实例事件传播，也不能让另一进程读取本进程签发的下载 grant，因此不足以支持多副本。进程回收、升级和重启会使尚未兑换的下载 grant、已兑换的短时下载 session 失效，并断开 SignalR；客户端应重新连接并重新申请下载。扩容前必须同时实现 SignalR backplane、跨实例业务事件传播/去重，以及下载 grant/session 的共享原子存储，完成故障切换与撤销回归后才能增加 worker 或副本。

项目复制由持久化 `ProjectCopyWorker` 执行，`App.CopyWorkerEnabled` 默认值为 `true`，并独立于维护任务的 `App.WorkerEnabled`。worker 用数据库命名锁保证同一数据库只有一个领取者，并在 `project_copy_worker_state` 递增 epoch 以阻止失去租约的旧 worker 提交；任务和进度保存在 `project_copy_jobs`。重启时，遗留的 `running` 任务会恢复为 `pending`，旧 execution token 的暂存目录会清理，再以新 token 和 epoch 执行。若显式关闭 `CopyWorkerEnabled`，复制提交返回 503，不能形成无人处理的排队任务。

## 准备

1. 将 `deloy` 下生成的整个版本文件夹复制到服务器的独立临时目录，保留其中 `manifest.json`，安装脚本会据此检查全部文件。默认不生成 ZIP；如果打包时显式使用了 `-CreateArchive`，则将 ZIP 与 `.sha256` 一并复制到服务器，核对哈希后解压。
2. 默认发布仍将本机私有默认值写入 `appsettings.Production.json`，包括数据库连接、JWT、实际访问地址、存储目录及本次包独立生成的初始管理员密码；`appsettings.json` 仅保留日志等基础设置。发布脚本默认拒绝脏工作区、HTTP/不安全 Cookie 和 root 数据库账号，始终执行 `npm ci`，并对输出目录以及 ZIP/校验边车设置仅当前账号、SYSTEM、Administrators 可读写的 ACL。隔离环境确有需要时必须显式传 `-AllowDirty` 或 `-AllowInsecurePrivateConfiguration`，对应选择会记录在 manifest。将默认发布包作为含凭据的私有制品保管，不要放入公开下载位置或给应用池写权限。直接把包作为现有 IIS 站点物理目录时，程序会读取包内配置；若使用正式安装脚本，则把配置复制到网站外复核后作为 `-ConfigPath` 传入。已有站点升级须保留原 JWT 和数据库配置。SMTP 在系统「系统参数」页面保存到数据库，尚未设置时不发送邮件。
   若不允许制品携带环境机密，发布时传 `-ExternalConfigurationTemplate`。该模式保留空的连接串、JWT 和初始密码，仅能配合下文正式安装脚本及一份已在服务器外部准备好的完整 `ConfigPath` 使用，不能直接绑定启动。
3. 发布包默认开启 `App.AutoInitializeDatabase=true` 和项目复制 worker。第一次启动时，如果配置的数据库不存在，会自动创建；如果是空库，会执行 EF Core 迁移建表并创建 `admin`。MySQL 账号需要目标库的创建、建表及数据读写权限；协作文件和复制任务暂存目录使用网站外的 `StorageRoot`，应用池身份需要该目录的修改权限。
4. 初始登录账号为 `admin`，初始密码查看私有包 `appsettings.Production.json` 中的 `App.BootstrapPassword`。发布脚本为每个私有包独立随机生成，不写回本机默认值，也不输出到日志；首次登录必须修改。已初始化的数据库在重启或升级时不会重建、重新播种或重置管理员密码；初始化完成后应从实际部署配置中移除初始密码。外部配置模板模式由管理员在服务器外部配置中另行生成并保管该密码。

自动初始化只处理不存在或完全为空的数据库，并以数据库锁协调并发启动。非空数据库只检查 EF 迁移历史和必要种子，绝不自动执行升级迁移。历史不匹配、旧版待迁移或初始化中断留下的部分表都会停止并报错，不会删除现有数据或自动重试 DDL。已有数据库升级仍需先备份，再显式运行 `dotnet .\Yf.Api.dll --migrate-database`。

如果禁用 `App.AutoInitializeDatabase`，仍可先手动创建空库，设置进程环境变量 `YF_BOOTSTRAP_PASSWORD`，再执行 `dotnet .\Yf.Api.dll --initialize-database`。正式安装脚本使用的外部配置也应包含上述初始化选项；启动后已存在的数据库不会要求保留初始密码。

本版本的升级迁移会删除 OEM 表、权限和专属配置，其中 OEM 业务数据不可恢复；对已有数据库执行 `--migrate-database` 前必须完成可验证的备份。只复制新程序而不迁移时，启动校验会拒绝旧的迁移状态。

当前升级还包含第 9 个 EF 迁移 `20260923141854_AddProjectCopyJobs`，创建持久化 `project_copy_jobs` 队列、`project_copy_worker_state` epoch 状态，以及幂等、领取、主项目任务列表索引。已有非空数据库不会在普通启动时自动升级；必须在停写、备份后执行 `dotnet .\Yf.Api.dll --migrate-database`，再启动启用复制 worker 的新版本。迁移未完成时不能先开放新前端的复制入口。

## 绑定现有开发 IIS 站点

若站点已经创建，网站物理目录可直接指向本包根目录；`appsettings.Production.json` 已写入发布时的配置，`web.config` 无需再设置 `YF_CONFIG_PATH`。应用池使用“无托管代码”、64 位，且 `processModel.maxProcesses=1`，并给其身份对程序目录只读、对独立存储目录修改权限。首次回收应用池会自动初始化新库，之后检查 `/health`。不要把包根目录开放为下载目录，也不要给应用池写入 `appsettings.Production.json` 的权限。本方式只解决当前开发站点直接绑定问题；下述安装和维护脚本仍使用站点外的生产配置。

## 安装新站点

在目标服务器管理员 PowerShell 中，从发布包根执行：

```powershell
.\install-iis.ps1 `
  -HostName 'yf.example.com' `
  -CertificateThumbprint '替换为LocalMachine-My证书指纹' `
  -ConfigPath 'D:\YfConfig\appsettings.Production.json' `
  -SiteRoot 'C:\inetpub\yf_system_dotnet'
```

脚本核对包文件 SHA-256、程序包/站点/配置/存储独立路径、证书、Hosting Bundle 和运行时，然后创建新应用池、新 HTTPS 站点。应用池会被明确设置并回读验证为 `processModel.maxProcesses=1`；无法保持单 worker 时安装停止。复制包后会清空站点内 `appsettings.json` 和 `appsettings.Production.json` 的凭据回退，正式站点只使用 `YF_CONFIG_PATH` 指向的外部配置；升级和恢复也执行同样处理。它关闭配置文件 ACL 继承，只保留当前管理员、SYSTEM、Administrators 完全控制和应用池身份只读；业务存储只给应用池修改，程序目录只读。前后端同站点、同来源，不需要 ARR、URL Rewrite 或 Rust Windows 服务。

脚本拒绝已存在站点/应用池和非空目标目录，不覆盖其他部署。所有预检完成后才开始写入；复制、ACL、应用池、站点、证书绑定或启动失败时，脚本删除本次创建的站点/池和复制内容，并恢复配置、存储及原有空站点目录的 ACL，随后可用原命令重跑。若警告回滚不完整，保持现场并先处理列出的准确资源。它不自动开放防火墙、不修改 DNS，也不停止现有后端服务。请按实际网络环境配置 DNS、443/TCP 与 HTTPS 证书，并在停写窗口切换入口。

如果配置文件父目录不允许应用池遍历，还需由管理员给对应应用池授予父目录“遍历文件夹”权限；父目录本身不能给应用池或广泛主体写入/删除子项权限，否则仍可能替换配置文件。不要给网站目录业务文件写权限，不要给 Everyone、Users 或 Authenticated Users 配置读权限。组织确需额外备份/运维主体读取配置时，安装后由管理员按最小权限显式添加，并重新核对应用池仍然只有读取权限。

## 正式服务器备份、升级与恢复

以下命令必须在**目标服务器的管理员 Windows PowerShell 5.1** 中执行，并保持 `maintain-iis.ps1` 与同一发布包中的 `maintenance-common.ps1` 位于同一目录。示例站点名为安装脚本默认值 `YfSystemDotNet`；如果安装时使用了其他名称，三种操作都必须传入该实际名称。

维护只支持由独立应用池承载、没有子应用的现有 IIS 站点。应用池必须使用 `ApplicationPoolIdentity`、不加载用户 profile，并保持 `processModel.maxProcesses=1`；维护脚本在停止任何进程前检查这些条件，web garden 配置会被拒绝。站点必须使用外部 JSON 作为唯一主配置，由 `web.config` 中唯一的 `YF_CONFIG_PATH` 指向该文件，并以 in-process 的 `dotnet .\Yf.Api.dll` 标准形式启动。执行前移除站点、应用池、应用池默认值、机器和当前 PowerShell 中的 `App__*` / `App:*` 高优先级覆盖；维护脚本会拒绝这些覆盖和继承的额外 `YF_CONFIG_PATH`，防止备份、迁移或健康检查连接到另一套资源。正式配置的 `WebBaseUrl` 必须是实际 HTTPS 来源，`CookieSecure` 必须为 `true`。

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

备份包含当前程序、独立存储、外部 JSON 配置、数据库表、触发器、存储过程和事件，并用清单记录文件 SHA-256；manifest 会显式标记 `containsSecrets=true`、`protection=restricted-acl`。它同时包含数据库凭据、JWT/SMTP 等密钥、业务数据和上传文件，目录 ACL 只解决本机访问控制，不是静态加密；必须放在网站目录之外受限且已启用 BitLocker、EFS 或等效受控加密的备份介质上，限制管理员/备份账号访问，不提交源码库，不通过普通文件共享长期暴露，并按保留策略安全清除。保留明文目录格式是为了维持现有跨机恢复合同；复制到其他介质前由运维层负责加密。远程数据库必须先满足上面的 `VerifyFull` 证书身份验证要求，不能依靠网络边界代替传输加密。

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

三种操作都会先停止该站点的专属应用池并等待其工作进程全部退出。脚本只管理这个命名站点的专属池，不能排除其他 IIS 站点、Windows 服务、计划任务、命令行工具或远程实例继续写同一数据库和存储；管理员必须在维护窗口前识别并停止所有外部写入者。新程序会在切换路径前执行一次离线 readiness；原池本来在运行时，切换后从配置中的同一 HTTPS 来源检查 `/health` 的 `status=ok, db=up`，默认最多等待 120 秒，可用 `-HealthCheckWaitSeconds` 和 `-HealthRequestTimeoutSeconds` 调整。未执行数据库迁移时，后续失败会自动恢复原 `physicalPath`，但池保持停止供人工核查；一旦 `-MigrateDatabase` 已开始，脚本绝不自动启动可能不兼容的旧版本，也不切回旧路径。此时按本次受保护备份恢复到一套新数据库、新存储、新程序目录和匹配配置，再切换站点。脚本不会删除旧数据库、旧存储或旧程序目录；旧资源确认不再需要前，按成套回退边界保留。

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

开发阶段的新版本不兼容旧上传客户端。前后端、数据库必须一起更新，不能混跑旧后端。顺序为：停止旧应用 → 备份数据库与文件 → `--migrate-database` → `--convert-file-blobs` → 启动。`maintain-iis.ps1 -MigrateDatabase` 会在迁移后自动执行转换。

文件内容转换是一次性维护命令，不在启动时执行：逐个复制并校验现存文件会远超 IIS 进程内托管默认的 `startupTimeLimit`（120 秒），放在启动流程里会被反复杀死重启。命令可重复执行，已转换的行会跳过；首次转换需预留与现存文件等量的磁盘空间。缺失或损坏的文件会让命令失败并报告文件 ID，修复后重跑即可。普通启动只做校验：仍有未转换的文件时拒绝启动，并提示运行该命令。

转换会保留原物理文件。确认业务验收通过、备份可用后，可以在停站状态下执行 `dotnet Yf.Api.dll --convert-file-blobs --remove-legacy-content`：它先完成（或确认）转换，再删除 `files/`、`copy-jobs/` 下不再被任何文件记录引用的旧文件；不会跟随符号链接或目录联接，也不会动 `blobs/`、留言图片和临时上传目录。输出 JSON 中报告转换数、删除文件数和字节数。

静态资源随前端构建生成 `.br/.gz`，发布包根的 `precompressed-assets.json` 用于验包，不应放在 `wwwroot`。必须整体替换发布目录，避免原资源与压缩版本不一致。API 不使用响应压缩。身份缓存依赖数据库九个安全表触发器和内部修订行；不要手工删除这些结构。触发器对部署的额外要求：

- 执行迁移的数据库账号需要 `TRIGGER` 权限。MySQL 开启二进制日志（`log_bin`）时，创建触发器还需要 `SUPER` 权限，或由 DBA 设置 `log_bin_trust_function_creators=1`，否则迁移会在目标服务器上失败。首次部署前在目标库确认。
- `mysqldump` 备份中的触发器带有 `DEFINER=<账号>@<主机>`。恢复到另一台服务器时，该账号必须在目标 MySQL 上存在（或恢复前把 `DEFINER` 改为目标账号），否则恢复失败，或此后对用户/供应商/会话表的写入报 “definer does not exist”。开发机的维护测试使用同一账号，覆盖不到跨账号恢复。
- 修订号只有一行：登录、刷新、注销及用户、供应商写入都会在同一事务中更新它，这些写入在该行上串行。这是有意取舍；若出现登录高峰排队，先检查长事务。

- 检查 `https://实际主机名/health` 返回 `status=ok, db=up`。
- 打开根页面，首次改密后检查菜单、权限、项目提交/确认，以及真实 PDF 预览和下载。
- 从主项目提交一次复制，确认 `POST /api/v1/projects/{id}/copy` 返回 202，主项目复制任务列表显示文件/字节进度；回收应用池后确认未完成任务从持久队列恢复，成功任务可进入目标子项目。同一 `idempotencyKey` 重试必须指向同一任务。
- 配置 SMTP 后检查管理员邮件状态与实际收件；后台邮件有持久队列、认领租约和重试。发送采用至少一次语义，极端断电可能重复，不能当作严格一次投递。
- 应用池使用 `maxProcesses=1`、AlwaysRunning、无空闲退出和站点预加载；同时核对同一业务环境没有第二个 API 副本。仍需监控应用池、数据库、磁盘和邮件失败。
- 每次备份、升级或恢复后检查受保护备份的清单和日志；升级/恢复还要完成浏览器业务验收。若执行过数据库迁移，回退必须按该版本的数据库备份计划成套处理。

应用只公开 `wwwroot` 静态资源，配置、DLL 和数据库不作为静态文件暴露。部署脚本在开发阶段只做语法与包检查；当前开发机没有创建或操作真实 IIS 站点，仅可读取 Microsoft.Web.Administration 默认配置。开发机的构建/隔离测试不能证明目标服务器的 IIS 站点、专属应用池、证书、网络或 SMTP 已验收。
SMTP 密码说明：SMTP 密码保存在数据库中，并使用 `JwtSecret` 派生的应用密钥加密。轮换 JWT 密钥后，已有 SMTP 密码会被标记为需要重新配置；轮换后请在系统参数页面重新保存 SMTP 凭据。
