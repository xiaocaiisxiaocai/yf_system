const { chromium } = require('playwright');
const crypto = require('node:crypto');
const {
  assert, OUT, s, record, login, reserveLoginBudget, api, track,
} = require(process.env.YF_BROWSER_SUPPORT_DIR + '/ui-lib.cjs');

const loginPath = '/api/v1/auth/login';
const isLogin = response => new URL(response.url()).pathname === loginPath
  && response.request().method() === 'POST';

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

async function createAccessUser(context, adminToken, permissions, departmentId, marker, key, permissionCodes) {
  const role = await (await api(context, 'POST', '/admin/roles', {
    name: '访问验收角色-' + key + '-' + marker,
    description: '独立浏览器访问控制验收',
  }, adminToken)).json();
  await api(context, 'PUT', '/admin/roles/' + role.id + '/permissions', {
    permissionIds: permissionCodes.map(code => {
      const permission = permissions.find(item => item.code === code);
      assert.ok(permission, 'permission fixture ' + code);
      return permission.id;
    }),
  }, adminToken);
  const employeeNo = 'access_' + key + '_' + marker;
  const initialPassword = 'Access!' + crypto.randomBytes(6).toString('base64url');
  const password = 'Access!' + crypto.randomBytes(6).toString('base64url');
  const user = await (await api(context, 'POST', '/admin/users', {
    employeeNo,
    password: initialPassword,
    realName: '访问验收' + key,
    email: employeeNo + '@example.invalid',
    departmentId,
    roleId: role.id,
  }, adminToken)).json();
  return { ...user, employeeNo, initialPassword, password, roleId: role.id };
}

(async () => {
  let browser;
  let page;
  try {
    browser = await chromium.launch({ channel: 'chrome', headless: true });
    const adminContext = await browser.newContext();
    const adminPage = await adminContext.newPage();
    const adminAuth = await login(adminPage, 'admin', s.adminPassword);
    await adminPage.waitForURL(s.base + '/');
    const adminToken = adminAuth.accessToken;
    const permissions = await (await api(adminContext, 'GET', '/permissions', undefined, adminToken)).json();
    const departments = await (await api(adminContext, 'GET', '/departments', undefined, adminToken)).json();
    const activeDepartments = [];
    const collectActiveDepartments = nodes => {
      for (const node of nodes) {
        if (node.status === 'ACTIVE') activeDepartments.push(node);
        collectActiveDepartments(node.children || []);
      }
    };
    collectActiveDepartments(departments);
    const departmentId = activeDepartments.at(-1)?.id;
    assert.ok(departmentId, 'an active department fixture is required for internal access users');
    const marker = crypto.randomBytes(4).toString('hex');
    const noMenu = await createAccessUser(adminContext, adminToken, permissions, departmentId, marker, 'none', []);
    const menuOnly = await createAccessUser(adminContext, adminToken, permissions, departmentId, marker, 'menu', ['org:user']);
    const revocable = await createAccessUser(
      adminContext, adminToken, permissions, departmentId, marker, 'revoke', ['org:user', 'user:manage'],
    );
    await adminContext.close();
    for (const user of [noMenu, menuOnly, revocable]) await initializePassword(browser, user);

    const noMenuContext = await browser.newContext({ viewport: { width: 390, height: 844 } });
    page = await noMenuContext.newPage();
    track(page, 'access-no-menu');
    const safeDeepLink = '/profile?source=access';
    let captchaRequests = 0;
    const countCaptcha = request => {
      if (new URL(request.url()).pathname === '/api/v1/auth/captcha') captchaRequests += 1;
    };
    page.on('request', countCaptcha);
    await page.goto(s.base + safeDeepLink);
    await page.waitForURL('**/login');

    await record('O01 受保护深链转到无验证码登录页', async () => {
      await page.getByRole('textbox', { name: '工号', exact: true }).waitFor();
      await page.getByRole('textbox', { name: '密码', exact: true }).waitFor();
      assert.equal(await page.getByRole('textbox', { name: '验证码', exact: true }).count(), 0);
      assert.equal(await page.getByRole('img', { name: '验证码', exact: true }).count(), 0);
      assert.equal(captchaRequests, 0, 'protected redirect must not request CAPTCHA');
    });

    let loginRequests = 0;
    page.on('request', request => {
      if (new URL(request.url()).pathname === loginPath && request.method() === 'POST') loginRequests += 1;
    });
    await record('O02-O03 错误密码可恢复并返回合法同源深链', async () => {
      const employee = page.getByRole('textbox', { name: '工号', exact: true });
      const password = page.getByRole('textbox', { name: '密码', exact: true });
      const wrongPassword = noMenu.password.slice(0, -1)
        + (noMenu.password.endsWith('X') ? 'Y' : 'X');
      await employee.fill(noMenu.employeeNo);
      await password.fill(wrongPassword);
      await reserveLoginBudget(noMenu.employeeNo);
      const failed = page.waitForResponse(isLogin);
      await page.getByRole('button', { name: '登录', exact: true }).click();
      assert.equal((await failed).status(), 401);
      await page.getByText('工号或密码错误', { exact: true }).waitFor();
      assert.equal(new URL(page.url()).pathname, '/login');
      assert.equal(loginRequests, 1);
      assert.equal(await employee.inputValue(), noMenu.employeeNo);
      await password.fill(noMenu.password);
      await reserveLoginBudget(noMenu.employeeNo);
      const recovered = page.waitForResponse(isLogin);
      await page.getByRole('button', { name: '登录', exact: true }).click();
      assert.equal((await recovered).status(), 200);
      await page.waitForURL(s.base + safeDeepLink);
      await page.getByRole('heading', { name: '个人资料', exact: true }).waitFor();
      assert.equal(new URL(page.url()).origin, s.base);
      assert.equal(loginRequests, 2, 'each click creates exactly one login request');
      assert.equal(captchaRequests, 0, 'credential recovery must not request CAPTCHA');
    });
    page.off('request', countCaptcha);

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
      await page.getByRole('menuitem', { name: '个人资料', exact: true }).click();
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
    await switchPage.goto(s.base + '/login');
    await switchPage.getByRole('textbox', { name: '工号', exact: true }).waitFor();

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
      await switchPage.getByRole('textbox', { name: '工号', exact: true }).fill(noMenu.employeeNo);
      await switchPage.getByRole('textbox', { name: '密码', exact: true }).fill(noMenu.password);
      await reserveLoginBudget(noMenu.employeeNo);
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
      }, adminToken);
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
