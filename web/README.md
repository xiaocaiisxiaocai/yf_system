# 供应商协作平台前端

React、TypeScript、Vite 和 Arco Design。API 通过 `/api/v1` 访问；开发代理指向 `127.0.0.1:8080`，正式环境由 ASP.NET Core 与前端同站点提供服务。

## 开发与验证

在本目录执行 `npm run dev`、`npm run build`、`npm run lint` 和 `npm test`。首次安装依赖使用 `npm ci`；开发环境使用 Node.js 22.13+ 或 24+。

`npm run dev` 和 `npm run build` 会先生成 Excel、PPTX 预览页面。`generated/`、`.excel-preview-build/`、`.pptx-preview-build/` 和 `dist/` 均为项目内构建产物，不应手工编辑。

## 文件预览

PDF 使用本地 PDF.js 6.3.289 按页渲染，支持翻页、页码跳转、适合宽度、缩放和错误重试；加密 PDF 提示下载查看。`pdf-assets.ts` 提供并打包字体、CMap 和图像解码资源，worker 使用本地资源 URL。

实际预览源码及来源记录位于 `vendor/`。项目根的 `third_party/` 还包含预览测试所需样本，不能作为无用目录直接删除。

正式部署须发布完整 `dist/`，保留 `pdfjs/`、`assets/` 及许可证，确保 `.mjs/.js`、`.wasm` 分别以 JavaScript、`application/wasm` 类型返回。项目统一使用 [IIS 发布脚本](../server_dotnet/scripts/publish-iis.ps1) 打包前后端，输出位于项目内 `deloy/`。

OEM 前端界面、路由、独立门户和客户端已暂时移除；后端 OEM 接口也已移除。数据库删除迁移需单独执行，后续可从保留的历史版本评估恢复。

Windows 浏览器验收须先遵守全局 CORE-19 的账户保护要求；普通构建、Lint 和 Node 回归测试不需要启动浏览器。
