# Office 预览源码接入说明

本次将 Excel 和 PDF 预览接入用户提供的 `vue-office源码2024-12-30.zip`，保留项目现有的鉴权接口和文件预览权限。

## 源码与构建

| 项目 | 位置 / 版本 |
| --- | --- |
| 原始完整源码 | `third_party/vue-office-source-2024-12-30`（本地解压，不提交） |
| Excel 适配源码 | `web/vendor/vue-office-excel`，原始 JS 包 1.7.14 |
| PDF 适配源码 | `web/vendor/vue-office-pdf`，原始 JS 包 2.0.10 |
| React 入口 | `web/src/components/ExcelPreview.tsx`、`PdfPreview.tsx` |
| Excel 解析依赖 | ExcelJS 4.4.0；旧 XLS 转换沿用 SheetJS 0.20.3 |
| PDF 解析依赖 | 沿用项目 PDF.js 6.3.289，不使用源码包内旧版引擎或外部 CDN |
| 上游仓库 | https://github.com/501351981/vue-office |

原始包 SHA-256：`ef8bb8ac7a02281aec026f0e6d822732d3439cecec8b282cde62f62833618a79`。
具体来源、许可证和修改说明见两个 vendor 目录中的 `SOURCE.md` 与 LICENSE。按用户授权用于本项目，未公开发布源码。

在 `web` 目录执行 `npm ci` 后，使用原有 `npm run dev` 或 `npm run build`。它们会先自动构建 Excel 独立渲染页面；无需新增后端服务、数据库迁移或 Office 安装。部署仍发布正常生成的 `web/dist`。

## 预览行为

- Excel：画布渲染，支持工作表切换、行列滚动、合并单元格、基础样式及普通嵌入图片；读取原文件字节，不修改上传文件。
- PDF：基于提供源码的多页滚动、可见范围渲染方式适配；不同页面分别计算尺寸，支持页码跳转、放大、缩小、适合宽度、适合整页及重置。
- 预览窗口尽量占满页面，桌面端文件名和 PDF 控件共用顶部一栏，窄屏自动换行。
- 两种预览均不提供下载入口。文件列表中的下载仍由原有下载权限控制；隐藏预览下载入口不等同于数字版权保护。
- 文件字节由原有授权接口获取。Excel 在独立受限 iframe 内渲染，关闭后释放整个运行环境；PDF 取消旧渲染任务并释放 canvas、监听器和 worker。

## 验证与边界

使用真实 XLSX 测试工作簿验证空白首表、多工作表、合并标题、样式、两种嵌入图片锚点、工作表切换及窗口缩放。PDF 使用真实文档验证实际渲染、顶部控件、放大缩小重置及桌面/窄屏边界。接口采用浏览器隔离夹具，未向业务数据库新增测试项目或文件。

持久回归测试位于 `web/test/excel-source.test.cjs`、`file-security.test.cjs`、`pdf-preview.test.cjs` 和相关 `regression.test.cjs` 用例，运行 `npm test`。

本次验证记录（2026-09-14）：

- 前端完整回归 191/191 通过；随后图片末端滚动范围修正的 9 项相关用例复测通过。
- TypeScript 与生产构建通过。Oxlint 无错误；保留第三方源码及原有测试的非阻断警告。
- 生产产物在本地临时预览服务验证：Excel 1440×900、390×844，PDF 1920×1080、1440×900、1280×600、390×844。
- 10 页横竖混排 PDF 验证跳页、可见页渲染、400% 水平滚动及重置。
- 浏览器证据保存在本地 `.runlogs/office-source/` 与 `.runlogs/preview-header-browser.json`；它们是隔离测试结果，不作为业务数据提交。

这不是桌面 Excel 的完整排版引擎。复杂图表、SmartArt、宏、WPS 特有图片公式、旧 XLS 图片及部分复杂条件格式需以实际文件继续验证，不能承诺所有 Office 特性百分之百还原。
