const { chromium } = require('playwright');
const crypto = require('node:crypto');
const {
  fs, assert, OUT, s, record, login, api, track,
} = require(process.env.YF_BROWSER_SUPPORT_DIR + '/ui-lib.cjs');

const captchaPath = '/api/v1/auth/captcha';
const loginPath = '/api/v1/auth/login';
const isCaptcha = response => new URL(response.url()).pathname === captchaPath;
const isLogin = response => new URL(response.url()).pathname === loginPath
  && response.request().method() === 'POST';

async function reserveCaptchaBudget(count) {
  const budgetFile = OUT + '/captcha-budget.json';
  let issued = fs.existsSync(budgetFile) ? JSON.parse(fs.readFileSync(budgetFile, 'utf8')) : [];
  issued = issued.filter(timestamp => Date.now() - timestamp < 61000);
  if (issued.length + count > 20) {
    const delay = 61000 - (Date.now() - issued[0]);
    console.log('Waiting for production CAPTCHA budget: ' + Math.ceil(delay / 1000) + 's');
    await new Promise(resolve => setTimeout(resolve, delay));
    issued = issued.filter(timestamp => Date.now() - timestamp < 61000);
  }
  issued.push(...Array.from({ length: count }, () => Date.now()));
  fs.writeFileSync(budgetFile, JSON.stringify(issued));
}

async function observedChallenge(requester, response) {
  assert.equal(response.status(), 200, 'CAPTCHA request');
  const challenge = await response.json();
  assert.ok(challenge.captchaId, 'CAPTCHA id');
  assert.match(challenge.svg, /^data:image\/(png|jpeg);base64,/, 'visible PNG/JPEG challenge');
  const observed = await requester.get(
    s.base + '/__test/captcha-answer/' + challenge.captchaId,
    { headers: { 'X-Test-Host-Key': s.key } },
  );
  assert.equal(observed.status(), 200, 'isolated TestHost challenge observer');
  return { ...challenge, answer: (await observed.json()).answer };
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

async function createAccessUser(context, permissions, marker, key, permissionCodes) {
  const role = await (await api(context, 'POST', '/admin/roles', {
    name: '访问验收角色-' + key + '-' + marker,
    description: '独立浏览器访问控制验收',
  }, s.adminToken)).json();
  await api(context, 'PUT', '/admin/roles/' + role.id + '/permissions', {
    permissionIds: permissionCodes.map(code => {
      const permission = permissions.find(item => item.code === code);
      assert.ok(permission, 'permission fixture ' + code);
      return permission.id;
    }),
  }, s.adminToken);
  const employeeNo = 'access_' + key + '_' + marker;
  const initialPassword = 'Access!' + crypto.randomBytes(18).toString('base64url');
  const password = 'Access!' + crypto.randomBytes(18).toString('base64url');
  const user = await (await api(context, 'POST', '/admin/users', {
    employeeNo,
    password: initialPassword,
    realName: '访问验收' + key,
    email: employeeNo + '@example.invalid',
    departmentId: null,
    roleId: role.id,
  }, s.adminToken)).json();
  return { ...user, employeeNo, initialPassword, password, roleId: role.id };
}

(async () => {
  let browser;
  let page;
  try {
    browser = await chromium.launch({ channel: 'chrome', headless: true });
    const adminContext = await browser.newContext();
    const permissions = await (await api(adminContext, 'GET', '/permissions', undefined, s.adminToken)).json();
    const marker = crypto.randomBytes(4).toString('hex');
    const noMenu = await createAccessUser(adminContext, permissions, marker, 'none', []);
    const menuOnly = await createAccessUser(adminContext, permissions, marker, 'menu', ['org:user']);
    const revocable = await createAccessUser(
      adminContext, permissions, marker, 'revoke', ['org:user', 'user:manage'],
    );
    await adminContext.close();
    for (const user of [noMenu, menuOnly, revocable]) await initializePassword(browser, user);

    const noMenuContext = await browser.newContext({ viewport: { width: 390, height: 844 } });
    page = await noMenuContext.newPage();
    track(page, 'access-no-menu');
    const safeDeepLink = '/profile?source=access';
    await reserveCaptchaBudget(4);
    const initialChallengeResponse = page.waitForResponse(isCaptcha);
    await page.goto(s.base + safeDeepLink);
    await page.waitForURL('**/login');
    let challenge = await observedChallenge(page.request, await initialChallengeResponse);

    await record('O01 验证码可见且手动刷新清空旧输入', async () => {
      const image = page.getByRole('img', { name: '验证码', exact: true });
      await image.waitFor();
      assert.match(await image.getAttribute('src'), /^data:image\/(png|jpeg);base64,/);
      await page.getByRole('textbox', { name: '验证码', exact: true }).fill('stale');
      const previousId = challenge.captchaId;
      const refreshed = page.waitForResponse(isCaptcha);
      await page.getByRole('button', { name: '刷新验证码', exact: true }).click();
      challenge = await observedChallenge(page.request, await refreshed);
      assert.notEqual(challenge.captchaId, previousId);
      assert.equal(await page.getByRole('textbox', { name: '验证码', exact: true }).inputValue(), '');
    });

    let loginRequests = 0;
    page.on('request', request => {
      if (new URL(request.url()).pathname === loginPath && request.method() === 'POST') loginRequests += 1;
    });
    await record('O02 错误密码留在登录页并自动取得新挑战', async () => {
      await page.getByRole('textbox', { name: '工号', exact: true }).fill(noMenu.employeeNo);
      await page.getByRole('textbox', { name: '密码', exact: true }).fill(noMenu.password + '-wrong');
      await page.getByRole('textbox', { name: '验证码', exact: true }).fill(challenge.answer);
      const failed = page.waitForResponse(isLogin);
      const refreshed = page.waitForResponse(isCaptcha);
      await page.getByRole('button', { name: '登录', exact: true }).click();
      assert.equal((await failed).status(), 401);
      challenge = await observedChallenge(page.request, await refreshed);
      await page.getByText('工号或密码错误', { exact: true }).waitFor();
      assert.equal(new URL(page.url()).pathname, '/login');
      assert.equal(loginRequests, 1);
    });

    await record('O02-O03 错误验证码恢复后返回合法同源深链', async () => {
      await page.getByRole('textbox', { name: '密码', exact: true }).fill(noMenu.password);
      await page.getByRole('textbox', { name: '验证码', exact: true }).fill('WRONG');
      const rejected = page.waitForResponse(isLogin);
      const refreshed = page.waitForResponse(isCaptcha);
      await page.getByRole('button', { name: '登录', exact: true }).click();
      assert.equal((await rejected).status(), 428);
      challenge = await observedChallenge(page.request, await refreshed);
      await page.getByText('需要图形验证码', { exact: true }).waitFor();

      await page.getByRole('textbox', { name: '验证码', exact: true }).fill(challenge.answer);
      const recovered = page.waitForResponse(isLogin);
      await page.getByRole('button', { name: '登录', exact: true }).click();
      assert.equal((await recovered).status(), 200);
      await page.waitForURL(s.base + safeDeepLink);
      await page.getByRole('heading', { name: '个人资料', exact: true }).waitFor();
      assert.equal(loginRequests, 3, 'each click creates exactly one login request');
    });

    await record('O07 无菜单账号主页说明且仍可进入个人资料', async () => {
      let dashboardRequests = 0;
      const countDashboard = request => {
        if (new URL(request.url()).pathname.startsWith('/api/v1/dashboard/')) dashboardRequests += 1;
      };
      page.on('request', countDashboard);
      await page.goto(s.base + '/');
      await page.getByText('暂无可用功能，请联系管理员', { exact: true }).waitFor();
      await page.getByText('工作台不可用', { exact: true }).waitFor();
      assert.equal(dashboardRequests, 0, 'no dashboard API without dashboard menu');
      assert.equal(await page.getByRole('button', { name: '打开导航菜单', exact: true }).count(), 0);
      await page.getByRole('button', { name: '账号菜单：' + noMenu.realName, exact: true }).click();
      await page.getByRole('menuitem', { name: '个人资料维护', exact: true }).click();
      await page.waitForURL(s.base + '/profile');
      await page.getByText(noMenu.employeeNo, { exact: true }).waitFor();
      page.off('request', countDashboard);
    });

    const dangerousTarget = '//evil.invalid/\\escape';
    const menuContext = await browser.newContext({ viewport: { width: 1440, height: 1000 } });
    await menuContext.addInitScript(target => {
      if (location.pathname === '/login') {
        history.replaceState({ ...history.state, usr: { from: target } }, '', location.href);
      }
    }, dangerousTarget);
    let externalRequests = 0;
    menuContext.on('request', request => {
      if (/^https?:\/\/evil\.invalid\//.test(request.url())) externalRequests += 1;
    });
    await menuContext.route(/^https?:\/\/evil\.invalid\//, route => route.abort());

    const switchPage = await menuContext.newPage();
    track(switchPage, 'access-switch-tab');
    await reserveCaptchaBudget(2);
    const switchInitialChallenge = switchPage.waitForResponse(isCaptcha);
    await switchPage.goto(s.base + '/login');
    await switchInitialChallenge;

    page = await menuContext.newPage();
    track(page, 'access-menu-only');
    const menuAuth = await login(page, menuOnly.employeeNo, menuOnly.password);
    await record('O03 危险回跳输入被限制在同源首页', async () => {
      await page.waitForURL(s.base + '/');
      assert.equal(new URL(page.url()).origin, s.base);
      assert.equal(new URL(page.url()).pathname, '/');
      assert.equal(externalRequests, 0);
      assert.deepEqual(menuAuth.menus, ['org:user']);
    });

    await record('R6 仅菜单无动作显示403且未知路径正常返回', async () => {
      let userDataRequests = 0;
      const countUsers = request => {
        if (new URL(request.url()).pathname === '/api/v1/admin/users') userDataRequests += 1;
      };
      page.on('request', countUsers);
      await page.goto(s.base + '/org/users');
      await page.getByText('无操作权限', { exact: true }).waitFor();
      assert.equal(userDataRequests, 0, 'client guard blocks protected user data request');
      await api(menuContext, 'GET', '/admin/users', undefined, menuAuth.accessToken, 403);
      await page.goto(s.base + '/does-not-exist');
      await page.getByText('页面不存在或已被移除', { exact: true }).waitFor();
      page.off('request', countUsers);
    });

    await record('O08 第二标签真实登录换号后两个标签不保留旧菜单', async () => {
      const refreshed = switchPage.waitForResponse(isCaptcha);
      await switchPage.getByRole('button', { name: '刷新验证码', exact: true }).click();
      const switchChallenge = await observedChallenge(switchPage.request, await refreshed);
      await switchPage.getByRole('textbox', { name: '工号', exact: true }).fill(noMenu.employeeNo);
      await switchPage.getByRole('textbox', { name: '密码', exact: true }).fill(noMenu.password);
      await switchPage.getByRole('textbox', { name: '验证码', exact: true }).fill(switchChallenge.answer);
      const switched = switchPage.waitForResponse(isLogin);
      await switchPage.getByRole('button', { name: '登录', exact: true }).click();
      assert.equal((await switched).status(), 200);
      await switchPage.waitForURL(s.base + '/');
      await switchPage.getByRole('button', { name: '账号菜单：' + noMenu.realName, exact: true }).waitFor();
      await page.getByRole('button', { name: '账号菜单：' + noMenu.realName, exact: true }).waitFor();
      assert.equal(await switchPage.getByRole('menuitem', { name: '用户管理', exact: true }).count(), 0);
      assert.equal(await page.getByRole('menuitem', { name: '用户管理', exact: true }).count(), 0);
      assert.equal(externalRequests, 0);
    });
    await menuContext.close();
    await noMenuContext.close();

    const revokeContext = await browser.newContext({ viewport: { width: 1440, height: 1000 } });
    page = await revokeContext.newPage();
    track(page, 'access-revocation');
    const revokeAuth = await login(page, revocable.employeeNo, revocable.password);
    await page.waitForURL(s.base + '/');
    await page.goto(s.base + '/org/users');
    await page.getByRole('heading', { name: '用户管理', exact: true }).waitFor();

    await record('O08 运行中撤权后403刷新权限并收敛为空菜单壳', async () => {
      await api(revokeContext, 'PUT', '/admin/roles/' + revocable.roleId + '/permissions', {
        permissionIds: [],
      }, s.adminToken);
      const denied = page.waitForResponse(response => (
        new URL(response.url()).pathname === '/api/v1/admin/users'
        && response.request().method() === 'GET'
        && response.status() === 403
      ));
      const refreshedProfile = page.waitForResponse(response => (
        new URL(response.url()).pathname === '/api/v1/auth/profile'
        && response.request().method() === 'GET'
        && response.status() === 200
      ));
      const search = page.getByPlaceholder('工号 / 姓名 / 邮箱', { exact: true });
      await search.fill(revocable.employeeNo);
      await search.press('Enter');
      await denied;
      await refreshedProfile;
      await page.waitForURL(s.base + '/');
      await page.getByText('暂无可用功能，请联系管理员', { exact: true }).waitFor();
      assert.equal(await page.getByRole('menuitem', { name: '用户管理', exact: true }).count(), 0);
      assert.equal(await page.getByRole('heading', { name: '用户管理', exact: true }).count(), 0);
      await api(revokeContext, 'GET', '/admin/users', undefined, revokeAuth.accessToken, 403);
    });
    await revokeContext.close();
  } catch (error) {
    if (page) {
      await page.screenshot({ path: OUT + '/access-failure.png', fullPage: true }).catch(() => {});
      console.log((await page.locator('body').innerText()).slice(-4500));
    }
    console.error(error.stack || error.message);
    process.exitCode = 1;
  } finally {
    if (browser) await browser.close();
  }
})();
