const { chromium } = require('playwright');
const crypto = require('node:crypto');
const {
  assert, OUT, s, record, login, reserveLoginBudget, api, track,
} = require(process.env.YF_BROWSER_SUPPORT_DIR + '/ui-lib.cjs');

const loginPath = '/api/v1/auth/login';
const refreshPath = '/api/v1/auth/refresh';
const summaryPath = '/api/v1/dashboard/summary';
const pendingPath = '/api/v1/dashboard/pending-projects';

const pathOf = value => new URL(value.url()).pathname;
const isLogin = response => pathOf(response) === loginPath
  && response.request().method() === 'POST';

async function createNoMenuUser(context) {
  const marker = crypto.randomBytes(4).toString('hex');
  const role = await (await api(context, 'POST', '/admin/roles', {
    name: '认证边界角色-' + marker,
    description: '独立浏览器认证入口验收',
  }, s.adminToken)).json();
  await api(context, 'PUT', '/admin/roles/' + role.id + '/permissions', {
    permissionIds: [],
  }, s.adminToken);

  const employeeNo = 'auth_edge_' + marker;
  const initialPassword = 'AuthEdge!' + crypto.randomBytes(6).toString('base64url');
  const password = 'AuthEdge!' + crypto.randomBytes(6).toString('base64url');
  const user = await (await api(context, 'POST', '/admin/users', {
    employeeNo,
    password: initialPassword,
    realName: '认证边界用户',
    email: employeeNo + '@example.invalid',
    departmentId: null,
    roleId: role.id,
  }, s.adminToken)).json();
  return { ...user, employeeNo, initialPassword, password };
}

async function initializePassword(browser, user) {
  const context = await browser.newContext({ viewport: { width: 1440, height: 1000 } });
  const page = await context.newPage();
  try {
    const initial = await login(page, user.employeeNo, user.initialPassword);
    assert.equal(initial.mustChangePassword, true, user.employeeNo + ' starts in forced-change state');
    await api(context, 'PUT', '/auth/password', {
      oldPassword: user.initialPassword,
      newPassword: user.password,
    }, initial.accessToken);
    await api(context, 'GET', '/auth/profile', undefined, initial.accessToken, 401);
  } finally {
    await context.close();
  }
}

(async () => {
  let browser;
  let page;
  try {
    browser = await chromium.launch({ channel: 'chrome', headless: true });

    const anonymousContext = await browser.newContext({ viewport: { width: 390, height: 844 } });
    page = await anonymousContext.newPage();
    track(page, 'auth-edges-anonymous-404');
    await record('O58 匿名未知路由显示404且返回受保护首页时安全转登录', async () => {
      let protectedRequests = 0;
      const countProtected = request => {
        const pathname = pathOf(request);
        if (pathname.startsWith('/api/v1/dashboard/')
          || pathname.startsWith('/api/v1/admin/')
          || pathname.startsWith('/api/v1/projects')) protectedRequests += 1;
      };
      page.on('request', countProtected);
      await page.goto(s.base + '/anonymous-does-not-exist');
      await page.getByText('页面不存在或已被移除', { exact: true }).waitFor();
      assert.equal(await page.getByRole('button', { name: /^账号菜单：/ }).count(), 0);
      await page.getByRole('button', { name: '返回工作台', exact: true }).click();
      await page.waitForURL('**/login');
      await page.getByRole('button', { name: '登录', exact: true }).waitFor();
      assert.equal(protectedRequests, 0, 'anonymous unknown route must not request protected data');
      page.off('request', countProtected);
    });
    await anonymousContext.close();

    const loginContext = await browser.newContext({ viewport: { width: 1440, height: 1000 } });
    page = await loginContext.newPage();
    track(page, 'auth-edges-login');
    let captchaRequests = 0;
    const countCaptcha = request => {
      if (pathOf(request) === '/api/v1/auth/captcha') captchaRequests += 1;
    };
    page.on('request', countCaptcha);
    await page.goto(s.base + '/login');

    await record('O01 登录页仅渲染工号密码且不请求验证码', async () => {
      await page.getByRole('textbox', { name: '工号', exact: true }).waitFor();
      await page.getByRole('textbox', { name: '密码', exact: true }).waitFor();
      assert.equal(await page.getByRole('textbox', { name: '验证码', exact: true }).count(), 0);
      assert.equal(await page.getByRole('img', { name: '验证码', exact: true }).count(), 0);
      assert.equal(await page.getByRole('button', { name: /验证码/ }).count(), 0);
      assert.equal(captchaRequests, 0, 'login page must not request CAPTCHA');
    });
    page.off('request', countCaptcha);

    let loginRequests = 0;
    const countLogin = request => {
      if (pathOf(request) === loginPath && request.method() === 'POST') loginRequests += 1;
    };
    page.on('request', countLogin);
    await record('O02 登录必填校验在客户端阻止空请求', async () => {
      await page.getByRole('button', { name: '登录', exact: true }).click();
      await page.getByText('请输入工号', { exact: true }).waitFor();
      await page.getByText('请输入密码', { exact: true }).waitFor();
      assert.equal(loginRequests, 0, 'empty form must not send login request');
    });

    await record('O04-O05 密码键盘显隐保留焦点且网络失败后可重试', async () => {
      const employee = page.getByRole('textbox', { name: '工号', exact: true });
      const password = page.getByRole('textbox', { name: '密码', exact: true });
      await employee.fill('admin');
      await password.fill(s.adminPassword);

      await password.focus();
      await page.keyboard.press('Tab');
      const showPassword = page.getByRole('button', { name: '显示密码', exact: true });
      await showPassword.waitFor();
      assert.equal(await showPassword.evaluate(element => document.activeElement === element), true);
      await page.keyboard.press('Enter');
      const hidePassword = page.getByRole('button', { name: '隐藏密码', exact: true });
      await hidePassword.waitFor();
      assert.equal(await password.getAttribute('type'), 'text');
      assert.equal(await hidePassword.evaluate(element => document.activeElement === element), true);
      assert.equal(loginRequests, 0, 'password visibility key must not submit the form');
      await page.keyboard.press('Enter');
      await showPassword.waitFor();
      assert.equal(await password.getAttribute('type'), 'password');

      await page.route('**/api/v1/auth/login', route => route.abort('failed'), { times: 1 });
      await reserveLoginBudget('admin');
      const failed = page.waitForEvent('requestfailed', request => (
        pathOf(request) === loginPath && request.method() === 'POST'
      ));
      await password.focus();
      await page.keyboard.press('Enter');
      await failed;
      await page.getByText('登录失败', { exact: true }).waitFor();
      assert.equal(new URL(page.url()).pathname, '/login');
      assert.equal(loginRequests, 1, 'password Enter sends one login request');
      assert.equal(await employee.inputValue(), 'admin');
    });

    await record('O02-O04 密码Enter直登且快速重复提交只建立一个会话', async () => {
      const responses = [];
      let intercepted = 0;
      const collect = response => {
        if (isLogin(response)) responses.push(response.status());
      };
      page.on('response', collect);
      await page.route('**/api/v1/auth/login', async route => {
        intercepted += 1;
        await new Promise(resolve => setTimeout(resolve, 250));
        await route.continue();
      });
      await reserveLoginBudget('admin', 2);
      const posted = page.waitForRequest(request => (
        pathOf(request) === loginPath && request.method() === 'POST'
      ));
      const logged = page.waitForResponse(response => isLogin(response) && response.status() === 200);
      const password = page.getByRole('textbox', { name: '密码', exact: true });
      await password.focus();
      await page.keyboard.press('Enter');
      await page.keyboard.press('Enter');
      const loginRequest = await posted;
      await logged;
      await page.waitForURL(s.base + '/');
      await page.getByRole('heading', { name: /^工作台/ }).waitFor();
      await page.waitForTimeout(800);
      const payload = loginRequest.postDataJSON();
      assert.equal(payload.employeeNo, 'admin');
      assert.equal(payload.password, s.adminPassword);
      assert.equal(Object.prototype.hasOwnProperty.call(payload, 'captchaId'), false);
      assert.equal(Object.prototype.hasOwnProperty.call(payload, 'captchaAnswer'), false);
      assert.equal(intercepted, 1, 'rapid Enter must not duplicate login POST');
      assert.deepEqual(responses, [200]);
      page.off('response', collect);
      await page.unroute('**/api/v1/auth/login');
    });
    page.off('request', countLogin);

    await page.getByText('暂无待确认项目', { exact: true }).waitFor();
    await record('O08 并发401只刷新一次并分别重放原请求', async () => {
      const attempts = new Map([[summaryPath, 0], [pendingPath, 0]]);
      let refreshRequests = 0;
      const countRefresh = request => {
        if (pathOf(request) === refreshPath && request.method() === 'POST') refreshRequests += 1;
      };
      page.on('request', countRefresh);
      await page.route('**/api/v1/dashboard/**', async route => {
        const pathname = pathOf(route.request());
        if (!attempts.has(pathname)) return route.continue();
        const next = attempts.get(pathname) + 1;
        attempts.set(pathname, next);
        if (next === 1) {
          await route.fulfill({
            status: 401,
            contentType: 'application/json',
            body: JSON.stringify({ code: 40101, message: '访问令牌无效或已过期' }),
          });
          return;
        }
        await route.continue();
      });
      const summaryReplayed = page.waitForResponse(response => (
        pathOf(response) === summaryPath && response.status() === 200
      ));
      const pendingReplayed = page.waitForResponse(response => (
        pathOf(response) === pendingPath && response.status() === 200
      ));
      await page.getByRole('button', { name: '刷新', exact: true }).click();
      await Promise.all([summaryReplayed, pendingReplayed]);
      assert.equal(refreshRequests, 1, 'concurrent expired responses share one refresh request');
      assert.equal(attempts.get(summaryPath), 2, 'summary request is replayed once');
      assert.equal(attempts.get(pendingPath), 2, 'pending request is replayed once');
      page.off('request', countRefresh);
      await page.unroute('**/api/v1/dashboard/**');
    });

    await record('O07 移动导航支持Escape键关闭并恢复页面操作', async () => {
      await page.setViewportSize({ width: 390, height: 844 });
      await page.getByRole('button', { name: '打开导航菜单', exact: true }).click();
      const drawer = page.locator('.mobile-nav-drawer');
      await drawer.waitFor({ state: 'visible' });
      await page.waitForFunction(() => (
        document.querySelector('.mobile-nav-drawer')?.classList.contains('slideLeft-enter-done')
      ));
      await page.waitForFunction(() => document.activeElement?.closest('.arco-drawer-wrapper') !== null);
      await page.keyboard.press('Escape');
      await drawer.waitFor({ state: 'hidden' });
      await page.getByRole('button', { name: /^账号菜单：/ }).waitFor();
    });

    await record('O58 已登录未知路由保留账号壳且按钮返回工作台', async () => {
      await page.setViewportSize({ width: 1440, height: 1000 });
      await page.goto(s.base + '/authenticated-does-not-exist');
      await page.getByText('页面不存在或已被移除', { exact: true }).waitFor();
      await page.getByRole('button', { name: /^账号菜单：/ }).waitFor();
      await page.getByRole('button', { name: '返回工作台', exact: true }).click();
      await page.waitForURL(s.base + '/');
      await page.getByRole('heading', { name: /^工作台/ }).waitFor();
    });
    await loginContext.close();

    const fixtureContext = await browser.newContext();
    const noMenu = await createNoMenuUser(fixtureContext);
    await fixtureContext.close();
    await initializePassword(browser, noMenu);

    const noMenuContext = await browser.newContext({ viewport: { width: 390, height: 844 } });
    page = await noMenuContext.newPage();
    track(page, 'auth-edges-no-menu');
    const noMenuAuth = await login(page, noMenu.employeeNo, noMenu.password);
    await page.waitForURL(s.base + '/');
    await record('O07 无菜单账号仍可进入改密页并从账号菜单退出', async () => {
      assert.deepEqual(noMenuAuth.menus, []);
      assert.equal(await page.getByRole('button', { name: '打开导航菜单', exact: true }).count(), 0);
      await page.getByRole('button', { name: '账号菜单：' + noMenu.realName, exact: true }).click();
      await page.getByRole('menuitem', { name: '个人资料维护', exact: true }).click();
      await page.waitForURL(s.base + '/profile');
      await page.getByText('登录密码', { exact: true }).waitFor();
      await page.getByRole('button', { name: '修改密码', exact: true }).waitFor();

      await page.getByRole('button', { name: '账号菜单：' + noMenu.realName, exact: true }).click();
      const loggedOut = page.waitForResponse(response => (
        pathOf(response) === '/api/v1/auth/logout'
        && response.request().method() === 'POST'
      ));
      await page.getByRole('menuitem', { name: '退出登录', exact: true }).click();
      assert.equal((await loggedOut).status(), 200);
      await page.waitForURL('**/login');
      await page.getByRole('button', { name: '登录', exact: true }).waitFor();
      await api(noMenuContext, 'GET', '/auth/profile', undefined, noMenuAuth.accessToken, 401);
    });
    await noMenuContext.close();
  } catch (error) {
    if (page) {
      await page.screenshot({ path: OUT + '/auth-edges-failure.png', fullPage: true }).catch(() => {});
      console.log((await page.locator('body').innerText()).slice(-4500));
    }
    console.error(error.stack || error.message);
    process.exitCode = 1;
  } finally {
    if (browser) await browser.close();
  }
})();
