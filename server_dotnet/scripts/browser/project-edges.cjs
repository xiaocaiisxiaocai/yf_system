const { chromium } = require('playwright');
const crypto = require('node:crypto');
const { assert, OUT, s, f, record, login, api, projectMetadata, action, track } = require(process.env.YF_BROWSER_SUPPORT_DIR + '/ui-lib.cjs');

async function loadProjectDefaults(context, token, supplierId) {
  return {
    workOrderNos: ['WO-' + crypto.randomBytes(4).toString('hex')],
    machineModel: '项目边界机型',
    ...await projectMetadata(context, token, supplierId),
  };
}

async function createGroup(context, token, supplierId, name, defaults) {
  const group = await (await api(context, 'POST', '/project-groups', {
    name,
    description: '主项目边界浏览器验收夹具',
    supplierId,
    ...defaults,
    workOrderNos: ['WO-' + crypto.randomBytes(4).toString('hex')],
    subprojectNames: [name + ' 子项目'],
  }, token)).json();
  const detail = await (await api(context, 'GET', '/project-groups/' + group.id, undefined, token)).json();
  assert.equal(detail.projects.length, 1, 'group fixture must contain one subproject');
  return { groupId: group.id, groupName: group.name, ...detail.projects[0] };
}

async function uploadFixture(context, token, projectId, name, bytes) {
  const fileMd5 = crypto.createHash('md5').update(bytes).digest('hex');
  const init = await (await api(context, 'POST', '/uploads/init', {
    projectId, fileName: name, fileSize: bytes.length, fileMd5,
  }, token)).json();
  for (let index = 0; index < init.totalChunks; index += 1) {
    await api(context, 'PUT', '/uploads/' + init.sessionId + '/chunks/' + index,
      bytes.subarray(index * init.chunkSize, Math.min((index + 1) * init.chunkSize, bytes.length)), token);
  }
  return (await api(context, 'POST', '/uploads/' + init.sessionId + '/merge', undefined, token)).json();
}

(async () => {
  let browser;
  let page;
  try {
    browser = await chromium.launch({ channel: 'chrome', headless: true });
    const context = await browser.newContext({ viewport: { width: 1440, height: 1000 } });
    page = await context.newPage();
    track(page, 'project-edges');
    const auth = await login(page, 'admin', s.adminPassword);
    await page.waitForURL(s.base + '/');
    const json = async (method, url, body, expected = 200) => (
      await api(context, method, url, body, auth.accessToken, expected)
    ).json();
    const defaults = await loadProjectDefaults(context, auth.accessToken, f.suppliers.a.id);
    const supplierBDefaults = await loadProjectDefaults(
      context, auth.accessToken, f.suppliers.b.id);
    const prefix = '边界主项目-' + crypto.randomBytes(5).toString('hex');
    const groups = [];
    for (let index = 0; index < 12; index += 1) {
      groups.push(await createGroup(
        context, auth.accessToken, index === 11 ? f.suppliers.b.id : f.suppliers.a.id,
        prefix + '-' + String(index).padStart(2, '0'),
        index === 11 ? supplierBDefaults : defaults));
    }
    let flow = await createGroup(context, auth.accessToken, f.suppliers.a.id, prefix + '-流程', defaults);
    const groupsPath = '/api/v1/project-groups';
    const pop = () => page.locator('.arco-popconfirm:visible').last()
      .getByRole('button', { name: '确定', exact: true }).click();
    const listResponse = (parameters, status = 200) => page.waitForResponse(response => {
      const url = new URL(response.url());
      return url.pathname === groupsPath && response.request().method() === 'GET'
        && response.status() === status
        && Object.entries(parameters).every(([key, value]) =>
          (url.searchParams.get(key) || '') === String(value));
    });
    const listAction = async (parameters, interact) => {
      const [response] = await Promise.all([listResponse(parameters), Promise.resolve().then(interact)]);
      const data = await response.json();
      if (data.list.length) await page.getByRole('link', { name: data.list[0].name, exact: true }).waitFor();
      else await page.getByText('暂无数据', { exact: true }).waitFor();
      return data;
    };
    const search = text => listAction({ keyword: text, page: 1 }, async () => {
      const input = page.locator('.page-toolbar').getByPlaceholder('主项目名称', { exact: true });
      await input.fill(text);
      await input.press('Enter');
    });
    await page.goto(s.base + '/projects');
    await page.getByRole('heading', { name: '项目协作', exact: true }).waitFor();

    await record('主项目名称清除、状态与 Robot 厂商筛选保持准确结果', async () => {
      const found = await search(prefix);
      assert.equal(found.total, 13);
      const toolbar = page.locator('.page-toolbar');
      const choose = async (position, label, parameters) => listAction(parameters, async () => {
        await toolbar.locator('.arco-select').nth(position).click();
        await page.getByRole('option', { name: label, exact: true }).click();
      });
      const state = await choose(0, '草稿', { keyword: prefix, status: 'DRAFT', page: 1 });
      assert.equal(state.total, 13); assert(state.list.every(item => item.status === 'DRAFT'));
      const supplier = await choose(1, f.suppliers.b.name, {
        keyword: prefix, supplierId: f.suppliers.b.id, status: 'DRAFT', page: 1,
      });
      assert.equal(supplier.total, 1); assert.equal(supplier.list[0].id, groups[11].groupId);
      await page.reload(); await page.getByRole('heading', { name: '项目协作', exact: true }).waitFor();
      const empty = await search(prefix + '-不存在'); assert.equal(empty.total, 0);
      const cleared = await listAction({ keyword: '', page: 1 }, async () => {
        const input = page.locator('.page-toolbar').getByPlaceholder('主项目名称', { exact: true });
        await input.hover();
        await page.locator('.page-toolbar .arco-input-search .arco-input-clear-icon').click();
        assert.equal(await input.inputValue(), '');
      });
      assert(cleared.total >= 13);
      await search(prefix);
    });

    await record('主项目分页和页大小返回真实行数', async () => {
      const second = await listAction({ keyword: prefix, page: 2, pageSize: 10 }, () =>
        page.locator('.arco-pagination-item').filter({ hasText: /^2$/ }).click());
      assert.equal(second.list.length, 3);
      const larger = await listAction({ keyword: prefix, pageSize: 20 }, async () => {
        await page.locator('.arco-pagination .arco-select').click();
        await page.getByRole('option', { name: '20 条/页', exact: true }).click();
      });
      assert.equal(larger.list.length, 13);
      const normal = await listAction({ keyword: prefix, pageSize: 10 }, async () => {
        await page.locator('.arco-pagination .arco-select').click();
        await page.getByRole('option', { name: '10 条/页', exact: true }).click();
      });
      assert.equal(normal.list.length, 10);
    });

    await record('主项目列表可重试，Robot 厂商失败可打开表单但禁止提交并保留草稿恢复', async () => {
      page.expectedServerErrors = new Set([groupsPath, '/api/v1/supplier-options']);
      const failGroups = route => route.fulfill({
        status: 503, contentType: 'application/json', body: '{"code":50301,"message":"temporary test failure"}',
      });
      await page.route('**/api/v1/project-groups?*', failGroups);
      await page.reload();
      await page.getByText('加载失败', { exact: true }).waitFor();
      await page.unroute('**/api/v1/project-groups?*', failGroups);
      await listAction({}, () => page.getByRole('button', { name: '重试', exact: true })
        .evaluate((button) => button.click()));
      const failSuppliers = route => route.fulfill({
        status: 503, contentType: 'application/json', body: '{"code":50301,"message":"temporary test failure"}',
      });
      await page.route('**/api/v1/supplier-options', failSuppliers);
      await page.reload();
      await page.getByRole('button', { name: '重试加载供应商', exact: true }).waitFor();
      assert(await page.getByRole('button', { name: '新建主项目', exact: true }).isEnabled());
      await page.getByRole('button', { name: '新建主项目', exact: true }).click();
      const dialog = page.getByRole('dialog');
      await dialog.getByText('Robot 厂商选项加载失败，请刷新基础数据后重试。', { exact: true }).waitFor();
      assert(await dialog.getByRole('button', { name: '创建主项目', exact: true }).isDisabled());
      await dialog.getByRole('textbox', { name: '主项目名称', exact: true }).fill('恢复后应保留的草稿');
      await page.unroute('**/api/v1/supplier-options', failSuppliers);
      const restored = page.waitForResponse(response => new URL(response.url()).pathname === '/api/v1/supplier-options' && response.status() === 200);
      await dialog.getByRole('button', { name: '刷新基础数据', exact: true }).click(); await restored;
      await page.waitForFunction(() => [...document.querySelectorAll('.arco-modal button')].some(button => button.textContent.trim() === '创建主项目' && !button.disabled));
      assert.equal(await dialog.getByRole('textbox', { name: '主项目名称', exact: true }).inputValue(), '恢复后应保留的草稿');
      await dialog.getByRole('button', { name: '创建主项目', exact: true }).click();
      await dialog.getByText('请至少创建一个子项目', { exact: true }).waitFor();
      await dialog.getByRole('button', { name: '取消', exact: true }).click();
      await dialog.waitFor({ state: 'hidden' });
      page.expectedServerErrors.clear();
      await search(flow.groupName);
    });

    await record('空供应商明确说明创建条件，维护入口和刷新恢复保留已填内容', async () => {
      const empty = route => route.fulfill({ status: 200, contentType: 'application/json', body: '[]' });
      await page.route('**/api/v1/supplier-options', empty);
      await page.reload();
      await page.getByRole('button', { name: '新建主项目', exact: true }).click();
      const dialog = page.getByRole('dialog');
      await dialog.getByText('暂无启用的供应商，请先新增或启用供应商。', { exact: false }).waitFor();
      assert(await dialog.getByRole('button', { name: '创建主项目', exact: true }).isDisabled());
      const link = dialog.getByRole('link', { name: '维护供应商（新窗口）', exact: true });
      assert.equal(await link.getAttribute('href'), '/suppliers');
      assert.equal(await link.getAttribute('target'), '_blank');
      await dialog.getByRole('textbox', { name: '主项目名称', exact: true }).fill('空供应商恢复草稿');
      await page.screenshot({ path: OUT + '/project-empty-supplier.png' });
      await page.unroute('**/api/v1/supplier-options', empty);
      await dialog.getByRole('button', { name: '刷新基础数据', exact: true }).click();
      await dialog.getByText('暂无启用的供应商，请先新增或启用供应商。', { exact: false }).waitFor({ state: 'hidden' });
      await page.waitForFunction(() => [...document.querySelectorAll('.arco-modal button')].some(button => button.textContent.trim() === '创建主项目' && !button.disabled));
      assert.equal(await dialog.getByRole('textbox', { name: '主项目名称', exact: true }).inputValue(), '空供应商恢复草稿');
      await dialog.getByRole('button', { name: '取消', exact: true }).click();
      await dialog.waitFor({ state: 'hidden' });
    });

    await record('缺少 Robot 料号和优先级时展示维护指引且刷新不清空表单', async () => {
      const empty = route => route.fulfill({ status: 200, contentType: 'application/json', body: '[]' });
      await page.route('**/api/v1/project-dictionaries?*', empty);
      await page.route('**/api/v1/robot-parts?*', empty);
      await page.getByRole('button', { name: '新建主项目', exact: true }).click();
      const dialog = page.getByRole('dialog');
      // Robot parts are loaded per supplier, so the missing-part guidance only appears after one is chosen.
      await dialog.getByPlaceholder('选择 Robot 厂商', { exact: true }).click();
      await page.getByRole('option', { name: f.suppliers.a.name, exact: true }).click();
      await dialog.getByText('所选 Robot 厂商暂无启用的料号', { exact: false }).waitFor();
      await dialog.getByText('暂无启用的优先级', { exact: false }).waitFor();
      assert(await dialog.getByRole('button', { name: '创建主项目', exact: true }).isDisabled());
      assert.equal(await dialog.getByRole('link', { name: '维护 Robot 料号（新窗口）', exact: true }).getAttribute('href'), '/system/dictionaries');
      await dialog.getByRole('textbox', { name: '主项目名称', exact: true }).fill('字典恢复草稿');
      await page.screenshot({ path: OUT + '/project-empty-metadata.png' });
      await page.unroute('**/api/v1/project-dictionaries?*', empty);
      await page.unroute('**/api/v1/robot-parts?*', empty);
      await dialog.getByRole('button', { name: '刷新基础数据', exact: true }).click();
      await dialog.getByText('创建前请补齐基础数据', { exact: true }).waitFor({ state: 'hidden' });
      await page.waitForFunction(() => [...document.querySelectorAll('.arco-modal button')].some(button => button.textContent.trim() === '创建主项目' && !button.disabled));
      assert.equal(await dialog.getByRole('textbox', { name: '主项目名称', exact: true }).inputValue(), '字典恢复草稿');
      await dialog.getByRole('button', { name: '取消', exact: true }).click();
      await dialog.waitFor({ state: 'hidden' });
      await search(flow.groupName);
    });

    await record('子项目详情页签通过URL持久化且主项目名称可更新', async () => {
      const detail = await json('GET', '/project-groups/' + flow.groupId);
      const updatedName = flow.groupName + '-已编辑';
      await json('PUT', '/project-groups/' + flow.groupId, {
        name: updatedName,
        description: detail.group.description,
        supplierId: detail.group.supplierId,
        workOrderNos: detail.group.workOrderNos,
        machineModel: detail.group.machineModel,
        robotPartId: detail.group.robotPartId,
        priorityId: detail.group.priorityId,
        expectedCompletionDate: detail.group.expectedCompletionDate,
      });
      const updated = await json('GET', '/project-groups/' + flow.groupId);
      assert.equal(updated.group.name, updatedName);
      flow.groupName = updatedName;
      await page.goto(s.base + '/project-groups/' + flow.groupId);
      await page.getByText(updatedName, { exact: true }).waitFor();
      await page.getByRole('button', { name: flow.name, exact: true }).click();
      for (const [label, tab] of [['留言', 'messages'], ['项目动态', 'activity'], ['文件', 'files']]) {
        await page.getByRole('tab', { name: label, exact: true }).click();
        await page.waitForURL(url => url.searchParams.get('tab') === tab);
        await page.reload(); await page.getByRole('tab', { name: label, exact: true, selected: true }).waitFor();
      }
      await page.goto(s.base + '/projects/' + flow.id + '?tab=unknown');
      await page.getByRole('tab', { name: '文件', exact: true, selected: true }).waitFor();
    });

    await record('子项目开始终止重新开始真实生效，终止状态禁止上传留言', async () => {
      await action(page, '/projects/' + flow.id + '/status', 'PUT', () =>
        page.getByRole('button', { name: '开始', exact: true }).click());
      assert.equal((await json('GET', '/projects/' + flow.id)).status, 'IN_PROGRESS');
      const terminate = async () => {
        await page.getByRole('button', { name: '终止', exact: true }).click();
        await action(page, '/projects/' + flow.id + '/status', 'PUT', pop);
        await page.getByRole('button', { name: '重新开始', exact: true }).waitFor();
        assert.equal((await json('GET', '/projects/' + flow.id)).status, 'TERMINATED');
      };
      await terminate(); assert.equal(await page.getByRole('button', { name: '上传文件', exact: true }).count(), 0);
      await page.getByRole('tab', { name: '留言', exact: true }).click();
      assert.equal(await page.getByRole('button', { name: '发送', exact: true }).count(), 0);
      await action(page, '/projects/' + flow.id + '/status', 'PUT', () =>
        page.getByRole('button', { name: '重新开始', exact: true }).click());
      assert.equal((await json('GET', '/projects/' + flow.id)).status, 'IN_PROGRESS');
      await terminate();
      await page.getByRole('button', { name: '返回主项目', exact: true }).click();
      await json('DELETE', '/projects/' + flow.id);
      await json('DELETE', '/project-groups/' + flow.groupId);
      await page.goto(s.base + '/projects');
      await page.getByRole('heading', { name: '项目协作', exact: true }).waitFor();
      await search(flow.groupName);
      assert.equal((await json('GET', '/project-groups?keyword=' + encodeURIComponent(flow.groupName))).total, 0);
    });

    await record('删除末页空主项目后自动回退到有效页码', async () => {
      await search(prefix);
      const second = await listAction({ keyword: prefix, page: 2 }, () =>
        page.locator('.arco-pagination-item').filter({ hasText: /^2$/ }).click());
      assert.equal(second.list.length, 2);
      const toDelete = groups.slice(10, 12);
      for (const item of toDelete) {
        await json('DELETE', '/projects/' + item.id);
        await json('DELETE', '/project-groups/' + item.groupId);
      }
      await page.reload();
      await page.getByRole('heading', { name: '项目协作', exact: true }).waitFor();
      await search(prefix);
      const remaining = await json('GET', '/project-groups?keyword=' + encodeURIComponent(prefix) + '&page=1&pageSize=10');
      assert.equal(remaining.total, 10); assert.equal(remaining.list.length, 10);
      await page.getByRole('link', { name: remaining.list[0].name, exact: true }).waitFor();
    });

    await record('非法主项目地址、详情读取失败和重试具有明确结果', async () => {
      const invalid = [];
      page.on('request', request => {
        if (new URL(request.url()).pathname === groupsPath + '/NaN') invalid.push(request.url());
      });
      await page.goto(s.base + '/project-groups/not-a-number');
      await page.getByText('主项目地址无效', { exact: true }).waitFor(); assert.equal(invalid.length, 0);
      const target = groupsPath + '/' + groups[9].groupId;
      page.expectedServerErrors = new Set([target]);
      const failDetail = route => route.fulfill({
        status: 503, contentType: 'application/json', body: '{"code":50301,"message":"temporary test failure"}',
      });
      await page.route('**' + target, failDetail);
      await page.goto(s.base + '/project-groups/' + groups[9].groupId);
      await page.getByText('主项目加载失败或没有访问权限', { exact: true }).waitFor();
      await page.unroute('**' + target, failDetail);
      await page.getByRole('button', { name: '重试', exact: true })
        .evaluate((button) => button.click());
      await page.getByText(groups[9].groupName, { exact: true }).waitFor();
      page.expectedServerErrors.clear();
    });

    await record('五种子项目状态汇总为四种主项目状态，供应商提交且内部确认', async () => {
      assert(f.users.a.uiFirstChanged, 'run users before project-edges');
      const statusPrefix = '状态主项目-' + crypto.randomBytes(5).toString('hex');
      const pdf = require('node:fs').readFileSync(OUT + '/valid-preview.pdf');
      const statuses = {};
      for (const status of ['DRAFT', 'IN_PROGRESS', 'PENDING_CONFIRMATION', 'COMPLETED', 'TERMINATED']) {
        const item = await createGroup(context, auth.accessToken, f.suppliers.a.id, statusPrefix + '-' + status, defaults);
        statuses[status] = item;
        if (status === 'DRAFT') continue;
        await json('PUT', '/projects/' + item.id + '/status', { status: 'IN_PROGRESS' });
        if (status === 'TERMINATED') {
          await json('PUT', '/projects/' + item.id + '/status', { status });
          continue;
        }
        if (status === 'PENDING_CONFIRMATION' || status === 'COMPLETED') {
          const upload = await uploadFixture(context, f.users.a.token, item.id, '状态验证.pdf', pdf);
          assert(upload.id || upload.fileId, 'status fixture file merge');
          const submission = await (await api(
            context, 'POST', '/projects/' + item.id + '/submit', {}, f.users.a.token)).json();
          assert(Number.isInteger(submission.latestSubmissionId) && submission.latestSubmissionId > 0);
          if (status === 'COMPLETED') await json('POST', '/projects/' + item.id + '/confirm', {
            expectedSubmissionId: submission.latestSubmissionId,
          });
        }
      }
      await page.goto(s.base + '/projects');
      await page.getByRole('heading', { name: '项目协作', exact: true }).waitFor();
      await search(statusPrefix);
      const groupedStatuses = [
        ['DRAFT', '草稿', [statuses.DRAFT.groupId]],
        ['IN_PROGRESS', '进行中', [statuses.IN_PROGRESS.groupId, statuses.PENDING_CONFIRMATION.groupId]],
        ['COMPLETED', '已完成', [statuses.COMPLETED.groupId]],
        ['TERMINATED', '已终止', [statuses.TERMINATED.groupId]],
      ];
      for (const [status, label, expectedIds] of groupedStatuses) {
        const data = await listAction({ keyword: statusPrefix, status, page: 1 }, async () => {
          await page.locator('.page-toolbar .arco-select').first().click();
          await page.getByRole('option', { name: label, exact: true }).click();
        });
        assert.equal(data.total, expectedIds.length);
        assert.deepEqual(new Set(data.list.map(item => item.id)), new Set(expectedIds));
        assert(data.list.every(item => item.status === status));
      }
      const pendingGroup = await json('GET', '/project-groups/' + statuses.PENDING_CONFIRMATION.groupId);
      assert.equal(pendingGroup.group.status, 'IN_PROGRESS');
      assert.equal(pendingGroup.group.pendingCount, 1);
      assert.equal(pendingGroup.projects[0].status, 'PENDING_CONFIRMATION');
      const active = statuses.IN_PROGRESS;
      await json('PUT', '/project-groups/' + active.groupId, {
        name: active.groupName + '-保存',
        description: '主项目更新后说明',
        supplierId: f.suppliers.a.id,
        ...defaults,
        workOrderNos: active.workOrderNos,
      });
      const persisted = await json('GET', '/project-groups/' + active.groupId);
      assert.equal(persisted.group.status, 'IN_PROGRESS');
      assert.equal(persisted.group.supplierId, f.suppliers.a.id);
    });
    await page.screenshot({ path: OUT + '/project-edges.png', fullPage: true });
  } catch (error) {
    if (page) {
      await page.screenshot({ path: OUT + '/project-edges-failure.png', fullPage: true }).catch(() => {});
      console.log((await page.locator('body').innerText()).slice(-5000));
    }
    console.error(error.stack || error.message);
    process.exitCode = 1;
  } finally {
    if (browser) await browser.close();
  }
})();
