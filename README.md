# 供应商协作平台

React + TypeScript 前端、ASP.NET Core 10 后端、MySQL 数据库。后端唯一入口是 `server_dotnet`。

## 目录

| 目录 | 内容 |
| --- | --- |
| `web/` | 前端源码、依赖、预览组件及测试 |
| `server_dotnet/Yf.Api/` | 后端业务代码及数据库迁移 |
| `server_dotnet/scripts/` | 开发检查、发布打包、自动化验证脚本 |
| `server_dotnet/deploy/` | 发布包附带的 IIS 安装、升级、备份和恢复脚本 |
| `server_dotnet/tests/`、`server_dotnet/TestHost/` | 后端测试和隔离测试宿主 |
| `server_dotnet/docs/` | 当前业务契约及 OEM 设计文档 |
| `docs/`、`docs/history/` | 需求、功能说明及历史验收记录 |
| `third_party/`、`web/vendor/` | Office 预览来源、必要测试样本和实际使用的预览组件源码 |
| `deloy/` | IIS 发布目录，默认只生成可直接复制的版本文件夹 |
| `.artifacts/cache/` | 可复用下载缓存，包括 ClamAV 程序和病毒库 |
| `.artifacts/reports/` | 验证报告和保留的近期验收证据 |
| `.artifacts/backups/` | 本地旧数据库及源码备份，可能含私有数据，不提交 Git |
| `.artifacts/cleanup/` | 本轮目录整理记录 |

`web/node_modules`、`web/dist`、`web/generated`、预览编译目录及 .NET `bin/obj` 是项目内的标准依赖或构建目录，保留现有工具链布局。`.artifacts` 不纳入 Git，也不会作为源码发布。

## 开发

前端在 `web` 目录执行 `npm run dev`。后端在项目根执行：

```powershell
dotnet run --project .\server_dotnet\Yf.Api\Yf.Api.csproj --launch-profile Yf.Local
```

启动条件和私有配置见 [后端说明](server_dotnet/README.md)。不要把密码、数据库连接或业务上传目录混入发布目录。

## IIS 打包

在项目根运行，默认生成项目内独立的发布目录：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\server_dotnet\scripts\publish-iis.ps1
```

发布脚本重新构建当前工作区的前后端，输出到项目根 `deloy/<版本目录>/`，默认不生成旁边的 ZIP、发布清单或 SHA256 文件。文件夹内部的 `manifest.json` 供安装脚本校验完整性，必须随整个文件夹保留。只有显式指定 `-CreateArchive` 才额外生成压缩包和对应校验文件。

本机发布默认值保存在被 Git 忽略的 `server_dotnet/deploy/publish-defaults.local.json`，无需命令行传参。发布出的 `appsettings.example.json` 会自动填入数据库连接和 JWT；JWT 首次自动生成并保存在本地，后续发布复用。该本地文件需要随开发环境私密保存，不会作为独立文件复制进发布包。源码中的通用配置模板保留占位符。新服务器的实际访问地址和存储目录仍需核对，已有部署继续保留原来的外部生产配置。

打包与目标服务器安装是两步：包内包含 `install-iis.ps1`、`maintain-iis.ps1` 及部署说明，服务器操作见 [IIS 部署说明](server_dotnet/deploy/README.md)。目标服务器的站点、外部配置、数据库、存储和备份路径仍按部署契约隔离，不属于开发机临时输出。

当前 OEM 前端入口及页面暂时隐藏，开关位于 `web/src/features.ts`；后端代码、数据和发布包中的扫描组件仍保留。

## 整理约定

- 发布内容放在项目内 `deloy`；开发缓存、测试及报告放在 `.artifacts`，不在仓库根散落日志或测试上传样本。
- 历史报告仅记录当时状态；其中旧路径、端口、提交及测试结果不代表当前环境。
- 备份、第三方来源、必要测试夹具和用户未提交代码不按临时文件清理。
- Windows 浏览器自动化遵守全局 CORE-19；本机有已知账户锁定风险，不能以重新启动浏览器的方式直接重跑旧验收脚本。
