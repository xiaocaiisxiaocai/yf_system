# Excel 预览源码来源与本地适配

- 用户提供并授权使用、修改的源码包：`vue-office源码2024-12-30.zip`。
- SHA-256：`ef8bb8ac7a02281aec026f0e6d822732d3439cecec8b282cde62f62833618a79`。
- 完整源码解压位置：项目根目录下 `third_party/vue-office-source-2024-12-30`，不纳入 Git。
- 来源仓库：https://github.com/501351981/vue-office
- 源码快照 gitHead：`d20568113bec480f6ca72924f6d0c1e3b0f1fe15`。
- 使用的包：`@js-preview/excel` 1.7.14，以及它引用的 `vue-excel` 渲染源码。
- 原始 LICENSE 保留于 `core/LICENSE`。包内还包含付费源码使用说明；本次按用户授权用于本项目，不发布该源码包到公共仓库或 npm。

## 接入方式

React 的 `ExcelPreview` 继续通过现有鉴权接口读取文件，取得字节后传入独立的 `sandbox="allow-scripts"` iframe。iframe 使用提供源码中的 JS 入口，不要求应用引入 Vue，也不依赖 Office 服务。

`viewer.js` 是本项目适配入口，`scripts/build-excel-preview.mjs` 把运行代码和样式构建成独立 HTML。`npm run dev` 和 `npm run build` 会自动先构建它。`generated/`、`.excel-preview-build/` 均为可重新生成的产物，不提交。

iframe 不获得同源权限，CSP 禁止网络请求，图片仅使用文档内的 data/blob 数据。父页面校验消息来源及随机通道；关闭预览中止请求并销毁 iframe。界面不提供保存、下载、打印或编辑入口，并拦截编辑快捷键。原有文件列表下载权限与入口保持独立。

## 修改范围

- 修复空白工作表的行数计算。
- 修复图片一格锚点、两格锚点、偏移和高清屏缩放；保留正确的 TypedArray 字节范围。
- 图片异步加载按工作簿/画布代次隔离，切换工作表后旧图片不再覆盖新表；加载后释放 Blob URL。
- 统一字体、行高与图片坐标的单位转换，并保留图片占用的行列范围。
- 窗口尺寸变化后重绘当前工作表及图片。
- 删除文档内容调试输出，采用已有编译样式，避免额外引入 Vue/LESS 构建链。

## 能力范围

主要面向 XLSX 的单元格样式、合并单元格、多工作表及普通嵌入图片。XLS 经过 SheetJS 转换，旧格式中的图片与特殊对象不保证保留。图表、SmartArt、宏、WPS 特有图片公式、复杂条件格式及 Excel 特有打印排版不承诺与桌面 Excel 完全一致。公式以文件保存的计算结果为主，预览不执行宏或外部数据刷新。
