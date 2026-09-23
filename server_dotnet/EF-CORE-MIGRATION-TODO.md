# Dapper → EF Core Code-First 迁移记录

**状态**：现有 `server_dotnet` 后端迁移完成（2026-09-17）。历史迁移 `AddOemPlatform` 曾增加 OEM 表；OEM 方案现已暂停，新的删除迁移将移除这些表。历史迁移文件仍保留，以便已有数据库按顺序升级。

## 已完成

- `Yf.Api` 使用 EF Core 9.0.20、Pomelo 9.0.20 和 MySqlConnector；生产项目已移除 Dapper 依赖。
- 27 张现有业务表已建立实体、Fluent 配置、`YfDbContext`、设计时工厂和 `InitialCreate` 迁移。
- Identity、Admin、Projects、Files、System、权限、审计、后台任务和开发数据重置均已改用 EF LINQ、实体写入、`ExecuteUpdateAsync` 或 `ExecuteDeleteAsync`。
- `SchemaBootstrap`、`SchemaMigrations` 仅保留调用兼容入口；实际建库、升级和启动校验统一由 `EfDatabaseLifecycle` 与 EF 迁移历史负责。
- 删除旧 `schema-baseline.json`、`SchemaShapeValidator`、手写版本 DDL 和基线导出脚本。
- 初始化种子改为 `BootstrapSeedCatalog` 的 EF 实体写入：`admin`、系统管理员角色、35 个权限、13 个系统参数和高／普通／低三个默认优先级。

## 数据库生命周期

- `--initialize-database` 只接受空库，执行 EF `InitialCreate` 后写入初始化种子。
- `--migrate-database` 只接受已有且非空的 EF 管理数据库；缺少、清空、乱序或包含未知记录的 `__EFMigrationsHistory` 会被拒绝。
- 普通启动只读校验迁移历史和运行门禁，不执行 DDL。
- 当前仍在开发阶段，不兼容旧手写 schema、`yf_schema_migrations`、SeaORM 历史或旧业务数据。切换时直接重建空开发库并重新初始化。
- 后续模型变化使用 `dotnet ef migrations add <Name>` 生成迁移，审查迁移和模型快照后再通过显式迁移命令应用。
- `20260923235004_AddFileStoragePathIndex` 是本轮性能审查生成的空设计检查点；实际 `storage_path` 前缀清理仍保持低频安全路径，未引入未验证的宽索引。

## 保留的原生 SQL 边界

普通查询、联表、聚合和增删改均使用 EF Core。原生 SQL 只保留在 EF 无法等价表达或必须依赖数据库原子语义的边界：

- `SELECT ... FOR UPDATE`、管理门禁共享锁和 MySQL 命名锁；
- 数据库 UTC 时间；
- 留言已读 `INSERT IGNORE`、邮件 outbox 幂等写入；
- 协作修订指纹所需的 MySQL `BIT_XOR`；
- EF 生命周期读取 `information_schema` 和获取迁移互斥锁。

这些语句均集中、参数化，并继续复用调用者的连接和事务。Dapper 仅保留在测试项目中，用于测试夹具和持久化断言。

## 验证结果

- `dotnet build`：API、测试项目和 TestHost 均为 0 警告、0 错误。
- .NET 测试：221/221 通过，失败、跳过、未运行均为 0。
- 隔离 HTTP/业务回归：180/180 通过；使用本机临时数据库和临时存储，未访问业务数据库，未发送真实邮件。
- 覆盖空库初始化、重复迁移、缺失历史拒绝、权限、登录、项目主子流程、并发锁、留言、通知、文件分片与预览、配置、审计和完整验收流程。

验证证据保存在本地 `.artifacts/tests/`；`.runlogs/` 已不再作为当前验证输出目录。
