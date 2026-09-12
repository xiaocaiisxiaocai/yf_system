const { chromium } = require('playwright');
const crypto = require('node:crypto');
const { assert, OUT, s, f, record, login, api, action, track } = require(process.env.YF_BROWSER_SUPPORT_DIR + '/ui-lib.cjs');

(async () => {
  let browser, page;
  try {
    browser = await chromium.launch({ channel: 'chrome', headless: true });
    const context = await browser.newContext({ viewport: { width: 1440, height: 1000 } });
    page = await context.newPage();
    track(page, 'project-edges');
    const auth = await login(page, 'admin', s.adminPassword);
    await page.waitForURL(s.base + '/');
    const json = async (method, url, body) => (await api(context, method, url, body, auth.accessToken)).json();
    const prefix = '边界项目-' + crypto.randomBytes(5).toString('hex');
    const seeds = [];
    for (let i = 0; i < 12; i++) seeds.push(await json('POST', '/projects', {
      name: prefix + '-' + String(i).padStart(2, '0'), supplierId: i === 11 ? f.suppliers.b.id : f.suppliers.a.id,
    }));
    let flow = await json('POST', '/projects', { name: prefix + '-流程', supplierId: f.suppliers.a.id });
    const projects = '/api/v1/projects';
    const row = name => page.getByRole('row').filter({ has: page.getByRole('link', { name, exact: true }) });
    const pop = () => page.locator('.arco-popconfirm:visible').last().getByRole('button', { name: '确定', exact: true }).click();
    const listResponse = (parameters, status = 200) => page.waitForResponse(response => {
      const url = new URL(response.url());
      return url.pathname === projects && response.request().method() === 'GET' && response.status() === status
        && Object.entries(parameters).every(([key, value]) => (url.searchParams.get(key) || '') === String(value));
    });
    const listAction = async (parameters, interact) => {
      const [response] = await Promise.all([listResponse(parameters), Promise.resolve().then(interact)]);
      const data = await response.json();
      if (data.list.length) await page.getByRole('link', { name: data.list[0].name, exact: true }).waitFor();
      else await page.getByText('暂无数据', { exact: true }).waitFor();
      return data;
    };
    const search = text => listAction({ keyword: text, page: 1 }, async () => {
      await page.locator('.page-toolbar').getByPlaceholder('项目名称', { exact: true }).fill(text);
      await page.locator('.page-toolbar').getByPlaceholder('项目名称', { exact: true }).press('Enter');
    });
    await page.goto(s.base + '/projects');
    await page.getByRole('heading', { name: '项目协作', exact: true }).waitFor();

    await record('项目名称查询清除、状态与供应商筛选保持准确结果', async () => {
      const found = await search(prefix);
      assert.equal(found.total, 13);
      const toolbar = page.locator('.page-toolbar');
      const choose = async (position, label, parameters) => listAction(parameters, async () => {
        await toolbar.locator('.arco-select').nth(position).click();
        await page.getByRole('option', { name: label, exact: true }).click();
      });
      const state = await choose(0, '草稿', { keyword: prefix, status: 'DRAFT', page: 1 });
      assert.equal(state.total, 13); assert(state.list.every(x => x.status === 'DRAFT'));
      const supplier = await choose(1, f.suppliers.b.name, { supplierId: f.suppliers.b.id, status: 'DRAFT', page: 1 });
      assert.equal(supplier.total, 1); assert.equal(supplier.list[0].id, seeds[11].id);
      await page.reload(); await page.getByRole('heading', { name: '项目协作', exact: true }).waitFor();
      const empty = await search(prefix + '-不存在'); assert.equal(empty.total, 0);
      const cleared = await listAction({ keyword: '', page: 1 }, async () => {
        const input = page.locator('.page-toolbar').getByPlaceholder('项目名称', { exact: true });
        await input.hover();
        await page.locator('.page-toolbar .arco-input-search .arco-input-clear-icon').click();
        assert.equal(await input.inputValue(), '', 'clear search input');
      });
      assert(cleared.total >= 13);
      await search(prefix);
    });

    await record('项目分页和页大小返回真实行数', async () => {
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

    await record('项目列表失败可重试，供应商选项失败禁止创建并可恢复', async () => {
      page.expectedServerErrors = new Set([projects, '/api/v1/supplier-options']);
      await page.route('**/api/v1/projects?*', route => route.fulfill({ status: 503, contentType: 'application/json', body: '{"code":50301,"message":"temporary test failure"}' }), { times: 1 });
      await page.reload(); await page.getByText('加载失败', { exact: true }).waitFor();
      await listAction({}, () => page.getByRole('button', { name: '重试', exact: true }).click());
      await page.route('**/api/v1/supplier-options', route => route.fulfill({ status: 503, contentType: 'application/json', body: '{"code":50301,"message":"temporary test failure"}' }), { times: 1 });
      await page.reload(); await page.getByRole('button', { name: '重试加载供应商', exact: true }).waitFor();
      assert(await page.getByRole('button', { name: '新建项目', exact: true }).isDisabled());
      const restored = page.waitForResponse(response => new URL(response.url()).pathname === '/api/v1/supplier-options' && response.status() === 200);
      await page.getByRole('button', { name: '重试加载供应商', exact: true }).click(); await restored;
      await page.getByRole('button', { name: '新建项目', exact: true }).click();
      const dialog = page.getByRole('dialog'); await dialog.getByRole('button', { name: '创建项目', exact: true }).click();
      await dialog.getByText('请输入项目名称', { exact: true }).waitFor();
      await dialog.getByRole('button', { name: '取消', exact: true }).click(); await dialog.waitFor({ state: 'hidden' });
      page.expectedServerErrors.clear();
      await search(flow.name);
    });

    await record('项目编辑持久化、说明展开收起及页签URL刷新', async () => {
      await row(flow.name).getByRole('button', { name: '编辑', exact: true }).click();
      const dialog = page.getByRole('dialog');
      assert(await dialog.getByRole('combobox').isDisabled());
      const name = flow.name + '-已编辑'; const description = '这是需要完整显示的项目说明。'.repeat(30);
      await dialog.getByPlaceholder('项目名称', { exact: true }).fill(name);
      await dialog.getByPlaceholder('选填', { exact: true }).fill(description);
      await action(page, '/projects/' + flow.id, 'PUT', () => dialog.getByRole('button', { name: '保存修改', exact: true }).click());
      flow = await json('GET', '/projects/' + flow.id); assert.equal(flow.name, name); assert.equal(flow.description, description);
      assert.equal(flow.supplierId, f.suppliers.a.id);
      await page.goto(s.base + '/projects/' + flow.id);
      await page.getByRole('button', { name: '展开项目说明', exact: true }).click();
      await page.getByRole('button', { name: '收起项目说明', exact: true }).click();
      for (const [label, tab] of [['留言', 'messages'], ['项目动态', 'activity'], ['成员', 'members'], ['文件', 'files']]) {
        await page.getByRole('tab', { name: label, exact: true }).click();
        await page.waitForURL(url => url.searchParams.get('tab') === tab);
        await page.reload(); await page.getByRole('tab', { name: label, exact: true, selected: true }).waitFor();
      }
      await page.goto(s.base + '/projects/' + flow.id + '?tab=unknown');
      await page.getByRole('tab', { name: '文件', exact: true, selected: true }).waitFor();
    });

    await record('开始终止重新开始真实生效，终止状态禁止上传留言', async () => {
      await action(page, '/projects/' + flow.id + '/status', 'PUT', () => page.getByRole('button', { name: '开始', exact: true }).click());
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
      await action(page, '/projects/' + flow.id + '/status', 'PUT', () => page.getByRole('button', { name: '重新开始', exact: true }).click());
      assert.equal((await json('GET', '/projects/' + flow.id)).status, 'IN_PROGRESS');
      await terminate();
      await page.getByRole('button', { name: '返回项目列表', exact: true }).click();
      await search(flow.name); await row(flow.name).getByRole('button', { name: '删除', exact: true }).click();
      await action(page, '/projects/' + flow.id, 'DELETE', pop); await row(flow.name).waitFor({ state: 'detached' });
      await api(context, 'GET', '/projects/' + flow.id, undefined, auth.accessToken, 404);
    });

    await record('删除末页草稿后自动回退到有效页码', async () => {
      await search(prefix);
      const second = await listAction({ keyword: prefix, page: 2 }, () => page.locator('.arco-pagination-item').filter({ hasText: /^2$/ }).click());
      assert.equal(second.list.length, 2);
      for (const item of second.list) {
        await row(item.name).getByRole('button', { name: '删除', exact: true }).click();
        await action(page, '/projects/' + item.id, 'DELETE', pop);
        await row(item.name).waitFor({ state: 'detached' });
      }
      await page.locator('.arco-pagination-item-active').filter({ hasText: /^1$/ }).waitFor();
      const remaining = await json('GET', '/projects?keyword=' + encodeURIComponent(prefix) + '&page=1&pageSize=10');
      assert.equal(remaining.total, 10); assert.equal(remaining.list.length, 10);
      await page.getByRole('link', { name: remaining.list[0].name, exact: true }).waitFor();
    });

    await record('非法项目地址、详情读取失败和重试具有明确结果', async () => {
      const invalid = []; page.on('request', request => { if (new URL(request.url()).pathname === projects + '/NaN') invalid.push(request.url()); });
      await page.goto(s.base + '/projects/not-a-number'); await page.getByText('项目地址无效', { exact: true }).waitFor(); assert.equal(invalid.length, 0);
      const target = projects + '/' + seeds[10].id;
      page.expectedServerErrors = new Set([target]);
      await page.route('**' + target, route => route.fulfill({ status: 503, contentType: 'application/json', body: '{"code":50301,"message":"temporary test failure"}' }), { times: 1 });
      await page.goto(s.base + '/projects/' + seeds[10].id); await page.getByText('项目加载失败或没有访问权限', { exact: true }).waitFor();
      await page.getByRole('button', { name: '重试', exact: true }).click();
      await page.getByRole('button', { name: '返回项目列表', exact: true }).waitFor(); page.expectedServerErrors.clear();
    });

    await record('O14-O17 五种状态筛选准确，进行中项目编辑仍持久化且供应商固定', async () => {
      assert(f.users.a.uiFirstChanged, 'run users before project-edges');
      const statusPrefix = '状态筛选-' + crypto.randomBytes(5).toString('hex');
      const pdf = require('node:fs').readFileSync(OUT + '/valid-preview.pdf');
      const md5 = crypto.createHash('md5').update(pdf).digest('hex');
      const byStatus = {};
      for (const status of ['DRAFT', 'IN_PROGRESS', 'PENDING_CONFIRMATION', 'COMPLETED', 'TERMINATED']) {
        const item = await json('POST', '/projects', { name: statusPrefix + '-' + status, supplierId: f.suppliers.a.id });
        byStatus[status] = item;
        if (status === 'DRAFT') continue;
        await json('PUT', '/projects/' + item.id + '/status', { status: 'IN_PROGRESS' });
        if (status === 'TERMINATED') await json('PUT', '/projects/' + item.id + '/status', { status });
        if (status === 'PENDING_CONFIRMATION' || status === 'COMPLETED') {
          const upload = await json('POST', '/uploads/init', { projectId: item.id, fileName: '状态验证.pdf', fileSize: pdf.length, fileMd5: md5 });
          for (let offset = 0, index = 0; offset < pdf.length; offset += upload.chunkSize, index++)
            await api(context, 'PUT', '/uploads/' + upload.sessionId + '/chunks/' + index, pdf.subarray(offset, offset + upload.chunkSize), auth.accessToken);
          await json('POST', '/uploads/' + upload.sessionId + '/merge');
          await json('POST', '/projects/' + item.id + '/submit', { confirmSide: 'SUPPLIER' });
          if (status === 'COMPLETED') await api(context, 'POST', '/projects/' + item.id + '/confirm', undefined, f.users.a.token);
        }
      }
      await page.goto(s.base + '/projects'); await page.getByRole('heading', { name: '项目协作', exact: true }).waitFor();
      await search(statusPrefix);
      for (const [status, label] of [['DRAFT', '草稿'], ['PENDING_CONFIRMATION', '待确认'], ['COMPLETED', '已完成'], ['TERMINATED', '已终止'], ['IN_PROGRESS', '进行中']]) {
        const data = await listAction({ keyword: statusPrefix, status, page: 1 }, async () => {
          await page.locator('.page-toolbar .arco-select').first().click();
          await page.getByRole('option', { name: label, exact: true }).click();
        });
        assert.equal(data.total, 1); assert.equal(data.list[0].id, byStatus[status].id); assert.equal(data.list[0].status, status);
      }
      const active = byStatus.IN_PROGRESS;
      await row(active.name).getByRole('button', { name: '编辑', exact: true }).click();
      const dialog = page.getByRole('dialog'); assert(await dialog.getByRole('combobox').isDisabled());
      const edited = active.name + '-保存'; await dialog.getByPlaceholder('项目名称', { exact: true }).fill(edited);
      await action(page, '/projects/' + active.id, 'PUT', () => dialog.getByRole('button', { name: '保存修改', exact: true }).click());
      const persisted = await json('GET', '/projects/' + active.id); assert.equal(persisted.name, edited); assert.equal(persisted.status, 'IN_PROGRESS');
      assert.equal(persisted.supplierId, f.suppliers.a.id);
    });
    await page.screenshot({ path: OUT + '/project-edges.png', fullPage: true });
  } catch (error) {
    if (page) { await page.screenshot({ path: OUT + '/project-edges-failure.png', fullPage: true }).catch(() => {}); console.log((await page.locator('body').innerText()).slice(-5000)); }
    console.error(error.stack); process.exitCode = 1;
  } finally { if (browser) await browser.close(); }
})();
