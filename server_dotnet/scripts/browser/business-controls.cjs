// DRAFT ONLY: untracked browser-acceptance supplement.
// Prerequisites/order: auth -> fixtures -> users -> this script.
// The main runner owns integration, execution and cleanup. This draft does not mutate f.uiProjects.a/b.
const { chromium } = require('playwright');
const crypto = require('node:crypto');
const {
  assert, OUT, s, f, record, login, api, action, track,
} = require(process.env.YF_BROWSER_SUPPORT_DIR + '/ui-lib.cjs');

const apiPath = value => new URL(value.url()).pathname;

async function uploadBytes(context, token, projectId, name, bytes) {
  const fileMd5 = crypto.createHash('md5').update(bytes).digest('hex');
  const initialized = await (await api(context, 'POST', '/uploads/init', {
    projectId, fileName: name, fileSize: bytes.length, fileMd5,
  }, token)).json();
  for (let index = 0; index < initialized.totalChunks; index += 1) {
    const start = index * initialized.chunkSize;
    const chunk = bytes.subarray(start, Math.min(start + initialized.chunkSize, bytes.length));
    await api(context, 'PUT', '/uploads/' + initialized.sessionId + '/chunks/' + index, chunk, token);
  }
  return (await api(context, 'POST', '/uploads/' + initialized.sessionId + '/merge', undefined, token)).json();
}

async function choose(page, placeholder, optionName) {
  await page.locator('.arco-select').filter({ has: page.getByPlaceholder(placeholder, { exact: true }) }).click();
  await page.getByRole('option', { name: optionName, exact: true }).click();
}

(async () => {
  let browser;
  let page;
  try {
    assert(f.users?.manager?.uiFirstChanged && f.users?.manager?.changedPassword,
      'run users before business-controls: project manager must have completed first password change');
    assert(f.users?.a?.uiFirstChanged && f.users?.a?.token,
      'run users before business-controls: supplier A needs a current token');
    assert(f.suppliers?.a?.id, 'fixtures must contain supplier A');

    browser = await chromium.launch({ channel: 'chrome', headless: true });
    const adminContext = await browser.newContext({ viewport: { width: 1440, height: 1000 } });
    page = await adminContext.newPage();
    track(page, 'business-controls-admin');
    const admin = await login(page, 'admin', s.adminPassword);
    await page.waitForURL(s.base + '/');
    const adminToken = admin.accessToken;
    const marker = crypto.randomBytes(5).toString('hex');
    const prefix = '业务控件补验-' + marker;
    const tinyFile = Buffer.from('browser-dashboard-fixture|' + marker);
    const pendingProjects = [];
    const messageText = '工作台未读键盘跳转-' + marker;
    let managerName;

    // API-only fixture setup: 11 pending projects make the dashboard paginator real.
    // Every project gets a valid available file because submit requires one.
    for (let index = 0; index < 11; index += 1) {
      const project = await (await api(adminContext, 'POST', '/projects', {
        name: prefix + '-' + String(index + 1).padStart(2, '0'),
        description: '独立工作台、权限和控件验收夹具',
        supplierId: f.suppliers.a.id,
      }, adminToken)).json();
      await api(adminContext, 'PUT', '/projects/' + project.id + '/status', { status: 'IN_PROGRESS' }, adminToken);
      await uploadBytes(adminContext, adminToken, project.id, prefix + '-' + index + '.zip', tinyFile);
      if (index === 0) {
        await api(adminContext, 'POST', '/projects/' + project.id + '/messages', {
          content: messageText,
        }, f.users.a.token);
      }
      const submitted = await (await api(adminContext, 'POST', '/projects/' + project.id + '/submit', {
        confirmSide: 'COMPANY',
      }, f.users.a.token)).json();
      assert(Number.isInteger(submitted.latestSubmissionId) && submitted.latestSubmissionId > 0);
      project.latestSubmissionId = submitted.latestSubmissionId;
      pendingProjects.push(project);
    }

    await record('项目管理员可见非本人非成员项目且无系统管理菜单', async () => {
      const managerContext = await browser.newContext({ viewport: { width: 1440, height: 1000 } });
      const managerPage = await managerContext.newPage();
      track(managerPage, 'business-controls-manager');
      try {
        const manager = await login(managerPage, f.users.manager.username, f.users.manager.changedPassword);
        managerName = manager.user.realName;
        assert.equal(typeof managerName, 'string');
        await managerPage.waitForURL(s.base + '/');
        assert(manager.permissions.includes('project:view_all'), 'project manager fixture must have project:view_all');
        for (const label of ['供应商管理', '用户管理', '组织架构', '角色权限', '操作日志', '系统参数']) {
          assert.equal(await managerPage.getByRole('menuitem', { name: label, exact: true }).count(), 0,
            'project manager must not expose system menu ' + label);
        }
        const listed = managerPage.waitForResponse(response => {
          const url = new URL(response.url());
          return url.pathname === '/api/v1/projects' && url.searchParams.get('keyword') === pendingProjects[0].name
            && response.status() === 200;
        });
        await managerPage.goto(s.base + '/projects');
        const search = managerPage.getByPlaceholder('项目名称', { exact: true });
        await search.fill(pendingProjects[0].name);
        await search.press('Enter');
        const body = await (await listed).json();
        assert(body.list.some(item => item.id === pendingProjects[0].id),
          'project:view_all must include a non-member project in the server result');
        await managerPage.getByRole('link', { name: pendingProjects[0].name, exact: true }).click();
        await managerPage.waitForURL(s.base + '/projects/' + pendingProjects[0].id);
        await managerPage.getByText(pendingProjects[0].name, { exact: true }).waitFor();
      } finally {
        await managerContext.close();
      }
    });

    await record('工作台填充统计和待确认分页与服务端结果一致', async () => {
      const summaryPromise = page.waitForResponse(response => apiPath(response) === '/api/v1/dashboard/summary'
        && response.status() === 200);
      const pendingFirstPromise = page.waitForResponse(response => {
        const url = new URL(response.url());
        return url.pathname === '/api/v1/dashboard/pending-projects'
          && url.searchParams.get('page') === '1' && url.searchParams.get('pageSize') === '10'
          && response.status() === 200;
      });
      await page.goto(s.base + '/');
      const summary = await (await summaryPromise).json();
      const pendingFirst = await (await pendingFirstPromise).json();
      assert(summary.pendingConfirmations >= 11);
      assert(summary.unreadMessages >= 1);
      for (const [title, value] of [['待内部验收项目', summary.pendingConfirmations], ['未读留言', summary.unreadMessages]]) {
        const card = page.locator('.dashboard-stat-card').filter({ hasText: title });
        await card.getByText(String(value), { exact: true }).waitFor();
      }
      assert.equal(pendingFirst.list.length, 10);
      for (const project of pendingFirst.list) {
        await page.getByRole('link', { name: project.name, exact: true }).waitFor();
      }
      const pendingSecondPromise = page.waitForResponse(response => {
        const url = new URL(response.url());
        return url.pathname === '/api/v1/dashboard/pending-projects'
          && url.searchParams.get('page') === '2' && url.searchParams.get('pageSize') === '10'
          && response.status() === 200;
      });
      await page.locator('.dashboard-pending .arco-pagination-item').filter({ hasText: /^2$/ }).click();
      const pendingSecond = await (await pendingSecondPromise).json();
      assert(pendingSecond.list.length >= 1);
      for (const project of pendingSecond.list) {
        await page.getByRole('link', { name: project.name, exact: true }).waitFor();
      }
    });

    await record('工作台待确认链接和未读留言键盘操作进入真实目标', async () => {
      const currentPending = await (await api(adminContext, 'GET',
        '/dashboard/pending-projects?page=2&pageSize=10', undefined, adminToken)).json();
      const target = currentPending.list.find(item => pendingProjects.some(project => project.id === item.id));
      assert(target, 'page 2 must contain a dedicated pending fixture');
      await page.getByRole('link', { name: target.name, exact: true }).click();
      await page.waitForURL(s.base + '/projects/' + target.id);
      await page.getByRole('button', { name: '返回项目列表', exact: true }).waitFor();

      await page.goto(s.base + '/');
      const message = page.locator('.dashboard-message-item').filter({ hasText: messageText });
      await message.waitFor();
      await message.locator('[aria-label="未读"]').waitFor();
      await message.focus();
      await page.keyboard.press('Enter');
      await page.waitForURL(url => url.pathname === '/projects/' + pendingProjects[0].id
        && url.searchParams.get('tab') === 'messages');
      await page.getByText(messageText, { exact: true }).waitFor();
    });

    const flowProject = pendingProjects[0];
    const rejectPath = '/api/v1/projects/' + flowProject.id + '/reject';
    await record('驳回必填校验和取消草稿不产生写入', async () => {
      let rejectWrites = 0;
      const countReject = request => {
        if (apiPath(request) === rejectPath && request.method() === 'POST') rejectWrites += 1;
      };
      page.on('request', countReject);
      await page.goto(s.base + '/projects/' + flowProject.id);
      await page.getByRole('button', { name: '验收驳回', exact: true }).click();
      let dialog = page.getByRole('dialog');
      await dialog.getByRole('button', { name: '确认驳回', exact: true }).click();
      await dialog.getByText('请填写驳回原因', { exact: true }).waitFor();
      assert.equal(rejectWrites, 0);
      await dialog.getByPlaceholder('请填写驳回原因', { exact: true }).fill('应在取消后丢弃');
      await dialog.getByRole('button', { name: '取消', exact: true }).click();
      await dialog.waitFor({ state: 'hidden' });
      await page.getByRole('button', { name: '验收驳回', exact: true }).click();
      dialog = page.getByRole('dialog');
      assert.equal(await dialog.getByPlaceholder('请填写驳回原因', { exact: true }).inputValue(), '');
      await dialog.getByRole('button', { name: '取消', exact: true }).click();
      assert.equal(rejectWrites, 0);
      page.off('request', countReject);
    });

    await record('浏览器保留旧提交版本时驳回409且不会误验收新提交', async () => {
      await page.goto(s.base + '/projects/' + flowProject.id);
      await page.getByRole('button', { name: '验收驳回', exact: true }).waitFor();
      const staleSubmissionId = flowProject.latestSubmissionId;
      const staleDetail = await (await api(
        adminContext, 'GET', '/projects/' + flowProject.id, undefined, adminToken)).json();
      const detailPath = '/api/v1/projects/' + flowProject.id;
      const keepStaleDetail = route => route.request().method() === 'GET'
        ? route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(staleDetail) })
        : route.continue();
      await page.route('**' + detailPath, keepStaleDetail);
      let resubmitted;
      try {
        await api(adminContext, 'POST', '/projects/' + flowProject.id + '/withdraw', {
          expectedSubmissionId: staleSubmissionId,
        }, adminToken);
        resubmitted = await (await api(adminContext, 'POST', '/projects/' + flowProject.id + '/submit', {
          confirmSide: 'COMPANY',
        }, f.users.a.token)).json();
        assert(resubmitted.latestSubmissionId > staleSubmissionId);
        await page.getByRole('button', { name: '验收驳回', exact: true }).click();
        const dialog = page.getByRole('dialog');
        await dialog.getByPlaceholder('请填写驳回原因', { exact: true }).fill('浏览器陈旧版本不得生效');
        const conflict = page.waitForResponse(response => apiPath(response) === rejectPath
          && response.request().method() === 'POST' && response.status() === 409);
        await dialog.getByRole('button', { name: '确认驳回', exact: true }).click();
        const response = await conflict;
        assert.equal(response.request().postDataJSON().expectedSubmissionId, staleSubmissionId);
      } finally {
        await page.unroute('**' + detailPath, keepStaleDetail);
      }
      const persisted = await (await api(
        adminContext, 'GET', '/projects/' + flowProject.id, undefined, adminToken)).json();
      assert.equal(persisted.status, 'PENDING_CONFIRMATION');
      assert.equal(persisted.latestSubmissionId, resubmitted.latestSubmissionId);
      flowProject.latestSubmissionId = resubmitted.latestSubmissionId;
      await page.reload();
      await page.getByRole('button', { name: '验收驳回', exact: true }).waitFor();
    });

    await record('驳回503保留草稿并可单次重试成功', async () => {
      const reason = '驳回失败后保留并重试-' + marker;
      page.expectedServerErrors = new Set([rejectPath]);
      await page.route('**' + rejectPath, route => route.fulfill({
        status: 503,
        contentType: 'application/json',
        body: JSON.stringify({ code: 50301, message: '驳回测试暂时不可用' }),
      }), { times: 1 });
      await page.getByRole('button', { name: '验收驳回', exact: true }).click();
      const dialog = page.getByRole('dialog');
      const reasonInput = dialog.getByPlaceholder('请填写驳回原因', { exact: true });
      await reasonInput.fill(reason);
      const failed = page.waitForResponse(response => apiPath(response) === rejectPath && response.status() === 503);
      await dialog.getByRole('button', { name: '确认驳回', exact: true }).click();
      await failed;
      assert.equal(await dialog.isVisible(), true);
      assert.equal(await reasonInput.inputValue(), reason);
      const rejected = await action(page, '/projects/' + flowProject.id + '/reject', 'POST', () => (
        dialog.getByRole('button', { name: '确认驳回', exact: true }).click()
      ));
      assert.equal(rejected.status, 'IN_PROGRESS');
      const persisted = await (await api(adminContext, 'GET', '/projects/' + flowProject.id, undefined, adminToken)).json();
      assert.equal(persisted.status, 'IN_PROGRESS');
      assert.equal(persisted.rejectReason, reason);
      page.expectedServerErrors.clear();
    });

    const memberProject = await (await api(adminContext, 'POST', '/projects', {
      name: prefix + '-成员保存', supplierId: f.suppliers.a.id,
    }, adminToken)).json();
    await api(adminContext, 'PUT', '/projects/' + memberProject.id + '/status', { status: 'IN_PROGRESS' }, adminToken);
    const memberPath = '/api/v1/projects/' + memberProject.id + '/members';

    await record('成员搜索清空和无匹配状态均可操作', async () => {
      await page.goto(s.base + '/projects/' + memberProject.id + '?tab=members');
      await page.getByRole('button', { name: '设置公司成员', exact: true }).click();
      const dialog = page.locator('.member-picker-dialog:visible');
      const search = dialog.getByPlaceholder('搜索姓名、工号或部门', { exact: true });
      await search.fill(f.users.manager.username);
      await dialog.getByText(managerName, { exact: true }).waitFor();
      assert.equal(await dialog.locator('.member-picker-option').count(), 1);
      await search.hover();
      await dialog.locator('.member-picker-search .arco-input-clear-icon').click();
      assert.equal(await search.inputValue(), '');
      assert(await dialog.locator('.member-picker-option').count() > 1);
      await search.fill('不存在成员-' + marker);
      await dialog.getByText('没有匹配的公司成员', { exact: true }).waitFor();
      await search.fill('');
      await dialog.getByText(managerName, { exact: true }).waitFor();
    });

    await record('成员保存503保留草稿并重试持久化', async () => {
      const dialog = page.locator('.member-picker-dialog:visible');
      const managerOption = dialog.locator('.member-picker-option').filter({ hasText: managerName });
      const managerCheckbox = managerOption.getByRole('checkbox');
      assert.equal(await managerCheckbox.isChecked(), false);
      await managerOption.click();
      assert.equal(await managerCheckbox.isChecked(), true);
      page.expectedServerErrors = new Set([memberPath]);
      await page.route('**' + memberPath, route => route.fulfill({
        status: 503,
        contentType: 'application/json',
        body: JSON.stringify({ code: 50301, message: '成员保存测试暂时不可用' }),
      }), { times: 1 });
      const failed = page.waitForResponse(response => apiPath(response) === memberPath && response.status() === 503);
      await dialog.getByRole('button', { name: '保存成员', exact: true }).click();
      await failed;
      assert.equal(await dialog.isVisible(), true);
      assert.equal(await managerCheckbox.isChecked(), true);
      await action(page, '/projects/' + memberProject.id + '/members', 'PUT', () => (
        dialog.getByRole('button', { name: '保存成员', exact: true }).click()
      ));
      await dialog.waitFor({ state: 'hidden' });
      const members = await (await api(adminContext, 'GET', '/projects/' + memberProject.id + '/members', undefined, adminToken)).json();
      assert(members.some(item => item.userId === f.users.manager.id));
      assert(members.some(item => item.userId === admin.user.id), 'member save must retain the operator');
      page.expectedServerErrors.clear();
    });

    await record('内置角色名称禁止编辑而说明草稿可取消', async () => {
      await page.goto(s.base + '/rbac/roles');
      const roles = await (await api(adminContext, 'GET', '/admin/roles', undefined, adminToken)).json();
      const builtIn = roles.list.find(item => item.isBuiltIn && item.name === '供应商人员')
        || roles.list.find(item => item.isBuiltIn);
      assert(builtIn, 'at least one built-in role must exist');
      const row = page.getByRole('row').filter({ hasText: builtIn.name });
      await row.getByRole('button', { name: '编辑', exact: true }).click();
      let dialog = page.getByRole('dialog');
      const name = dialog.getByPlaceholder('角色名称', { exact: true });
      const description = dialog.getByPlaceholder('选填', { exact: true });
      assert.equal(await name.isDisabled(), true);
      const originalDescription = await description.inputValue();
      await description.fill('取消后不得保存-' + marker);
      await dialog.getByRole('button', { name: '取消', exact: true }).click();
      await dialog.waitFor({ state: 'hidden' });
      await row.getByRole('button', { name: '编辑', exact: true }).click();
      dialog = page.getByRole('dialog');
      assert.equal(await dialog.getByPlaceholder('选填', { exact: true }).inputValue(), originalDescription);
      await dialog.getByRole('button', { name: '取消', exact: true }).click();
      const after = await (await api(adminContext, 'GET', '/admin/roles', undefined, adminToken)).json();
      assert.equal(after.list.find(item => item.id === builtIn.id).description, builtIn.description);
    });

    await record('审计具体动作对象日期组合筛选匹配服务端结果', async () => {
      await page.goto(s.base + '/logs');
      await page.getByRole('button', { name: '查询', exact: true }).waitFor();
      await choose(page, '具体操作', '提交验收（PROJECT_SUBMIT）');
      await choose(page, '对象类型', '项目');
      const rangeInputs = page.locator('.audit-filter-panel .arco-picker input');
      assert.equal(await rangeInputs.count(), 2);
      await rangeInputs.nth(0).click();
      await page.locator('.arco-picker-range-container:visible').waitFor();
      await rangeInputs.nth(0).fill('2000-01-01 00:00:00');
      await rangeInputs.nth(0).press('Enter');
      await rangeInputs.nth(1).fill('2099-12-31 23:59:59');
      await rangeInputs.nth(1).press('Enter');
      assert.equal(await rangeInputs.nth(0).inputValue(), '2000-01-01 00:00:00');
      assert.equal(await rangeInputs.nth(1).inputValue(), '2099-12-31 23:59:59');
      const filtered = page.waitForResponse(response => {
        const url = new URL(response.url());
        return url.pathname === '/api/v1/admin/audit-logs'
          && url.searchParams.get('action') === 'PROJECT_SUBMIT'
          && url.searchParams.get('targetType') === 'project'
          && !!url.searchParams.get('start') && !!url.searchParams.get('end')
          && response.status() === 200;
      });
      await page.getByRole('button', { name: '查询', exact: true }).click();
      const result = await (await filtered).json();
      assert(result.total >= 11);
      assert(result.list.every(item => item.action === 'PROJECT_SUBMIT' && item.targetType === 'project'));
      await page.getByText('PROJECT_SUBMIT', { exact: true }).first().waitFor();
    });

    await record('审计分页页大小和重置均发出真实查询', async () => {
      const reset = page.waitForResponse(response => {
        const url = new URL(response.url());
        return url.pathname === '/api/v1/admin/audit-logs'
          && !url.searchParams.has('action') && !url.searchParams.has('targetType')
          && !url.searchParams.has('start') && !url.searchParams.has('end')
          && response.status() === 200;
      });
      await page.getByRole('button', { name: '重置', exact: true }).click();
      const resetResult = await (await reset).json();
      assert(resetResult.total > 20, 'dedicated setup must create enough audit rows for pagination');
      const resized = page.waitForResponse(response => {
        const url = new URL(response.url());
        return url.pathname === '/api/v1/admin/audit-logs'
          && url.searchParams.get('page') === '1' && url.searchParams.get('pageSize') === '10'
          && response.status() === 200;
      });
      await page.locator('.arco-pagination .arco-select').click();
      await page.getByRole('option', { name: '10 条/页', exact: true }).click();
      const first = await (await resized).json();
      assert.equal(first.list.length, 10);
      const nextPage = page.waitForResponse(response => {
        const url = new URL(response.url());
        return url.pathname === '/api/v1/admin/audit-logs'
          && url.searchParams.get('page') === '2' && url.searchParams.get('pageSize') === '10'
          && response.status() === 200;
      });
      await page.locator('.arco-pagination-item').filter({ hasText: /^2$/ }).click();
      const second = await (await nextPage).json();
      assert(second.list.length > 0);
      assert.equal(new Set([...first.list, ...second.list].map(item => item.id)).size,
        first.list.length + second.list.length, 'audit pages must not duplicate rows');
    });

    await page.screenshot({ path: OUT + '/business-controls-final.png', fullPage: true });
    await adminContext.close();
  } catch (error) {
    if (page) {
      await page.screenshot({ path: OUT + '/business-controls-failure.png', fullPage: true }).catch(() => {});
      console.log((await page.locator('body').innerText()).slice(-5000));
    }
    console.error(error.stack || error.message);
    process.exitCode = 1;
  } finally {
    if (browser) await browser.close();
  }
})().catch(error => {
  console.error(error.stack || error.message);
  process.exitCode = 1;
});
