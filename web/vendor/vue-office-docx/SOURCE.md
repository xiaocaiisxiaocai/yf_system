# Word 预览源码来源与本地适配

- 用户提供并授权使用、修改的源码包：`vue-office源码2024-12-30.zip`。
- SHA-256：`ef8bb8ac7a02281aec026f0e6d822732d3439cecec8b282cde62f62833618a79`。
- 完整源码解压位置：项目根目录下 `third_party/vue-office-source-2024-12-30`，不纳入 Git。
- 来源仓库：https://github.com/501351981/vue-office
- 源码快照 gitHead：`d20568113bec480f6ca72924f6d0c1e3b0f1fe15`。
- 使用的封装：`@vue-office/docx` 1.6.3 的 `src/docx.js`、`src/hack.js`，以及 `@js-preview/docx` 1.6.4 的 `index.css` 和外层容器结构，原样保留于 `core/packages/`。该封装把文件字节交给独立渲染内核 `docx-preview`；原始压缩包没有包含该内核源码或 `node_modules`。
- 内核固定为 `docx-preview` 0.3.7（Apache-2.0），在源码包声明的 `^0.3.2` 范围内，由本地构建打包，不使用 CDN 或 Office 云服务。它只依赖项目已有的 `jszip`。
- Vue Office 原始 LICENSE 保留于 `core/LICENSE`。

## 接入方式

React 的 `DocxPreview` 通过鉴权接口读取 DOCX 字节（Robot 协作默认 `/files/{id}/content`，ODM/OEMA 经 `loadContent` 走 `/oem/files/{id}/content`），再传入独立的 `sandbox="allow-scripts"` iframe。`viewer.js` 沿用源码封装的 `docx.render(...)` 调用链，补充本项目需要的预检、缩放、错误通知和内容清理。

`scripts/build-docx-preview.mjs` 把本地渲染器及样式构建成带 CSP 的独立 HTML（`generated/docx-viewer.html`，约 200 KiB）。iframe 没有同源权限，CSP 禁止网络、表单、对象、媒体和子页面；图片与内嵌字体只允许文档内的 data/blob 数据（`useBase64URL`）。渲染后会移除超链接及非 data/blob 资源地址，界面不提供保存、下载、打印或编辑入口。

- `renderAltChunks` 关闭：altChunk 是任意 HTML，内核会把它放进嵌套 iframe。
- 批注与修订不渲染（`renderComments`、`renderChanges` 关闭）。
- 打开压缩包前按 `archive-limits.js` 的 `documentArchiveLimits` 计数实际展开字节（条目不超过 5000，单条目 128 MiB，合计 512 MiB）；XML 条目单个不超过 32 MiB、合计 96 MiB，含 `<!DOCTYPE` / `<!ENTITY` 或解析失败的直接拒绝。

父页面校验 iframe 消息来源和随机通道。切换文件、关闭和重试均中止原请求并销毁旧 iframe。

## 能力范围

支持 `.docx` 的文本、样式、表格、图片、页眉页脚、分节与显式分页。分页依据文档内的分页符和 Word 上次保存时记录的分页位置，与桌面 Word 的实际分页可能不同；复杂域、公式、SmartArt、嵌入对象、宏不保证一致，预览不会执行宏。字体取决于客户端是否安装对应字体。

旧版 `.doc`（OLE2 二进制）不是 ZIP，无法在浏览器内渲染，只能下载。

## 验证（2026-11-10）

用含标题、中文正文、表格、PNG 图片、外部超链接和分页符的 DOCX，在真实浏览器的沙箱 iframe 中渲染并截图核对，适合宽度与 150% 缩放均正常。Robot 协作文件列表与 ODM/OEMA 传递单共用该组件。组件消息边界、外部取数、非 ZIP 拒绝与重试由 `src/test/previews/docx-preview.test.tsx` 覆盖。
