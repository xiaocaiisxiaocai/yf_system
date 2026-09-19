/*
 * Reusable collaboration acceptance. Run after auth -> fixtures -> users.
 * It owns one disposable project, uses UI for collaboration mutations, and
 * uses APIs only for fixture setup, bulk seeding, and postconditions.
 */
const { chromium } = require('playwright');
const crypto = require('node:crypto');
const {
  fs, assert, OUT, s, f, record, login, api, action, track,
} = require(process.env.YF_BROWSER_SUPPORT_DIR + '/ui-lib.cjs');

const apiPath = path => '/api/v1' + path;
const pathOf = value => new URL(value.url()).pathname;
const notificationRow = (drawer, id) => drawer.locator(`[data-notification-id="${id}"]`);

async function json(context, token, method, path, body, expected = 200) {
  return (await api(context, method, path, body, token, expected)).json();
}

async function createProjectGroup(context, token, supplierId, ownerId, name) {
  const vendors = await json(context, token, 'GET', '/project-dictionaries?type=ROBOT_VENDOR&enabledOnly=true');
  assert(vendors.length > 0, 'project fixture needs a Robot vendor');
  const models = await json(context, token, 'GET',
    `/project-dictionaries?type=ROBOT_MODEL&parentId=${vendors[0].id}&enabledOnly=true`);
  const priorities = await json(context, token, 'GET', '/project-dictionaries?type=PRIORITY&enabledOnly=true');
  assert(models.length > 0 && priorities.length > 0, 'project fixture needs model and priority options');
  const group = await json(context, token, 'POST', '/project-groups', {
    name,
    description: '浏览器协作通知独立夹具',
    supplierId,
    workOrderNos: ['WO-' + crypto.randomBytes(4).toString('hex')],
    machineModel: '浏览器协作机型',
    robotVendorId: vendors[0].id,
    robotModelId: models[0].id,
    responsibleUserId: ownerId,
    priorityId: priorities[0].id,
    expectedCompletionDate: '2099-12-31',
    subprojectNames: [name + ' 子项目'],
  });
  const detail = await json(context, token, 'GET', `/project-groups/${group.id}`);
  assert.equal(detail.projects.length, 1, 'project group fixture must contain one subproject');
  return { ...detail.projects[0], projectGroupId: group.id };
}

async function waitResponse(page, path, method = 'GET', status = 200, timeout = 12000) {
  return page.waitForResponse(response => pathOf(response) === apiPath(path)
    && response.request().method() === method && response.status() === status, { timeout });
}

async function waitSummary(page, accept, timeout = 30000) {
  const deadline = Date.now() + timeout;
  while (Date.now() < deadline) {
    const response = await waitResponse(page, '/collaboration/summary', 'GET', 200, deadline - Date.now());
    const body = await response.json();
    if (accept(body)) return body;
  }
  throw new Error('collaboration summary did not reach the expected state');
}

async function waitPendingProject(page, projectId, timeout = 12000) {
  const deadline = Date.now() + timeout;
  while (Date.now() < deadline) {
    const response = await waitResponse(
      page, '/dashboard/pending-projects', 'GET', 200, deadline - Date.now());
    const body = await response.json();
    if (body.list?.some(item => item.id === projectId)) return body;
  }
  throw new Error('pending-project response did not include the submitted project');
}

async function listNotifications(context, token, unreadOnly) {
  const items = [];
  for (let page = 1; page <= 10; page += 1) {
    const result = await json(context, token, 'GET',
      `/collaboration/notifications?page=${page}&pageSize=100&unreadOnly=${unreadOnly}`);
    items.push(...result.list);
    if (items.length >= result.total || result.list.length === 0) return { ...result, list: items };
  }
  throw new Error('notification fixture unexpectedly exceeded 1000 rows');
}

async function listDashboardMessages(context, token, unreadOnly) {
  const result = await json(context, token, 'GET',
    `/dashboard/messages?page=1&pageSize=100&unreadOnly=${unreadOnly}`);
  return result.list;
}

async function openNotifications(page) {
  await page.getByRole('button', { name: /^协作动态通知(?:，\d+ 条未查看)?$/ }).click();
  const drawer = page.locator('[data-collaboration-drawer="true"]');
  await drawer.waitFor({ state: 'visible' });
  return drawer;
}

async function closeNotifications(page, drawer) {
  await page.locator('.collaboration-drawer:visible')
    .getByRole('button', { name: '关闭抽屉', exact: true }).click();
  await drawer.waitFor({ state: 'hidden' });
}

async function chooseNotificationTab(drawer, name) {
  const tab = drawer.getByRole('tab', { name, exact: true });
  if ((await tab.getAttribute('aria-selected')) !== 'true') await tab.click();
}

(async () => {
  let browser;
  let adminContext;
  let supplierContext;
  let supplierBContext;
  let mobileContext;
  let adminPage;
  let supplierPage;
  let supplierBPage;
  let mobilePage;
  const originalUiProjects = JSON.stringify(f.uiProjects);
  try {
    assert(f.users?.a?.uiFirstChanged && f.users?.a?.token, 'supplier A must complete users step');
    assert(f.users?.b?.uiFirstChanged && f.users?.b?.token, 'supplier B must complete users step');
    assert(f.suppliers?.a?.id && f.suppliers?.b?.id, 'supplier fixtures A and B are required');

    browser = await chromium.launch({ channel: 'chrome', headless: true });
    adminContext = await browser.newContext({ viewport: { width: 1440, height: 1000 } });
    supplierContext = await browser.newContext({ viewport: { width: 1440, height: 1000 } });
    adminPage = await adminContext.newPage();
    supplierPage = await supplierContext.newPage();
    track(adminPage, 'collaboration-admin');
    track(supplierPage, 'collaboration-supplier-a');
    adminPage.setDefaultTimeout(12000);
    supplierPage.setDefaultTimeout(12000);

    const adminAuth = await login(adminPage, 'admin', s.adminPassword);
    const supplierAuth = await login(supplierPage, f.users.a.username, f.users.a.changedPassword);
    await Promise.all([adminPage.waitForURL(s.base + '/'), supplierPage.waitForURL(s.base + '/')]);
    const adminToken = adminAuth.accessToken;
    const supplierToken = supplierAuth.accessToken;
    const marker = crypto.randomBytes(5).toString('hex');
    const ownerOptions = await json(adminContext, adminToken, 'GET', '/project-owner-options');
    const owner = ownerOptions.find(item => item.sectionName?.trim());
    assert(owner, 'project owner fixture must provide an active owner with a section');
    const project = await createProjectGroup(
      adminContext, adminToken, f.suppliers.a.id, owner.id, '协作联动-' + marker);
    const projectName = project.name;
    await json(adminContext, adminToken, 'PUT', `/projects/${project.id}/status`, {
      status: 'IN_PROGRESS',
    });

    const supplierInput = () => supplierPage.getByPlaceholder('输入留言，Ctrl+Enter 发送', { exact: true });
    const adminInput = () => adminPage.getByPlaceholder('输入留言，Ctrl+Enter 发送', { exact: true });
    await adminPage.goto(s.base + '/');
    await adminPage.getByRole('heading', { name: new RegExp('^工作台 · ') }).waitFor();
    await supplierPage.goto(s.base + `/projects/${project.id}?tab=messages`);
    await supplierInput().waitFor();
    const liveMessageText = '供应商即时留言-' + marker;
    let liveMessage;

    await record('协作双会话中供应商留言使异页管理员十二秒内出现未查看角标', async () => {
      const before = await json(adminContext, adminToken, 'GET', '/collaboration/summary');
      const summaryChanged = waitSummary(adminPage,
        value => value.revision !== before.revision && value.unreadCount > before.unreadCount);
      await supplierInput().fill(liveMessageText);
      const started = Date.now();
      liveMessage = await action(supplierPage, `/projects/${project.id}/messages`, 'POST',
        () => supplierInput().press('Control+Enter'));
      const next = await summaryChanged;
      await adminPage.getByRole('button', { name: new RegExp(`^协作动态通知，${next.unreadCount} 条未查看$`) }).waitFor();
      assert(Date.now() - started <= 12000, 'admin notification badge must update within 12 seconds');
      assert.equal(adminPage.url(), s.base + '/', 'admin stays on the dashboard without a refresh');
    });

    await record('通知深链定位真实留言且目标模式仍可继续编辑', async () => {
      const notification = (await listNotifications(adminContext, adminToken, true)).list
        .find(item => item.type === 'MESSAGE' && item.targetId === liveMessage.id);
      assert(notification, 'live message notification exists');
      const drawer = await openNotifications(adminPage);
      const row = notificationRow(drawer, notification.id);
      await row.waitFor();
      const targetLoaded = waitResponse(adminPage, `/projects/${project.id}/messages`);
      await row.getByRole('button', { name: `查看通知：${notification.title}`, exact: true }).click();
      await targetLoaded;
      await adminPage.waitForURL(url => url.pathname === `/projects/${project.id}`
        && url.searchParams.get('tab') === 'messages'
        && url.searchParams.get('target') === String(liveMessage.id));
      await drawer.waitFor({ state: 'hidden' });
      await adminPage.locator(`[data-message-id="${liveMessage.id}"]`).getByText(liveMessageText, { exact: true }).waitFor();
      await adminInput().waitFor();
      assert.equal(await adminInput().isEnabled(), true);
    });

    await record('二十二条真实未查看通知形成两页积压且翻页无重复', async () => {
      for (let index = 0; index < 22; index += 1) {
        await json(supplierContext, supplierToken, 'POST',
          `/projects/${project.id}/messages`, { content: `协作积压-${String(index + 1).padStart(2, '0')}-${marker}` });
      }
      const summary = await waitSummary(adminPage, value => value.unreadCount >= 22);
      assert(summary.unreadCount >= 22);
      const drawer = await openNotifications(adminPage);
      await chooseNotificationTab(drawer, '未查看');
      await drawer.locator('[data-notification-id]').first().waitFor();
      const firstIds = await drawer.locator('[data-notification-id]').evaluateAll(nodes => nodes.map(node => node.dataset.notificationId));
      assert.equal(firstIds.length, 20);
      const secondPage = waitResponse(adminPage, '/collaboration/notifications');
      await drawer.locator('.arco-pagination-item').filter({ hasText: /^2$/ }).click();
      const pageTwoResponse = await secondPage;
      assert.equal(new URL(pageTwoResponse.url()).searchParams.get('page'), '2');
      const pageTwoBody = await pageTwoResponse.json();
      await notificationRow(drawer, pageTwoBody.list[0].id).waitFor();
      const secondIds = await drawer.locator('[data-notification-id]').evaluateAll(nodes => nodes.map(node => node.dataset.notificationId));
      assert(secondIds.length >= 2);
      assert.equal(new Set([...firstIds, ...secondIds]).size, firstIds.length + secondIds.length);
      await closeNotifications(adminPage, drawer);
    });

    await record('另一参与人发言时实时追加新留言并保留供应商草稿和既有列表', async () => {
      await supplierPage.getByText(`协作积压-22-${marker}`, { exact: true }).waitFor();
      const beforeIds = await supplierPage.locator('.msg-item')
        .evaluateAll(nodes => nodes.map(node => node.dataset.messageId));
      assert(beforeIds.length >= 23);
      const draft = '尚未发送的供应商草稿-' + marker;
      const adminText = '管理员并发留言-' + marker;
      await supplierInput().fill(draft);
      // The first notification above has the 12s user-facing SLA. This
      // follow-up refresh has no such SLA and can wait for the current
      // message burst to settle without making the integration test flaky.
      const supplierReload = waitResponse(
        supplierPage, `/projects/${project.id}/messages`, 'GET', 200, 30000).then(
        response => ({ response }), error => ({ error }));
      const actorPage = await adminContext.newPage();
      track(actorPage, 'collaboration-admin-actor');
      actorPage.setDefaultTimeout(12000);
      try {
        await actorPage.goto(s.base + `/projects/${project.id}?tab=messages&target=${liveMessage.id}`);
        const actorInput = actorPage.getByPlaceholder('输入留言，Ctrl+Enter 发送', { exact: true });
        await actorInput.fill(adminText);
        await action(actorPage, `/projects/${project.id}/messages`, 'POST',
          () => actorInput.press('Control+Enter'));
      } finally {
        await actorPage.close();
      }
      const supplierReloadResult = await supplierReload;
      if (supplierReloadResult.error) throw supplierReloadResult.error;
      await supplierPage.getByText(adminText, { exact: true }).waitFor();
      assert.equal(await supplierInput().inputValue(), draft);
      const afterIds = await supplierPage.locator('.msg-item')
        .evaluateAll(nodes => nodes.map(node => node.dataset.messageId));
      assert.equal(new Set(afterIds).size, afterIds.length);
      assert(beforeIds.every(id => afterIds.includes(id)), 'realtime refresh preserves every previously loaded message');
      assert.equal(afterIds.length, beforeIds.length + 1);
      assert.equal(await supplierInput().inputValue(), draft);
      await supplierInput().fill('');
    });

    const uploadName = '供应商联动上传-' + marker + '.pdf';
    await record('供应商真实上传后管理员文件列表按协作修订静默刷新', async () => {
      const initialAdminFiles = waitResponse(adminPage, `/projects/${project.id}/files`);
      const initialSupplierFiles = waitResponse(supplierPage, `/projects/${project.id}/files`);
      await Promise.all([
        adminPage.goto(s.base + `/projects/${project.id}?tab=files`),
        supplierPage.goto(s.base + `/projects/${project.id}?tab=files`),
        initialAdminFiles,
        initialSupplierFiles,
      ]);
      await Promise.all([
        adminPage.getByRole('tab', { name: '文件', exact: true, selected: true }).waitFor(),
        supplierPage.getByRole('tab', { name: '文件', exact: true, selected: true }).waitFor(),
      ]);
      await supplierPage.getByRole('button', { name: '上传文件', exact: true }).click();
      const dialog = supplierPage.getByRole('dialog', { name: '上传文件' });
      await dialog.getByLabel('选择上传文件').setInputFiles({
        name: uploadName,
        mimeType: 'application/pdf',
        buffer: fs.readFileSync(OUT + '/valid-preview.pdf'),
      });
      const adminReload = waitResponse(adminPage, `/projects/${project.id}/files`);
      const merged = supplierPage.waitForResponse(response =>
        /^\/api\/v1\/uploads\/[^/]+\/merge$/.test(pathOf(response))
        && response.request().method() === 'POST' && response.status() === 200);
      await dialog.getByRole('button', { name: '上传所选文件', exact: true }).click();
      await merged;
      await dialog.waitFor({ state: 'hidden' });
      await adminReload;
      await adminPage.getByRole('row').filter({ hasText: uploadName }).waitFor();
      const stored = await json(adminContext, adminToken, 'GET',
        `/projects/${project.id}/files?keyword=${encodeURIComponent(uploadName)}&page=1&pageSize=10`);
      assert.equal(stored.total, 1);
    });

    await record('供应商提交后管理员工作台待确认区自动出现项目', async () => {
      await adminPage.goto(s.base + '/');
      await adminPage.getByRole('heading', { name: new RegExp('^工作台 · ') }).waitFor();
      await adminPage.getByRole('heading', { name: '公司内部待验收子项目', exact: true }).waitFor();
      const beforePending = await json(adminContext, adminToken, 'GET',
        '/dashboard/pending-projects?page=1&pageSize=100');
      assert(!beforePending.list.some(item => item.id === project.id));
      const pendingReload = waitPendingProject(adminPage, project.id);
      const submit = action(supplierPage, `/projects/${project.id}/submit`, 'POST', async () => {
        await supplierPage.getByRole('button', { name: '提交公司验收', exact: true }).click();
        await supplierPage.locator('.arco-popconfirm:visible').getByRole('button', { name: '确定', exact: true }).click();
      });
      const submitted = await submit;
      assert(Number.isInteger(submitted.latestSubmissionId) && submitted.latestSubmissionId > 0);
      const pending = await pendingReload;
      assert(pending.list.some(item => item.id === project.id));
      const projectLabel = project.projectGroupName
        ? `${project.projectGroupName} / ${projectName}` : projectName;
      await adminPage.getByRole('link', { name: projectLabel, exact: true }).waitFor();
      await supplierPage.getByText('待内部验收', { exact: true }).first().waitFor();
      await supplierPage.getByText('验收方：公司', { exact: true }).waitFor();
      assert.equal(await supplierPage.getByRole('button', { name: '验收通过', exact: true }).count(), 0);
      assert.equal(await supplierPage.getByRole('button', { name: '验收驳回', exact: true }).count(), 0);
    });

    let markedNotification;
    await record('通知查看状态独立于留言已读和流程待确认并在刷新后持久', async () => {
      // The admin page viewed the project earlier to validate deep-linking, so
      // those messages are correctly marked read by the visibility observer.
      // Create a fresh message after returning to the dashboard to keep this
      // assertion focused on notification-read versus message-read state.
      const independentText = '通知独立消息-' + marker;
      await supplierPage.goto(s.base + `/projects/${project.id}?tab=messages`);
      await supplierInput().waitFor();
      const independent = await action(supplierPage, `/projects/${project.id}/messages`, 'POST',
        async () => { await supplierInput().fill(independentText); await supplierInput().press('Control+Enter'); });
      const unread = await listNotifications(adminContext, adminToken, true);
      const unreadMessages = await listDashboardMessages(adminContext, adminToken, true);
      const unreadMessageIds = new Set(unreadMessages.map(item => item.id));
      markedNotification = unread.list.find(item => item.type === 'MESSAGE'
        && item.targetId === independent.id && unreadMessageIds.has(item.targetId));
      assert(markedNotification,
        'the notification list needs an item whose notification and message are both unread');
      const drawer = await openNotifications(adminPage);
      await chooseNotificationTab(drawer, '未查看');
      const row = notificationRow(drawer, markedNotification.id);
      await row.waitFor();
      const marked = waitResponse(adminPage, '/collaboration/reads', 'POST');
      await row.getByRole('button', { name: `标记为已查看：${markedNotification.title}`, exact: true }).click();
      await marked;
      await adminPage.waitForFunction(id =>
        document.querySelector(`[data-notification-id="${id}"]`)?.getAttribute('data-read') === 'true',
      markedNotification.id);
      assert.equal(await row.getAttribute('data-read'), 'true');
      assert.equal(await row.getByRole('button', { name: /^标记为已查看：/ }).count(), 0);
      const allNotifications = await listNotifications(adminContext, adminToken, false);
      assert.equal(allNotifications.list.find(item => item.id === markedNotification.id).read, true);
      assert((await listDashboardMessages(adminContext, adminToken, true))
        .some(item => item.id === markedNotification.targetId), 'notification view must not mark message content read');
      const pending = await json(adminContext, adminToken, 'GET', '/dashboard/pending-projects?page=1&pageSize=100');
      assert(pending.list.some(item => item.id === project.id), 'notification view must not finish pending confirmation');
      await closeNotifications(adminPage, drawer);
      await adminPage.reload();
      await adminPage.getByRole('heading', { name: new RegExp('^工作台 · ') }).waitFor();
      const reopened = await openNotifications(adminPage);
      await chooseNotificationTab(reopened, '全部');
      const persisted = notificationRow(reopened, markedNotification.id);
      await persisted.waitFor();
      assert.equal(await persisted.getAttribute('data-read'), 'true');
    });

    await record('查看已标记通知才实际读取留言且不改变待确认任务', async () => {
      const drawer = adminPage.locator('[data-collaboration-drawer="true"]');
      const row = notificationRow(drawer, markedNotification.id);
      const messageRead = waitResponse(adminPage, '/messages/read', 'POST');
      await row.getByRole('button', { name: `查看通知：${markedNotification.title}`, exact: true }).click();
      await adminPage.waitForURL(url => url.pathname === `/projects/${project.id}`
        && url.searchParams.get('target') === String(markedNotification.targetId));
      await messageRead;
      assert(!(await listDashboardMessages(adminContext, adminToken, true))
        .some(item => item.id === markedNotification.targetId), 'visible target is now actually read');
      const pending = await json(adminContext, adminToken, 'GET', '/dashboard/pending-projects?page=1&pageSize=100');
      assert(pending.list.some(item => item.id === project.id));
      await adminInput().waitFor();
    });

    await record('管理员确认后供应商项目状态无需刷新自动变为已完成', async () => {
      const supplierReload = waitResponse(supplierPage, `/projects/${project.id}`);
      await adminPage.getByRole('button', { name: '验收通过', exact: true }).click();
      await action(adminPage, `/projects/${project.id}/confirm`, 'POST',
        () => adminPage.locator('.arco-popconfirm:visible').getByRole('button', { name: '确定', exact: true }).click());
      await supplierReload;
      await supplierPage.getByText('已完成', { exact: true }).first().waitFor();
      assert.equal((await json(adminContext, adminToken, 'GET', `/projects/${project.id}`)).status, 'COMPLETED');
    });

    await record('供应商B只能看到己方通知与留言且不能读取本次甲方项目', async () => {
      supplierBContext = await browser.newContext({ viewport: { width: 1440, height: 1000 } });
      supplierBPage = await supplierBContext.newPage();
      track(supplierBPage, 'collaboration-supplier-b');
      supplierBPage.setDefaultTimeout(12000);
      const supplierBAuth = await login(supplierBPage, f.users.b.username, f.users.b.changedPassword);
      await supplierBPage.waitForURL(s.base + '/');
      const visibleProjects = await json(supplierBContext, supplierBAuth.accessToken, 'GET', '/project-groups?page=1&pageSize=100');
      const visibleIds = new Set(visibleProjects.list.map(item => item.id));
      assert(!visibleIds.has(project.projectGroupId));
      const ownNotifications = await listNotifications(supplierBContext, supplierBAuth.accessToken, false);
      assert(ownNotifications.list.every(item => item.projectGroupName !== project.projectGroupName));
      assert((await listDashboardMessages(supplierBContext, supplierBAuth.accessToken, false))
        .every(item => item.projectGroupName !== project.projectGroupName));
      await json(supplierBContext, supplierBAuth.accessToken, 'GET', `/projects/${project.id}`, undefined, 403);
      const drawer = await openNotifications(supplierBPage);
      await chooseNotificationTab(drawer, '全部');
      if (ownNotifications.total === 0) await drawer.locator('.arco-empty').waitFor();
      else await drawer.locator('[data-notification-id]').first().waitFor();
      assert.equal(await drawer.getByText(projectName, { exact: true }).count(), 0);
      await closeNotifications(supplierBPage, drawer);
    });

    await record('协作摘要503显示降级状态并由显式重试恢复', async () => {
      const summaryPath = apiPath('/collaboration/summary');
      adminPage.expectedServerErrors = new Set([summaryPath]);
      // Keep the injected failure active until the UI has rendered the
      // degraded state.  The realtime poller may immediately retry after a
      // single 503, which otherwise makes this assertion race the recovery.
      const failSummary = route => route.fulfill({
        status: 503,
        contentType: 'application/json',
        body: '{"code":50301,"message":"协作摘要测试暂时不可用"}',
      });
      await adminPage.route('**' + summaryPath, failSummary);
      const failed = waitResponse(adminPage, '/collaboration/summary', 'GET', 503);
      // Install the failure before navigation so a long-running realtime
      // session cannot consume the initial summary before the fault injection.
      await adminPage.goto(s.base + '/');
      await adminPage.getByRole('heading', { name: new RegExp('^工作台 · ') }).waitFor();
      await failed;
      await adminPage.getByRole('status').filter({ hasText: '更新暂时中断' }).waitFor();
      await adminPage.unroute('**' + summaryPath, failSummary);
      const restored = waitResponse(adminPage, '/collaboration/summary');
      const recoveredMessages = waitResponse(adminPage, '/dashboard/messages');
      const retrySummary = adminPage.getByRole('button', { name: '重新获取协作通知', exact: true });
      await retrySummary.waitFor({ state: 'visible' });
      // SignalR/dashboard revisions may replace the button between the wait
      // and click. Dispatch on the currently attached semantic control to
      // avoid a false detached-element failure in the browser contract.
      await retrySummary.evaluate((button) => button.click());
      await restored;
      await recoveredMessages;
      await adminPage.getByText('更新暂时中断', { exact: true }).waitFor({ state: 'hidden' });
      const onlineRefresh = waitResponse(adminPage, '/collaboration/summary');
      await adminPage.evaluate(() => window.dispatchEvent(new Event('online')));
      await onlineRefresh;
      adminPage.expectedServerErrors.clear();
    });

    await record('390x600通知抽屉适配视口并支持键盘关闭和焦点返回', async () => {
      mobileContext = await browser.newContext({ viewport: { width: 390, height: 600 } });
      mobilePage = await mobileContext.newPage();
      track(mobilePage, 'collaboration-mobile');
      mobilePage.setDefaultTimeout(12000);
      await login(mobilePage, 'admin', s.adminPassword);
      await mobilePage.waitForURL(s.base + '/');
      const bell = mobilePage.getByRole('button', { name: /^协作动态通知/ });
      await bell.focus();
      const drawer = await openNotifications(mobilePage);
      const shell = mobilePage.locator('.collaboration-drawer:visible');
      await mobilePage.waitForFunction(() => {
        const element = document.querySelector('.collaboration-drawer');
        if (!element) return false;
        const bounds = element.getBoundingClientRect();
        return bounds.width > 0 && bounds.x >= -1 && bounds.right <= innerWidth + 1;
      });
      const box = await shell.boundingBox();
      assert(box && box.x >= -1 && box.x + box.width <= 391 && box.y >= -1 && box.y + box.height <= 601);
      await drawer.locator('[data-notification-id]').first().waitFor();
      await mobilePage.screenshot({ path: OUT + '/collaboration-mobile.png', animations: 'disabled' });
      await mobilePage.keyboard.press('Tab');
      assert(await shell.evaluate(element => element.contains(document.activeElement)), 'focus remains inside drawer');
      await mobilePage.keyboard.press('Escape');
      await drawer.waitFor({ state: 'hidden' });
      await bell.waitFor();
      await mobilePage.waitForFunction(() => document.activeElement?.classList.contains('collaboration-bell'));
      await mobilePage.screenshot({ path: OUT + '/collaboration-mobile-dashboard.png', animations: 'disabled', fullPage: true });
      const overflow = await mobilePage.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
      assert(overflow <= 1, '390px page has no horizontal overflow');
    });

    assert.equal(JSON.stringify(f.uiProjects), originalUiProjects, 'collaboration step must not mutate f.uiProjects');
    await adminPage.screenshot({ path: OUT + '/collaboration.png', fullPage: true });
  } catch (error) {
    if (adminPage) {
      await adminPage.screenshot({ path: OUT + '/collaboration-failure.png', fullPage: true }).catch(() => {});
      console.log((await adminPage.locator('body').innerText()).slice(-5000));
    }
    console.error(error.stack);
    process.exitCode = 1;
  } finally {
    for (const context of [mobileContext, supplierBContext, supplierContext, adminContext]) {
      if (context) await context.close().catch(() => {});
    }
    if (browser) await browser.close();
  }
})();
