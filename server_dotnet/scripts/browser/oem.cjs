// OEM business line, driven end to end through the real UI:
// admin creates an OEM vendor + account (/oem/admin/companies) → an internal sender submits a
// PDF to that vendor → the section leader approves from the paged approval inbox → the vendor
// changes its first-login password in /oem-portal, previews (watermarked) and downloads the
// released file → the vendor sends a file back and the internal sender downloads it.
// File validation/promotion runs through the TestHost's opt-in OEM job loop
// (YF_TESTHOST_OEM_PROCESS=1 + App__OemStorageRoot set by test-browser.py).
const { chromium } = require('playwright');
const crypto = require('node:crypto');
const {
  fs, assert, OUT, s, f, save, record, login, reserveLoginBudget, api, action, track,
} = require(process.env.YF_BROWSER_SUPPORT_DIR + '/ui-lib.cjs');

const sha = file => crypto.createHash('sha256').update(fs.readFileSync(file)).digest('hex');
const sleep = ms => new Promise(resolve => setTimeout(resolve, ms));
const marker = crypto.randomBytes(4).toString('hex');
// Test-only fixture credentials for a disposable database; never reused outside this run.
const password = () => 'Oem9!' + crypto.randomBytes(6).toString('base64url');
let browser;
let lastPage;

async function newPage(label) {
  const context = await browser.newContext({ viewport: { width: 1440, height: 1000 }, acceptDownloads: true });
  const page = await context.newPage();
  track(page, label);
  lastPage = page;
  return { context, page };
}

async function json(context, token, method, url, body, expected = 200) {
  return (await api(context, method, url, body, token, expected)).json();
}

const visibleModal = page => page.locator('.arco-modal:visible').last();

/** Repeats `probe` (which may reload the page) until it returns a truthy value. */
async function poll(label, probe, timeout = 90000) {
  const deadline = Date.now() + timeout;
  for (;;) {
    const value = await probe();
    if (value) return value;
    if (Date.now() > deadline) throw new Error(label + ' did not happen within ' + timeout / 1000 + 's');
    await sleep(1000);
  }
}

/** Internal first login: forced password change, then sign in with the new password. */
async function internalFirstLogin(page, user) {
  const first = await login(page, user.username, user.password);
  assert.equal(first.mustChangePassword, true);
  await page.waitForURL('**/change-password');
  await page.getByRole('textbox', { name: '原密码', exact: true }).fill(user.password);
  await page.getByRole('textbox', { name: '新密码', exact: true }).fill(user.changedPassword);
  await page.getByRole('textbox', { name: '确认新密码', exact: true }).fill(user.changedPassword);
  await page.getByRole('button', { name: '确认修改', exact: true }).click();
  await page.waitForURL('**/login');
  const result = await login(page, user.username, user.changedPassword);
  assert.equal(result.mustChangePassword, false);
  await page.waitForURL(url => !/\/(login|change-password)$/.test(url.pathname));
  return result;
}

async function portalLogin(page, username, secret) {
  await page.getByLabel('登录账号', { exact: true }).fill(username);
  await page.getByLabel('密码', { exact: true }).fill(secret);
  await reserveLoginBudget(username);
  const response = await action(page, '/oem/auth/login', 'POST',
    () => page.getByRole('button', { name: '登录', exact: true }).click());
  return response;
}

async function downloadRow(page, row, target) {
  const ready = page.waitForEvent('download');
  await row.getByRole('button', { name: '下载', exact: true }).click();
  const item = await ready;
  await item.saveAs(target);
  assert.equal(await item.failure(), null);
}

/** Opens a transfer detail and reloads until its attachment row offers a download. */
async function openReleasedFile(page, url, fileName) {
  await page.goto(s.base + url);
  return poll('download of ' + fileName, async () => {
    // Wait for the authenticated render before any reload: reloading while the boot-time
    // refresh is in flight would drop the rotated cookie and trip refresh-replay revocation.
    const row = page.getByRole('row').filter({ hasText: fileName });
    await row.waitFor();
    if (await row.getByRole('button', { name: '下载', exact: true }).isVisible()) return row;
    await page.reload();
    return null;
  });
}

(async () => {
  try {
    browser = await chromium.launch({ channel: 'chrome', headless: true });
    const outboundName = 'oem-outbound-' + marker + '.pdf';
    const inboundName = 'oem-inbound-' + marker + '.pdf';
    const outbound = OUT + '/' + outboundName;
    const inbound = OUT + '/' + inboundName;
    fs.copyFileSync(process.env.YF_PROJECT_ROOT + '/web/test/fixtures/pdf-compatibility.pdf', outbound);
    fs.writeFileSync(inbound, '%PDF-1.4\n% OEM inbound process sheet ' + marker + '\n%%EOF\n');
    const companyName = 'UI OEM厂商-' + marker;
    const vendor = { username: 'uioem_' + marker, password: password(), changedPassword: password(), realName: 'UI厂商联系人' };

    // Admin session: OEM directory management happens in the UI; roles/org/users are harness fixtures.
    const admin = await newPage('oem-admin');
    const adminAuth = await login(admin.page, 'admin', s.adminPassword);
    await admin.page.waitForURL(s.base + '/');
    const adminToken = adminAuth.accessToken;
    const users = {};
    await record('OEM 测试夹具：发送人/课别主管角色、组织课别与主管', async () => {
      const permissions = await json(admin.context, adminToken, 'GET', '/permissions');
      const ids = codes => codes.map(code => {
        const item = permissions.find(permission => permission.code === code);
        assert.ok(item, 'permission exists: ' + code);
        return item.id;
      });
      const role = async (name, codes) => {
        const created = await json(admin.context, adminToken, 'POST', '/admin/roles', { name: name + '-' + marker, description: 'OEM 浏览器验收' });
        await api(admin.context, 'PUT', '/admin/roles/' + created.id + '/permissions', { permissionIds: ids(codes) }, adminToken);
        return created.id;
      };
      const senderRole = await role('OEM发送人', ['oem:transfer_create', 'oem:transfer_view', 'oem:file_download']);
      const leaderRole = await role('OEM课别主管', ['oem:flow_approve', 'oem:transfer_view']);
      const division = await json(admin.context, adminToken, 'POST', '/admin/departments', { name: 'OEM事业部-' + marker, parentId: null, sortNo: 950 });
      const department = await json(admin.context, adminToken, 'POST', '/admin/departments', { name: 'OEM部门-' + marker, parentId: division.id, sortNo: 951 });
      const section = await json(admin.context, adminToken, 'POST', '/admin/departments', { name: 'OEM课别-' + marker, parentId: department.id, sortNo: 952 });
      for (const [key, realName, roleId] of [['sender', 'OEM发送人', senderRole], ['leader', 'OEM课别主管', leaderRole]]) {
        const initial = password();
        const created = await json(admin.context, adminToken, 'POST', '/admin/users', {
          employeeNo: 'oem_' + key + '_' + marker, password: initial, realName,
          email: key + '.' + marker + '@example.invalid', departmentId: section.id, roleId,
        });
        users[key] = { id: created.id, username: created.employeeNo, password: initial, changedPassword: password(), realName };
      }
      await api(admin.context, 'PUT', '/admin/departments/' + section.id + '/leader', { leaderUserId: users.leader.id }, adminToken);
      f.oem = { companyName, sectionId: section.id, users: Object.fromEntries(Object.entries(users).map(([key, user]) => [key, { id: user.id, username: user.username }])) };
      save();
    });

    await record('管理员在 OEM 厂商与账号页面新增厂商和首次登录账号', async () => {
      const page = admin.page;
      await page.goto(s.base + '/oem/admin/companies');
      await page.getByText('OEM 厂商与账号', { exact: true }).waitFor();
      await page.getByRole('button', { name: '新增厂商', exact: true }).click();
      let modal = visibleModal(page);
      await modal.getByLabel('厂商名称', { exact: true }).fill(companyName);
      await modal.getByLabel('联系人', { exact: true }).fill('UI联系人');
      const company = await action(page, '/oem/companies', 'POST',
        () => modal.getByRole('button', { name: '确定', exact: true }).click());
      assert.equal(company.name, companyName);
      f.oem.companyId = company.id;
      save();
      const row = page.getByRole('row').filter({ hasText: companyName });
      await row.waitFor();
      await row.getByRole('button', { name: '账号', exact: true }).click();
      const drawer = page.locator('.oem-accounts-drawer .arco-drawer:visible');
      await drawer.getByText(companyName + ' · 登录账号', { exact: true }).waitFor();
      await drawer.getByRole('button', { name: '新增账号', exact: true }).click();
      modal = visibleModal(page);
      await modal.getByLabel('登录账号', { exact: true }).fill(vendor.username);
      await modal.getByLabel('姓名', { exact: true }).fill(vendor.realName);
      await modal.getByLabel('邮箱', { exact: true }).fill(vendor.username + '@vendor.invalid');
      await modal.getByLabel('初始密码', { exact: true }).fill(vendor.password);
      const account = await action(page, '/oem/companies/' + company.id + '/accounts', 'POST',
        () => modal.getByRole('button', { name: '确定', exact: true }).click());
      assert.equal(account.employeeNo, vendor.username);
      assert.equal(account.mustChangePassword, true);
      const accountRow = drawer.getByRole('row').filter({ hasText: vendor.username });
      await accountRow.getByText('待改密', { exact: true }).waitFor();
      await page.screenshot({ path: OUT + '/oem-admin-vendor-account.png', fullPage: true });
      await drawer.locator('.arco-drawer-close-icon').click();
      await drawer.waitFor({ state: 'hidden' });
      await page.reload();
      await page.getByRole('row').filter({ hasText: companyName }).getByText('1 / 1', { exact: true }).waitFor();
    });
    await admin.context.close();

    const sender = await newPage('oem-sender');
    let outboundId;
    await record('内部发送人首次改密后选择 OEM 厂商、上传 PDF 并提交审批', async () => {
      await internalFirstLogin(sender.page, users.sender);
      const page = sender.page;
      await page.goto(s.base + '/oem');
      await page.waitForURL(s.base + '/oem/transfers');
      await page.getByRole('button', { name: '发送文件', exact: true }).click();
      const modal = visibleModal(page);
      await modal.getByText('发送文件', { exact: true }).first().waitFor();
      await modal.getByPlaceholder('选择厂商', { exact: true }).click();
      await page.getByRole('option', { name: companyName, exact: true }).click();
      await modal.getByLabel('选择文件', { exact: true }).setInputFiles(outbound);
      await modal.getByText(outboundName, { exact: true }).waitFor();
      const sent = await action(page, '/send', 'POST',
        () => modal.getByRole('button', { name: '提交审批', exact: true }).click());
      assert.equal(sent.summary.direction, 'INTERNAL_TO_OEM');
      assert.equal(sent.summary.lifecycleStatus, 'SEALED');
      assert.equal(sent.summary.title, outboundName);
      outboundId = sent.summary.id;
      await page.waitForURL(s.base + '/oem/transfers/' + outboundId);
      await page.getByText(outboundName, { exact: true }).first().waitFor();
      f.oem.outboundId = outboundId;
      save();
      await page.screenshot({ path: OUT + '/oem-sender-submitted.png', fullPage: true });
    });

    const leader = await newPage('oem-leader');
    await record('课别主管在分页待审批列表看到校验完成的传递单并审批通过', async () => {
      await internalFirstLogin(leader.page, users.leader);
      const page = leader.page;
      // The inbox lists the task only after the TestHost job loop validated and promoted the file.
      const listed = await poll('pending approval for ' + outboundName, async () => {
        const pending = page.waitForResponse(r => new URL(r.url()).pathname === '/api/v1/oem/approvals/pending'
          && r.request().method() === 'GET');
        await page.goto(s.base + '/oem/approvals');
        const response = await pending;
        assert.equal(response.status(), 200);
        const query = new URL(response.url()).searchParams;
        assert.equal(query.get('page'), '1');
        assert.equal(query.get('pageSize'), '20');
        const body = await response.json();
        assert.equal(body.page, 1);
        assert.equal(body.pageSize, 20);
        return body.list.find(task => task.transferId === outboundId) ? body : null;
      });
      assert.ok(listed.total >= 1);
      await page.getByText('待我审批', { exact: true }).first().waitFor();
      await page.locator('.arco-pagination').waitFor();
      const row = page.getByRole('row').filter({ hasText: outboundName });
      await row.getByText(users.sender.realName + '（' + users.sender.username + '）', { exact: true }).waitFor();
      await page.screenshot({ path: OUT + '/oem-approval-inbox.png', fullPage: true });
      await row.getByRole('link', { name: outboundName, exact: true }).click();
      await page.waitForURL(s.base + '/oem/transfers/' + outboundId);
      const approved = await action(page, '/approve', 'POST',
        () => page.getByRole('button', { name: '审批通过', exact: true }).click());
      assert.equal(approved.summary.approvalStatus, 'APPROVED');
      await page.getByText('已审批通过', { exact: true }).waitFor();
      await page.screenshot({ path: OUT + '/oem-approved.png', fullPage: true });
    });
    await leader.context.close();

    const portal = await newPage('oem-vendor');
    await record('OEM 厂商门户首次登录强制改密并以新密码重新登录', async () => {
      const page = portal.page;
      await page.goto(s.base + '/oem-portal');
      await page.waitForURL('**/oem-portal/login');
      const first = await portalLogin(page, vendor.username, vendor.password);
      assert.equal(first.mustChangePassword, true);
      await page.waitForURL('**/oem-portal/change-password');
      await page.getByText('首次登录请修改密码', { exact: true }).waitFor();
      await page.getByLabel('当前密码', { exact: true }).fill(vendor.password);
      await page.getByLabel('新密码', { exact: true }).fill(vendor.changedPassword);
      await page.getByLabel('确认新密码', { exact: true }).fill(vendor.changedPassword);
      await action(page, '/oem/auth/password', 'PUT',
        () => page.getByRole('button', { name: '保存', exact: true }).click());
      await page.waitForURL('**/oem-portal/login');
      const second = await portalLogin(page, vendor.username, vendor.changedPassword);
      assert.equal(second.mustChangePassword, false);
      await page.waitForURL('**/oem-portal/transfers');
    });

    await record('OEM 厂商看到已发布传递单，带水印页内预览 PDF 并下载 SHA 一致', async () => {
      const page = portal.page;
      await poll('released transfer visible to vendor', async () => {
        const listed = page.waitForResponse(r => new URL(r.url()).pathname === '/api/v1/oem/transfers'
          && r.request().method() === 'GET');
        await page.goto(s.base + '/oem-portal/transfers');
        const response = await listed;
        assert.equal(response.status(), 200);
        return (await response.json()).list.some(item => item.id === outboundId);
      });
      await page.getByRole('link', { name: outboundName, exact: true }).click();
      await page.waitForURL('**/oem-portal/transfers/' + outboundId);
      const row = await openReleasedFile(page, '/oem-portal/transfers/' + outboundId, outboundName);
      await row.getByRole('button', { name: '预览', exact: true }).click();
      const dialog = page.getByRole('dialog');
      await dialog.getByText('预览：' + outboundName, { exact: true }).waitFor();
      await dialog.locator('canvas').first().waitFor();
      await page.waitForFunction(() => {
        const canvas = document.querySelector('[role=dialog] canvas');
        return canvas && canvas.width > 0 && canvas.height > 0;
      });
      const watermark = dialog.locator('.preview-watermark');
      await watermark.waitFor({ state: 'visible' });
      assert.equal(await watermark.getAttribute('aria-hidden'), 'true');
      const stamp = await watermark.locator('span').first().textContent() || '';
      assert.match(stamp, /^\S+ .+ \d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}$/, 'OEM preview watermark');
      assert.ok(stamp.startsWith(vendor.username + ' ' + vendor.realName), 'watermark names the vendor viewer');
      await page.screenshot({ path: OUT + '/oem-vendor-preview.png', fullPage: true });
      await dialog.getByRole('button', { name: '关闭文件预览', exact: true }).click();
      await dialog.waitFor({ state: 'hidden' });
      const received = OUT + '/oem-vendor-received.pdf';
      await downloadRow(page, row, received);
      assert.equal(sha(received), sha(outbound));
    });

    let inboundId;
    await record('OEM 厂商从门户回传 PDF，无需内部审批', async () => {
      const page = portal.page;
      await page.goto(s.base + '/oem-portal/transfers');
      await page.getByRole('button', { name: '发送文件', exact: true }).click();
      const modal = visibleModal(page);
      await modal.getByLabel('选择文件', { exact: true }).setInputFiles(inbound);
      await modal.getByText(inboundName, { exact: true }).waitFor();
      const sent = await action(page, '/send', 'POST',
        () => modal.getByRole('button', { name: '发送文件', exact: true }).click());
      assert.equal(sent.summary.direction, 'OEM_TO_INTERNAL');
      assert.equal(sent.summary.approvalStatus, 'NOT_REQUIRED');
      inboundId = sent.summary.id;
      await page.waitForURL('**/oem-portal/transfers/' + inboundId);
      f.oem.inboundId = inboundId;
      save();
      await page.screenshot({ path: OUT + '/oem-vendor-inbound-sent.png', fullPage: true });
    });

    lastPage = sender.page;
    await record('内部发送人在“OEM 发来”中看到回传并下载 SHA 一致', async () => {
      const page = sender.page;
      await page.goto(s.base + '/oem/transfers');
      const filtered = page.waitForResponse(r => {
        const url = new URL(r.url());
        return url.pathname === '/api/v1/oem/transfers' && url.searchParams.get('direction') === 'OEM_TO_INTERNAL';
      });
      await page.getByText('OEM 发来', { exact: true }).click();
      assert.equal((await filtered).status(), 200);
      const link = page.getByRole('link', { name: inboundName, exact: true });
      await link.waitFor();
      await page.getByRole('row').filter({ hasText: inboundName }).getByText(companyName, { exact: true }).waitFor();
      await link.click();
      await page.waitForURL(s.base + '/oem/transfers/' + inboundId);
      const row = await openReleasedFile(page, '/oem/transfers/' + inboundId, inboundName);
      const received = OUT + '/oem-internal-received.pdf';
      await downloadRow(page, row, received);
      assert.equal(sha(received), sha(inbound));
      await page.screenshot({ path: OUT + '/oem-internal-inbound.png', fullPage: true });
    });
  } catch (error) {
    if (lastPage) {
      await lastPage.screenshot({ path: OUT + '/oem-failure.png', fullPage: true }).catch(() => {});
      console.log((await lastPage.locator('body').innerText().catch(() => '')).slice(0, 3000));
    }
    console.error(error.stack || error.message);
    process.exitCode = 1;
  } finally {
    if (browser) await browser.close();
  }
})();
