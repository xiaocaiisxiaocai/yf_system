# 项目踩坑记录

## 本地数据库测试会留下 `yf_t_*` 临时库并大量写盘（2026-11-18，2026-11-19 更新）

- 现象：本地 MySQL 里累积了几十个 `yf_t_<guid>` 库；测试期间 SSD 持续写入约 2 TB/天。
- 根因：每个 DB 用例都新建一个临时库并重放全部 EF 迁移，几十次表重建乘以用例数；运行被中断（脚本按错误终止、超时、手动停止）时，未跑完的用例来不及删库。
- 现状：`TestSchemaTemplate` 让每个测试进程只迁移一次模板库 `yf_tpl_*`，各用例从模板复制表、数据和 trigger；`TestSchemaTemplateTests` 保证复制结果与真实迁移一致。只有测初始化／迁移本身的用例（`SchemaDatabaseScope.InitializeWithMigrationsAsync`、迁移测试）仍重放迁移。新增需要已初始化库的 helper 时调用 `TestSchemaTemplate.InitializeEmptyAsync`，不要直接调 `SchemaBootstrap.InitializeEmptyAsync`。
- 规避：本地只用 `--filter` 跑相关用例，先编译再 `--no-build`，不要整批反复重跑；完整回归交给 PR 的 CI。包装脚本调用 `dotnet test` 时不要设 `$ErrorActionPreference='Stop'`，否则第一条 stderr 就中断整轮、留下临时库。测试结束后清理残留：无活动连接且超过 1 小时未写入的 `yf_t_<32位hex>`、`yf_test_dotnet_files_<32位hex>` 直接 `DROP` 并回报数量；`yf_tpl_*` 由下一次测试进程按锁自动清理。

## 在云测试服务器上跑全量测试会有 2 个启动并发用例超时（2026-11-19）

- 现象：`YF_TEST_DATABASE_URL` 指向云服务器 MySQL（经 SSH 隧道 `127.0.0.1:13306`，2 核 2G）时，全量 1066 个用例只有 `SchemaShapeTests.FirstStartupInitializesMissingOrEmptyDatabaseOnce`（`createDatabase` 为 true／false 各一）失败，约 32 秒报 `Command Timeout expired`，单独重跑稳定复现；其余通过，全量约 43 分钟。
- 根因：两个并发启动争同一个 `GET_LOCK`，后到者等锁 60 秒，但 MySqlConnector 默认命令超时 30 秒；云端整套迁移耗时远超 30 秒（模板库首次迁移约 2 分 13 秒），先到者没跑完就被打断。是环境耗时差异，不是业务缺陷。
- 规避：云端验证时把这两个用例视为已知环境失败，其余结果有效；要让它们在云端通过，需在用例里把命令超时调到大于迁移耗时，或把测试放到服务器本机跑。在 `server_dotnet/` 目录执行 `dotnet test`，否则 `global.json` 不生效，会换成 .NET 10 SDK 并报 VSTest 不再支持。

## 授权保存必须一次请求、一个事务（2026-11-18）

- 现象：角色抽屉与用户编辑框各自把"数据范围"和"权限/资料"拆成两次 PUT；第二步被拒绝时第一步已落库，出现"只生效一半"，旧页面还可能把别人刚撤销的授权写回去。
- 根因：把范围当成独立资源分笔提交，且前端按打开时的快照整份提交。
- 规避：角色用 `PUT /admin/roles/{id}/authorization`（`permissionIds`/`scopes` 任一为 null 表示不改），用户把 `scopes` 并入 `PUT /admin/users/{id}`；前端只发送实际变化的业务域，并在写前复核服务端当前值。

## 权限拆分的回填只能按旧接口的真实能力（2026-11-18）

- 现象：给"只有菜单码"的角色（`org:user`、`supplier:list`、`rbac:role`）或只有 `oem:transfer_view` 的角色回填读取码后，升级当天这些角色获得了原本没有的能力：用户/供应商/角色列表可读，OEM 厂商联系人邮箱与共享账号可读。
- 根因：把菜单码、相邻动作码当成旧读能力的证据；旧读接口实际检查的是 `user:manage`/`supplier:manage|account`/`role:manage`/`oem:company_manage|account_manage`。
- 规避：回填来源只取旧接口真正检查的码；新读码默认不回填（`oem:directory_view` 尤其只显式授予）。新增读码时逐个域核对"旧接口是否允许"，并给夹具角色显式补 `role_data_scopes`。

## 数据范围是授权，不是显示项（2026-11-18）

- 现象：只修了"用户覆盖"的委派上限，仍有三个入口能扩大范围：分配/更换角色（目标角色默认 ALL）、清空角色默认（成员是组织主管则回退 ORG）、旧表单整份回写（把他人已收窄的范围改回 ALL）。
- 根因：范围被当成角色/用户的附属数据，写入路径各自实现；前端按表单快照整体提交。
- 规避：任何"授予"路径都按**写入后的有效范围**校验（`ScopeWithRoleAsync`、清空回退、`CanGrant`）；前端只提交改过的业务域并在写前复核；角色保存先写范围再写权限。

## 权限迁移不要写死权限 ID（2026-11-18）

- 现象：`AddDataScopesAndCrudPermissions` 用 `INSERT INTO permissions(id,...)` 插入 224–230；在升级库上可能主键冲突，`--migrate-database` 失败，站点无法启动。
- 根因：早期权限迁移一律按 code 插入、让 MySQL 自增分配 ID，升级库的自增游标可能已经越过这些固定值；固定 ID 只属于全新库的 `BootstrapSeedCatalog`。
- 规避：新增权限统一写 `INSERT INTO permissions(code,name,type,parent_id,sort_no)` 并用 `NOT EXISTS` 判重；上线前在备份库上实跑一次迁移。迁移的 `Down` 会连表删掉已配置的数据范围，回滚后要重新配置。

## 数据范围必须覆盖所有"展示另一个项目"的读路径（2026-11-18）

- 现象：项目列表和详情已按数据范围过滤，复制履历里仍显示范围外关联项目的名称、供应商和负责人；只有 `user:view` 的账号还能点开一个必然 403 的用户编辑框。
- 根因：`ProjectCopyService.HistoryAsync` 用 `current.IsInternal` 判断关联项目可见性（引入数据范围后"内部账号"不再等于"能看"）；前端入口只按"能否进入页面"渲染。
- 规避：任何展示另一个项目的位置都走 `ProjectAccessService`（复制履历、文件映射、实时受众同源）；新增项目读接口先确认它用 `VisibleQueryAsync` 或 `RequireViewForValidatedActorAsync`。管理页的编辑/数据范围入口按 `user:manage` 渲染，别只按"页面可见"。

## 服务端分页表格的筛选必须走服务端（2026-11-17）

- 现象：给 `FileTable` 加"文件类型"下拉后，筛选只作用于当前这 10 条，分页器仍显示未筛选的总数，第 2 页起可能整页空白；空表与"N 条"并存。
- 根因：列表是 `GET /projects/{id}/files?page=&pageSize=` 的服务端分页，`data.list` 只是一页；前端 `filter` 得到的结果集与 `data.total` 不是同一个东西。
- 规避：`FileService.ListAsync` 增加 `ext`（逗号分隔扩展名，大小写不敏感）查询参数，筛选和总数在同一条 SQL 里；前端只把类别映射成 `ext` 并重置 `page`，不再本地过滤。给任何服务端分页表格加筛选时，先确认后端支持该维度。

## Arco 组织树搜索默认匹配编号（2026-11-11）

- `TreeSelect showSearch` 默认按节点的 `value/key` 搜索，选项值是组织编号时按中文名称搜不到。需提供 `filterTreeNode` 按 `node.props.title` 匹配，编号仍作为选中值。

## Arco Layout 不渲染 Sider 时退回纵向排列；导航与路由守卫规则只留一份（2026-11-11）

- 现象：没有协作菜单的账号（如只有 ODM/OEMA 权限）登录后，左侧业务切换栏变成顶部一条短条；此前菜单、业务入口、首页落点和路由守卫各写一份权限判断，出现过“菜单可见但进入是 403”或“仿真协作入口不该出现却出现”。
- 根因：Arco `Layout` 只在渲染了 `Sider` 时才横向排列子元素，侧栏按权限隐藏后自动变成纵向；权限规则分散在多处，改一处漏一处。
- 规避：主布局显式横排（`.layout-main.arco-layout { flex-direction: row }`）。协作页面的菜单可见、首页落点和 `App.tsx` 路由守卫都从 `web/src/layouts/navigation.ts` 的 `COLLABORATION_PAGES` 读取（守卫用 `pageGuard(code)`，组织架构主管特例用 `canManageLeaders`）；新增页面只在这里登记，`module-navigation.test.tsx` 会校验两边一致。

## 空 ODM/OEMA 厂商被最后账号保护拦住（2026-11-06）

- 现象：厂商没有传递单，但删最后一个账号提示必须保留启用账号；删厂商又要求先删账号。仅测试登录或改密也会被审计历史判断挡住。
- 根因：把业务厂商的账号保护无条件应用到初始化目录，并把认证日志当成业务引用。
- 列表只展示当前人可见的传递单，即使管理员也看不到他人的草稿。诊断时按目标账号实际所属厂商核对草稿、已废弃记录及其业务引用。
- 修复：只含未发送空草稿的启用厂商可以删除最后账号。账号删除只清理其创建或关闭的空草稿，厂商删除再清理所属的其余空草稿。文件行（包括已移除）、上传会话、审批、通知或已发送记录继续触发历史保护，清理失败不落部分变更。认证和草稿操作审计保留。隔离 HTTP 用例见 `OemDirectoryVisibilityTests`。

## 后端全量测试偶发超时（2026-11-08）

- 现象：`dotnet test .\tests\Yf.Api.Tests.csproj` 全量跑时随机几个数据库测试失败（如 `LoginThrottleTests`、`RobotPartAuthorizationTests`、`OemDeliveryClockSkewTests`），单独重跑全部通过。
- 根因：每个测试新建库并跑全部 EF 迁移；xUnit 默认按 CPU 核数（本机 14）并行，MySQL 5.7 并发 DDL 互相拖慢，单测 10 秒的用例在全量里跑到 80 秒，逼近 90/120 秒的 `Timeout`。时钟偏差用例的 6 秒到期窗口还要覆盖发起下载的数据库操作，负载下会提前过期。
- 修复：`tests/xunit.runner.json` 设 `maxParallelThreads: 6`（排队的测试不计时，总时长不变约 23 分钟，最慢用例耗时占超时上限从 89% 降到 57%）；时钟偏差用例窗口放宽到 15 秒，文件加大到完整传输约 40 秒。
- 规避：不要靠提高单个 `Timeout` 掩盖；新增依赖真实时间窗口的用例，窗口要覆盖负载下的数据库往返。用 TRX（`--logger trx`）对比耗时与超时上限。

## 浏览器协作验收与复制任务单测的时序（2026-11-08）

- 协作验收中，留言输入框先于首批留言加载完成出现。此时按 Ctrl+Enter 会保留草稿，服务端收到 0 个 POST。`collaboration.cjs` 填写正文及所需签名后会等发送按钮启用，再按快捷键。另有受控加载用例检查草稿保留和请求数。
- `web/src/test/ProjectGroupDetail.test.tsx` 曾在断言“排队中”之前收到模拟的“已完成”，造成偶发失败。用例现在先检查入队结果，再释放完成响应并检查进入副本的入口。修改轮询时应保留这两个阶段的独立断言。
