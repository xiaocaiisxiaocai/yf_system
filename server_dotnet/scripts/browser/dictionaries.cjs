const { chromium } = require('playwright');
const crypto = require('node:crypto');
const { assert, OUT, s, f, record, login, api, navigate, action, track } = require(process.env.YF_BROWSER_SUPPORT_DIR + '/ui-lib.cjs');

const endpoint = '/api/v1/project-dictionaries';
const types = {
  vendor: { key: 'ROBOT_VENDOR', label: 'Robot 厂商' },
  model: { key: 'ROBOT_MODEL', label: 'Robot 型号' },
  priority: { key: 'PRIORITY', label: '优先级' },
};

async function createDictionary(context, token, type, name, parentId = null, sortNo = 900) {
  return (await api(context, 'POST', '/project-dictionaries', {
    type, name, parentId, sortNo, enabled: true,
  }, token)).json();
}

(async () => {
  let browser, page;
  try {
    browser = await chromium.launch({ channel: 'chrome', headless: true });
    const context = await browser.newContext({ viewport: { width: 1440, height: 1000 } });
    page = await context.newPage();
    track(page, 'dictionaries');
    const auth = await login(page, 'admin', s.adminPassword);
    await page.waitForURL(s.base + '/');
    const adminToken = auth.accessToken;
    const suffix = crypto.randomBytes(5).toString('hex');
    const names = {
      vendor: '字典厂商-' + suffix,
      model: '字典型号-' + suffix,
      priority: '字典优先级-' + suffix,
    };
    const edited = Object.fromEntries(Object.entries(names).map(([key, value]) => [key, value + '-已编辑']));
    const row = name => page.getByRole('row').filter({ has: page.getByText(name, { exact: true }) });
    const tab = label => page.getByRole('tab', { name: label, exact: true });
    const dialog = () => page.getByRole('dialog');
    const chooseTab = async kind => {
      await tab(types[kind].label).click();
      await tab(types[kind].label).waitFor({ state: 'visible' });
    };

    let injected = false;
    await page.route('**/api/v1/project-dictionaries?*', async route => {
      const url = new URL(route.request().url());
      if (!injected && url.searchParams.get('type') === 'ROBOT_VENDOR') {
        injected = true;
        await route.fulfill({
          status: 503,
          contentType: 'application/json',
          body: JSON.stringify({ code: 50301, message: 'temporary dictionary failure' }),
        });
        return;
      }
      await route.continue();
    });
    page.expectedServerErrors = new Set([endpoint]);
    await navigate(page, '/system/dictionaries');
    await record('数据字典加载错误提供真实重试并恢复三类数据', async () => {
      await page.getByText('字典加载失败', { exact: true }).waitFor();
      await page.getByRole('button', { name: '重试', exact: true }).click();
      await page.getByRole('heading', { name: '数据字典', exact: true }).waitFor();
      await page.getByRole('columnheader', { name: '名称', exact: true }).waitFor();
      assert.equal(await page.getByRole('columnheader', { name: /编码|代码|code/i }).count(), 0);
      for (const item of Object.values(types)) await tab(item.label).waitFor();
    });
    await page.unroute('**/api/v1/project-dictionaries?*');
    page.expectedServerErrors.clear();

    let vendor;
    await record('Robot 厂商名称必填且创建请求没有 code 字段', async () => {
      await page.getByRole('button', { name: '新增 Robot 厂商', exact: true }).click();
      await dialog().getByRole('button', { name: '保存', exact: true }).click();
      await dialog().getByText('请输入名称', { exact: true }).waitFor();
      await dialog().getByPlaceholder('Robot 厂商', { exact: true }).fill(names.vendor);
      const requestReady = page.waitForRequest(request => new URL(request.url()).pathname === endpoint
        && request.method() === 'POST');
      vendor = await action(page, '/project-dictionaries', 'POST', () =>
        dialog().getByRole('button', { name: '保存', exact: true }).click());
      const body = (await requestReady).postDataJSON();
      assert.deepEqual(Object.keys(body).sort(), ['enabled', 'name', 'parentId', 'sortNo', 'type']);
      assert.equal(body.name, names.vendor); assert.equal(body.parentId, null);
      await row(names.vendor).waitFor();
    });

    await record('Robot 厂商编辑后持久化回显', async () => {
      await row(names.vendor).getByRole('button', { name: '编辑', exact: true }).click();
      await dialog().getByPlaceholder('Robot 厂商', { exact: true }).fill(edited.vendor);
      await action(page, '/project-dictionaries/' + vendor.id, 'PUT', () =>
        dialog().getByRole('button', { name: '保存', exact: true }).click());
      await row(edited.vendor).waitFor();
    });

    let model;
    await chooseTab('model');
    await record('Robot 型号名称和上级厂商必填且保存父级关联', async () => {
      await page.getByRole('button', { name: '新增 Robot 型号', exact: true }).click();
      await dialog().getByRole('button', { name: '保存', exact: true }).click();
      await dialog().getByText('请输入名称', { exact: true }).waitFor();
      await dialog().getByText('请选择厂商', { exact: true }).waitFor();
      await dialog().getByPlaceholder('Robot 型号', { exact: true }).fill(names.model);
      await dialog().getByRole('button', { name: '保存', exact: true }).click();
      await dialog().getByText('请选择厂商', { exact: true }).waitFor();
      await dialog().getByPlaceholder('选择厂商', { exact: true }).click();
      await page.getByRole('option', { name: edited.vendor, exact: true }).click();
      model = await action(page, '/project-dictionaries', 'POST', () =>
        dialog().getByRole('button', { name: '保存', exact: true }).click());
      assert.equal(model.parentId, vendor.id); assert.equal(model.parentName, edited.vendor);
      await row(names.model).getByText(edited.vendor, { exact: true }).waitFor();
    });

    await record('Robot 型号编辑保留父级并持久化回显', async () => {
      await row(names.model).getByRole('button', { name: '编辑', exact: true }).click();
      await dialog().getByPlaceholder('Robot 型号', { exact: true }).fill(edited.model);
      const updated = await action(page, '/project-dictionaries/' + model.id, 'PUT', () =>
        dialog().getByRole('button', { name: '保存', exact: true }).click());
      assert.equal(updated.parentId, vendor.id); assert.equal(updated.parentName, edited.vendor);
      await row(edited.model).getByText(edited.vendor, { exact: true }).waitFor();
    });

    await record('型号厂商筛选与无结果空态可清空恢复', async () => {
      await page.getByLabel('筛选 Robot 厂商', { exact: true }).click();
      await page.getByRole('option', { name: edited.vendor, exact: true }).click();
      await row(edited.model).waitFor();
      await page.getByLabel('搜索字典', { exact: true }).fill('不存在-' + suffix);
      await page.getByText('未找到匹配项', { exact: true }).waitFor();
      await page.getByText('请调整关键字或筛选条件', { exact: true }).waitFor();
      await page.getByRole('button', { name: '清空筛选', exact: true }).click();
      assert.equal(await page.getByLabel('搜索字典', { exact: true }).inputValue(), '');
      await row(edited.model).waitFor();
    });

    let priority;
    await chooseTab('priority');
    await record('优先级名称必填并完成创建编辑', async () => {
      await page.getByRole('button', { name: '新增 优先级', exact: true }).click();
      await dialog().getByRole('button', { name: '保存', exact: true }).click();
      await dialog().getByText('请输入名称', { exact: true }).waitFor();
      await dialog().getByPlaceholder('优先级', { exact: true }).fill(names.priority);
      priority = await action(page, '/project-dictionaries', 'POST', () =>
        dialog().getByRole('button', { name: '保存', exact: true }).click());
      await row(names.priority).waitFor();
      await row(names.priority).getByRole('button', { name: '编辑', exact: true }).click();
      await dialog().getByPlaceholder('优先级', { exact: true }).fill(edited.priority);
      await action(page, '/project-dictionaries/' + priority.id, 'PUT', () =>
        dialog().getByRole('button', { name: '保存', exact: true }).click());
      await row(edited.priority).waitFor();
    });

    const deleteRow = async (name, id) => {
      await row(name).getByRole('button', { name: '删除', exact: true }).click();
      const responseReady = page.waitForResponse(response =>
        new URL(response.url()).pathname === endpoint + '/' + id
        && response.request().method() === 'DELETE');
      await page.locator('.arco-popconfirm:visible').last()
        .getByRole('button', { name: '确定', exact: true }).click();
      assert.equal((await responseReady).status(), 204, 'successful dictionary deletion contract');
      await row(name).waitFor({ state: 'detached' });
    };
    await record('三类未引用字典均可从界面删除', async () => {
      await deleteRow(edited.priority, priority.id);
      await chooseTab('model'); await deleteRow(edited.model, model.id);
      await chooseTab('vendor'); await deleteRow(edited.vendor, vendor.id);
    });

    const refVendor = await createDictionary(context, adminToken, 'ROBOT_VENDOR', '引用厂商-' + suffix);
    const refModel = await createDictionary(context, adminToken, 'ROBOT_MODEL', '引用型号-' + suffix, refVendor.id);
    const refPriority = await createDictionary(context, adminToken, 'PRIORITY', '引用优先级-' + suffix);
    const owners = await (await api(context, 'GET', '/project-owner-options', undefined, adminToken)).json();
    assert(owners.length > 0, 'dictionary reference fixture needs an owner');
    await api(context, 'POST', '/project-groups', {
      name: '字典引用项目-' + suffix,
      description: '证明被项目引用的字典不能删除',
      supplierId: f.suppliers.a.id,
      workOrderNos: ['DICT-' + suffix],
      machineModel: '字典引用机型',
      robotVendorId: refVendor.id,
      robotModelId: refModel.id,
      responsibleUserId: owners[0].id,
      priorityId: refPriority.id,
      expectedCompletionDate: '2099-12-31',
      subprojectNames: ['字典引用子项目-' + suffix],
    }, adminToken);
    await page.reload();
    await page.getByRole('columnheader', { name: '名称', exact: true }).waitFor();

    const refused = async (kind, item) => {
      await chooseTab(kind);
      await row(item.name).getByRole('button', { name: '删除', exact: true }).click();
      const responseReady = page.waitForResponse(response => new URL(response.url()).pathname === endpoint + '/' + item.id
        && response.request().method() === 'DELETE');
      await page.locator('.arco-popconfirm:visible').last().getByRole('button', { name: '确定', exact: true }).click();
      const response = await responseReady;
      assert.equal(response.status(), 409);
      assert.equal((await response.json()).message, '字典项已被项目引用，可停用但不能删除');
      await row(item.name).waitFor();
    };
    await record('项目引用的厂商型号优先级删除均被拒绝且行保留', async () => {
      await refused('vendor', refVendor);
      await refused('model', refModel);
      await refused('priority', refPriority);
    });

    await page.screenshot({ path: OUT + '/dictionaries.png', fullPage: true });
    await context.close();
  } catch (error) {
    if (page) {
      await page.screenshot({ path: OUT + '/dictionaries-failure.png', fullPage: true }).catch(() => {});
      console.log((await page.locator('body').innerText()).slice(-5000));
    }
    console.error(error.stack);
    process.exitCode = 1;
  } finally {
    if (browser) await browser.close();
  }
})();
