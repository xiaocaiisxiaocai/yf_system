const { chromium } = require('playwright');
const crypto = require('node:crypto');
const { assert, OUT, s, f, record, login, api, action, track } = require(process.env.YF_BROWSER_SUPPORT_DIR + '/ui-lib.cjs');

async function createProjectGroup(context, token, supplierId, ownerId, name) {
  const vendors = await (await api(
    context, 'GET', '/project-dictionaries?type=ROBOT_VENDOR&enabledOnly=true', undefined, token)).json();
  assert(vendors.length > 0, 'message edges need a Robot vendor');
  const models = await (await api(
    context, 'GET', '/project-dictionaries?type=ROBOT_MODEL&parentId=' + vendors[0].id
      + '&enabledOnly=true', undefined, token)).json();
  const priorities = await (await api(
    context, 'GET', '/project-dictionaries?type=PRIORITY&enabledOnly=true', undefined, token)).json();
  assert(models.length > 0 && priorities.length > 0, 'message edges need model and priority options');
  const group = await (await api(context, 'POST', '/project-groups', {
    name,
    description: '独立留言边界浏览器验收夹具',
    supplierId,
    workOrderNos: ['WO-' + crypto.randomBytes(4).toString('hex')],
    machineModel: '留言边界机型',
    robotVendorId: vendors[0].id,
    robotModelId: models[0].id,
    responsibleUserId: ownerId,
    priorityId: priorities[0].id,
    expectedCompletionDate: '2099-12-31',
    subprojectNames: [name + ' 子项目'],
  }, token)).json();
  const detail = await (await api(context, 'GET', '/project-groups/' + group.id, undefined, token)).json();
  assert.equal(detail.projects.length, 1, 'message edges group must contain one subproject');
  return detail.projects[0];
}

(async () => {
  let browser, page;
  try {
    browser = await chromium.launch({ channel: 'chrome', headless: true });
    const context = await browser.newContext({ viewport: { width: 1440, height: 1000 } });
    page = await context.newPage(); track(page, 'message-edges');
    const admin = await login(page, 'admin', s.adminPassword); await page.waitForURL(s.base + '/');
    const request = async (method, path, body, expected = 200) =>
      (await api(context, method, path, body, admin.accessToken, expected)).json();
    const owners = await request('GET', '/project-owner-options');
    assert(owners.length > 0, 'message edges need an active project owner');
    const project = await createProjectGroup(
      context, admin.accessToken, f.suppliers.a.id, owners[0].id,
      '留言边界-' + crypto.randomBytes(5).toString('hex'));
    await request('PUT', '/projects/' + project.id + '/status', { status: 'IN_PROGRESS' });
    const endpoint = '/api/v1/projects/' + project.id + '/messages';
    const relative = '/projects/' + project.id + '/messages';
    const input = () => page.getByPlaceholder('输入留言，Ctrl+Enter 发送', { exact: true });
    const waitList = () => page.waitForResponse(response => new URL(response.url()).pathname === endpoint
      && response.request().method() === 'GET' && response.status() === 200);
    const auditCount = async id => (await request('GET', '/admin/audit-logs?action=MESSAGE_CREATE&targetType=message&targetId=' + id)).total;
    const pendingMail = async () => (await request('GET', '/admin/system/mail-status')).queue.pending;
    let posts = 0;
    page.on('request', req => { if (new URL(req.url()).pathname === endpoint && req.method() === 'POST') posts++; });
    await page.goto(s.base + '/projects/' + project.id + '?tab=messages'); await input().waitFor();

    await record('已读刷新后的未读角标不会被迟到的首屏摘要覆盖', async () => {
      const raceProject = await createProjectGroup(
        context, admin.accessToken, f.suppliers.a.id, owners[0].id,
        '未读摘要竞态-' + crypto.randomBytes(4).toString('hex'));
      await request('PUT', '/projects/' + raceProject.id + '/status', { status: 'IN_PROGRESS' });
      const supplierContext = await browser.newContext();
      let release;
      const held = new Promise(resolve => { release = resolve; });
      let arrived;
      const firstArrived = new Promise(resolve => { arrived = resolve; });
      const summaryPath = '/api/v1/projects/' + raceProject.id + '/summary';
      let requests = 0;
      const handler = async route => {
        if (++requests !== 1) return route.continue();
        const response = await route.fetch();
        assert.equal((await response.json()).unreadMessages, 1, 'old snapshot must contain a real unread message');
        arrived(); await held; await route.fulfill({ response });
      };
      try {
        const supplierPage = await supplierContext.newPage(); track(supplierPage, 'summary-race-supplier');
        const supplier = await login(supplierPage, f.users.a.username, f.users.a.changedPassword);
        await api(supplierContext, 'POST', '/projects/' + raceProject.id + '/messages',
          { content: '等待管理员实际阅读的摘要竞态留言' }, supplier.accessToken);
        await page.route('**' + summaryPath, handler);
        await page.goto(s.base + '/projects/' + raceProject.id);
        await firstArrived;
        const read = page.waitForResponse(response => new URL(response.url()).pathname === '/api/v1/messages/read'
          && response.request().method() === 'POST' && response.status() === 200);
        const refreshed = page.waitForResponse(response => new URL(response.url()).pathname === summaryPath && response.status() === 200);
        await page.getByRole('tab', { name: /留言/ }).click();
        await page.getByText('等待管理员实际阅读的摘要竞态留言', { exact: true }).waitFor();
        await read;
        assert.equal((await (await refreshed).json()).unreadMessages, 0);
        const stale = page.waitForResponse(response => new URL(response.url()).pathname === summaryPath);
        release(); await (await stale).finished();
        await page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))));
        assert.equal(await page.getByRole('tab', { name: /留言/ }).locator('.arco-badge').count(), 0);
        assert.equal((await request('GET', '/projects/' + raceProject.id + '/summary')).unreadMessages, 0);
        await page.screenshot({ path: OUT + '/summary-late-response.png' });
      } finally {
        release(); await page.unroute('**' + summaryPath, handler); await supplierContext.close();
      }
      await page.goto(s.base + '/projects/' + project.id + '?tab=messages'); await input().waitFor();
    });

    await record('O33 空白留言与Ctrl+Enter不产生写入', async () => {
      const mailBefore = await pendingMail(); const before = posts;
      await input().fill('  \n \t  ');
      assert(await page.getByRole('button', { name: '发送', exact: true }).isDisabled());
      await input().press('Control+Enter');
      assert.equal((await request('GET', relative)).total, 0);
      assert.equal(await pendingMail(), mailBefore); assert.equal(posts, before);
    });

    let first;
    await record('O33 Ctrl+Enter发送去除首尾空白，等待响应期间重复键入仅提交一次', async () => {
      const mailBefore = await pendingMail(); const before = posts;
      let release, reached;
      const held = new Promise(resolve => { release = resolve; });
      const entered = new Promise(resolve => { reached = resolve; });
      const handler = async route => {
        if (route.request().method() !== 'POST') return route.continue();
        const response = await route.fetch(); reached(); await held; await route.fulfill({ response });
      };
      await page.route('**' + endpoint, handler);
      await input().fill('  Ctrl+Enter可靠发送  ');
      try {
        const response = page.waitForResponse(res => new URL(res.url()).pathname === endpoint && res.request().method() === 'POST');
        await input().press('Control+Enter'); await entered;
        await input().press('Control+Enter'); assert.equal(posts, before + 1);
        release(); const result = await response; assert.equal(result.status(), 200); first = await result.json();
      } finally { release(); await page.unroute('**' + endpoint, handler); }
      await page.getByText('Ctrl+Enter可靠发送', { exact: true }).waitFor();
      assert.equal(first.content, 'Ctrl+Enter可靠发送'); assert.equal(await input().inputValue(), '');
      assert.equal((await request('GET', relative)).total, 1); assert.equal(await auditCount(first.id), 1);
      assert.equal(await pendingMail(), mailBefore + 1);
    });

    await record('O33 留言发送失败保留草稿，重试后仅有一条持久化留言', async () => {
      const body = '故障恢复后只发一次'; const mailBefore = await pendingMail();
      page.expectedServerErrors = new Set([endpoint]);
      await page.route('**' + endpoint, async route => {
        if (route.request().method() === 'POST') await route.fulfill({ status: 503, contentType: 'application/json', body: '{"code":50301,"message":"留言测试暂时不可用"}' });
        else await route.continue();
      }, { times: 1 });
      await input().fill(body);
      const failed = page.waitForResponse(response => new URL(response.url()).pathname === endpoint && response.status() === 503);
      await input().press('Control+Enter'); await failed;
      await page.getByText('留言测试暂时不可用', { exact: true }).waitFor();
      assert.equal(await input().inputValue(), body); assert.equal((await request('GET', relative)).total, 1);
      assert.equal(await pendingMail(), mailBefore);
      const sent = await action(page, relative, 'POST', () => input().press('Control+Enter'));
      await page.getByText(body, { exact: true }).waitFor();
      assert.equal((await request('GET', relative)).total, 2); assert.equal(await auditCount(sent.id), 1);
      assert.equal(await pendingMail(), mailBefore + 1); page.expectedServerErrors.clear();
    });

    const seeded = [];
    for (let i = 0; i < 22; i++) seeded.push(await request('POST', relative, { content: '分页留言-' + String(i).padStart(2, '0') }));
    await record('O35 留言追加失败保留已读列表，重试取得剩余游标页且不重复', async () => {
      const loaded = waitList(); await page.reload(); await loaded;
      await page.getByRole('button', { name: '加载更多（20/24）', exact: true }).waitFor();
      assert.equal(await page.locator('.msg-item').count(), 20);
      page.expectedServerErrors = new Set([endpoint]);
      await page.route('**' + endpoint + '?*', route => route.fulfill({ status: 503, contentType: 'application/json', body: '{"code":50301,"message":"分页测试暂时不可用"}' }), { times: 1 });
      await page.getByRole('button', { name: '加载更多（20/24）', exact: true }).click();
      await page.getByText('加载失败', { exact: true }).waitFor(); assert.equal(await page.locator('.msg-item').count(), 20);
      const retried = waitList(); await page.getByRole('button', { name: '重试', exact: true }).click(); await retried;
      await page.getByText(first.content, { exact: true }).waitFor();
      const ids = await page.locator('.msg-item').evaluateAll(nodes => nodes.map(node => node.dataset.messageId));
      assert.equal(ids.length, 24); assert.equal(new Set(ids).size, 24);
      assert.equal(await page.getByRole('button', { name: /加载更多/ }).count(), 0); page.expectedServerErrors.clear();
    });

    await record('O36 管理员删除留言后双方重读不可见且审计只记一次', async () => {
      const target = seeded[21]; const item = page.locator('[data-message-id="' + target.id + '"]');
      await item.getByRole('button', { name: '删除', exact: true }).click();
      await action(page, '/messages/' + target.id, 'DELETE', () => page.locator('.arco-popconfirm:visible').getByRole('button', { name: '确定', exact: true }).click());
      await item.waitFor({ state: 'detached' });
      assert.equal((await request('GET', relative)).total, 23);
      const logs = await request('GET', '/admin/audit-logs?action=MESSAGE_DELETE&targetType=message&targetId=' + target.id);
      assert.equal(logs.total, 1);
      const vendor = await browser.newContext();
      try {
        const data = await (await api(vendor, 'GET', relative + '?targetId=' + target.id, undefined, f.users.a.token)).json();
        assert.equal(data.total, 0); assert.equal(data.list.length, 0);
      } finally { await vendor.close(); }
    });

    await record('O37-O38 动态筛选分页、追加失败重试、目标跳转与不可用标记', async () => {
      await page.getByRole('tab', { name: '项目动态', exact: true }).click();
      const feed = page.locator('.project-activity-feed'); await feed.locator('[data-activity-id]').first().waitFor();
      const select = page.locator('.project-activity-toolbar .arco-select');
      const activityPath = '/api/v1/projects/' + project.id + '/activities';
      await select.click();
      const [filtered] = await Promise.all([
        page.waitForResponse(response => {
          const url = new URL(response.url());
          return url.pathname === activityPath && url.searchParams.get('type') === 'MESSAGE' && response.status() === 200;
        }),
        page.getByRole('option', { name: '留言', exact: true }).click(),
      ]);
      const filteredIds = (await filtered.json()).list.map(item => String(item.id));
      assert.equal(filteredIds.length, 20);
      await page.waitForFunction(ids => JSON.stringify(Array.from(document.querySelectorAll('.project-activity-feed [data-activity-id]'), node => node.dataset.activityId)) === JSON.stringify(ids), filteredIds);
      const firstIds = await feed.locator('[data-activity-id]').evaluateAll(nodes => nodes.map(node => node.dataset.activityId));
      assert.equal(firstIds.length, 20);
      page.expectedServerErrors = new Set([activityPath]);
      await page.route('**' + activityPath + '?*', route => route.fulfill({ status: 503, contentType: 'application/json', body: '{"code":50301,"message":"动态测试暂时不可用"}' }), { times: 1 });
      await feed.getByRole('button', { name: '加载更多', exact: true }).click();
      await feed.getByText('加载失败', { exact: true }).waitFor();
      assert.deepEqual(await feed.locator('[data-activity-id]').evaluateAll(nodes => nodes.map(node => node.dataset.activityId)), firstIds);
      await feed.getByRole('button', { name: '重试', exact: true }).click();
      await feed.getByRole('button', { name: '查看' + first.content, exact: true }).waitFor();
      const allIds = await feed.locator('[data-activity-id]').evaluateAll(nodes => nodes.map(node => node.dataset.activityId));
      assert(allIds.length > 20); assert.equal(allIds.length, new Set(allIds).size);
      assert.equal(await feed.locator('[data-activity-type]:not([data-activity-type="MESSAGE"])').count(), 0);
      const unavailable = feed.locator('.project-activity-item').filter({ hasText: '留言已不可用' });
      assert(await unavailable.count() > 0, 'deleted message activities must remain visible with an unavailable target');
      assert.equal(await unavailable.getByRole('button').count(), 0);
      const [targetResponse] = await Promise.all([
        page.waitForResponse(response => {
          const url = new URL(response.url());
          return url.pathname === endpoint && url.searchParams.get('targetId') === String(first.id) && response.status() === 200;
        }),
        feed.getByRole('button', { name: '查看' + first.content, exact: true }).click(),
      ]);
      const targetIds = (await targetResponse.json()).list.map(item => String(item.id));
      assert.deepEqual(targetIds, [String(first.id)]);
      await page.waitForURL(url => url.searchParams.get('tab') === 'messages' && url.searchParams.get('target') === String(first.id));
      await page.getByText('已定位到目标内容', { exact: true }).waitFor();
      await page.waitForFunction(ids => JSON.stringify(Array.from(document.querySelectorAll('.msg-item'), node => node.dataset.messageId)) === JSON.stringify(ids), targetIds);
      await page.getByRole('button', { name: '显示全部', exact: true }).click();
      await page.waitForURL(url => !url.searchParams.has('target'));
      await page.getByRole('button', { name: '加载更多（20/23）', exact: true }).waitFor(); page.expectedServerErrors.clear();
    });

    await record('O33 4000字边界保留完整Unicode字符，超出输入限制且API独立拒绝', async () => {
      const content = '中'.repeat(3998) + '🙂🙂';
      await input().fill(content);
      assert.equal(await input().inputValue(), content, '4000 Unicode scalars must be accepted without truncating a surrogate pair');
      const message = await action(page, relative, 'POST', () => input().press('Control+Enter'));
      assert.equal(message.content, content);
      const stored = await request('GET', relative + '?targetId=' + message.id); assert.equal(stored.list[0].content, content);
      await input().fill(content + '超'); assert.equal(await input().inputValue(), content);
      const before = (await request('GET', relative)).total;
      await request('POST', relative, { content: content + '超' }, 400);
      assert.equal((await request('GET', relative)).total, before);
    });
  } catch (error) {
    if (page) { await page.screenshot({ path: OUT + '/message-edges-failure.png', fullPage: true }).catch(() => {}); console.log((await page.locator('body').innerText()).slice(-4000)); }
    console.error(error.stack); process.exitCode = 1;
  } finally { if (browser) await browser.close(); }
})();
