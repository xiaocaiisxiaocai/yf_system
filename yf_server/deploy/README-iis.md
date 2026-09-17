# 在另一台 Windows 服务器正式部署

发布包包含前端、Rust 后端、迁移程序、WinSW 服务包装器和安装脚本。目标机无需 Node.js、npm、Rust、Git 或 Python。构建来源与工具版本见 deploy-manifest.json，逐文件校验见 SHA256SUMS.txt。

## 1. 在开发机生成发布包

从项目根目录运行 PowerShell 7.2+：

```powershell
pwsh -File .\yf_server\scripts\deploy-iis.ps1
```

默认生成 .runlogs\iis-release.zip 和 .runlogs\iis-release.zip.sha256。已有输出可加 -Clean，旧包会改名保留。工作区有未提交修改时需显式加 -AllowDirty，清单会如实记录；不影响包内程序执行。仅输出前后端、配置示例和安装工具，不复制生产数据或配置。

## 2. 目标机准备（只需首次完成）

- Windows Server x64，启用 IIS 静态内容、默认文档、匿名身份验证及管理工具。
- 安装 [URL Rewrite 2](https://www.iis.net/downloads/microsoft/url-rewrite) 和 [ARR](https://www.iis.net/downloads/microsoft/application-request-routing)。
- 安装 [Visual C++ x64 Runtime](https://learn.microsoft.com/en-us/cpp/windows/latest-supported-vc-redist)；WinSW 2.12.0 x64 自带 .NET 运行时。
- 将 HTTPS 证书（带私钥）导入本机计算机的个人证书存储 LocalMachine\My。域名必须匹配证书、解析到该服务器；域名访问和 HTTPS 端口需要通过网络／防火墙策略放行。脚本不创建 DNS 或防火墙规则。
- 准备 MySQL 实例和数据库；需要迁移时在服务器安装 MySQL 客户端 mysqldump。备份账号要有读取目标库、例程、触发器与事件所需权限。
- 创建独立上传存储目录，例如 D:\yf_storage。从包内 server\config.example.toml 复制到 C:\ProgramData\yf_system\config.local.toml，仅在目标机填写数据库、随机 JWT 密钥、存储路径及正式网址。此文件应仅允许管理员和 SYSTEM 访问，不放进 IIS 目录。数据库 URL 中的特殊密码字符需 URL 编码。

生产配置采用模板中的单行 TOML 写法，路径使用 D:/yf_storage。关键字段：

```toml
[server]
addr = "127.0.0.1:8080"
trust_loopback_proxy = true
[database]
url = "mysql://USER:URL_ENCODED_PASSWORD@DB_HOST:3306/yf_system"
auto_migrate = false
[storage]
root = "D:/yf_storage"
[jwt]
secret = "请替换为至少32字符的随机密钥"
cookie_secure = true
[web]
base_url = "https://yf.example.com"
```

上面仅展示关键字段，请保留完整示例中的其余配置。数据库 TLS 按实际 CA/主机名配置；模板默认要求验证数据库身份。

## 3. 复制并解压

把 ZIP 和外部 .sha256 一起复制到目标机，打开管理员 Windows PowerShell 5.1（64 位）或 PowerShell 7：

```powershell
$expected = ((Get-Content .\iis-release.zip.sha256 -Raw).Trim() -split '\s+')[0]
if ((Get-FileHash .\iis-release.zip -Algorithm SHA256).Hash -ne $expected) { throw 'ZIP 校验失败' }
Expand-Archive .\iis-release.zip -DestinationPath C:\Deploy\yf-release
Set-Location C:\Deploy\yf-release
Unblock-File .\install-iis.ps1
Get-ChildItem .\scripts -Filter *.ps1 | Unblock-File
```

解压目录每次用新的空目录，不能与程序、存储、备份目录重叠。保留发布包原始文件，不在其中创建生产配置或修改文件。

## 4. 首次部署或需要升级数据库

先在同一个管理员 PowerShell 会话准备参数。修改示例值以匹配目标服务器；密码隐藏输入，不写在命令行：

```powershell
function Read-DeploySecret([string]$Prompt) {
    $secure = Read-Host $Prompt -AsSecureString
    $ptr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
    try { [Runtime.InteropServices.Marshal]::PtrToStringBSTR($ptr) }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($ptr); $secure.Dispose() }
}
$deploy = @{
    HostName = 'yf.example.com'
    CertificateThumbprint = '填入实际证书指纹'
    ConfigPath = 'C:\ProgramData\yf_system\config.local.toml'
    SiteRoot = 'C:\inetpub\yf_system\site'
    ServerRoot = 'C:\Services\yf_system'
    BackupRoot = 'D:\DeployBackups\yf_system'
    MigrationMode = 'Up'
    DatabaseHost = 'DB_HOST'
    DatabasePort = 3306
    DatabaseName = 'yf_system'
    DatabaseUser = 'yf_backup'
}
$env:YF_DATABASE_URL = Read-DeploySecret '输入与配置 database.url 完全一致的迁移连接串'
$env:YF_BACKUP_DB_PASSWORD = Read-DeploySecret '备份账号密码'
try {
    .\install-iis.ps1 @deploy -Plan
    if (-not $?) { throw '预检失败，停止部署' }
    # 仅首次空库添加 -InitializeDatabase；脚本在停服务前隐藏询问初始管理员密码。
    .\install-iis.ps1 @deploy -InitializeDatabase
} finally {
    Remove-Item Env:\YF_DATABASE_URL, Env:\YF_BACKUP_DB_PASSWORD -ErrorAction SilentlyContinue
}
```

已有库升级时删除 -InitializeDatabase。Up 会先停止服务与站点，再备份数据库、完整存储及旧程序，然后运行 migration.exe up。迁移连接与备份主机／库名会和生产配置核对。首次创建的数据库也要先存在；存储目录可以为空。

**迁移 016 会清空旧项目业务聚合并登记文件清理，属于单向迁移。** 已有旧系统执行 Up 前请先核对源数据库版本和停写窗口；迁移已启动或新版后端启动后不能只回退程序，必须成套恢复数据库、存储及程序。程序备份不等于数据库可恢复性验收。

## 5. 数据库已完成本版本迁移，仅更新程序

```powershell
$deploy = @{
    HostName = 'yf.example.com'
    CertificateThumbprint = '填入实际证书指纹'
    ConfigPath = 'C:\ProgramData\yf_system\config.local.toml'
    MigrationMode = 'Skip'
}
.\install-iis.ps1 @deploy -Plan
if (-not $?) { throw '预检失败，停止部署' }
.\install-iis.ps1 @deploy
```

-Plan 是可选的只读预检；去掉它才正式部署。正式执行会再次预检。Skip 不运行迁移，也不备份业务数据库／存储，需已有本版本数据库与可用数据备份；仍会校验并备份程序文件。

## 6. 执行结果与故障处理

脚本会配置独立 IIS 应用池、HTTPS 绑定、SPA 回退和 /api 反向代理；ARR 代理启用是服务器级设置，影响该 IIS 实例。HTTP_X_FORWARDED_FOR 会覆盖为 REMOTE_ADDR。后端以 LocalService 运行，仅获程序读取、日志和指定存储目录修改权限。迁移以管理员会话执行。

程序备份失败时保留旧程序；开始替换后的任何失败会尽量停止本次服务及站点，保留新程序、日志、旧程序与数据备份供修复，不自动恢复 IIS 或数据库。成功输出仅表示后端 /health、IIS 首页、/login 深链接和验证码 API 通过；还需从用户电脑登录、上传／下载及验证权限。服务启动后日志在 ServerRoot\logs。

更改站点／服务名称或目录时不能直接接管同名资源；脚本会拒绝不匹配的旧目录、账号、端口、证书或嵌套／联接路径。若生产服务通过机器级环境变量覆盖配置，请先人工统一配置来源。

## 开源工具与来源

- [WinSW 2.12.0](https://github.com/winsw/winsw/releases/tag/v2.12.0)：Windows 服务包装器，MIT，许可证随包置于 tools\WinSW-LICENSE.txt。
- WinSW 来源、固定 SHA-256、构建工具版本见 deploy-manifest.json。固定哈希是在本次从官方 Release 获取后记录，用于重复构建一致性，不是上游签名验真。
- IIS 配置参考 [微软 URL Rewrite 文档](https://learn.microsoft.com/en-us/iis/extensions/url-rewrite-module/setting-http-request-headers-and-iis-server-variables)，服务配置参考 [WinSW 对应版本文档](https://github.com/winsw/winsw/blob/v2.12.0/doc/xmlConfigFile.md)。
