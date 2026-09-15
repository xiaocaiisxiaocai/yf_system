# PPTX 预览源码来源与本地适配

- 用户提供并授权使用、修改的源码包：`vue-office源码2024-12-30.zip`。
- SHA-256：`ef8bb8ac7a02281aec026f0e6d822732d3439cecec8b282cde62f62833618a79`。
- 完整源码解压位置：项目根目录下 `third_party/vue-office-source-2024-12-30`，不纳入 Git。
- 来源仓库：https://github.com/501351981/vue-office
- 源码快照 gitHead：`d20568113bec480f6ca72924f6d0c1e3b0f1fe15`。
- 使用的 Vue 封装：`@vue-office/pptx` 0.0.6。该封装把文件字节交给独立渲染内核 `pptx-preview`；原始压缩包没有包含该内核源码或 `node_modules`。
- 按原封装所声明的依赖固定使用 `pptx-preview` 0.0.19（ISC），由本地构建打包，不使用 CDN 或 Office 云服务。该旧版内核只使用 `uuid` 的 `v4` API；项目通过 npm override 固定到 API 兼容且已修复已知安全问题的 `uuid` 11.1.1。
- Vue Office 原始 LICENSE 保留于 `core/LICENSE`。

## 接入方式

React 的 `PptxPreview` 通过现有鉴权接口读取 PPTX 字节，再传入独立的 `sandbox="allow-scripts"` iframe。`viewer.js` 沿用源码封装的 `init(...).preview(arrayBuffer)` 调用链，并补充本项目需要的翻页、缩放、错误通知和尺寸适配。

`scripts/build-pptx-preview.mjs` 把本地渲染器及样式构建成带 CSP 的独立 HTML。iframe 没有同源权限，CSP 禁止网络、表单、对象、媒体和子页面；图片只允许文档内的 data/blob 数据。渲染后会移除超链接及非 data/blob 资源地址，界面不提供保存、下载、打印或编辑入口。

父页面校验 iframe 消息来源和随机通道。切换文件、关闭和重试均中止原请求并销毁旧 iframe；旧页面迟到的消息和字节不会进入新预览。

## 能力范围

主要支持 PPTX 中的普通文本、图片、表格、背景、常见形状和分组对象，并显示全部幻灯片。复杂图表、SmartArt、公式、动画、音视频、宏、外部链接资源以及部分高级填充或形状不保证与桌面 PowerPoint 完全一致；预览不会执行宏、刷新外部数据或播放嵌入媒体。
