const { chromium } = require('playwright');
const crypto = require('node:crypto');
const assert = require('node:assert/strict');
const {
  OUT, s, f, record, login, api, navigate, action, track,
} = require(process.env.YF_BROWSER_SUPPORT_DIR + '/ui-lib.cjs');

function flattenDepartments(nodes, result = []) {
  for (const node of nodes) {
    result.push(node);
    flattenDepartments(node.children || [], result);
  }
  return result;
}

(async () => {
  let browser;
  let page;
  try {
    browser = await chromium.launch({ channel: 'chrome', headless: true });
    const context = await browser.newContext({ viewport: { width: 1440, height: 1000 } });
    page = await context.newPage();
    track(page, 'accounts');

    const auth = await login(page, 'admin', s.adminPassword);
    await page.waitForURL(s.base + '/');
    const token = auth.accessToken;
    const marker = crypto.randomBytes(5).toString('hex');
    const password = () => 'Ui9!' + crypto.randomBytes(6).toString('base64url');
    const userPassword = password();
    const userResetPassword = password();
    const accountPassword = password();
    const accountResetPassword = password();
    const userNo = 'uiuser_' + marker;
    const accountNo = 'uivendor_' + marker;
    const userEmail = userNo + '@example.invalid';
    const editedUserEmail = userNo + '.edit@example.invalid';
    const accountEmail = accountNo + '@example.invalid';
    const editedAccountEmail = accountNo + '.edit@example.invalid';
    const divisionName = 'UI事业部-' + marker;
    const departmentName = 'UI部门-' + marker;
    const sectionName = 'UI课别-' + marker;
    const editedSectionName = sectionName + '-已编辑';
    const supplierName = 'UI供应商-' + marker;
    const editedSupplierName = supplierName + '-已编辑';
    const internalRoleId = Number(f.roles && f.roles['内部成员']);
    assert.ok(internalRoleId > 0, 'fixture must contain the 内部成员 role id');

    const currentModal = () => page.locator('.arco-modal:visible').last();
    const currentDrawer = () => page.locator('.account-drawer:visible');
    const confirmPop = () => page.locator('.arco-popconfirm:visible').last()
      .getByRole('button', { name: '确定', exact: true }).click();

    async function getJson(method, url, data, expected = 200) {
      return (await api(context, method, url, data, token, expected)).json();
    }

    async function pickVisibleOption(label) {
      const popup = page.locator('.arco-select-popup:visible, .arco-tree-select-popup:visible').last();
      await popup.getByText(label, { exact: true }).last().click();
    }

    async function openChoice(root, placeholder) {
      await root.getByPlaceholder(placeholder, { exact: true }).locator('..').click();
    }

    async function closeDrawer(drawer) {
      await drawer.locator('.arco-drawer-close-icon').click();
      await drawer.waitFor({ state: 'hidden' });
    }

    async function filteredList(path, expectedQuery, interact) {
      const responsePromise = page.waitForResponse((response) => {
        const url = new URL(response.url());
        if (url.pathname !== '/api/v1' + path || response.request().method() !== 'GET') return false;
        return Object.entries(expectedQuery).every(([key, value]) => url.searchParams.get(key) === String(value));
      });
      await interact();
      const response = await responsePromise;
      assert.equal(response.status(), 200, 'filtered GET ' + path);
      return response.json();
    }

    async function readUser(id) {
      const data = await getJson('GET', '/admin/users?page=1&pageSize=100&keyword=' + encodeURIComponent(userNo));
      return data.list.find((item) => item.id === id);
    }

    async function readSupplier(id) {
      return getJson('GET', '/admin/suppliers/' + id);
    }

    async function readAccounts(supplierId) {
      return getJson('GET', '/admin/suppliers/' + supplierId + '/accounts');
    }

    async function revealTreeChild(parentName, childName) {
      const child = page.getByRole('treeitem').filter({ hasText: childName });
      if (!await child.isVisible().catch(() => false)) {
        const parent = page.getByRole('treeitem').filter({ hasText: parentName });
        if (await parent.getAttribute('aria-expanded') !== 'true') {
          await parent.getByRole('button', { name: 'expand button', exact: true }).click();
        }
      }
      await child.waitFor();
    }

    let division;
    let department;
    let section;
    await navigate(page, '/org/depts');
    await page.getByRole('heading', { name: '组织架构', exact: true }).waitFor();

    await record('组织三级必填取消、创建编辑与持久化', async () => {
      await page.getByRole('button', { name: '新增事业部', exact: true }).click();
      let modal = currentModal();
      await modal.getByRole('button', { name: '创建事业部', exact: true }).click();
      await modal.getByText('请输入事业部名称', { exact: true }).waitFor();
      await modal.getByRole('button', { name: '取消', exact: true }).click();
      await modal.waitFor({ state: 'hidden' });

      await page.getByRole('button', { name: '新增事业部', exact: true }).click();
      modal = currentModal();
      await modal.getByPlaceholder('请输入事业部名称', { exact: true }).fill(divisionName);
      division = await action(page, '/admin/departments', 'POST', () => (
        modal.getByRole('button', { name: '创建事业部', exact: true }).click()
      ));
      assert.ok(division.id, 'division create response id');
      await page.getByText(divisionName, { exact: true }).first().waitFor();

      await page.getByText(divisionName, { exact: true }).first().click();
      await page.getByRole('button', { name: '新增部门', exact: true }).click();
      modal = currentModal();
      await modal.getByPlaceholder('请输入部门名称', { exact: true }).fill(departmentName);
      department = await action(page, '/admin/departments', 'POST', () => (
        modal.getByRole('button', { name: '创建部门', exact: true }).click()
      ));
      assert.ok(department.id, 'department create response id');
      await revealTreeChild(divisionName, departmentName);

      await page.getByText(departmentName, { exact: true }).first().click();
      await page.getByRole('button', { name: '新增课别', exact: true }).click();
      modal = currentModal();
      await modal.getByPlaceholder('请输入课别名称', { exact: true }).fill(sectionName);
      section = await action(page, '/admin/departments', 'POST', () => (
        modal.getByRole('button', { name: '创建课别', exact: true }).click()
      ));
      assert.ok(section.id, 'section create response id');
      await revealTreeChild(departmentName, sectionName);

      await page.getByText(sectionName, { exact: true }).first().click();
      await page.getByRole('button', { name: '编辑课别', exact: true }).click();
      modal = currentModal();
      await modal.getByPlaceholder('请输入课别名称', { exact: true }).fill(editedSectionName);
      await modal.getByPlaceholder('数字越小越靠前', { exact: true }).fill('7');
      const updated = await action(page, '/admin/departments/' + section.id, 'PUT', () => (
        modal.getByRole('button', { name: '保存课别', exact: true }).click()
      ));
      assert.equal(updated.id, section.id, 'section update response id');
      await page.getByText(editedSectionName, { exact: true }).first().waitFor();

      const tree = flattenDepartments(await getJson('GET', '/departments'));
      const savedDivision = tree.find((item) => item.id === division.id);
      const savedDepartment = tree.find((item) => item.id === department.id);
      const savedSection = tree.find((item) => item.id === section.id);
      assert.equal(savedDivision.name, divisionName);
      assert.equal(savedDivision.parentId, null);
      assert.equal(savedDepartment.name, departmentName);
      assert.equal(savedDepartment.parentId, division.id);
      assert.equal(savedSection.name, editedSectionName);
      assert.equal(savedSection.parentId, department.id);
      assert.equal(savedSection.sortNo, 7);
    });

    let user;
    const userRow = () => page.getByRole('row').filter({ hasText: userNo });
    await navigate(page, '/org/users');
    await page.getByRole('heading', { name: '用户管理', exact: true }).waitFor();

    for (const viewport of [{ width: 1920, height: 945 }, { width: 1366, height: 768 }, { width: 1280, height: 600 }]) {
      await record(`新增用户下拉树和角色不被裁剪 ${viewport.width}x${viewport.height}`, async () => {
        await page.setViewportSize(viewport);
        await page.getByRole('button', { name: '新增用户', exact: true }).click();
        const modal = currentModal();
        for (const [placeholder, label] of [['选择组织', editedSectionName + '（课别）'], ['选择角色', '内部成员']]) {
          await openChoice(modal, placeholder);
          const popup = page.locator('.arco-select-popup:visible, .arco-tree-select-popup:visible').last();
          await popup.waitFor();
          const option = popup.getByText(label, { exact: true }).last();
          await option.scrollIntoViewIfNeeded();
          // A locator being visible alone does not prove it is outside the clipping region.
          await page.waitForFunction(() => {
            const popups = [...document.querySelectorAll('.arco-select-popup, .arco-tree-select-popup')].filter(e => e.getBoundingClientRect().height > 0);
            const popup = popups.at(-1);
            if (!popup) return false;
            const box = popup.getBoundingClientRect();
            return !popup.closest('.arco-modal-content') && !!popup.closest('[role="dialog"]') && box.top >= 0 && box.bottom <= innerHeight && box.left >= 0 && box.right <= innerWidth;
          });
          await option.click({ trial: true });
          await page.screenshot({ path: OUT + `/user-popup-${viewport.width}-${viewport.height}-${placeholder === '选择组织' ? 'org' : 'role'}.png`, fullPage: true });
          await option.click();
          await popup.waitFor({ state: 'hidden' });
          await modal.getByText(label, { exact: true }).waitFor();
        }
        await modal.getByRole('button', { name: '取消', exact: true }).click();
        await modal.waitFor({ state: 'hidden' });
      });
    }
    await page.setViewportSize({ width: 1440, height: 1000 });

    await record('内部用户必填取消、创建及筛选持久化', async () => {
      await page.getByRole('button', { name: '新增用户', exact: true }).click();
      let modal = currentModal();
      await modal.getByRole('button', { name: '创建用户', exact: true }).click();
      await modal.getByText('请输入工号', { exact: true }).waitFor();
      await modal.getByText('请输入初始密码', { exact: true }).waitFor();
      await modal.getByText('请输入姓名', { exact: true }).waitFor();
      await modal.getByText('请输入邮箱', { exact: true }).waitFor();
      await modal.getByText('请选择角色', { exact: true }).waitFor();
      await modal.getByRole('button', { name: '取消', exact: true }).click();
      await modal.waitFor({ state: 'hidden' });

      await page.getByRole('button', { name: '新增用户', exact: true }).click();
      modal = currentModal();
      await modal.getByPlaceholder('3-32 位字母、数字或下划线', { exact: true }).fill(userNo);
      await modal.getByPlaceholder('6-20 位', { exact: true }).fill(userPassword);
      await modal.getByPlaceholder('姓名', { exact: true }).fill('UI内部用户');
      await modal.getByPlaceholder('name@example.com', { exact: true }).fill(userEmail);
      await openChoice(modal, '选择组织');
      await pickVisibleOption(editedSectionName + '（课别）');
      await openChoice(modal, '选择角色');
      await pickVisibleOption('内部成员');
      user = await action(page, '/admin/users', 'POST', () => (
        modal.getByRole('button', { name: '创建用户', exact: true }).click()
      ));
      assert.ok(user.id, 'user create response id');
      await userRow().waitFor();

      const saved = await readUser(user.id);
      assert.equal(saved.employeeNo, userNo);
      assert.equal(saved.email, userEmail);
      assert.equal(saved.departmentId, section.id);
      assert.equal(saved.roleId, internalRoleId);

      const toolbar = page.locator('.page-toolbar');
      let filtered = await filteredList('/admin/users', { keyword: userNo }, async () => {
        const input = toolbar.getByPlaceholder('工号 / 姓名 / 邮箱', { exact: true });
        await input.fill(userNo);
        await input.press('Enter');
      });
      assert.ok(filtered.list.some((item) => item.id === user.id), 'employee filter includes created user');

      filtered = await filteredList('/admin/users', { keyword: userNo, status: 'ACTIVE' }, async () => {
        await openChoice(toolbar, '全部状态');
        await pickVisibleOption('启用');
      });
      assert.ok(filtered.list.some((item) => item.id === user.id), 'status filter includes created user');

      filtered = await filteredList('/admin/users', {
        keyword: userNo, status: 'ACTIVE', departmentId: section.id,
      }, async () => {
        await openChoice(toolbar, '全部组织');
        await pickVisibleOption(editedSectionName + '（课别）');
      });
      assert.ok(filtered.list.some((item) => item.id === user.id), 'department filter includes created user');
      await userRow().waitFor();
    });

    await navigate(page, '/org/users');
    await page.getByRole('heading', { name: '用户管理', exact: true }).waitFor();
    await userRow().waitFor();

    await record('内部用户编辑、重置密码与启停持久化', async () => {
      await userRow().getByRole('button', { name: '编辑', exact: true }).click();
      let modal = currentModal();
      await modal.getByPlaceholder('姓名', { exact: true }).fill('UI内部用户已编辑');
      await modal.getByPlaceholder('name@example.com', { exact: true }).fill(editedUserEmail);
      const updated = await action(page, '/admin/users/' + user.id, 'PUT', () => (
        modal.getByRole('button', { name: '保存用户', exact: true }).click()
      ));
      assert.equal(updated.id, user.id, 'user update response id');
      await userRow().getByText('UI内部用户已编辑', { exact: true }).waitFor();
      let saved = await readUser(user.id);
      assert.equal(saved.realName, 'UI内部用户已编辑');
      assert.equal(saved.email, editedUserEmail);
      assert.equal(saved.departmentId, section.id);
      assert.equal(saved.roleId, internalRoleId);

      await userRow().getByRole('button', { name: '重置密码', exact: true }).click();
      modal = currentModal();
      await modal.getByPlaceholder('6-20 位', { exact: true }).fill(userResetPassword);
      const reset = await action(page, '/admin/users/' + user.id + '/password', 'PUT', () => (
        modal.getByRole('button', { name: '确认重置', exact: true }).click()
      ));
      assert.deepEqual(reset, {});
      saved = await readUser(user.id);
      assert.equal(saved.employeeNo, userNo, 'user remains readable after password reset');

      await userRow().getByRole('button', { name: '禁用', exact: true }).click();
      const disabled = await action(page, '/admin/users/' + user.id + '/status', 'PUT', confirmPop);
      assert.equal(disabled.status, 'DISABLED');
      await userRow().getByRole('button', { name: '启用', exact: true }).waitFor();
      saved = await readUser(user.id);
      assert.equal(saved.status, 'DISABLED');

      await userRow().getByRole('button', { name: '启用', exact: true }).click();
      const enabled = await action(page, '/admin/users/' + user.id + '/status', 'PUT', confirmPop);
      assert.equal(enabled.status, 'ACTIVE');
      await userRow().getByRole('button', { name: '禁用', exact: true }).waitFor();
      saved = await readUser(user.id);
      assert.equal(saved.status, 'ACTIVE');
    });

    await record('内部用户删除与列表移除', async () => {
      await userRow().getByRole('button', { name: '删除', exact: true }).click();
      const removed = await action(page, '/admin/users/' + user.id, 'DELETE', confirmPop);
      assert.deepEqual(removed, {});
      await userRow().waitFor({ state: 'detached' });
      assert.equal(await readUser(user.id), undefined);
    });

    let supplier;
    let account;
    const supplierRow = () => page.getByRole('row').filter({ hasText: editedSupplierName });
    const accountRow = () => currentDrawer().getByRole('row').filter({ hasText: accountNo });
    await navigate(page, '/suppliers');
    await page.getByRole('heading', { name: '供应商管理', exact: true }).waitFor();

    await record('供应商必填取消、创建编辑与筛选持久化', async () => {
      await page.getByRole('button', { name: '新增供应商', exact: true }).click();
      let modal = currentModal();
      await modal.getByRole('button', { name: '创建供应商', exact: true }).click();
      await modal.getByText('请输入名称', { exact: true }).waitFor();
      await modal.getByRole('button', { name: '取消', exact: true }).click();
      await modal.waitFor({ state: 'hidden' });

      await page.getByRole('button', { name: '新增供应商', exact: true }).click();
      modal = currentModal();
      await modal.getByPlaceholder('公司全称', { exact: true }).fill(supplierName);
      await modal.getByPlaceholder('选填', { exact: true }).fill('UI自动验收临时供应商');
      supplier = await action(page, '/admin/suppliers', 'POST', () => (
        modal.getByRole('button', { name: '创建供应商', exact: true }).click()
      ));
      assert.ok(supplier.id, 'supplier create response id');
      await page.getByRole('row').filter({ hasText: supplierName }).waitFor();

      const originalRow = page.getByRole('row').filter({ hasText: supplierName });
      await originalRow.getByRole('button', { name: '编辑', exact: true }).click();
      modal = currentModal();
      await modal.getByPlaceholder('公司全称', { exact: true }).fill(editedSupplierName);
      await modal.getByPlaceholder('选填', { exact: true }).fill('UI自动验收已编辑');
      const updated = await action(page, '/admin/suppliers/' + supplier.id, 'PUT', () => (
        modal.getByRole('button', { name: '保存修改', exact: true }).click()
      ));
      assert.equal(updated.id, supplier.id, 'supplier update response id');
      await supplierRow().waitFor();
      let saved = await readSupplier(supplier.id);
      assert.equal(saved.name, editedSupplierName);
      assert.equal(saved.remark, 'UI自动验收已编辑');

      const toolbar = page.locator('.page-toolbar');
      let filtered = await filteredList('/admin/suppliers', { keyword: editedSupplierName }, async () => {
        const input = toolbar.getByPlaceholder('供应商名称', { exact: true });
        await input.fill(editedSupplierName);
        await input.press('Enter');
      });
      assert.ok(filtered.list.some((item) => item.id === supplier.id), 'supplier name filter includes created supplier');

      filtered = await filteredList('/admin/suppliers', {
        keyword: editedSupplierName, status: 'ACTIVE',
      }, async () => {
        await openChoice(toolbar, '全部状态');
        await pickVisibleOption('启用');
      });
      assert.ok(filtered.list.some((item) => item.id === supplier.id), 'supplier status filter includes created supplier');
      saved = await readSupplier(supplier.id);
      assert.equal(saved.status, 'ACTIVE');
    });

    await navigate(page, '/suppliers');
    await page.getByRole('heading', { name: '供应商管理', exact: true }).waitFor();
    await supplierRow().waitFor();

    await record('供应商账号必填取消、创建编辑与密码重置', async () => {
      const manualRoleName = '手工供应商角色-' + marker;
      const manualRole = await getJson('POST', '/admin/roles', { name: manualRoleName, description: '隔离开户验收角色' });
      const permissionList = await getJson('GET', '/permissions');
      await getJson('PUT', '/admin/roles/' + manualRole.id + '/permissions', {
        permissionIds: permissionList.filter(item => ['dashboard', 'project:list'].includes(item.code)).map(item => item.id),
      });
      await supplierRow().getByRole('button', { name: '账号管理', exact: true }).click();
      let drawer = currentDrawer();
      await drawer.getByText('账号管理 · ' + editedSupplierName, { exact: true }).waitFor();
      await drawer.getByRole('button', { name: '新增账号', exact: true }).click();
      let modal = currentModal();
      await modal.getByRole('button', { name: '创建账号', exact: true }).click();
      await modal.getByText('请输入工号', { exact: true }).waitFor();
      await modal.getByText('请输入初始密码', { exact: true }).waitFor();
      await modal.getByText('请输入姓名', { exact: true }).waitFor();
      await modal.getByText('请输入邮箱', { exact: true }).waitFor();
      await modal.getByRole('button', { name: '取消', exact: true }).click();
      await modal.waitFor({ state: 'hidden' });

      await drawer.getByRole('button', { name: '新增账号', exact: true }).click();
      modal = currentModal();
      await modal.getByPlaceholder('3-32 位字母、数字或下划线', { exact: true }).fill(accountNo);
      await modal.getByPlaceholder('6-20 位', { exact: true }).fill(accountPassword);
      await modal.getByPlaceholder('姓名', { exact: true }).fill('UI供应商账号');
      await modal.getByPlaceholder('name@example.com', { exact: true }).fill(accountEmail);
      await openChoice(modal, '选择供应商角色');
      await pickVisibleOption(manualRoleName);
      account = await action(page, '/admin/suppliers/' + supplier.id + '/accounts', 'POST', () => (
        modal.getByRole('button', { name: '创建账号', exact: true }).click()
      ));
      assert.ok(account.id, 'supplier account create response id');
      await accountRow().waitFor();
      let saved = (await readAccounts(supplier.id)).find((item) => item.id === account.id);
      assert.equal(saved.employeeNo, accountNo);
      assert.equal(saved.email, accountEmail);
      assert.equal(saved.roleId, manualRole.id);
      assert.equal(saved.roleName, manualRoleName);
      assert.equal(saved.status, 'ACTIVE');

      await accountRow().getByRole('button', { name: '编辑', exact: true }).click();
      modal = currentModal();
      await modal.getByPlaceholder('姓名', { exact: true }).fill('UI供应商账号已编辑');
      await modal.getByPlaceholder('name@example.com', { exact: true }).fill(editedAccountEmail);
      const updated = await action(page, '/admin/supplier-accounts/' + account.id, 'PUT', () => (
        modal.getByRole('button', { name: '保存账号', exact: true }).click()
      ));
      assert.equal(updated.id, account.id, 'supplier account update response id');
      await accountRow().getByText('UI供应商账号已编辑', { exact: true }).waitFor();
      saved = (await readAccounts(supplier.id)).find((item) => item.id === account.id);
      assert.equal(saved.realName, 'UI供应商账号已编辑');
      assert.equal(saved.email, editedAccountEmail);

      await accountRow().getByRole('button', { name: '重置密码', exact: true }).click();
      modal = currentModal();
      await modal.getByPlaceholder('6-20 位', { exact: true }).fill(accountResetPassword);
      const reset = await action(page, '/admin/supplier-accounts/' + account.id + '/password', 'PUT', () => (
        modal.getByRole('button', { name: '确认重置', exact: true }).click()
      ));
      assert.deepEqual(reset, {});
      saved = (await readAccounts(supplier.id)).find((item) => item.id === account.id);
      assert.equal(saved.employeeNo, accountNo, 'account remains readable after password reset');
    });

    await record('供应商及账号启停边界持久化', async () => {
      let drawer = currentDrawer();
      let updated = await action(page, '/admin/supplier-accounts/' + account.id + '/status', 'PUT', () => (
        accountRow().getByRole('button', { name: '禁用', exact: true }).click()
      ));
      assert.equal(updated.status, 'DISABLED');
      await accountRow().getByRole('button', { name: '启用', exact: true }).waitFor();
      let savedAccount = (await readAccounts(supplier.id)).find((item) => item.id === account.id);
      assert.equal(savedAccount.status, 'DISABLED');

      updated = await action(page, '/admin/supplier-accounts/' + account.id + '/status', 'PUT', () => (
        accountRow().getByRole('button', { name: '启用', exact: true }).click()
      ));
      assert.equal(updated.status, 'ACTIVE');
      await accountRow().getByRole('button', { name: '禁用', exact: true }).waitFor();
      savedAccount = (await readAccounts(supplier.id)).find((item) => item.id === account.id);
      assert.equal(savedAccount.status, 'ACTIVE');

      await closeDrawer(drawer);
      await supplierRow().getByRole('button', { name: '禁用', exact: true }).click();
      let savedSupplier = await action(page, '/admin/suppliers/' + supplier.id + '/status', 'PUT', confirmPop);
      assert.equal(savedSupplier.status, 'DISABLED');
      await supplierRow().getByRole('button', { name: '启用', exact: true }).waitFor();
      savedSupplier = await readSupplier(supplier.id);
      assert.equal(savedSupplier.status, 'DISABLED');

      await supplierRow().getByRole('button', { name: '账号管理', exact: true }).click();
      drawer = currentDrawer();
      await drawer.getByText('账号管理 · ' + editedSupplierName, { exact: true }).waitFor();
      assert.equal(await drawer.getByRole('button', { name: '新增账号', exact: true }).isDisabled(), true);
      await closeDrawer(drawer);

      await supplierRow().getByRole('button', { name: '启用', exact: true }).click();
      savedSupplier = await action(page, '/admin/suppliers/' + supplier.id + '/status', 'PUT', confirmPop);
      assert.equal(savedSupplier.status, 'ACTIVE');
      await supplierRow().getByRole('button', { name: '禁用', exact: true }).waitFor();
      savedSupplier = await readSupplier(supplier.id);
      assert.equal(savedSupplier.status, 'ACTIVE');
    });

    await record('供应商账号和供应商删除生效', async () => {
      await supplierRow().getByRole('button', { name: '账号管理', exact: true }).click();
      const drawer = currentDrawer();
      await drawer.getByText('账号管理 · ' + editedSupplierName, { exact: true }).waitFor();
      await accountRow().getByRole('button', { name: '删除', exact: true }).click();
      const removedAccount = await action(page, '/admin/supplier-accounts/' + account.id, 'DELETE', confirmPop);
      assert.deepEqual(removedAccount, {});
      await accountRow().waitFor({ state: 'detached' });
      assert.equal((await readAccounts(supplier.id)).some((item) => item.id === account.id), false);
      await closeDrawer(drawer);

      await supplierRow().getByRole('button', { name: '删除', exact: true }).click();
      const removedSupplier = await action(page, '/admin/suppliers/' + supplier.id, 'DELETE', confirmPop);
      assert.deepEqual(removedSupplier, {});
      await supplierRow().waitFor({ state: 'detached' });
      await api(context, 'GET', '/admin/suppliers/' + supplier.id, undefined, token, 404);
    });

    await navigate(page, '/org/depts');
    await page.getByRole('heading', { name: '组织架构', exact: true }).waitFor();
    await record('组织三级自有节点逆序删除生效', async () => {
      await revealTreeChild(divisionName, departmentName);
      await revealTreeChild(departmentName, editedSectionName);
      for (const item of [
        { id: section.id, name: editedSectionName, kind: '课别' },
        { id: department.id, name: departmentName, kind: '部门' },
        { id: division.id, name: divisionName, kind: '事业部' },
      ]) {
        await page.getByText(item.name, { exact: true }).first().click();
        await page.getByRole('button', { name: '删除' + item.kind, exact: true }).click();
        const removed = await action(page, '/admin/departments/' + item.id, 'DELETE', confirmPop);
        assert.deepEqual(removed, {});
        await page.getByText(item.name, { exact: true }).first().waitFor({ state: 'detached' });
      }
      const tree = flattenDepartments(await getJson('GET', '/departments'));
      assert.equal(tree.some((item) => [division.id, department.id, section.id].includes(item.id)), false);
    });
  } catch (error) {
    if (page) {
      await page.screenshot({ path: OUT + '/accounts-failure.png', fullPage: true }).catch(() => {});
      console.log((await page.locator('body').innerText()).slice(-4500));
    }
    console.error(error.message);
    process.exitCode = 1;
  } finally {
    if (browser) await browser.close();
  }
})();
