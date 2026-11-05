const { chromium } = require('playwright');
const {
  assert, OUT, s, f, save, record, login, api, navigate, action, track, projectMetadata,
} = require(process.env.YF_BROWSER_SUPPORT_DIR + '/ui-lib.cjs');

const pathOf = response => new URL(response.url()).pathname;
const capture = async (context, mode, failures = 1) => {
  const result = await fetch(s.smtpCapture.control, {
    method: 'POST', headers: { 'Content-Type': 'application/json', 'X-Test-Key': s.smtpCapture.token },
    body: JSON.stringify({ mode, failures }),
  });
  assert.equal(result.status, 200, 'local SMTP capture control');
};
const captured = async () => {
  const result = await fetch(s.smtpCapture.control, { headers: { 'X-Test-Key': s.smtpCapture.token } });
  assert.equal(result.status, 200, 'local SMTP capture evidence');
  return result.json();
};

(async () => {
  let browser;
  let page;
  let context;
  try {
    assert.ok(s.smtpCapture?.control && s.smtpCapture?.token, 'owned loopback SMTP capture is required');
    browser = await require(process.env.YF_BROWSER_SUPPORT_DIR + '/shared-browser.cjs')(chromium);
    context = await browser.newContext({ viewport: { width: 1440, height: 1000 } });
    page = await context.newPage();
    track(page, 'mail-notifications');
    const auth = await login(page, 'admin', s.adminPassword);
    await page.waitForURL(s.base + '/');
    const read = async (method, url, body, expected = 200) =>
      (await api(context, method, url, body, auth.accessToken, expected)).json();
    const readSettings = () => read('GET', '/admin/system/mail-settings');
    const readMail = scope => read('GET', '/admin/system/mail-status' + (scope ? '?scope=' + scope : ''));
    const readConfigs = async () => Object.fromEntries((await read('GET', '/admin/system/configs'))
      .map(item => [item.key, item.value]));
    const testRecipient = 'browser-mail-test@example.invalid';
    const fields = {
      host: () => page.getByLabel('SMTP 服务器', { exact: true }),
      port: () => page.getByLabel('SMTP 端口', { exact: true }),
      username: () => page.getByLabel('SMTP 登录账号', { exact: true }),
      from: () => page.getByLabel('发件邮箱', { exact: true }),
      password: () => page.getByLabel('邮箱密码或授权码', { exact: true }),
      security: () => page.getByLabel('SMTP 连接加密', { exact: true }),
    };
    const selectSecurity = async () => {
      await fields.security().click();
      const option = page.getByRole('option', { name: 'STARTTLS（通常为 587）', exact: true });
      await option.waitFor();
      await option.evaluate(element => element.click());
    };
    const smtpSave = () => page.getByRole('button', { name: '保存邮箱设置', exact: true });
    const sendTest = async expectedText => {
      const submit = page.getByRole('button', { name: '发送测试邮件', exact: true });
      await submit.click();
      const result = page.getByRole('status');
      await result.waitFor();
      await page.getByText(expectedText, { exact: false }).last().waitFor();
      return result.innerText();
    };
    const originalSettings = await readSettings();
    const originalRobot = (await readMail()).notificationPolicy;
    const originalOem = await read('GET', '/oem/notify-policies');
    const originalConfigs = await readConfigs();

    await navigate(page, '/system/config');
    await page.getByRole('tab', { name: 'SMTP 配置', exact: true }).click();
    await fields.host().waitFor();
    await record('真实 SMTP 配置仅能指向本轮本机捕获器，草稿未保存前测试按钮禁用', async () => {
      await fields.host().fill('localhost');
      await fields.port().fill(String(s.smtpCapture.port));
      await fields.username().fill(s.smtpCapture.username);
      await fields.from().fill(s.smtpCapture.from);
      await fields.password().fill(s.smtpCapture.password);
      await selectSecurity();
      assert.equal(await page.getByRole('button', { name: '发送测试邮件', exact: true }).isDisabled(), true,
        'unsaved SMTP settings cannot be used for a test send');
      await action(page, '/admin/system/mail-settings', 'PUT', () => smtpSave().click());
      assert.deepEqual(await readSettings(), {
        host: 'localhost', port: s.smtpCapture.port, username: s.smtpCapture.username,
        from: s.smtpCapture.from, security: 'StartTls', hasPassword: true, configured: true,
        passwordNeedsUpdate: false,
      });
    });

    await record('浏览器测试邮件经真实 TLS SMTP 投递并出现在发送日志，不进入业务队列', async () => {
      const queueBefore = (await readMail()).queue;
      await capture(context, 'accept');
      await page.getByLabel('测试收件邮箱', { exact: true }).fill(testRecipient);
      const visible = await sendTest('SMTP 服务器已接受测试邮件');
      assert.match(visible, /SMTP 服务器已接受测试邮件/);
      await page.getByText('测试邮件：服务器已接受', { exact: true }).waitFor();
      const afterCapture = await captured();
      assert.equal(afterCapture.messages.length, 1);
      assert(afterCapture.messages[0].recipients.some(value => value.includes(testRecipient)));
      assert(afterCapture.messages[0].body.includes('Content-Type:') && afterCapture.messages[0].body.length > 100,
        'capture contains the encoded MIME message body');
      const afterMail = await readMail();
      assert.deepEqual(afterMail.queue, queueBefore, 'manual test mail must not enter or change the business outbox');
      assert(afterMail.recent.some(item => item.action === 'EMAIL_TEST_STARTED'));
      assert(afterMail.recent.some(item => item.action === 'EMAIL_TEST_SENT'));
      assert(afterMail.recent.every(item => !JSON.stringify(item.detail).includes(s.smtpCapture.password)),
        'mail audit must not reveal SMTP credentials');
    });

    await record('SMTP 认证失败、收件拒绝与结果不确定均准确记录且不会自动重发', async () => {
      const before = await readMail();
      await capture(context, 'auth_failure');
      const authFailure = await sendTest('SMTP 认证失败');
      assert.match(authFailure, /SMTP 认证失败/);
      await page.getByText('测试邮件：发送失败', { exact: true }).waitFor();
      await capture(context, 'reject_recipient');
      const rejection = await sendTest('SMTP 服务器拒绝发送');
      assert.match(rejection, /SMTP 服务器拒绝发送/);
      await capture(context, 'accept_drop');
      const uncertain = await sendTest('结果不确定');
      assert.match(uncertain, /结果不确定/);
      await page.getByText('测试邮件：结果不确定', { exact: true }).waitFor();
      const after = await readMail();
      assert.deepEqual(after.queue, before.queue, 'manual test failures and unknown outcome never enqueue retry mail');
      const testLogs = after.recent.filter(item => item.action.startsWith('EMAIL_TEST_'));
      for (const actionName of ['EMAIL_TEST_FAILED', 'EMAIL_TEST_UNKNOWN'])
        assert(testLogs.some(item => item.action === actionName), 'audit includes ' + actionName);
      const evidence = await captured();
      assert.equal(evidence.messages.length, 2, 'only accepted and uncertain DATA bodies reach the local sink');
      assert.equal(evidence.attempts, 2, 'auth and RCPT rejection happen before DATA');
      assert(evidence.messages.every(message => message.recipients.some(value => value.includes(testRecipient))));
      assert(testLogs.every(item => JSON.stringify(item.detail).includes('.invalid') ||
        JSON.stringify(item.detail).includes('**')), 'test logs contain only the synthetic test recipient');
      assert.equal(after.recent.some(item => JSON.stringify(item.detail).includes(s.smtpCapture.password)), false,
        'mail logs never expose the SMTP password');
    });

    await record('提醒规则 Tab 分别保存 Robot 与 ODM/OEMA 开关并保持两边设置隔离', async () => {
      await page.getByRole('tab', { name: '邮件提醒规则', exact: true }).click();
      const switchTo = async (label, value) => {
        const toggle = page.getByRole('switch', { name: label, exact: true });
        await toggle.waitFor();
        if ((await toggle.getAttribute('aria-checked')) !== String(value)) await toggle.click();
      };
      const robotProjectStart = !originalRobot.events.projectStarted;
      await page.locator('.system-reminder-card .arco-radio-button').filter({ hasText: 'Robot 仿真协作' })
        .evaluate(element => element.click());
      await switchTo('开始子项目邮件提醒', robotProjectStart);
      await page.getByRole('button', { name: '保存邮件提醒', exact: true }).click();
      await page.getByText('邮件提醒设置已保存', { exact: true }).waitFor();
      const robotSaved = await readConfigs();
      assert.equal(robotSaved['notify.event.project_started'], String(robotProjectStart));
      assert.equal(robotSaved['oem.notify.enabled'], originalConfigs['oem.notify.enabled']);

      const oemStart = originalOem.find(item => item.key === 'oem.notify.event.transfer_released');
      assert(oemStart, 'OEM transfer release rule exists');
      await page.locator('.system-reminder-card .arco-radio-button').filter({ hasText: 'ODM/OEMA' })
        .evaluate(element => element.click());
      await page.getByRole('switch', { name: '文件送达提醒', exact: true }).waitFor();
      const oemToggle = page.getByRole('switch', { name: '文件送达提醒', exact: true });
      const target = oemStart.value !== 'true';
      if ((await oemToggle.getAttribute('aria-checked')) !== String(target)) await oemToggle.click();
      await page.getByRole('button', { name: '保存 ODM/OEMA 邮件提醒', exact: true }).click();
      await page.getByText('ODM/OEMA 邮件提醒规则已保存', { exact: true }).waitFor();
      const oemSaved = await read('GET', '/oem/notify-policies');
      assert.equal(oemSaved.find(item => item.key === oemStart.key).value, String(target));
      assert.equal((await readConfigs())['notify.event.project_started'], String(robotProjectStart),
        'OEM rule save cannot rewrite Robot policy');

      await page.locator('.system-reminder-card .arco-radio-button').filter({ hasText: 'Robot 仿真协作' })
        .evaluate(element => element.click());
      await switchTo('开始子项目邮件提醒', originalRobot.events.projectStarted);
      await page.getByRole('button', { name: '保存邮件提醒', exact: true }).click();
      await page.getByText('邮件提醒设置已保存', { exact: true }).waitFor();
      await page.locator('.system-reminder-card .arco-radio-button').filter({ hasText: 'ODM/OEMA' })
        .evaluate(element => element.click());
      const restoreOem = page.getByRole('switch', { name: '文件送达提醒', exact: true });
      if ((await restoreOem.getAttribute('aria-checked')) !== String(oemStart.value === 'true')) await restoreOem.click();
      await page.getByRole('button', { name: '保存 ODM/OEMA 邮件提醒', exact: true }).click();
      await page.getByText('ODM/OEMA 邮件提醒规则已保存', { exact: true }).waitFor();
      assert.deepEqual(await read('GET', '/oem/notify-policies'), originalOem);
    });

    await record('Robot 与 ODM/OEMA 独立查询邮件队列和日志，系统审计能筛到测试邮件', async () => {
      await page.getByRole('tab', { name: 'SMTP 配置', exact: true }).click();
      await page.getByRole('combobox', { name: '邮件日志业务', exact: true }).waitFor();
      const robot = await readMail('collaboration');
      const oem = await readMail('oem');
      const both = await readMail('all');
      assert(robot.recent.every(item => !item.action.startsWith('OEM_')));
      assert(oem.recent.some(item => item.action.startsWith('EMAIL_TEST_')));
      assert(both.recent.some(item => item.action.startsWith('EMAIL_TEST_')));
      assert(oem.queue && both.queue, 'each business has queue counts');
      const scope = page.getByRole('combobox', { name: '邮件日志业务', exact: true });
      await scope.click();
      await page.getByRole('option', { name: 'ODM/OEMA', exact: true }).click();
      await page.getByRole('button', { name: 'ODM/OEMA 日志', exact: true }).waitFor();
      await scope.click();
      await page.getByRole('option', { name: 'Robot 仿真协作', exact: true }).click();
      await page.getByRole('button', { name: '全部日志', exact: true }).waitFor();
      await page.getByRole('button', { name: '全部日志', exact: true }).click();
      await page.getByPlaceholder('搜索姓名、对象或详情').fill('EMAIL_TEST_SENT');
      const result = await Promise.all([
        page.waitForResponse(response => pathOf(response) === '/api/v1/admin/audit-logs'
          && new URL(response.url()).searchParams.get('keyword') === 'EMAIL_TEST_SENT'),
        page.getByRole('button', { name: '查询', exact: true }).click(),
      ]).then(([response]) => response.json());
      assert(result.list.some(item => item.action === 'EMAIL_TEST_SENT'));
    });

    await record('真实业务邮件只进入隔离 outbox，多个 Robot 联络人均收到而共用账号地址不收信', async () => {
      const previousConfigs = await readConfigs();
      const updates = [
        ['notify.enabled', 'true'], ['notify.internal.enabled', 'true'], ['notify.supplier.enabled', 'true'],
        ['notify.event.project_started', 'true'],
      ].filter(([key, value]) => previousConfigs[key] !== value);
      if (updates.length) await read('PUT', '/admin/system/configs', { items: updates.map(([key, value]) => ({ key, value })) });

      const supplierId = f.suppliers.a.id;
      const contacts = await read('GET', `/admin/suppliers/${supplierId}/contacts`);
      const generated = [1, 2].map(index => ({ name: `SMTP 合成联络人 ${index}-${Date.now()}`,
        email: `robot-smtp-${index}-${Date.now()}-${Math.random().toString(16).slice(2)}@example.invalid` }));
      for (const contact of generated) {
        const created = await read('POST', `/admin/suppliers/${supplierId}/contacts`, { ...contact, sortNo: 900 + generated.indexOf(contact) });
        await read('PUT', `/admin/supplier-contacts/${created.id}/status`, { status: 'ACTIVE' });
      }
      const defaults = await projectMetadata(context, auth.accessToken, supplierId);
      const mailManagerContext = await browser.newContext({ viewport: { width: 1440, height: 1000 } });
      try {
        const mailManagerPage = await mailManagerContext.newPage();
        track(mailManagerPage, 'mail-notifications-manager');
        const mailManager = await login(mailManagerPage, f.users.manager.username, f.users.manager.changedPassword);
        await mailManagerPage.waitForURL(s.base + '/');
        const group = await (await api(mailManagerContext, 'POST', '/project-groups', {
          name: 'SMTP 隔离验收-' + Date.now(), description: '独立临时数据库', supplierId,
          ...defaults, machineModel: 'SMTP 隔离验收机型',
          workOrderNos: ['SMTP-' + Date.now()], subprojectNames: ['SMTP 邮件队列验收'],
        }, mailManager.accessToken)).json();
        const detail = await read('GET', '/project-groups/' + group.id);
        const project = detail.projects[0];
        await api(mailManagerContext, 'PUT', `/projects/${project.id}/status`,
          { status: 'IN_PROGRESS', expectedStatus: 'DRAFT' }, mailManager.accessToken);
      const queued = await fetch(s.base + '/__test/mail/outbox', {
        headers: { 'X-Test-Key': s.smtpCapture.token },
      });
      assert.equal(queued.status, 200);
      const pendingRows = await queued.json();
      assert(pendingRows.length > 0, 'business events create durable queue rows while workers are off');
      assert(pendingRows.every(item => item.recipientEmail.toLowerCase().endsWith('.invalid')),
        'every queued recipient belongs to an isolated synthetic domain');
      const projectRows = pendingRows.filter(item => item.eventType === 'PROJECT_STARTED');
      assert(projectRows.length >= generated.length, 'start mail fans out to each active supplier contact');
      for (const contact of generated)
        assert(projectRows.some(item => item.recipientEmail === contact.email), 'active contact receives mail: ' + contact.email);
      const oemRows = pendingRows.filter(item => item.eventType === 'OEM_TRANSFER_RELEASED');
      assert(oemRows.length >= f.oem.contacts.length, 'OEM release mail fans out to all active company contacts');
      for (const contact of f.oem.contacts)
        assert(oemRows.some(item => item.recipientEmail === contact.email), 'active OEM contact receives mail: ' + contact.email);

      await capture(context, 'transient', 1);
      const firstDrain = await fetch(s.base + '/__test/mail/drain', {
        method: 'POST', headers: { 'X-Test-Key': s.smtpCapture.token },
      });
      assert.equal(firstDrain.status, 200);
      const first = await firstDrain.json();
      assert(first.taken > 0, 'the test-only drain invokes the real durable MailService');
      const afterFailure = await (await fetch(s.base + '/__test/mail/outbox', {
        headers: { 'X-Test-Key': s.smtpCapture.token },
      })).json();
      const retryCandidate = afterFailure.find(item => item.status === 'PENDING' && item.retryCount > 0);
      assert(retryCandidate,
        'SMTP 451 leaves a durable pending row with a retry count');

      const readOutbox = async () => (await fetch(s.base + '/__test/mail/outbox', {
        headers: { 'X-Test-Key': s.smtpCapture.token },
      })).json();
      let finalRows = await readOutbox();
      for (let cycle = 0; finalRows.some(item => item.status === 'PENDING') && cycle < 10; cycle += 1) {
        await capture(context, 'accept');
        const retryDrain = await fetch(s.base + '/__test/mail/drain?advance=true', {
          method: 'POST', headers: { 'X-Test-Key': s.smtpCapture.token },
        });
        assert.equal(retryDrain.status, 200);
        assert((await retryDrain.json()).taken > 0, 'each bounded mail-worker batch takes due queue rows');
        finalRows = await readOutbox();
      }
      assert(finalRows.every(item => ['SENT', 'CANCELLED'].includes(item.status)),
        'every queued row was sent or rechecked and cancelled before the acceptance run completed: '
        + JSON.stringify(finalRows.map(item => ({ id: item.id, eventType: item.eventType, status: item.status,
          retryCount: item.retryCount, nextAttemptAt: item.nextAttemptAt }))));
      assert.equal(finalRows.find(item => item.id === retryCandidate.id)?.status, 'SENT',
        'the known SMTP 451 failure must be retried to successful delivery');
      for (const contact of generated) {
        assert.equal(finalRows.find(item => item.eventType === 'PROJECT_STARTED'
          && item.recipientEmail === contact.email)?.status, 'SENT',
        'the live project-start notification reaches its active contact: ' + contact.email);
      }
      const delivered = await captured();
      const deliveredRecipients = delivered.messages.flatMap(message => message.recipients).join('\n');
      for (const contact of generated) assert(deliveredRecipients.includes(contact.email));
      assert(!deliveredRecipients.includes('auto_supplier_a'), 'shared supplier login is never an SMTP recipient');
      assert(delivered.messages.some(message => message.recipients.length === 1), 'mail is sent per recipient');
      const robotLogs = await readMail('collaboration');
      assert(robotLogs.recent.some(item => item.action === 'EMAIL_SENT'));
      const oemLogs = await readMail('oem');
      assert(oemLogs.recent.some(item => item.action === 'OEM_EMAIL_SENT'));
      const targetId = String(retryCandidate.id);
      const oemRetries = await read('GET', `/oem/audit-logs?action=OEM_EMAIL_RETRY&targetId=${targetId}&page=1&pageSize=20`);
      const robotRetries = await read('GET', `/admin/audit-logs?action=EMAIL_RETRY&targetId=${targetId}&page=1&pageSize=20`);
      assert(oemRetries.list.length + robotRetries.list.length > 0, 'transient retry is visible in the audit log');

      if (updates.length) await read('PUT', '/admin/system/configs', {
        items: updates.map(([key]) => ({ key, value: previousConfigs[key] })),
      });
      f.mailAcceptance = { projectId: project.id, contactCount: generated.length,
        persistedOutboxRows: finalRows.length, capturedMessages: delivered.messages.length };
      save();
      } finally {
        await mailManagerContext.close();
      }
    });

    await record('SMTP 双栏、Robot/OEM 规则在 1440px 与 390px 内部滚动并且不超出页面', async () => {
      await navigate(page, '/system/config');
      for (const viewport of [{ width: 1440, height: 1000 }, { width: 390, height: 600 }]) {
        await page.setViewportSize(viewport);
        await page.getByRole('tab', { name: 'SMTP 配置', exact: true }).click();
        await page.getByLabel('SMTP 服务器', { exact: true }).waitFor();
        const smtp = await page.evaluate(() => {
          const tabs = document.querySelector('.system-config-tabs');
          const workspace = document.querySelector('.system-mail-workspace');
          const editor = document.querySelector('.system-mail-editor');
          const log = document.querySelector('.system-mail-log');
          return { width: innerWidth, height: innerHeight, page: document.documentElement.scrollHeight,
            tabsBottom: tabs.getBoundingClientRect().bottom, editorBottom: editor.getBoundingClientRect().bottom,
            logLeft: log.getBoundingClientRect().left, editorLeft: editor.getBoundingClientRect().left,
            columns: getComputedStyle(workspace).gridTemplateColumns, rows: getComputedStyle(workspace).gridTemplateRows };
        });
        assert(smtp.page <= viewport.height + 1 && smtp.tabsBottom <= viewport.height + 1,
          'SMTP tab must stay within the viewport: ' + JSON.stringify(smtp));
        if (viewport.width > 800) assert(smtp.columns.split(' ').length === 2 && smtp.editorLeft < smtp.logLeft,
          'desktop SMTP settings and logs are side by side');
        else assert(smtp.rows.split(' ').length === 2, 'mobile SMTP settings and logs stack within the pane');

        await page.getByRole('tab', { name: '邮件提醒规则', exact: true }).click();
        await page.getByRole('switch', { name: '邮件通知总开关', exact: true }).waitFor();
        const rules = await page.evaluate(() => {
          const tabs = document.querySelector('.system-config-tabs');
          const card = document.querySelector('.system-reminder-card');
          return { page: document.documentElement.scrollHeight, tabsBottom: tabs.getBoundingClientRect().bottom,
            cardBottom: card.getBoundingClientRect().bottom };
        });
        assert(rules.page <= viewport.height + 1 && rules.tabsBottom <= viewport.height + 1
          && rules.cardBottom <= viewport.height + 1, 'reminder tab fits its viewport: ' + JSON.stringify(rules));
      }
      await page.setViewportSize({ width: 1440, height: 1000 });
      await page.screenshot({ path: OUT + '/mail-notifications-final.png', animations: 'disabled' });
    });
  } catch (error) {
    if (page) {
      await page.screenshot({ path: OUT + '/mail-notifications-failure.png', animations: 'disabled', fullPage: true }).catch(() => {});
      console.log((await page.locator('body').innerText()).slice(-4000));
    }
    console.error(error.stack);
    process.exitCode = 1;
  } finally {
    if (browser) await browser.close();
  }
})();
