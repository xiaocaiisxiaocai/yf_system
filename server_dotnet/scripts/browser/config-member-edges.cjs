const { chromium } = require('playwright');
const crypto = require('node:crypto');
const {
  fs, assert, OUT, s, f, record, login, api, action, track,
} = require(process.env.YF_BROWSER_SUPPORT_DIR + '/ui-lib.cjs');

const MB = 1024 * 1024;
const configLabels = {
  'notify.enabled': '邮件通知',
  'upload.allowed_exts': '允许上传类型',
  'upload.chunk_size': '上传分片大小',
  'upload.max_file_size': '单文件大小上限',
};
const pathOf = response => new URL(response.url()).pathname;
const normalizeExtensions = value => Array.from(new Set(String(value).split(',')
  .map(item => item.trim().toLowerCase()).filter(Boolean))).sort().join(',');

(async () => {
  let browser;
  let page;
  let context;
  let token;
  let project;
  const createdUsers = [];
  try {
    browser = await chromium.launch({ channel: 'chrome', headless: true });
    context = await browser.newContext({ viewport: { width: 1440, height: 1000 } });
    page = await context.newPage();
    track(page, 'config-member-edges');
    const auth = await login(page, 'admin', s.adminPassword);
    await page.waitForURL(s.base + '/');
    token = auth.accessToken;
    const adminId = auth.user.id;
    const marker = crypto.randomBytes(5).toString('hex');
    const password = () => 'UiEdge9!' + crypto.randomBytes(6).toString('base64url');
    const roleId = Number(f.roles && f.roles['内部成员']);
    assert.ok(roleId > 0, 'fixture must contain the 内部成员 role id');
    assert.ok(f.suppliers && f.suppliers.a && f.suppliers.a.id, 'fixture must contain supplier a');
    const json = async (method, url, data, expected = 200) => (
      await api(context, method, url, data, token, expected)
    ).json();
    const createUser = async (suffix, realName) => {
      const employeeNo = 'edge_' + suffix + '_' + marker;
      const user = await json('POST', '/admin/users', {
        employeeNo,
        password: password(),
        realName,
        email: employeeNo + '@example.invalid',
        departmentId: null,
        roleId,
      });
      createdUsers.push(user.id);
      return user;
    };

    const disabledName = '边界停用成员-' + marker;
    const activeName = '边界候选成员-' + marker;
    const disabledUser = await createUser('disabled', disabledName);
    const activeUser = await createUser('active', activeName);
    project = await json('POST', '/projects', {
      name: '成员边界项目-' + marker,
      supplierId: f.suppliers.a.id,
    });
    await json('PUT', '/projects/' + project.id + '/members', { userIds: [disabledUser.id] });
    await json('PUT', '/admin/users/' + disabledUser.id + '/status', { status: 'DISABLED' });
    await page.goto(s.base + '/projects/' + project.id + '?tab=members');
    await page.getByRole('button', { name: '设置公司成员', exact: true }).waitFor();
    await page.getByText(disabledName, { exact: true }).waitFor();

    const picker = () => page.locator('.member-picker-dialog:visible');
    const option = name => picker().locator('.member-picker-option').filter({ hasText: name });
    const checked = async name => option(name).getByRole('checkbox').isChecked();
    const toggle = async name => option(name).click();
    const memberIds = async () => (await json('GET', '/projects/' + project.id + '/members'))
      .map(member => member.userId).sort((a, b) => a - b);

    await record('O40 成员选项失败可重试且取消草稿不写入', async () => {
      const before = await memberIds();
      page.expectedServerErrors = new Set(['/api/v1/internal-user-options']);
      await page.route('**/api/v1/internal-user-options', route => route.fulfill({
        status: 503,
        contentType: 'application/json',
        body: JSON.stringify({ code: 50301, message: 'temporary member option failure' }),
      }), { times: 1 });
      await page.getByRole('button', { name: '设置公司成员', exact: true }).click();
      await page.getByText('成员选项加载失败', { exact: true }).waitFor();
      assert.equal(await picker().count(), 0, 'failed option request must not open picker');

      const retried = page.waitForResponse(response => pathOf(response) === '/api/v1/internal-user-options'
        && response.request().method() === 'GET' && response.status() === 200);
      await page.getByRole('button', { name: '设置公司成员', exact: true }).click();
      await retried;
      await picker().waitFor();
      assert.equal(await checked(activeName), false);
      assert.equal(await checked(disabledName), true);
      await toggle(activeName);
      await toggle(auth.user.realName);
      assert.equal(await checked(activeName), true);
      assert.equal(await checked(auth.user.realName), false);
      await picker().getByRole('button', { name: '取消', exact: true }).click();
      await picker().waitFor({ state: 'hidden' });
      assert.deepEqual(await memberIds(), before, 'cancel must not write member changes');

      await page.getByRole('button', { name: '设置公司成员', exact: true }).click();
      await picker().waitFor();
      assert.equal(await checked(activeName), false, 'cancelled option must reset on reopen');
      assert.equal(await checked(disabledName), true, 'existing disabled member remains selected for correction');
      assert.equal(await checked(auth.user.realName), true, 'operator selection resets from persisted members');
      page.expectedServerErrors.clear();
    });

    await record('O40 停用既有成员阻止保存且操作人必保留', async () => {
      let memberWrites = 0;
      const countWrites = request => {
        if (pathOf(request) === '/api/v1/projects/' + project.id + '/members'
          && request.method() === 'PUT') memberWrites += 1;
      };
      page.on('request', countWrites);
      await picker().getByRole('button', { name: '保存成员', exact: true }).click();
      await page.getByText('请先取消选择已停用的公司成员', { exact: true }).waitFor();
      assert.equal(memberWrites, 0, 'disabled selected member must block PUT');
      assert.equal(await picker().isVisible(), true, 'blocked save keeps picker open');

      await toggle(disabledName);
      await toggle(auth.user.realName);
      await toggle(activeName);
      assert.equal(await checked(disabledName), false);
      assert.equal(await checked(auth.user.realName), false, 'operator is deliberately removed from draft');
      assert.equal(await checked(activeName), true);
      const requestPromise = page.waitForRequest(request => pathOf(request) === '/api/v1/projects/' + project.id + '/members'
        && request.method() === 'PUT');
      await action(page, '/projects/' + project.id + '/members', 'PUT', () => (
        picker().getByRole('button', { name: '保存成员', exact: true }).click()
      ));
      const request = await requestPromise;
      const body = request.postDataJSON();
      assert.deepEqual([...body.userIds].sort((a, b) => a - b), [adminId, activeUser.id].sort((a, b) => a - b));
      assert.equal(memberWrites, 1, 'corrected draft makes one PUT');
      page.off('request', countWrites);
      const companyMembers = page.locator('section.member-group').first();
      await companyMembers.getByText(activeName, { exact: true }).waitFor();
      assert.equal(await companyMembers.getByText(disabledName, { exact: true }).count(), 0,
        'disabled member removed from visible company member list');
      assert.deepEqual(await memberIds(), [adminId, activeUser.id].sort((a, b) => a - b),
        'server persistence must retain operator and remove disabled member');
    });

    await page.goto(s.base + '/system/config');
    await page.getByRole('heading', { name: '系统参数', exact: true }).waitFor();
    await page.getByLabel('存储告警阈值', { exact: true }).waitFor();
    const readConfigRows = async () => (await json('GET', '/admin/system/configs'));
    const originalRows = await readConfigRows();
    const original = Object.fromEntries(originalRows.map(row => [row.key, row.value]));
    const keys = originalRows.map(row => row.key);
    for (const key of Object.keys(configLabels)) assert.ok(keys.includes(key), 'missing public config ' + key);
    assert.ok(keys.length >= Object.keys(configLabels).length, 'public config list must not be empty');
    assert.ok(keys.every(key => typeof original[key] === 'string'), 'editable config values must be strings');
    const extToken = 'cfg' + marker;
    const changes = Object.fromEntries(keys.map(key => {
      const value = original[key];
      if (key === 'notify.enabled') {
        const next = value === 'true' ? 'false' : 'true';
        return [key, { input: next, persisted: next }];
      }
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
      if (key === 'notify.enabled') {
        await editor.click();
        await page.getByRole('option', { name: apiValue === 'true' ? '启用' : '关闭', exact: true }).click();
      } else if (key === 'upload.chunk_size' || key === 'upload.max_file_size') {
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
      const deferredKey = 'notify.enabled';
      await editConfig(deferredKey, original[deferredKey]);
      dirty -= 1;
      await assertDirty(dirty);
      let body = await saveConfigs();
      let sent = payloadMap(body);
      assert.deepEqual(Object.keys(sent).sort(), keys.filter(key => key !== deferredKey).sort(),
        'first save sends only dirty keys');
      for (const key of Object.keys(sent)) assert.equal(sent[key], changes[key].persisted, 'request value ' + key);
      assert.equal(sent['upload.chunk_size'], changes['upload.chunk_size'].persisted, 'chunk MB converted to bytes');
      assert.equal(sent['upload.max_file_size'], changes['upload.max_file_size'].persisted, 'file limit MB converted to bytes');

      await editConfig(deferredKey, changes[deferredKey].persisted);
      await assertDirty(1);
      body = await saveConfigs();
      sent = payloadMap(body);
      assert.deepEqual(Object.keys(sent), [deferredKey], 'second save sends only deferred dirty key');
      assert.equal(sent[deferredKey], changes[deferredKey].persisted);
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
      await page.getByLabel('存储告警阈值', { exact: true }).waitFor();
      assert.equal(Number(await page.getByLabel('上传分片大小', { exact: true }).inputValue()), Number(original['upload.chunk_size']) / MB);
      assert.equal(Number(await page.getByLabel('单文件大小上限', { exact: true }).inputValue()), Number(original['upload.max_file_size']) / MB);
      await assertDirty(0);
    });

    await record('O40 自有成员项目与账号清理生效', async () => {
      await json('DELETE', '/projects/' + project.id);
      project = null;
      for (const id of createdUsers.splice(0)) await json('DELETE', '/admin/users/' + id);
    });
    await page.screenshot({ path: OUT + '/config-member-edges-final.png', fullPage: true });
    await record('O40/O56 浏览器运行无未捕获脚本异常和服务端500', async () => {
      for (const name of ['page-errors.jsonl', 'http-errors.jsonl']) {
        assert(!fs.existsSync(OUT + '/' + name) || fs.readFileSync(OUT + '/' + name, 'utf8').trim() === '');
      }
    });
  } catch (error) {
    if (page) {
      await page.screenshot({ path: OUT + '/config-member-edges-failure.png', fullPage: true }).catch(() => {});
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
