const { chromium } = require('playwright');
const crypto = require('node:crypto');
const {
  fs, assert, OUT, s, record, login, api, action, track,
} = require(process.env.YF_BROWSER_SUPPORT_DIR + '/ui-lib.cjs');

const MB = 1024 * 1024;
const configLabels = {
  'upload.allowed_exts': '允许上传类型',
  'upload.chunk_size': '上传分片大小',
  'upload.max_file_size': '单文件大小上限',
};
const notificationConfigKeys = new Set([
  'notify.enabled', 'notify.internal.enabled', 'notify.supplier.enabled',
  'notify.event.message_created', 'notify.event.file_uploaded',
  'notify.event.project_submitted', 'notify.event.project_confirmed',
  'notify.event.project_rejected', 'notify.event.project_withdrawn',
]);
const pathOf = response => new URL(response.url()).pathname;
const normalizeExtensions = value => Array.from(new Set(String(value).split(',')
  .map(item => item.trim().toLowerCase()).filter(Boolean))).sort().join(',');

(async () => {
  let browser;
  let page;
  let context;
  let token;
  try {
    browser = await chromium.launch({ channel: 'chrome', headless: true });
    context = await browser.newContext({ viewport: { width: 1440, height: 1000 } });
    page = await context.newPage();
    track(page, 'config-edges');
    const auth = await login(page, 'admin', s.adminPassword);
    await page.waitForURL(s.base + '/');
    token = auth.accessToken;
    const marker = crypto.randomBytes(5).toString('hex');
    const json = async (method, url, data, expected = 200) => (
      await api(context, method, url, data, token, expected)
    ).json();
    const robotParts = await json('GET', '/robot-parts?enabledOnly=true');
    await record('Robot 料号选项返回启用且包含供应商显示信息的目录项', async () => {
      assert(robotParts.length > 0, 'robot part options must not be empty');
      assert(robotParts.every(part => part.supplierId && part.supplierName && part.partNumber),
        'every robot part must expose supplier and part-number identity');
    });

    await page.goto(s.base + '/system/config');
    await page.getByRole('heading', { name: '系统参数', exact: true }).waitFor();
    await page.getByLabel('允许上传类型', { exact: true }).waitFor();
    assert.equal(await page.getByLabel('存储告警阈值', { exact: true }).count(), 0);
    const readConfigRows = async () => (await json('GET', '/admin/system/configs'));
    const originalRows = await readConfigRows();
    const original = Object.fromEntries(originalRows.map(row => [row.key, row.value]));
    const keys = originalRows.map(row => row.key).filter(key => !notificationConfigKeys.has(key));
    for (const key of Object.keys(configLabels)) assert.ok(keys.includes(key), 'missing public config ' + key);
    assert.ok(keys.length >= Object.keys(configLabels).length, 'public config list must not be empty');
    assert.ok(keys.every(key => typeof original[key] === 'string'), 'editable config values must be strings');
    const extToken = 'cfg' + marker;
    const changes = Object.fromEntries(keys.map(key => {
      const value = original[key];
      if (key === 'upload.chunk_size') {
        const currentMb = Number(value) / MB;
        const display = currentMb >= 64 ? currentMb - 0.25 : currentMb + 0.25;
        return [key, { input: String(display), persisted: String(Math.round(display * MB)) }];
      }
      if (key === 'upload.max_file_size') {
        const currentMb = Number(value) / MB;
        const display = currentMb >= 20 * 1024 ? currentMb - 1 : currentMb + 1;
        return [key, { input: String(display), persisted: String(Math.round(display * MB)) }];
      }
      if (key === 'upload.allowed_exts') {
        const input = value + ',' + extToken;
        return [key, { input, persisted: normalizeExtensions(input) }];
      }
      const next = value + '-edge-' + marker;
      return [key, { input: next, persisted: next }];
    }));
    const editConfig = async (key, apiValue) => {
      const label = configLabels[key] || key;
      const editor = page.getByLabel(label, { exact: true });
      if (key === 'upload.chunk_size' || key === 'upload.max_file_size') {
        await editor.fill(String(Number(apiValue) / MB));
      } else {
        await editor.fill(apiValue);
      }
    };
    const assertDirty = async count => {
      if (count === 0) {
        assert.equal(await page.locator('.system-config-dirty').count(), 0);
      } else {
        await page.getByText('已修改 ' + count + ' 项', { exact: true }).waitFor();
      }
      assert.equal(await page.locator('.system-config-row--dirty').count(), count, 'dirty row count');
    };
    const saveConfigs = async () => {
      const requestPromise = page.waitForRequest(request => pathOf(request) === '/api/v1/admin/system/configs'
        && request.method() === 'PUT');
      const reloadPromise = page.waitForResponse(response => pathOf(response) === '/api/v1/admin/system/configs'
        && response.request().method() === 'GET' && response.status() === 200);
      await action(page, '/admin/system/configs', 'PUT', () => (
        page.getByRole('button', { name: '保存', exact: true }).click()
      ));
      const request = await requestPromise;
      await reloadPromise;
      await page.getByLabel(configLabels[keys.find(key => configLabels[key])] || keys[0], { exact: true }).waitFor();
      return request.postDataJSON();
    };
    const payloadMap = body => Object.fromEntries(body.items.map(item => [item.key, item.value]));

    await record('O56 全部公开参数逐项编辑、仅提交脏项、MB换算并恢复', async () => {
      let dirty = 0;
      for (const key of keys) {
        await editConfig(key, changes[key].persisted);
        dirty += 1;
        await assertDirty(dirty);
      }
      let body = await saveConfigs();
      let sent = payloadMap(body);
      assert.deepEqual(Object.keys(sent).sort(), keys.sort(), 'first save sends every changed public key');
      for (const key of Object.keys(sent)) assert.equal(sent[key], changes[key].persisted, 'request value ' + key);
      assert.equal(sent['upload.chunk_size'], changes['upload.chunk_size'].persisted, 'chunk MB converted to bytes');
      assert.equal(sent['upload.max_file_size'], changes['upload.max_file_size'].persisted, 'file limit MB converted to bytes');

      const persisted = Object.fromEntries((await readConfigRows()).map(row => [row.key, row.value]));
      for (const key of keys) assert.equal(persisted[key], changes[key].persisted, 'persisted config ' + key);

      dirty = 0;
      for (const key of keys) {
        await editConfig(key, original[key]);
        dirty += 1;
        await assertDirty(dirty);
      }
      body = await saveConfigs();
      sent = payloadMap(body);
      assert.deepEqual(Object.keys(sent).sort(), keys.slice().sort(), 'restore sends all changed public keys');
      for (const key of keys) assert.equal(sent[key], original[key], 'restore request value ' + key);
      const restored = Object.fromEntries((await readConfigRows()).map(row => [row.key, row.value]));
      for (const key of keys) {
        const expected = key === 'upload.allowed_exts' ? normalizeExtensions(original[key]) : original[key];
        assert.equal(restored[key], expected, 'restored config ' + key);
      }
      assert.deepEqual(normalizeExtensions(restored['upload.allowed_exts']), normalizeExtensions(original['upload.allowed_exts']),
        'allowed extension set restored with API canonical ordering');
      await page.reload();
      await page.getByLabel('允许上传类型', { exact: true }).waitFor();
      assert.equal(Number(await page.getByLabel('上传分片大小', { exact: true }).inputValue()), Number(original['upload.chunk_size']) / MB);
      assert.equal(Number(await page.getByLabel('单文件大小上限', { exact: true }).inputValue()), Number(original['upload.max_file_size']) / MB);
      await assertDirty(0);
    });

    await record('Robot 料号选项在系统参数检查后仍可稳定读取', async () => {
      const refreshed = await json('GET', '/robot-parts?enabledOnly=true');
      assert.deepEqual(refreshed.map(part => part.id), robotParts.map(part => part.id));
    });
    await page.screenshot({ path: OUT + '/config-edges-final.png', fullPage: true });
    await record('Robot 料号和系统参数浏览器检查无未捕获脚本异常和服务端500', async () => {
      for (const name of ['page-errors.jsonl', 'http-errors.jsonl']) {
        assert(!fs.existsSync(OUT + '/' + name) || fs.readFileSync(OUT + '/' + name, 'utf8').trim() === '');
      }
    });
  } catch (error) {
    if (page) {
      await page.screenshot({ path: OUT + '/config-edges-failure.png', fullPage: true }).catch(() => {});
      console.log((await page.locator('body').innerText()).slice(-4500));
    }
    console.error(error.stack || error.message);
    process.exitCode = 1;
  } finally {
    if (browser) await browser.close();
  }
})().catch(error => {
  console.error(error.stack || error.message);
  process.exitCode = 1;
});
