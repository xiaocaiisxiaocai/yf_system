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

## 主题与几何保真适配（2026-09-15）

`normalize-pptx.js` 在已通过 ZIP/XML 预检的内存副本上展开渲染内核缺失的 DrawingML 规则：

- 沿幻灯片、版式、母版的关系读取对应主题，避免母版回链版式造成主题解析循环。
- 按 `fillRef` / `lnRef` 索引展开主题填充、渐变与线条，替换 `phClr`，保留显式 `noFill` 和本地属性的优先级。
- 解析主题色映射、透明度、亮度和饱和度变换，以及在线性光空间应用的 tint/shade；转换为内核可直接读取的 RGB 颜色。
- 补齐缺省中文主题字体与显式西文字体；实际显示仍取决于客户端是否安装对应字体。
- `geometry.js` 补充 `flowChartConnector` 圆形及单边大括号/中括号，保留变换、尺寸、调整值和线条。弧线按预设公式转换为内核支持的三次贝塞尔。

依据：[Microsoft FillReference 定义](https://learn.microsoft.com/en-us/dotnet/api/documentformat.openxml.drawing.fillreference)、[Apache POI 的 DrawingML 预设几何](https://github.com/apache/poi/blob/trunk/poi/src/main/resources/org/apache/poi/sl/draw/geom/presetShapeDefinitions.xml)。不修改 npm 内核或服务器原文件。

用用户实际上传的 6 页文件逐页验证，确认圆形、单边大括号、8 个渐变和宋体主题字体恢复；另回归原源码包 13 页演示文件在桌面与窄屏的图片、翻页、缩放。几何、颜色与预览隔离的 14 项针对性测试通过。用户业务文件不纳入 Git，截图及复核记录仅存于本地 `.runlogs/pptx-fidelity-20260915/`。

## 文本位置修正

`patch-renderer.mjs` 在构建时修正固定版本内核的文本排版：去除人为增加的 `20% × 字号` 段落上间距，将缺省文字框内边距恢复为左右 7.2、上下 3.6 磅，保留显式内边距、段落间距、形状位置和大小的小数精度。默认单行高度按 1.2 倍字号处理，显式行距仍优先。构建会验证每个补丁锚点；升级依赖后若内核变化，构建失败并要求复核，不静默丢失修正。npm 安装文件和用户原 PPTX 均不修改。

针对用户反馈的搜索框，使用 PowerPoint 只读打开原文件并导出对照图、读取文本框测量值。在浏览器 78%、100%、150% 缩放下，标题及搜索文字段落左上角相对 PowerPoint 坐标偏差均小于 0.05 磅。此验证针对位置与间距，不代表所有字体的像素栅格化完全相同。测试覆盖默认/显式/零内边距、段落间距、小数坐标和依赖补丁失效检测；证据位于 `.runlogs/pptx-text-layout-20260915/`。
