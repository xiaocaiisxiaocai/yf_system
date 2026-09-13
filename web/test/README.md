# 测试目录说明

`npm test` 只执行本目录的 `*.test.cjs`，这些文件是可重复运行的前端自动回归。

`browser-*.acceptance.txt` 是早期真实页面验收时保存的手动脚本片段，包含当时的端口、记录 ID 和页面数据。它们不属于 `npm test`，也不属于当前 `server_dotnet/scripts/test-browser.py` 自动浏览器验收入口，不能作为当前版本已执行或通过的证据。

需要保留其中的场景时，应使用当前隔离 fixture 改写到 `server_dotnet/scripts/browser/*.cjs`，并由 `test-browser.py` 编排和记录结果。`create-preview-samples.cjs` 仅用于生成预览样本，不是测试用例。
