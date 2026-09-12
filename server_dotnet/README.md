# ASP.NET Core API 后端

当前维护的独立 .NET 10 后端，配套 React 前端位于 `../web`，接口为 `/api/v1`。原 Rust 目录 `../yf_server` 仅保留归档参考，不作为运行、测试或升级依赖。源码合并不会自动切换正在使用的服务。

## 功能与结构

- `Yf.Api/Modules/Identity`：验证码、登录、JWT、刷新会话轮换/重放撤销、个人资料与改密。
- `Yf.Api/Modules/Admin`：组织、账号、角色、供应商和供应商账号；权限委派上限与最后管理员保护。
- `Yf.Api/Modules/Projects`：项目、成员、提交/确认/驳回/撤回、留言/已读、动态、工作台。
- `Yf.Api/Modules/Files`：分片上传与续传、合并校验、下载、Range 预览、批量 ZIP、软删除和垃圾清理。
- `Yf.Api/Modules/System`：参数、存储容量、日志、邮件 outbox 与 TLS SMTP 后台发送。
- `Yf.Api/Infrastructure`：MySQL/Dapper、统一错误、事务权限门禁、审计及空库初始化。

采用 ASP.NET Core Minimal API；按业务模块拆分，数据库访问使用参数化 SQL。沿用现有业务数据，后续接口、数据库升级与测试由 .NET 独立维护；Rust 源码仅保留归档参考。

## 本地运行

需要 .NET 10 SDK、MySQL（现有结构兼容 MySQL 5.7/8）以及现有数据对应的独立存储目录。

1. 将 `deploy/appsettings.example.json` 复制到 **IIS 网站以外**的私有目录，填写连接串、随机 JWT 密钥、存储绝对路径和网站来源。该文件包含机密，不要提交版本库。
2. 本地 HTTP 调试设置 `CookieSecure=false`，`WebBaseUrl=http://127.0.0.1:5173`。正式 HTTPS 必须为 `true`。
3. 在 `server_dotnet` 目录执行：

```powershell
$env:YF_CONFIG_PATH = 'D:\YfConfig\appsettings.Local.json'
$env:ASPNETCORE_URLS = 'http://127.0.0.1:8080'
dotnet run --project .\Yf.Api
```

另一个终端在 `web` 目录执行：

```powershell
npm ci
npm run dev
```

前端开发代理仍指向 `127.0.0.1:8080`。先停止你确认属于该项目的旧后端，再用新后端占用此端口；不要同时让两个后端写同一套业务库与文件目录。若需并行评估，使用独立数据库、存储、监听端口及前端代理。

配置优先级：`appsettings.json` → 当前环境配置 → `appsettings.Local.json` → `YF_CONFIG_PATH` 指定文件 → 环境变量 → 命令行。本地直接运行时可使用外部配置或进程级 `App__ConnectionString` / `App__JwtSecret` 环境变量，避免在命令行出现密码。正式 IIS 安装和维护只支持 `YF_CONFIG_PATH` 指向的网站外部 JSON 主配置，并拒绝站点、应用池、机器或维护进程中的 `App__*` / `App:*` 高优先级覆盖。配置更改后重启应用。

## 数据库初始化与升级

启动只校验结构和 `.NET` 迁移历史，不自动执行 DDL。新安装直接初始化；导入现有第 16 或 17 版结构时，先停写、备份数据库和文件，再运行：

```powershell
$env:YF_CONFIG_PATH = 'D:\YfConfig\appsettings.Local.json'
dotnet run --project .\Yf.Api -- --migrate-database
```

升级命令使用数据库命名锁防止同时迁移，校验业务表完整性，并建立 `yf_schema_migrations` 独立历史。第 16 版缺失的刷新会话族字段、数据回填与索引由 .NET 补齐；第 17 版直接接管；同时撤销供应商角色的内部管理授权并记录审计。步骤可在 DDL 中断后重跑，重复执行不删除业务数据。启动拒绝未知版本或被修改的历史。后续升级在 `Infrastructure/SchemaMigrations.cs` 中维护。

结构预检以内嵌 `schema-baseline.json` 为依据，检查列定义、主键、唯一键、业务索引列顺序、外键规则及表引擎；现有 `.NET` 迁移表本身也须满足结构和历史要求。不支持的结构漂移会在迁移命令执行 DDL、回填数据或记录历史之前被拒绝，不自动修复业务表。第 16 版缺少会话族列/索引及合法的中断状态可继续升级。

第 15 版及更早的旧业务结构包含有损工作流转换，不在本次自动导入范围内；命令会在改动前拒绝，需要另行审查数据转换和备份恢复方案，不依赖运行 Rust 升级。

新安装可由 DBA 先创建空库和专用账号，然后执行独立初始化：

```powershell
$env:YF_CONFIG_PATH = 'D:\YfConfig\appsettings.Local.json'
$secret = Read-Host '初始管理员密码（12–64字符，禁止常见弱密码）' -AsSecureString
$credential = New-Object System.Net.NetworkCredential('', $secret)
$env:YF_BOOTSTRAP_PASSWORD = $credential.Password
try { dotnet run --project .\Yf.Api -- --initialize-database }
finally { Remove-Item Env:\YF_BOOTSTRAP_PASSWORD; $credential = $null; $secret = $null }
```

初始化创建 `admin` 并强制首次改密，不输出密码。非空数据库一律拒绝；MySQL DDL 无法完整回滚，若初始化失败会保留失败现场，不会自动删库。排查并由数据库管理员确认后，换一个空库重试。

内嵌初始 schema 是第 17 版的结构快照，只包含表结构、内置角色、权限、系统参数与导入来源历史，不含业务数据、密码或测试账号。历史 `seaql_migrations` 仅作为来源记录；运行版本以 `yf_schema_migrations` 为准。`scripts/export-baseline.py` 只从 .NET 初始化的临时库导出种子，不调用其他后端。维护时使用 `python .\scripts\export-baseline.py --output D:\Temp\待审查基线.json` 导出到新文件；审查差异后再更新内嵌快照，工具拒绝覆盖现有文件。

切换现有系统必须先备份 MySQL、文件与配置，停止旧入口写入，再执行显式迁移和 .NET 启动。验证登录、权限、上传/下载和项目流程后开放访问。失败回退按该次部署的程序与数据库备份成套恢复；不承诺新旧后端可以互换运行或共享会话。

密码规则：12–64 个 Unicode 字符，最多 256 UTF-8 字节，拒绝常见弱密码和简单重复；已有 Argon2 PHC 密码继续验证，不强制批量重置。

## 测试

在 `server_dotnet` 目录：

```powershell
dotnet test --project .\tests\Yf.Api.Tests.csproj
dotnet build .\TestHost\Yf.Api.TestHost.csproj
# 仅在当前进程设置本机测试管理连接，勿将真实凭据写入命令历史或版本库。
# $env:YF_TEST_DATABASE_URL 的格式为 mysql://账号:URL编码密码@127.0.0.1:3306/ignored
python .\scripts\test-isolated.py

# 真实维护备份/恢复测试；同样必须显式设置上面的本机测试管理连接。
python .\scripts\test-maintenance.py
```

HTTP 测试需要 Python 3.11+ 与 `pymysql`，仅使用显式设置的 `YF_TEST_DATABASE_URL`（本机 MySQL 测试管理账号，需创建/删除测试库及查看锁等待）。不读取 Rust 配置。测试在随机命名的 `yf_test_dotnet_*` 库与临时存储中运行，结束只删除自身资源；邮件发送禁用，通知验证仅检查 outbox。

`test-maintenance.py` 还要求本机 `mysql.exe` 与 `mysqldump.exe` 可用，并使用随机命名的 `yf_test_maintenance_*` 数据库验证真实 MySQL、程序和存储字节的备份/恢复、篡改拒绝及非空目标拒绝。它只接受 `localhost`、`127.0.0.1` 或 `::1` 的显式 `YF_TEST_DATABASE_URL`，会创建并删除自身测试数据库；客户端应为与测试 MySQL 兼容的 5.7 或更高版本。该测试不启动或操作 IIS，不验证目标服务器的站点、专属应用池、HTTPS 证书、权限或网络。

先测试实际生产入口的启动、健康和验证码返回，再通过独立 `TestHost` 执行完整 HTTP 用例。测试宿主使用同一 API 工厂，仅注册内存 CAPTCHA 观察器和带随机密钥的一次性答案路由，绑定回环地址；生产 API 不注册该观察器、不映射答案路由，发布包不包含测试宿主。验证码生产图像不再采用可由固定像素解码的数码管字体，并对发放与登录尝试限速；这不代表其能抵抗所有 OCR。

`tests/Contracts/api-v1.json` 固定前端 HTTP 方法/路径契约，不再解析 Rust 路由。项目回归验证真实数据库锁等待、并发提交与撤回；上传回归验证慢请求、并发初始化、中断恢复及文件清理。

测试脚本中的 PDF 样本用于传输字节/Range 验证；这些检查不等同于在浏览器里渲染真实 PDF，也不等同于目标服务器 IIS 或真实 SMTP 验收。

### 浏览器自动化

`scripts/test-browser.py` 使用已安装的 Chrome 和现有 Playwright `run.js`，验证构建后的 React 与同一 ASP.NET API 的登录、改密、管理操作、项目协作、文件预览下载以及页面布局。先构建对应源码，再运行；脚本不会安装依赖、启动业务实例或读取项目私有配置。

```powershell
dotnet build .\TestHost\Yf.Api.TestHost.csproj --no-restore
Push-Location ..\web
try { npm run build } finally { Pop-Location }
# 显式设置本机测试管理连接 YF_TEST_DATABASE_URL（与 HTTP 测试相同）。
$env:YF_PLAYWRIGHT_RUNNER = 'C:\你的工具目录\playwright-skill\run.js'
python .\scripts\test-browser.py --output ..\.runlogs\browser-NEW
```

每次使用新的空证据目录。结果含实际步骤、源码与被测构建产物 SHA-256、截图、下载完整性及资源清理状态；测试期间修改源码或产物会使本轮校验失败。它创建随机临时数据库、文件目录、账号和独立回环测试宿主，保留生产限速，验证码答案仅通过测试宿主观察器获取，邮件发送关闭。结束只清理本轮资源与私有登录状态。

`--steps auth fixtures system` 可单独检查系统参数和日志；`--steps auth fixtures project-edges access` 检查项目筛选分页、终止重启、异常重试和权限变化、多标签换号。`business` 必须在 `users` 后，`final` 和 `layout` 必须在 `business` 后。完整默认步骤对应 `scripts/browser` 中的脚本，不代表覆盖所有状态组合、实际移动设备、目标 IIS 或外部 SMTP。

## IIS 发布

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\publish-iis.ps1 -FreshOutputDirectory D:\Releases\YfDotNet-NEW
```

发布脚本参数以 `Get-Help .\scripts\publish-iis.ps1 -Detailed` 为准。输出目录必须是新目录或空目录。发布包包含后端、`wwwroot` 前端、IIS 配置、安装脚本、说明与 SHA-256 清单。将整个发布包复制到另一台服务器，按包内 `README.md` 安装。不会在开发电脑上自动部署 IIS。

开发机可运行 `python .\scripts\verify-release.py D:\Releases\YfDotNet-NEW.zip`，先核对解压 ZIP 的全部清单哈希、安全配置与缺配置启动拒绝，实测生产入口、静态页面；完整 HTTP 测试由独立宿主加载发布包中的同一 API 二进制与依赖。报告区分这些证据，不将开发机检查表述为目标 IIS/SMTP 验收。

## IIS 正式服务器维护

发布包同时包含 `maintain-iis.ps1` 与 `maintenance-common.ps1`。它们只维护已经存在、使用专属应用池且没有子应用的同名 IIS 站点；应用池必须使用 `ApplicationPoolIdentity` 且不加载用户 profile。正式站点必须通过唯一 `YF_CONFIG_PATH` 使用网站外部 JSON，保持 HTTPS `WebBaseUrl` 和 `CookieSecure=true`，并以 in-process 的 `dotnet .\Yf.Api.dll` 标准形式启动。维护不调用 Rust，也不支持 `App__*` / `App:*`、额外 `YF_CONFIG_PATH` 或命令行配置覆盖。

在目标服务器管理员 Windows PowerShell 5.1 中，从发布包根执行。`mysql.exe`、`mysqldump.exe` 使用与服务器兼容的 5.7 或更高版本；不在 `PATH` 时传绝对路径：

```powershell
# 当前站点的完整离线备份
.\maintain-iis.ps1 -Action Backup -SiteName 'YfSystemDotNet' `
  -BackupDirectory 'E:\YfBackups\2026-09-11-before-upgrade' `
  -MySqlDump 'C:\Program Files\MySQL\MySQL Server 8.0\bin\mysqldump.exe'

# 先备份，再把已核对的新包切换到新空程序目录；仅需升级 schema 时保留 -MigrateDatabase
.\maintain-iis.ps1 -Action Upgrade -SiteName 'YfSystemDotNet' `
  -BackupDirectory 'E:\YfBackups\2026-09-11-before-upgrade' `
  -PackageRoot 'D:\Packages\YfDotNet-NEW' `
  -NewSiteRoot 'C:\inetpub\yf_system_dotnet_20260911' `
  -MySqlDump 'C:\Program Files\MySQL\MySQL Server 8.0\bin\mysqldump.exe' `
  -MigrateDatabase

# 使用同一 SiteName 的备份恢复；配置必须指向另一套新空数据库和新空存储
.\maintain-iis.ps1 -Action Restore -SiteName 'YfSystemDotNet' `
  -BackupDirectory 'E:\YfBackups\2026-09-11-before-upgrade' `
  -NewSiteRoot 'C:\inetpub\yf_system_dotnet_restore_20260911' `
  -RestoreConfigPath 'D:\YfConfig\appsettings.Restored.json' `
  -MySql 'C:\Program Files\MySQL\MySQL Server 8.0\bin\mysql.exe'
```

Restore 的新数据库名必须不同于当前数据库，执行前由 DBA 确认其中表、routines、events 全部为空；新程序目录、新存储和新外部配置也必须与当前资源及备份互相独立。恢复沿用备份内程序和 schema，不能同时传 `-MigrateDatabase`，并且恢复配置必须保持当前站点完全相同的 HTTPS origin。备份包含外部配置、数据库、程序和存储，也包含密钥与业务数据，必须在网站目录之外使用受限 ACL 和加密备份介质保护，禁止提交版本库或长期放在普通共享目录。

维护期间脚本只停止该命名站点的专属应用池，并等待其 worker 全部退出；它不能排除其他 IIS 站点、服务、计划任务或远程实例写同一数据库和存储，管理员必须在维护窗口前停止所有外部写入者。任一步骤或恢复启动后的 HTTPS `/health` 检查失败，应用池保持停止。旧数据库、存储和程序不会被删除，新数据库导入失败时可能留有部分数据；调查后换新的空目标重试。完整参数、路径隔离、回退和灾难恢复步骤见发布包内 `README.md`。新机器必须先用备份对应版本的 `install-iis.ps1` 创建同名站点，再运行 Restore；该流程不承诺从裸机一键恢复。

当前开发机未创建或操作真实 IIS 站点，只能读取 Microsoft.Web.Administration 默认配置；维护测试不构成目标服务器 IIS、证书、权限或网络验收。

## 依赖与来源

| 组件 | 版本 | 仓库/官方来源 | 用途 |
|---|---|---|---|
| ASP.NET Core | .NET 10 | https://github.com/dotnet/aspnetcore | HTTP、路由、IIS、静态文件 |
| Dapper | 2.1.79 | https://github.com/DapperLib/Dapper | 参数化查询映射 |
| MySqlConnector | 2.6.2 | https://github.com/mysql-net/MySqlConnector | MySQL 异步驱动 |
| Konscious Argon2 | 1.3.1 | https://github.com/kmaragon/Konscious.Security.Cryptography | 兼容已有 Argon2 密码 |
| IdentityModel JWT | 8.22.0 | https://github.com/AzureAD/azure-activedirectory-identitymodel-extensions-for-dotnet | JWT 签发校验 |
| MailKit | 4.17.0 | https://github.com/jstedfast/MailKit | TLS SMTP |
| xUnit v3 | 4.0.0 | https://github.com/xunit/xunit | 自动化测试 |

准确传递依赖版本见各项目 `packages.lock.json`。前端依赖与许可沿用 `web/package-lock.json` 及发布的第三方许可文件。新功能、安全修复、契约与数据库版本以本目录的 .NET 后端为维护入口，不要求同步 Rust。
