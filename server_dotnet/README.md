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

配置优先级：`appsettings.json` → 当前环境配置 → `appsettings.Local.json` → `YF_CONFIG_PATH` 指定文件 → 环境变量 → 命令行。推荐机密使用外部配置或 `App__ConnectionString` / `App__JwtSecret` 环境变量，避免在命令行出现密码。配置更改后重启应用。

## 数据库初始化与升级

启动只校验结构和 `.NET` 迁移历史，不自动执行 DDL。新安装直接初始化；导入现有第 16 或 17 版结构时，先停写、备份数据库和文件，再运行：

```powershell
$env:YF_CONFIG_PATH = 'D:\YfConfig\appsettings.Local.json'
dotnet run --project .\Yf.Api -- --migrate-database
```

升级命令使用数据库命名锁防止同时迁移，校验业务表完整性，并建立 `yf_schema_migrations` 独立历史。第 16 版缺失的刷新会话族字段、数据回填与索引由 .NET 补齐；第 17 版直接接管；同时撤销供应商角色的内部管理授权并记录审计。步骤可在 DDL 中断后重跑，重复执行不删除业务数据。启动拒绝未知版本或被修改的历史。后续升级在 `Infrastructure/SchemaMigrations.cs` 中维护。

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
```

HTTP 测试需要 Python 3.11+ 与 `pymysql`，仅使用显式设置的 `YF_TEST_DATABASE_URL`（本机 MySQL 测试管理账号，需创建/删除测试库及查看锁等待）。不读取 Rust 配置。测试在随机命名的 `yf_test_dotnet_*` 库与临时存储中运行，结束只删除自身资源；邮件发送禁用，通知验证仅检查 outbox。

先测试实际生产入口的启动、健康和验证码返回，再通过独立 `TestHost` 执行完整 HTTP 用例。测试宿主使用同一 API 工厂，仅注册内存 CAPTCHA 观察器和带随机密钥的一次性答案路由，绑定回环地址；生产 API 不注册该观察器、不映射答案路由，发布包不包含测试宿主。验证码生产图像不再采用可由固定像素解码的数码管字体，并对发放与登录尝试限速；这不代表其能抵抗所有 OCR。

`tests/Contracts/api-v1.json` 固定前端 HTTP 方法/路径契约，不再解析 Rust 路由。项目回归验证真实数据库锁等待、并发提交与撤回；上传回归验证慢请求、并发初始化、中断恢复及文件清理。

测试脚本中的 PDF 样本用于传输字节/Range 验证；这些检查不等同于在浏览器里渲染真实 PDF，也不等同于目标服务器 IIS 或真实 SMTP 验收。

## IIS 发布

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\publish-iis.ps1 -FreshOutputDirectory D:\Releases\YfDotNet-NEW
```

发布脚本参数以 `Get-Help .\scripts\publish-iis.ps1 -Detailed` 为准。输出目录必须是新目录或空目录。发布包包含后端、`wwwroot` 前端、IIS 配置、安装脚本、说明与 SHA-256 清单。将整个发布包复制到另一台服务器，按包内 `README.md` 安装。不会在开发电脑上自动部署 IIS。

开发机可运行 `python .\scripts\verify-release.py D:\Releases\YfDotNet-NEW.zip`，先核对解压 ZIP 的全部清单哈希、安全配置与缺配置启动拒绝，实测生产入口、静态页面；完整 HTTP 测试由独立宿主加载发布包中的同一 API 二进制与依赖。报告区分这些证据，不将开发机检查表述为目标 IIS/SMTP 验收。

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
