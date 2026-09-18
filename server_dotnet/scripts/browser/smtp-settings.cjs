const { chromium } = require('playwright');
const {
  assert, OUT, s, record, login, api, navigate, action, track,
} = require(process.env.YF_BROWSER_SUPPORT_DIR + '/ui-lib.cjs');

const MAIL_SETTINGS_PATH = '/api/v1/admin/system/mail-settings';
const MAIL_STATUS_PATH = '/api/v1/admin/system/mail-status';
const CONFIGS_PATH = '/api/v1/admin/system/configs';
const FIRST = {
  host: 'smtp-one.example.invalid',
  port: 2525,
  username: 'browser-one@example.invalid',
  from: 'notice-one@example.invalid',
  security: 'StartTls',
};
const SECOND_HOST = 'smtp-two.example.invalid';
const SECOND_USERNAME = 'browser-two@example.invalid';
const RETAINED_FROM = 'notice-retained@example.invalid';
const FINAL_FROM = 'notice-final@example.invalid';
const AUTH_CODE_A = 'synthetic-smtp-auth-A9!';
const AUTH_CODE_B = 'synthetic-smtp-auth-B9!';
const AUTH_CODE_C = 'synthetic-smtp-auth-C9!';

const pathOf = value => new URL(value.url()).pathname;
const isMailSettingsWrite = value => pathOf(value) === MAIL_SETTINGS_PATH
  && value.request().method() === 'PUT';

(async () => {
  let browser;
  let page;
  try {
    browser = await chromium.launch({ channel: 'chrome', headless: true });
    const context = await browser.newContext({ viewport: { width: 1440, height: 1000 } });
    page = await context.newPage();
    track(page, 'smtp-settings');

    const auth = await login(page, 'admin', s.adminPassword);
    await page.waitForURL(s.base + '/');

    const readJson = async path => (await api(
      context, 'GET', path.replace('/api/v1', ''), undefined, auth.accessToken,
    )).json();
    const readSettings = () => readJson(MAIL_SETTINGS_PATH);
    const readStatus = () => readJson(MAIL_STATUS_PATH);
    const readConfigs = () => readJson(CONFIGS_PATH);
    const configMap = async () => Object.fromEntries(
      (await readConfigs()).map(item => [item.key, item.value]),
    );

    const fields = {
      host: () => page.getByLabel('SMTP 服务器', { exact: true }),
      port: () => page.getByLabel('SMTP 端口', { exact: true }),
      username: () => page.getByLabel('SMTP 登录账号', { exact: true }),
      from: () => page.getByLabel('发件邮箱', { exact: true }),
      password: () => page.getByLabel('邮箱密码或授权码', { exact: true }),
      security: () => page.getByLabel('SMTP 连接加密', { exact: true }),
    };
    const smtpSave = () => page.getByRole('button', { name: '保存邮箱设置', exact: true });
    // The SMTP and notification sections intentionally use the same visible
    // wording. Keep this locator tied to the first form action so a second
    // section cannot make the browser contract ambiguous.
    const smtpCancel = () => page.getByRole('button', { name: '取消修改', exact: true }).first();
    const configReset = () => page.getByRole('button', { name: '重置', exact: true });
    const configSave = () => page.getByRole('button', { name: '保存', exact: true });
    const selectTab = async (name) => {
      await page.getByRole('tab', { name, exact: true }).click();
    };

    const selectSecurity = async (value) => {
      const labels = {
        Auto: '自动（465 使用 TLS，其他端口使用 STARTTLS）',
        SslOnConnect: 'TLS / SSL（通常为 465）',
        StartTls: 'STARTTLS（通常为 587）',
      };
      await fields.security().click();
      const option = page.getByRole('option', { name: labels[value], exact: true });
      await option.waitFor();
      // Arco repositions the popup while the page is being restored after a
      // form reset; Playwright's viewport check can race that animation even
      // though the semantic option is already attached.  Activate the
      // resolved option directly, preserving the same user-facing event.
      await option.evaluate(element => element.click());
      assert((await fields.security().innerText()).includes(labels[value]), 'selected SMTP security is visible');
    };

    const fillSmtp = async (value) => {
      if (value.host !== undefined) await fields.host().fill(value.host);
      if (value.port !== undefined) await fields.port().fill(String(value.port));
      if (value.username !== undefined) await fields.username().fill(value.username);
      if (value.from !== undefined) await fields.from().fill(value.from);
      if (value.password !== undefined) await fields.password().fill(value.password);
      if (value.security !== undefined) await selectSecurity(value.security);
    };

    const assertSettings = (actual, expected) => {
      for (const [key, value] of Object.entries(expected)) assert.equal(actual[key], value, 'mail-settings.' + key);
      assert.equal(Object.hasOwn(actual, 'password'), false, 'mail-settings must not expose password');
      assert.equal(Object.hasOwn(actual, 'protectedPassword'), false, 'mail-settings must not expose encrypted password');
    };

    const expectNoUiWrite = async (trigger, visibleMessage) => {
      let writes = 0;
      const countWrite = request => {
        const url = new URL(request.url());
        if (url.pathname === MAIL_SETTINGS_PATH && request.method() === 'PUT') writes += 1;
      };
      page.on('request', countWrite);
      try {
        await trigger();
        await page.getByText(visibleMessage, { exact: true }).last().waitFor();
        assert.equal(writes, 0, 'client-side rejection must not send mail-settings PUT');
      } finally {
        page.off('request', countWrite);
      }
    };

    const withExpectedServerErrors = async (paths, fn) => {
      const previous = page.expectedServerErrors;
      page.expectedServerErrors = new Set([...(previous || []), ...paths]);
      try {
        return await fn();
      } finally {
        page.expectedServerErrors = previous;
      }
    };

    const originalSettings = await readSettings();
    const originalConfigs = await configMap();
    const originalStatus = await readStatus();
    const originalAllowed = originalConfigs['upload.allowed_exts'];
    const originalAllowedItems = originalAllowed.split(',');
    const configDraft = (originalAllowedItems.includes('smtpdraft')
      ? originalAllowedItems.filter(item => item !== 'smtpdraft')
      : [...originalAllowedItems, 'smtpdraft']).sort().join(',');
    await navigate(page, '/system/config');
    await selectTab('SMTP 配置');
    await fields.host().waitFor();

    await record('SMTP 六字段与三种连接加密均可通过鼠标编辑且取消不写入', async () => {
      await fillSmtp({ ...FIRST, password: AUTH_CODE_A });
      for (const security of ['Auto', 'SslOnConnect', 'StartTls']) await selectSecurity(security);
      assert.equal(await fields.host().inputValue(), FIRST.host);
      assert.equal(await fields.port().inputValue(), String(FIRST.port));
      assert.equal(await fields.username().inputValue(), FIRST.username);
      assert.equal(await fields.from().inputValue(), FIRST.from);
      assert.equal(await fields.password().inputValue(), AUTH_CODE_A);
      assert.equal(await fields.password().getAttribute('type'), 'password');
      await smtpCancel().click();
      assert.deepEqual(await readSettings(), originalSettings);
    });

    await record('普通参数草稿与 SMTP 草稿分别取消重置且互不清除', async () => {
      const allowed = page.getByLabel('允许上传类型', { exact: true });
      await selectTab('系统参数');
      await allowed.fill(configDraft);
      await selectTab('SMTP 配置');
      await fields.host().fill(FIRST.host);
      await smtpCancel().click();
      await selectTab('系统参数');
      assert.equal(await allowed.inputValue(), configDraft, 'canceling SMTP must retain ordinary config draft');
      await selectTab('SMTP 配置');
      await fields.host().fill(FIRST.host);
      await selectTab('系统参数');
      await configReset().click();
      await selectTab('SMTP 配置');
      assert.equal(await fields.host().inputValue(), FIRST.host, 'resetting ordinary configs must retain SMTP draft');
      await selectTab('系统参数');
      assert.equal(await allowed.inputValue(), originalAllowed);
      await selectTab('SMTP 配置');
      await smtpCancel().click();
      assert.deepEqual(await configMap(), originalConfigs);
      assert.deepEqual(await readSettings(), originalSettings);
    });

    await record('普通参数真实保存仅提交一次，保存锁与刷新失败提示不清除 SMTP 草稿', async () => {
      const allowed = page.getByLabel('允许上传类型', { exact: true });
      await selectTab('系统参数');
      await allowed.fill(configDraft);
      await selectTab('SMTP 配置');
      await fields.host().fill(FIRST.host);
      let release = () => {};
      let reached = () => {};
      let writes = 0;
      let failRefresh = true;
      const held = new Promise(resolve => { release = resolve; });
      const entered = new Promise(resolve => { reached = resolve; });
      const handler = async route => {
        if (route.request().method() === 'PUT') {
          writes += 1;
          const response = await route.fetch();
          reached();
          await held;
          await route.fulfill({ response });
          return;
        }
        if (route.request().method() === 'GET' && failRefresh) {
          failRefresh = false;
          await route.fulfill({
            status: 503,
            contentType: 'application/json',
            body: JSON.stringify({ code: 50301, message: '验收模拟参数状态刷新失败' }),
          });
          return;
        }
        await route.continue();
      };
      await page.route('**' + CONFIGS_PATH, handler);
      await withExpectedServerErrors([CONFIGS_PATH], async () => {
        try {
          await selectTab('系统参数');
          const saved = page.waitForResponse(response => pathOf(response) === CONFIGS_PATH
            && response.request().method() === 'PUT');
          const failedRefresh = page.waitForResponse(response => pathOf(response) === CONFIGS_PATH
            && response.request().method() === 'GET' && response.status() === 503);
          await configSave().click();
          await entered;
          assert.equal(writes, 1, 'ordinary config save must issue one PUT');
          assert(await allowed.isDisabled(), 'ordinary config input frozen while saving');
          assert(await configSave().isDisabled(), 'ordinary config save frozen while saving');
          assert(await configReset().isDisabled(), 'ordinary config reset frozen while saving');
          assert.equal(await fields.host().isEnabled(), true, 'SMTP controls use an independent save lock');
          assert.equal(await fields.host().inputValue(), FIRST.host);
          release();
          assert.equal((await saved).status(), 200);
          await failedRefresh;
        } finally {
          release();
          await page.unroute('**' + CONFIGS_PATH, handler);
        }
      });
      await page.getByText('参数已保存，但最新状态刷新失败，请稍后刷新页面', { exact: true }).last().waitFor();
      await selectTab('系统参数');
      assert.equal(await allowed.inputValue(), configDraft);
      assert.equal(await configSave().isDisabled(), true, 'successful write clears the ordinary dirty state');
      await selectTab('SMTP 配置');
      assert.equal(await fields.host().inputValue(), FIRST.host, 'failed refresh must retain SMTP draft');
      assert.equal((await configMap())['upload.allowed_exts'], configDraft);
      assert.deepEqual(await readSettings(), originalSettings);

      await selectTab('系统参数');
      await allowed.fill(originalAllowed);
      await action(page, '/admin/system/configs', 'PUT', () => configSave().click());
      await page.waitForFunction(() => [...document.querySelectorAll('button')]
        .some(button => button.textContent.trim() === '保存' && button.disabled));
      assert.equal(await configSave().isDisabled(), true);
      await selectTab('SMTP 配置');
      assert.equal(await fields.host().inputValue(), FIRST.host, 'successful ordinary refresh must retain SMTP draft');
      assert.deepEqual(await configMap(), originalConfigs);
      await smtpCancel().click();
    });

    await record('SMTP 空必填项在客户端拒绝且保留六字段草稿', async () => {
      await fillSmtp({ ...FIRST, from: '', password: AUTH_CODE_A });
      await expectNoUiWrite(
        () => smtpSave().click(),
        '请填写 SMTP 服务器、登录账号、发件邮箱和邮箱密码或授权码',
      );
      assert.equal(await fields.host().inputValue(), FIRST.host);
      assert.equal(await fields.from().inputValue(), '');
      assert.equal(await fields.password().inputValue(), AUTH_CODE_A);
      assert.deepEqual(await readSettings(), originalSettings);
      await smtpCancel().click();
    });

    await record('SMTP 非法端口由控件夹紧且服务端独立拒绝越界请求', async () => {
      await fillSmtp({ ...FIRST, port: 0, password: AUTH_CODE_A });
      await fields.port().press('Tab');
      assert.equal(await fields.port().inputValue(), '1', 'Arco InputNumber clamps below-min input before UI submit');
      assert.equal(await fields.password().inputValue(), AUTH_CODE_A);
      assert.deepEqual(await readSettings(), originalSettings);
      // Negative API contract check only: the rejected request cannot persist and does not replace core UI saves below.
      const rejected = await api(context, 'PUT', '/admin/system/mail-settings', {
        ...FIRST, port: 0, password: AUTH_CODE_A,
      }, auth.accessToken, 400);
      assert.equal((await rejected.json()).message, 'SMTP 端口需为 1–65535');
      assert.deepEqual(await readSettings(), originalSettings);
      await smtpCancel().click();
    });

    await record('SMTP 真实 UI 保存时控件冻结且状态刷新失败仍明确成功', async () => {
      const allowed = page.getByLabel('允许上传类型', { exact: true });
      await selectTab('系统参数');
      await allowed.fill(configDraft);
      await selectTab('SMTP 配置');
      await fillSmtp({ ...FIRST, password: AUTH_CODE_A });
      let release = () => {};
      let reached = () => {};
      let writes = 0;
      const held = new Promise(resolve => { release = resolve; });
      const entered = new Promise(resolve => { reached = resolve; });
      const writeHandler = async route => {
        if (route.request().method() !== 'PUT') return route.continue();
        writes += 1;
        const response = await route.fetch();
        reached();
        await held;
        await route.fulfill({ response });
      };
      await page.route('**' + MAIL_SETTINGS_PATH, writeHandler);
      await page.route('**' + MAIL_STATUS_PATH, route => route.fulfill({
        status: 503,
        contentType: 'application/json',
        body: JSON.stringify({ code: 50301, message: '验收模拟状态刷新失败' }),
      }), { times: 1 });
      await withExpectedServerErrors([MAIL_STATUS_PATH], async () => {
        try {
          const savedResponse = page.waitForResponse(response => isMailSettingsWrite(response));
          const failedRefresh = page.waitForResponse(response => pathOf(response) === MAIL_STATUS_PATH && response.status() === 503);
          await smtpSave().click();
          await entered;
          for (const field of Object.values(fields)) assert(await field().isDisabled(), 'SMTP field frozen while saving');
          assert.match(await smtpSave().getAttribute('class'), /arco-btn-loading/, 'SMTP save indicates its pending state');
          // Arco loading buttons suppress clicks without setting native disabled.
          await smtpSave().click();
          assert.equal(writes, 1, 'clicking a pending save must not create another PUT');
          assert(await smtpCancel().isDisabled(), 'SMTP cancel frozen while saving');
          assert(await page.getByRole('button', { name: '显示密码', exact: true }).isDisabled(), 'password toggle frozen while saving');
          release();
          assert.equal((await savedResponse).status(), 200);
          await failedRefresh;
        } finally {
          release();
          await page.unroute('**' + MAIL_SETTINGS_PATH, writeHandler);
        }
      });
      await page.getByText('邮箱设置已保存，但邮件状态刷新失败，请稍后刷新页面', { exact: true }).last().waitFor();
      await fields.host().waitFor({ state: 'visible' });
      assert.equal(await fields.host().isEnabled(), true);
      assertSettings(await readSettings(), { ...FIRST, hasPassword: true, configured: true, passwordNeedsUpdate: false });
      const status = await readStatus();
      assert.equal(status.configured, true);
      assert.equal(status.host, FIRST.host);
      assert.equal(status.port, FIRST.port);
      assert.deepEqual(status.queue, originalStatus.queue, 'saving SMTP must not enqueue or send mail');
      await selectTab('系统参数');
      assert.equal(await allowed.inputValue(), configDraft, 'SMTP save must retain ordinary config draft');
      assert.deepEqual(await configMap(), originalConfigs);
      await configReset().click();
      await selectTab('SMTP 配置');
    });

    await record('SMTP 刷新持久化且授权码不回显，留空可保留原授权码', async () => {
      const loaded = page.waitForResponse(response => pathOf(response) === MAIL_SETTINGS_PATH && response.status() === 200);
      await page.reload();
      await loaded;
      await selectTab('SMTP 配置');
      await fields.host().waitFor();
      assert.equal(await fields.host().inputValue(), FIRST.host);
      assert.equal(await fields.port().inputValue(), String(FIRST.port));
      assert.equal(await fields.username().inputValue(), FIRST.username);
      assert.equal(await fields.from().inputValue(), FIRST.from);
      assert.equal(await fields.password().inputValue(), '');
      assert.equal(await fields.password().getAttribute('type'), 'password');
      assert.equal(await fields.password().getAttribute('placeholder'), '已设置，留空保留原授权码');
      assertSettings(await readSettings(), { ...FIRST, hasPassword: true, configured: true, passwordNeedsUpdate: false });

      await fields.from().fill(RETAINED_FROM);
      await action(page, '/admin/system/mail-settings', 'PUT', () => smtpSave().click());
      await page.getByText('邮箱设置已保存，无需重启', { exact: true }).last().waitFor();
      assert.equal(await fields.password().inputValue(), '');
      assertSettings(await readSettings(), { ...FIRST, from: RETAINED_FROM, hasPassword: true, configured: true });
    });

    await record('更换 SMTP 服务器必须填写新授权码后才能保存', async () => {
      const before = await readSettings();
      await fields.host().fill(SECOND_HOST);
      await expectNoUiWrite(
        () => smtpSave().click(),
        '更改 SMTP 服务器或登录账号时，请重新填写授权码',
      );
      assert.deepEqual(await readSettings(), before);
      assert.equal(await fields.host().inputValue(), SECOND_HOST);
      await fields.password().fill(AUTH_CODE_B);
      await action(page, '/admin/system/mail-settings', 'PUT', () => smtpSave().click());
      assertSettings(await readSettings(), {
        host: SECOND_HOST, username: FIRST.username, from: RETAINED_FROM,
        port: FIRST.port, security: FIRST.security, hasPassword: true, configured: true,
      });
    });

    await record('更换 SMTP 登录账号必须填写新授权码后才能保存', async () => {
      const before = await readSettings();
      await fields.username().fill(SECOND_USERNAME);
      await expectNoUiWrite(
        () => smtpSave().click(),
        '更改 SMTP 服务器或登录账号时，请重新填写授权码',
      );
      assert.deepEqual(await readSettings(), before);
      assert.equal(await fields.username().inputValue(), SECOND_USERNAME);
      await fields.password().fill(AUTH_CODE_C);
      await action(page, '/admin/system/mail-settings', 'PUT', () => smtpSave().click());
      assertSettings(await readSettings(), {
        host: SECOND_HOST, username: SECOND_USERNAME, from: RETAINED_FROM,
        port: FIRST.port, security: FIRST.security, hasPassword: true, configured: true,
      });
    });

    await record('SMTP 保存失败保留草稿，重试后只持久化最终值', async () => {
      const before = await readSettings();
      await fillSmtp({ port: 465, from: FINAL_FROM, security: 'SslOnConnect' });
      await withExpectedServerErrors([MAIL_SETTINGS_PATH], async () => {
        await page.route('**' + MAIL_SETTINGS_PATH, route => route.fulfill({
          status: 503,
          contentType: 'application/json',
          body: JSON.stringify({ code: 50301, message: '验收模拟 SMTP 保存失败' }),
        }), { times: 1 });
        const failed = page.waitForResponse(response => isMailSettingsWrite(response) && response.status() === 503);
        await smtpSave().click();
        await failed;
        await page.getByText('验收模拟 SMTP 保存失败', { exact: true }).waitFor();
      });
      assert.equal(await fields.port().inputValue(), '465');
      assert.equal(await fields.from().inputValue(), FINAL_FROM);
      assert((await fields.security().innerText()).includes('TLS / SSL（通常为 465）'));
      assert.equal(await smtpSave().isEnabled(), true);
      assert.deepEqual(await readSettings(), before);

      await action(page, '/admin/system/mail-settings', 'PUT', () => smtpSave().click());
      const finalSettings = await readSettings();
      assertSettings(finalSettings, {
        host: SECOND_HOST, username: SECOND_USERNAME, from: FINAL_FROM,
        port: 465, security: 'SslOnConnect', hasPassword: true, configured: true,
      });
      assert.deepEqual(await configMap(), originalConfigs, 'SMTP flow must not modify ordinary system configs');
      const finalStatus = await readStatus();
      assert.equal(finalStatus.configured, true);
      assert.equal(finalStatus.host, SECOND_HOST);
      assert.equal(finalStatus.port, 465);
      assert.deepEqual(finalStatus.queue, originalStatus.queue, 'worker-disabled flow must not send mail');
    });

    await record('SMTP 页面内容保持在视口内，超长内容只在卡片内部滚动', async () => {
      const metrics = await page.evaluate(() => {
        const tabs = document.querySelector('.system-config-tabs');
        const card = document.querySelector('.system-mail-card');
        const body = card?.querySelector('.arco-card-body');
        const tabsRect = tabs?.getBoundingClientRect();
        const cardRect = card?.getBoundingClientRect();
        const root = document.documentElement;
        const describe = element => {
          if (!element) return null;
          const rect = element.getBoundingClientRect();
          const style = getComputedStyle(element);
          return {
            rect: { top: rect.top, bottom: rect.bottom, height: rect.height },
            offsetHeight: element.offsetHeight,
            clientHeight: element.clientHeight,
            scrollHeight: element.scrollHeight,
            boxSizing: style.boxSizing,
            height: style.height,
            paddingTop: style.paddingTop,
            paddingBottom: style.paddingBottom,
            overflow: style.overflow,
          };
        };
        return {
          viewportHeight: window.innerHeight,
          documentScrollHeight: root.scrollHeight,
          tabsBottom: tabsRect?.bottom ?? Number.POSITIVE_INFINITY,
          cardBottom: cardRect?.bottom ?? Number.POSITIVE_INFINITY,
          bodyClientHeight: body?.clientHeight ?? 0,
          bodyScrollHeight: body?.scrollHeight ?? 0,
          elements: {
            tabs: describe(tabs),
            content: describe(tabs?.querySelector(':scope > .arco-tabs-content')),
            inner: describe(tabs?.querySelector(':scope > .arco-tabs-content > .arco-tabs-content-inner')),
            pane: describe(tabs?.querySelector('.arco-tabs-content-item-active')),
            card: describe(card),
            body: describe(body),
          },
        };
      });
      const metricSummary = JSON.stringify(metrics);
      assert(metrics.tabsBottom <= metrics.viewportHeight + 1, `SMTP tabs must stay inside the viewport: ${metricSummary}`);
      assert(metrics.cardBottom <= metrics.viewportHeight + 1, `SMTP card must stay inside the viewport: ${metricSummary}`);
      assert(metrics.documentScrollHeight <= metrics.viewportHeight + 1, `SMTP must not create outer page overflow: ${metricSummary}`);
      assert(metrics.bodyScrollHeight >= metrics.bodyClientHeight, 'SMTP card body must own any overflow');
    });

    await record('SMTP 窄屏布局不撑高页面且提醒消息自动改为单列', async () => {
      const previousViewport = page.viewportSize();
      await page.setViewportSize({ width: 390, height: 600 });
      try {
        await selectTab('SMTP 配置');
        await fields.host().waitFor();
        const metrics = await page.evaluate(() => {
          const tabs = document.querySelector('.system-config-tabs');
          const card = document.querySelector('.system-mail-card');
          const eventPanel = document.querySelector('.system-notification-panel--events');
          const body = card?.querySelector('.arco-card-body');
          const tabsRect = tabs?.getBoundingClientRect();
          const cardRect = card?.getBoundingClientRect();
          const root = document.documentElement;
          return {
            viewportHeight: window.innerHeight,
            documentScrollHeight: root.scrollHeight,
            tabsBottom: tabsRect?.bottom ?? Number.POSITIVE_INFINITY,
            cardBottom: cardRect?.bottom ?? Number.POSITIVE_INFINITY,
            bodyClientHeight: body?.clientHeight ?? 0,
            bodyScrollHeight: body?.scrollHeight ?? 0,
            eventColumns: eventPanel ? getComputedStyle(eventPanel).gridTemplateColumns : '',
          };
        });
        assert(metrics.tabsBottom <= metrics.viewportHeight + 1, `窄屏 SMTP tabs 必须在视口内: ${JSON.stringify(metrics)}`);
        assert(metrics.cardBottom <= metrics.viewportHeight + 1, `窄屏 SMTP 卡片必须在视口内: ${JSON.stringify(metrics)}`);
        assert(metrics.documentScrollHeight <= metrics.viewportHeight + 1, `窄屏 SMTP 不得产生外层滚动: ${JSON.stringify(metrics)}`);
        assert(metrics.bodyScrollHeight >= metrics.bodyClientHeight, '窄屏 SMTP 卡片 body 必须承载溢出');
        assert.equal(metrics.eventColumns.split(' ').length, 1, '窄屏提醒消息必须为单列');
      } finally {
        await page.setViewportSize(previousViewport);
        await page.reload();
        await selectTab('SMTP 配置');
        await fields.host().waitFor();
      }
    });

    await page.screenshot({ path: OUT + '/smtp-settings-final.png', animations: 'disabled', fullPage: true });
  } catch (error) {
    if (page) {
      await page.screenshot({ path: OUT + '/smtp-settings-failure.png', animations: 'disabled', fullPage: true }).catch(() => {});
      console.log((await page.locator('body').innerText()).slice(-4000));
    }
    console.error(error.stack);
    process.exitCode = 1;
  } finally {
    if (browser) await browser.close();
  }
})();
