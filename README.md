# 供应商协作平台

React + TypeScript 前端、ASP.NET Core 8 后端、MySQL 数据库。后端唯一入口是 `server_dotnet`。

## 目录

| 目录 | 内容 |
| --- | --- |
| `web/` | 前端源码、依赖、预览组件及测试 |
| `server_dotnet/Yf.Api/` | 后端业务代码及数据库迁移 |
| `server_dotnet/scripts/` | 开发检查、发布打包、自动化验证脚本 |
| `server_dotnet/deploy/` | 发布包附带的 IIS 安装、升级、备份和恢复脚本 |
| `server_dotnet/tests/`、`server_dotnet/TestHost/` | 后端测试和隔离测试宿主 |
| `server_dotnet/docs/` | 当前业务契约文档 |
| `docs/`、`docs/history/` | 需求、功能说明及历史验收记录 |
| `third_party/`、`web/vendor/` | Office 预览来源、必要测试样本和实际使用的预览组件源码 |
| `deloy/` | IIS 发布目录，默认只生成可直接复制的版本文件夹 |
| `.artifacts/cache/` | 可复用的开发下载缓存 |
| `.artifacts/reports/` | 验证报告和保留的近期验收证据 |
| `.artifacts/backups/` | 本地旧数据库及源码备份，可能含私有数据，不提交 Git |
| `.artifacts/cleanup/` | 本轮目录整理记录 |

`web/node_modules`、`web/dist`、`web/generated`、预览编译目录及 .NET `bin/obj` 是项目内的标准依赖或构建目录，保留现有工具链布局。`.artifacts` 不纳入 Git，也不会作为源码发布。

## 开发

前端在 `web` 目录执行 `npm run dev`。后端在项目根执行：

```powershell
dotnet run --project .\server_dotnet\Yf.Api\Yf.Api.csproj --launch-profile Yf.Local
```

启动条件和私有配置见 [后端说明](server_dotnet/README.md)。IIS 发布包的 `appsettings.Production.json` 现已包含目标站点配置及数据库/JWT 凭据；发布目录应作为私有制品保管，不要放入公开下载位置或提交 Git。

## IIS 打包

在项目根运行，默认生成项目内独立的发布目录：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\server_dotnet\scripts\publish-iis.ps1
```

发布脚本重新构建当前工作区的前后端，输出到项目根 `deloy/<版本目录>/`，默认不生成旁边的 ZIP、发布清单或 SHA256 文件。文件夹内部的 `manifest.json` 供安装脚本校验完整性，必须随整个文件夹保留。只有显式指定 `-CreateArchive` 才额外生成压缩包和对应校验文件。

本机发布默认值保存在被 Git 忽略的 `server_dotnet/deploy/publish-defaults.local.json`，无需命令行传参。发布脚本将数据库连接、JWT、实际 `WebBaseUrl` 及存储目录写入发布包的 `appsettings.Production.json`；JWT 首次自动生成并在本机复用。发布包默认支持首次启动自动建库、EF 迁移建表及创建管理员；初始密码在 `appsettings.Production.json` 的 `App.BootstrapPassword`，首次登录必须修改，重启不会重置。已有非空库仍需显式升级迁移。首次发布前必须在该私有默认值文件中填写目标站点 `WebBaseUrl`，必要时覆盖 `StorageRoot` 和 `CookieSecure`。源码中的通用配置模板保留占位符。已有站点升级应保留其原 JWT 和数据库配置，不要直接用新包的配置覆盖。

打包与目标服务器安装是两步：包内包含 `install-iis.ps1`、`maintain-iis.ps1` 及部署说明，服务器操作见 [IIS 部署说明](server_dotnet/deploy/README.md)。目标服务器的站点、外部配置、数据库、存储和备份路径仍按部署契约隔离，不属于开发机临时输出。

OEM 方案已暂停；当前前后端与新发布包均不包含 OEM 功能或病毒库。新增的数据库迁移会删除 OEM 表及专属数据；已有数据库只有在备份后显式执行迁移才会发生删除，恢复方案前需重新评估数据和迁移边界。

## 整理约定

- 发布内容放在项目内 `deloy`；开发缓存、测试及报告放在 `.artifacts`，不在仓库根散落日志或测试上传样本。
- 历史报告仅记录当时状态；其中旧路径、端口、提交及测试结果不代表当前环境。
- 备份、第三方来源、必要测试夹具和用户未提交代码不按临时文件清理。
- Windows 浏览器自动化遵守全局 CORE-19；本机有已知账户锁定风险，不能以重新启动浏览器的方式直接重跑旧验收脚本。
