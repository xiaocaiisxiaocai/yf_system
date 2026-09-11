# React + TypeScript + Vite

## 本项目 PDF 在线预览

PDF 使用本地 PDF.js 6.3.289 按页渲染，不依赖浏览器内置 PDF 插件。支持翻页、页码跳转、适合宽度、缩放和错误重试；密码保护的 PDF 提示下载查看。

开发/构建使用 Node.js 22.13+ 或 24+（本机验证为24.19.0），运行 `npm ci` 后使用原有 `npm run dev` / `npm run build`。
`pdf-assets.ts` 为开发服务提供字体、CMap 和图像解码资源，并自动打包到 `dist/pdfjs/<版本>/`；worker 通过 Vite 生成本地资源 URL，渲染引擎按需加载。
部署时发布整个 `dist`，保留 `pdfjs` 和 `assets` 目录及许可证，确保 `.mjs/.js`、`.wasm` 分别以 JavaScript、`application/wasm` 类型返回。PDF 文档仍通过既有带认证的 `/files/:id/content` 获取。

This template provides a minimal setup to get React working in Vite with HMR and some Oxlint rules.

Currently, two official plugins are available:

- [@vitejs/plugin-react](https://github.com/vitejs/vite-plugin-react/blob/main/packages/plugin-react) uses [Oxc](https://oxc.rs)
- [@vitejs/plugin-react-swc](https://github.com/vitejs/vite-plugin-react/blob/main/packages/plugin-react-swc) uses [SWC](https://swc.rs/)

## React Compiler

The React Compiler is not enabled on this template because of its impact on dev & build performances. To add it, see [this documentation](https://react.dev/learn/react-compiler/installation).

## Expanding the Oxlint configuration

If you are developing a production application, we recommend enabling type-aware lint rules by installing `oxlint-tsgolint` and editing `.oxlintrc.json`:

```json
{
  "$schema": "./node_modules/oxlint/configuration_schema.json",
  "plugins": ["react", "typescript", "oxc"],
  "options": {
    "typeAware": true
  },
  "rules": {
    "react/rules-of-hooks": "error",
    "react/only-export-components": ["warn", { "allowConstantExport": true }]
  }
}
```

See the [Oxlint rules documentation](https://oxc.rs/docs/guide/usage/linter/rules) for the full list of rules and categories.
