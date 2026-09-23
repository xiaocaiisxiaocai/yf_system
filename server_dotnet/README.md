# ASP.NET Core API 后端

当前维护的独立 .NET 8 后端，配套 React 前端位于 `../web`，接口为 `/api/v1`。原 Rust 归档目录 `yf_server` 已从仓库移除（历史版本仍可通过 git 记录查阅），不作为运行、测试或升级依赖。数据库由 EF Core Code-First 迁移管理；开发阶段不兼容旧 schema 或旧数据。

新库初始化只创建 `admin` 用户和系统管理员角色，其他角色由管理员手工配置。已有开发数据可通过独立清理命令重置，同时保留 SMTP、系统参数和 admin 原密码，见[开发数据初始化说明](../docs/开发数据初始化说明.md)。

## 功能与结构

- `Yf.Api/Modules/Identity`：工号密码登录、JWT、刷新会话轮换/重放撤销、个人资料与改密。
- `Yf.Api/Modules/Admin`：组织、账号、角色、供应商和供应商账号；权限委派上限与最后管理员保护。
- `Yf.Api/Modules/Projects`：主项目、子项目、提交/确认/驳回/撤回、留言/已读、动态、工作台。
- `Yf.Api/Modules/Files`：分片上传与续传、合并校验、下载、文档/视频 Range 预览、批量 ZIP、软删除和垃圾清理。
- `Yf.Api/Modules/System`：参数、日志、邮件 outbox 与 TLS SMTP 后台发送。
- `Yf.Api/Infrastructure`：EF Core/MySQL、统一错误、事务权限门禁、审计及空库初始化。

当前协作模型是“主项目 + 子项目”。主项目集中维护供应商、工令号、机型、Robot 信息、负责人、课别、优先级和预计完成日期；创建时至少同时创建一个子项目。文件、留言、动态与验收只属于子项目。普通内部用户按唯一当前负责人访问，拥有 `project:view_all` 的内部管理员可全局查看；供应商企业下全部启用账号共享该企业的项目范围，系统不再使用项目成员关系。

项目验收统一由公司内部处理。只有供应商账号可使用 `project:submit` 提交进行中的子项目；内部账号提交会被后端拒绝。确认／驳回需要启用的内部账号、`project:confirm` 和项目访问权限。提交版本、负责人变更和已完成子项目的冻结均由服务端校验并留痕；负责人仍负责未结束主项目时，禁用、撤销项目权限或调岗前必须先交接，被项目或历史记录引用的账号不能删除。供应商账号不能验收，即使存在历史遗留的确认权限也会被后端拒绝。详细规则见[主项目与子项目协作契约](docs/主项目与子项目协作契约-2026-09-16.md)。

后续业务逻辑复查补齐了申请版本、防止无验收人提交、通知与授权范围一致性、负责人完整性和旧成员模型清理。确认、驳回、撤回必须带所查看申请的 `expectedSubmissionId`；旧版本返回 409。数据库结构由 EF Core Code-First 迁移维护。见[主项目与子项目协作契约](docs/主项目与子项目协作契约-2026-09-16.md)。

采用 ASP.NET Core Minimal API；按业务模块拆分，普通业务查询和写入使用 EF Core，必须使用的 MySQL 行锁和命名锁采用参数化原生 SQL。后续接口、数据库升级与测试均由 .NET 独立维护。

## 删除操作

物理删除由独立权限点控制：`project:delete`、`file:delete`、`supplier:delete`、`supplier:account_delete`、`user:delete`、`dept:delete`、`role:delete`、`log:delete` 和既有的 `message:delete_any`。新增权限只授予系统管理员角色，之后可在角色页按职责分配；管理页访问和其他编辑操作仍使用各自的菜单与管理权限。后端会在删除事务内重新校验权限，防止并发撤权后继续执行。

| 对象 | 删除条件与保留规则 |
|---|---|
| 子项目 | 仅草稿或已终止状态可删除，且不能有文件、留言或上传记录；锁定项目后重新校验，不级联删除业务内容 |
| 用户、供应商账号 | 无业务历史或引用；有关联记录时使用禁用，保留创建人、确认人和已读凭证 |
| 供应商 | 无项目且无账号；不级联删除账号 |
| 组织、角色 | 有下级组织或绑定用户时拒绝删除；内置角色可分配权限、禁用或删除，名称保持固定 |
| 操作日志 | 手动清理与 `AUDIT_LOG_DELETE` 留痕同事务，记录实际删除 ID 和数量；清理记录本身不可手动删除。后台每小时自动删除超过 `App:AuditRetentionDays`（默认 30 天）的日志（含清理记录），并写入一条 `AUDIT_LOG_RETENTION` 汇总；项目动态另存于 `project_activities`，不受影响 |

系统管理员角色绑定启用用户时必须保留用户管理和角色管理入口，避免管理员在权限分配时锁死系统；角色禁用和删除仍受绑定用户校验约束。删除相关的越权与保留规则回归包含在 `python scripts/test-isolated.py` 完整套件内，脚本只允许本机 MySQL，创建并清理临时测试库，不使用业务库运行删除测试。

## 本地运行

需要 .NET 8 SDK、MySQL（现有结构兼容 MySQL 5.7/8）以及现有数据对应的独立存储目录。

1. 将 `deploy/appsettings.example.json` 复制到 **IIS 网站以外**的私有目录，填写连接串、随机 JWT 密钥、存储绝对路径和网站来源。该文件包含机密，不要提交版本库。
2. 本地 HTTP 调试设置 `CookieSecure=false`，`WebBaseUrl=http://127.0.0.1:5180`。正式 HTTPS 必须为 `true`。
3. 在 `server_dotnet` 目录执行：

```powershell
$env:YF_CONFIG_PATH = 'D:\YfConfig\appsettings.Local.json'
dotnet run --project .\Yf.Api --launch-profile Yf.Local
```

另一个终端在 `web` 目录执行：

```powershell
npm ci
npm run dev -- --host 127.0.0.1 --port 5180 --strictPort
```

前端开发代理仍指向 `127.0.0.1:8080`。先停止你确认属于该项目的旧后端，再用新后端占用此端口；不要同时让两个后端写同一套业务库与文件目录。若需并行评估，使用独立数据库、存储、监听端口及前端代理。

配置优先级：`appsettings.json` → 当前环境配置 → `appsettings.Local.json` → `YF_CONFIG_PATH` 指定文件 → 环境变量 → 命令行。本地直接运行时可使用外部配置或进程级 `App__ConnectionString` / `App__JwtSecret` 环境变量，避免在命令行出现密码。正式 IIS 安装和维护只支持 `YF_CONFIG_PATH` 指向的网站外部 JSON 主配置，并拒绝站点、应用池、机器或维护进程中的 `App__*` / `App:*` 高优先级覆盖。配置更改后重启应用。

### 开发启动前检查

`Yf.Local` 启动配置使用 `Development` 环境并监听 `http://127.0.0.1:8080`，不自动打开浏览器。可在仓库根目录先运行：

```powershell
# 首次使用先还原锁定依赖；之后检查脚本每次重新构建当前后端。
dotnet restore .\server_dotnet\Yf.Api\Yf.Api.csproj --locked-mode
powershell -NoProfile -File .\server_dotnet\scripts\check-dev.ps1
# 使用外部配置时显式指定与启动相同的文件：
powershell -NoProfile -File .\server_dotnet\scripts\check-dev.ps1 -ConfigPath 'D:\YfConfig\appsettings.Local.json'
```

脚本默认检查 `Yf.Api/appsettings.Local.json`，输出 JSON，失败返回非零。它先构建源码，再检查实际生效的配置、数据库迁移和存储目录的临时文件读写与清理。检查不执行数据库初始化或清理，不创建业务记录、不发送邮件、不启动后台任务。已有 `App__*` 环境变量仍参与配置覆盖，检查和正常启动应使用相同环境。

`readyForStartup=true` 仅表示本次依赖检查通过，不能证明 API 已监听。启动后还需检查 `http://127.0.0.1:8080/health` 与实际登录/业务接口；现有 `/health` 只检查数据库连接。

常见检查结果：

| `issues` 代码 | 处理方向 |
|---|---|
| `configuration-invalid` / `non-loopback-target` | 核对 JSON、存储路径隔离及本机数据库/网站地址；本工具仅用于本地开发 |
| `database-not-ready` | 核对数据库连通性、账号授权及 EF 迁移历史；不要直接清空数据库 |
| `storage-read-write-failed` | 核对存储目录是否存在、当前用户读写删除权限和磁盘状态 |
| `readiness-timeout` | 依赖检查超过 60 秒；核对数据库响应和存储 I/O |


## 数据库初始化与升级

### 网页邮箱设置

系统设置不再显示存储状态或存储告警阈值，也不再生成或发送存储告警通知。旧数据库中的阈值与历史告警记录保留但不再使用；文件上传、独立存储目录校验和单文件限制照常执行。

具有 `config:manage` 权限的内部管理员可在“系统参数 → 邮件发送”配置 SMTP 服务器、端口、登录账号、发件邮箱、授权码和 TLS/STARTTLS，并在“邮件提醒规则”中独立控制邮件总开关、内部员工/外部企业（供应商）收件对象，以及新留言、新文件、提交验收、验收通过、验收驳回和撤回验收六类消息。保存成功后用于发送任务的后续批次，无需重启；关闭规则不会创建新的对应邮件，已入队邮件在 SMTP 发送前会再次校验并取消。授权码留空时保留原值；更换服务器或登录账号时必须重新填写授权码，避免将原凭据发送给另一个服务器。保存本身不会发出测试邮件。

网页设置保存在 `system_configs` 的内部项 `mail.smtp`，普通参数接口不会读取或修改此项。授权码使用 AES-GCM 加密，接口及审计均不回显；密钥从私有 `App:JwtSecret` 按专用用途派生，备份恢复必须同时保留数据库与应用配置。更换 JWT 密钥后应在页面重新填写授权码，页面会提示凭据需要更新。网页尚未保存时继续使用配置文件的 `App:Smtp`。配置文件 `Security` 默认为 `Auto`（465 使用 TLS，其他端口使用 STARTTLS），也可指定 `SslOnConnect` 或 `StartTls`；不提供明文连接选项。

### 初始化与升级命令

对已有非空数据库，启动只读核对 `__EFMigrationsHistory` 是否与当前程序集的迁移集合完全一致，不自动执行升级 DDL。开启 `AutoInitializeDatabase` 时，不存在的数据库或空库会在首次启动执行建库和初始化迁移。开发阶段不接管旧的手写 schema 或历史数据；数据库缺少 EF 历史、历史为空或包含未知迁移时会拒绝启动和升级，应重建空开发库。

已有 EF 管理的开发库应用新增迁移时运行：

```powershell
$env:YF_CONFIG_PATH = 'D:\YfConfig\appsettings.Local.json'
dotnet run --project .\Yf.Api -- --migrate-database
```

当前移除 OEM 的升级迁移会删除 OEM 表和专属数据。对已有数据库执行前必须完成可验证的备份；仅更新程序而不执行迁移时，启动校验会因迁移未完成而拒绝启动。

升级命令使用数据库命名锁防止并发迁移，只接受已经由 EF 历史管理的非空数据库，然后调用当前程序集内的生成迁移。空数据库必须使用 `--initialize-database`，由 `InitialCreate` 建表后在一个 EF 事务内创建 `admin`、系统管理员角色、35 个权限及 13 个系统参数。MySQL DDL 不能完整回滚；迁移中断后不要手工补历史，应检查现场并重建开发库。

在线文件预览支持 PDF、XLS、XLSX、PPTX，以及 PNG、JPG、JPEG、GIF、WebP、BMP 图片，统一使用 `GET /api/v1/files/{id}/content`，并受 50 MiB 单文件上限、`file:preview` 权限和项目可见范围约束。图片响应按扩展名返回规范 MIME 类型，不信任历史文件记录中的 MIME。视频预览支持 MP4、WebM 和 OGV，不整文件缓冲，也不使用文档/图片预览上限；浏览器先用正常 Bearer 会话调用 `POST /api/v1/files/{id}/media-session`，接口返回同源 `url` 和 300 秒有效期并设置仅限该文件媒体路径的 HttpOnly Cookie。随后原生 `<video>` 对 `GET /api/v1/files/{id}/media` 发起 Range 请求，每次请求都重新验证登录会话、账号状态、预览权限和项目范围。前端可在有效期过半时续签，媒体 URL 保持不变。

留言截图由 `message_images` 表管理，文件存放在私有存储根目录的 `message-images` 子目录，不发布到 `wwwroot`。`POST /api/v1/projects/{id}/messages` 同时支持原 JSON 正文和 multipart 的 `content` + 重复 `images`；每条最多 9 张、图片合计最多 50 MiB，单张还受系统参数 `upload.max_file_size` 的较小值约束。只接受 PNG、JPEG、GIF、WebP、BMP，并同时校验扩展名和文件签名。读取使用认证接口 `GET /api/v1/messages/{messageId}/images/{imageId}`，每次重新校验正常留言和项目可见范围，不使用 `file:preview` 权限。

项目模型包含工令号、机台机型、机器人厂商/型号、负责人及其直属课别、优先级和预计完成日期，并提供可维护的项目字典接口。字段、权限和停用/删除规则见 [项目元数据与字典契约](docs/项目元数据与字典契约-2026-09-15.md)。

项目复制与引用履历采用两阶段快照：事务外流式校验并复制物理文件，提交前重新锁定和核对源项目、工令及文件集合，避免大文件 I/O 长时间占用管理事务；复制不会带入留言、回执、历史动态或邮件。接口、权限、失败清理和脱敏规则见 [项目复制与引用履历契约](docs/项目复制与引用履历契约-2026-09-15.md)。

项目协作采用“主项目 + 子项目”。主项目集中维护供应商、工令号、机型、Robot 信息、负责人、课别、优先级和预计完成日期；文件、留言、动态与验收只属于子项目。最后一个子项目验收完成时，主项目在同一事务内自动完成。详细规则见 [主项目与子项目协作契约](docs/主项目与子项目协作契约-2026-09-16.md)。

负责人完整性约束要求 `project_groups.responsible_user_id` 只能引用内部负责人，项目访问按负责人或 `project:view_all` 判定；系统不使用项目成员表或成员权限。主项目业务资料更新不会覆盖已完成子项目；已完成子项目保持可读和可下载，业务内容冻结，但负责人和课别仍随主项目交接。

当前模型包含邮件提醒规则参数：总开关 `notify.enabled`、收件对象开关 `notify.internal.enabled` / `notify.supplier.enabled`，以及六类协作消息开关 `notify.event.*`。新库由 EF 种子写入默认开启项，后续调整由系统参数接口维护。

新库的默认上传白名单已包含 PPTX、MP4、WebM、OGV 和上述六种图片格式。现有库的管理员自定义白名单不会在启动时改写；需要启用这些格式时，先取得具有 `config:manage` 权限的短时访问令牌，再运行 `scripts/append-preview-upload-extensions.ps1`。脚本通过管理 API 只提交 `upload.allowed_exts`，仅补齐缺失类型，并回读核对其他公开系统参数未变化；空白值表示不限制类型，脚本会原样保留并报告 `unrestricted=true`。不要把令牌字面量写入命令历史。

`Yf.Api/Infrastructure/Migrations` 及 `YfDbContextModelSnapshot` 是唯一 schema 权威。不要使用 `EnsureCreated`、手写建表脚本或直接修改 `__EFMigrationsHistory`；模型变化通过 `dotnet ef migrations add` 生成迁移并审查差异。

IIS 发布包默认启用 `App.AutoInitializeDatabase=true`：首次启动自动创建不存在的数据库、对空库执行 EF 迁移并创建管理员。初始密码在发布包 `appsettings.Production.json` 的 `App.BootstrapPassword`，首次登录强制修改；后续重启不会重置账号。非空数据库仍只校验，不自动执行升级迁移（包括删除 OEM 数据的迁移）。MySQL 账号必须具备目标库创建和建表权限。

不开启自动初始化时，也可由 DBA 先创建空库和专用账号，然后执行独立初始化：

```powershell
$env:YF_CONFIG_PATH = 'D:\YfConfig\appsettings.Local.json'
$secret = Read-Host '初始管理员密码（6–20字符，禁止常见弱密码）' -AsSecureString
$credential = New-Object System.Net.NetworkCredential('', $secret)
$env:YF_BOOTSTRAP_PASSWORD = $credential.Password
try { dotnet run --project .\Yf.Api -- --initialize-database }
finally { Remove-Item Env:\YF_BOOTSTRAP_PASSWORD; $credential = $null; $secret = $null }
```

初始化创建 `admin` 并强制首次改密，不输出密码。非空数据库一律拒绝；MySQL DDL 无法完整回滚，若初始化失败会保留失败现场，不会自动删库。排查并由数据库管理员确认后，换一个空库重试。

初始化种子由 `BootstrapSeedCatalog` 通过 EF 实体写入，不再内嵌或导出 schema JSON。当前开发阶段需要切换旧数据库时，删除并重建空开发库后重新初始化；不提供旧手写迁移历史收编或数据转换。

新建账号、初始化管理员、重置密码和修改密码统一要求 6–20 个 Unicode 字符，拒绝常见弱密码和简单重复。登录只需工号和密码，不再提供或校验图形验证码。已有 Argon2 PHC 密码继续验证，包括此前设置的超过 20 字符的密码，不强制批量重置；旧密码验证仍保留 256 UTF-8 字节的输入上限。

登录保护：保留现有 IP 与 IP+账号限流；同一账号连续失败 10 次后暂停密码登录 15 分钟。失败计数和截止时间保存在数据库，跨 IP、应用实例和进程重启共享；账号按数据库身份合并计算，大小写变化不会重置计数。锁定期间尝试正确或错误密码均返回统一失败消息，重试不会延长截止时间。到期后可重新尝试，成功登录、已登录用户完成改密或管理员重置密码会清除计数与暂停状态。此保护复用现有用户字段，无需数据库迁移；不自动注销其他有效会话，改密仍按原规则撤销旧会话。可结合登录失败审计监控恶意锁号。

远程 MySQL 连接必须设置 `SslMode=VerifyFull`，校验证书链与主机名；私有 CA 可通过连接串 `SslCa` 指定可信 PEM 文件。仅 `localhost` 或明确回环 IP 的本机连接保留原有 TLS 模式，混合本机/远程主机列表按远程处理；API 连接与维护脚本均拒绝弱化远程校验。现有远程连接串升级前应先配置匹配域名的服务器证书和可信 CA，不能把代码更新视为服务器证书已部署。

## 测试

在 `server_dotnet` 目录运行。完整回归前，先为当前进程设置指向独立本机测试实例的 `YF_TEST_DATABASE_URL`；数据库测试未配置连接时会跳过，单凭命令退出码为 0 不能证明完整回归通过，必须同时核对失败、跳过和未运行数量均为 0。

```powershell
# 先安全设置当前进程的 YF_TEST_DATABASE_URL，不把密码写入命令历史。
# 连接格式：mysql://账号:URL编码密码@127.0.0.1:测试端口/ignored
dotnet test .\tests\Yf.Api.Tests.csproj
dotnet build .\TestHost\Yf.Api.TestHost.csproj
python .\scripts\test-isolated.py
# 仅运行同一隔离生命周期中的文件 HTTP 合同：
python .\scripts\test-isolated.py --files-only

# 真实维护备份/恢复测试；同样必须显式设置上面的本机测试管理连接。
python .\scripts\test-maintenance.py
```

HTTP 测试需要 Python 3.11+ 与 `pymysql`，仅使用显式设置的 `YF_TEST_DATABASE_URL`（本机 MySQL 测试管理账号，需创建/删除测试库及查看锁等待）。不读取 Rust 配置。测试在随机命名的 `yf_test_dotnet_*` 库与临时存储中运行，结束只删除自身资源；邮件发送禁用，通知验证仅检查 outbox。

测试必须保留 Python 断言，不使用 `python -O` 或 `PYTHONOPTIMIZE`。HTTP 与浏览器入口会在使用 TestHost 前核对其实际加载的 API 和托管依赖字节；只构建 API、未同步构建 TestHost 时应重新构建，不能跳过一致性检查。

`test-maintenance.py` 还要求本机 `mysql.exe` 与 `mysqldump.exe` 可用，并使用随机命名的 `yf_test_maintenance_*` 数据库验证真实 MySQL、程序和存储字节的备份/恢复、篡改拒绝及非空目标拒绝。它只接受 `localhost`、`127.0.0.1` 或 `::1` 的显式 `YF_TEST_DATABASE_URL`，会创建并删除自身测试数据库；客户端应为与测试 MySQL 兼容的 5.7 或更高版本。该测试不启动或操作 IIS，不验证目标服务器的站点、专属应用池、HTTPS 证书、权限或网络。

先测试实际生产入口的启动、健康和工号密码登录，再通过独立 `TestHost` 执行完整 HTTP 用例。测试宿主使用同一 API 工厂，绑定回环地址；正式 API 和测试宿主均不提供验证码或答案接口，发布包不包含测试宿主。

`tests/Contracts/api-v1.json` 固定前端 HTTP 方法/路径契约，不再解析 Rust 路由。项目回归验证真实数据库锁等待、并发提交与撤回；上传回归验证慢请求、并发初始化、中断恢复及文件清理。

测试脚本中的 PDF 样本用于传输字节/Range 验证；这些检查不等同于在浏览器里渲染真实 PDF，也不等同于目标服务器 IIS 或真实 SMTP 验收。

### 浏览器自动化

默认套件也包含数据字典三类条目的增改删、关联保护，以及图片、PPTX、视频和留言图片上传/粘贴预览。布局检查覆盖全部页面路由的桌面、平板和窄屏尺寸。`--continue-on-failure` 用于一次收集独立步骤的失败，任意失败仍返回非零；最终验收需全部通过。需要隔离并行构建产物时，可通过 `YF_BROWSER_HOST_DIR` 指定同一次构建生成的 API/TestHost 目录，仍会验证两者字节一致性。

视频测试样本在本地生成；如果 Chrome 的 MediaRecorder 不输出有效视频，会使用已安装的 Python OpenCV／NumPy 生成 VP8 WebM。缺少该编码能力时会明确失败，不会安装依赖或跳过播放断言。此依赖仅用于测试样本生成，应用播放视频不需要 Python。

`scripts/test-browser.py` 使用已安装的 Chrome 和现有 Playwright `run.js`，验证构建后的 React 与同一 ASP.NET API 的登录、改密、管理操作、项目协作、文件预览下载以及页面布局。先构建对应源码，再运行；脚本不会安装依赖、启动业务实例或读取项目私有配置。

```powershell
dotnet build .\TestHost\Yf.Api.TestHost.csproj --no-restore
Push-Location ..\web
try { npm run build } finally { Pop-Location }
# 显式设置本机测试管理连接 YF_TEST_DATABASE_URL（与 HTTP 测试相同）。
$env:YF_PLAYWRIGHT_RUNNER = 'C:\你的工具目录\playwright-skill\run.js'
python .\scripts\test-browser.py --output ..\.artifacts\tests\browser\browser-NEW
```

每次使用新的空证据目录。结果含实际步骤、源码与被测构建产物 SHA-256、截图、下载完整性及资源清理状态；测试期间修改源码或产物会使本轮校验失败。它创建随机临时数据库、文件目录、账号和独立回环测试宿主，使用工号密码直接登录，保留生产限速，邮件发送关闭。结束只清理本轮资源与私有登录状态。

`--steps auth fixtures system` 可单独检查系统参数和日志；`--steps auth fixtures users project-edges access` 检查五态筛选、编辑、分页、终止重启、异常重试和权限变化、多标签换号。`business`、`project-edges`、`file-edges`、`message-edges` 必须在 `users` 后，`final` 和 `layout` 必须在 `business` 后。

默认步骤还包括 `auth-edges`（匿名 404、认证失败恢复、键盘、跳转和会话刷新）、`file-edges`（双向文件筛选分页、失败恢复和多表预览）、负责人和项目选项边界、`message-edges`（Unicode 长度、重复发送、失败重试、游标分页和动态跳转）。故障注入只作用于本轮浏览器路由，成功重试仍调用真实隔离 API。完整默认步骤对应 `scripts/browser` 中的脚本，不代表覆盖所有状态组合、实际移动设备、目标 IIS 或外部 SMTP。

## IIS 发布

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\publish-iis.ps1
```

发布脚本参数以 `Get-Help .\scripts\publish-iis.ps1 -Detailed` 为准。默认在项目根 `deloy` 下创建唯一版本目录；也可用 `-FreshOutputDirectory` 指定该目录下尚不存在或为空的子目录，项目外路径会被拒绝。默认只生成版本文件夹，包含后端、`wwwroot` 前端、IIS 配置、安装脚本、说明及内部 `manifest.json`；仅显式增加 `-CreateArchive` 时生成 ZIP、发布清单和 SHA256 文件。发布脚本从被 Git 忽略的 `deploy/publish-defaults.local.json` 写出可直接供 IIS 读取的 `appsettings.Production.json`，其中包含数据库和 JWT 凭据；必须先填写实际 `WebBaseUrl`，必要时覆盖 `StorageRoot` 与 `CookieSecure`。将整个发布包作为私有制品复制到目标服务器，按包内 `README.md` 部署。不会在开发电脑上自动部署 IIS。

开发机可直接运行 `python .\scripts\verify-release.py ..\deloy\<版本目录>` 验证默认发布文件夹；无需创建 ZIP。验证会在项目内临时目录复制制品，核对全部文件哈希与大小、实际 .NET 8 runtimeconfig、打包 SDK、IIS 启动配置及包内配置字段，再用该发布二进制执行隔离 HTTP 检查。若已用 `-CreateArchive` 生成 ZIP，也可传入 ZIP 路径；ZIP 模式额外核对 CRC、`.zip.sha256` 和 `.release-manifest.json`，不会跳过其校验。报告写入 `.artifacts/reports/releases`。测试报告、浏览器证据和可清理临时目录写入 `.artifacts/tests`。这些是开发机真实运行证据，不代表目标 IIS 或外部 SMTP 验收。

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
| ASP.NET Core | .NET 8 | https://github.com/dotnet/aspnetcore | HTTP、路由、IIS、静态文件 |
| Microsoft.EntityFrameworkCore | 9.0.20 | https://github.com/dotnet/efcore | Code-First 模型、查询、写入和迁移 |
| Pomelo.EntityFrameworkCore.MySql | 9.0.0 | https://github.com/PomeloFoundation/Pomelo.EntityFrameworkCore.MySql | MySQL EF Core provider |
| MySqlConnector | 2.6.2 | https://github.com/mysql-net/MySqlConnector | MySQL 异步驱动 |
| Konscious Argon2 | 1.3.1 | https://github.com/kmaragon/Konscious.Security.Cryptography | 兼容已有 Argon2 密码 |
| IdentityModel JWT | 8.23.0 | https://github.com/AzureAD/azure-activedirectory-identitymodel-extensions-for-dotnet | JWT 签发校验 |
| MailKit | 4.18.0 | https://github.com/jstedfast/MailKit | TLS SMTP |
| xUnit v3 | 4.0.0 | https://github.com/xunit/xunit | 自动化测试 |

测试项目仍保留 Dapper 2.1.79 仅用于少量独立测试夹具和数据库断言，生产 `Yf.Api` 不引用 Dapper。

准确传递依赖版本见各项目 `packages.lock.json`。前端依赖与许可沿用 `web/package-lock.json` 及发布的第三方许可文件。新功能、安全修复、契约与数据库版本以本目录的 .NET 后端为维护入口，不要求同步 Rust。
