# 测试目录说明

前端自动回归已统一迁移到 `web/src/test/`，`npm test` 只运行 Vitest。组件交互使用 Testing Library 与真实 React DOM；模块测试直接导入生产代码。本目录保留预览样本和历史浏览器验收资料，不再保存 Node test runner 用例。

迁移前 299 项场景清单位于 `web/src/test/migration/previous-cases.json`；用例分布在 `web/src/test/migrated/`。运行 `npm run test:watch` 可以监听开发改动。

`browser-*.acceptance.txt` 是早期真实页面验收时保存的手动脚本片段，包含当时的端口、记录 ID 和页面数据。它们不属于 `npm test`，也不属于当前 `server_dotnet/scripts/test-browser.py` 自动浏览器验收入口，不能作为当前版本已执行或通过的证据。

需要保留其中的场景时，应使用当前隔离 fixture 改写到 `server_dotnet/scripts/browser/*.cjs`，并由 `test-browser.py` 编排和记录结果。`create-preview-samples.cjs` 仅用于生成预览样本，不是测试用例。
