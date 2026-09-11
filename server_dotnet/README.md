# ASP.NET Core API 后端

独立的 .NET 10 后端。原 Rust 后端 `../yf_server` 保留；React 前端 `../web` 共用，接口仍为 `/api/v1`。本次新增在 `codex/aspnet-core-backend` 分支，不自动切换正在使用的服务。

## 功能与结构

- `Yf.Api/Modules/Identity`：验证码、登录、JWT、刷新会话轮换/重放撤销、个人资料与改密。
- `Yf.Api/Modules/Admin`：组织、账号、角色、供应商和供应商账号；权限委派上限与最后管理员保护。
- `Yf.Api/Modules/Projects`：项目、成员、提交/确认/驳回/撤回、留言/已读、动态、工作台。
- `Yf.Api/Modules/Files`：分片上传与续传、合并校验、下载、Range 预览、批量 ZIP、软删除和垃圾清理。
- `Yf.Api/Modules/System`：参数、存储容量、日志、邮件 outbox 与 TLS SMTP 后台发送。
- `Yf.Api/Infrastructure`：MySQL/Dapper、统一错误、事务权限门禁、审计及空库初始化。

采用 ASP.NET Core Minimal API；按业务模块拆分，数据库访问使用参数化 SQL。没有新增第二套业务数据模型。

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

## 数据库与双后端兼容

接入已有库要求最新迁移为 `m20260911_000017_auth_session_families`。启动只检查版本，不执行历史迁移。旧版本库先备份并按 Rust 已有迁移流程升级；新后端拒绝未知或不完整版本。

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

内嵌 schema 来源为同一仓库 Rust 的 17 个迁移，经隔离空库生成；只包含表结构、内置角色、权限、系统参数、迁移记录，不含业务数据、用户密码或测试账号。`scripts/export-baseline.py` 是维护工具，普通部署不需要 Python 或 Rust。

切换现有系统时：备份 MySQL 与文件目录，停止写入，使用相同数据库与存储路径；相同 JWT 密钥可保留格式兼容的会话，主动更换密钥会要求重新登录。先验证登录、权限、上传/下载和项目流程，再开放用户访问。回退时停止 .NET 后端，恢复 Rust 启动入口；本次新增不改变业务表结构。备份恢复会回退切换后的新写入，必须按停写窗口处理。

密码规则与当前前端/Rust 一致：12–64 个 Unicode 字符，最多 256 UTF-8 字节，拒绝常见弱密码和简单重复；已有 Argon2 PHC 密码可继续验证，不强制批量重置。

## 测试

在 `server_dotnet` 目录：

```powershell
dotnet test --project .\tests\Yf.Api.Tests.csproj
dotnet build .\Yf.Api\Yf.Api.csproj
python .\scripts\test-isolated.py
```

HTTP 测试需要 Python 3.11+ 与 `pymysql`，仅从旧后端本机私有配置读取 MySQL 管理连接，在随机命名的 `yf_test_dotnet_*` 库和临时存储目录运行；结束时只删除自己创建的测试资源，禁用邮件 worker，不发送真实邮件。它会真实执行账号、权限、会话、项目及文件接口。旧业务库不会成为测试库。

测试脚本中的 PDF 样本用于传输字节/Range 验证；这些检查不等同于在浏览器里渲染真实 PDF，也不等同于目标服务器 IIS 或真实 SMTP 验收。

## IIS 发布

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\publish-iis.ps1 -FreshOutputDirectory D:\Releases\YfDotNet-NEW
```

发布脚本参数以 `Get-Help .\scripts\publish-iis.ps1 -Detailed` 为准。输出目录必须是新目录或空目录。发布包包含后端、`wwwroot` 前端、IIS 配置、安装脚本、说明与 SHA-256 清单。将整个发布包复制到另一台服务器，按包内 `README.md` 安装。不会在开发电脑上自动部署 IIS。

开发机可运行 `python .\scripts\verify-release.py D:\Releases\YfDotNet-NEW.zip`，对解压出的真实发布程序核对 ZIP/清单哈希、安全配置、缺配置启动拒绝、前端静态页面及隔离 HTTP 测试。可选的 `YF_TEST_RUST_EXE` 指向已构建的 Rust `server.exe`，会额外在同一临时库上依次切换两个后端，验证 JWT 与刷新会话双向兼容；不会同时运行两个服务。

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

准确传递依赖版本见各项目 `packages.lock.json`。前端依赖与许可沿用 `web/package-lock.json` 及发布的第三方许可文件。持续维护时两套后端的接口、安全策略与数据库版本需要一起更新；不能只改一套却继续宣称行为等价。
