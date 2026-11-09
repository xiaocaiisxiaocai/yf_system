// Full-browser acceptance supplement for dashboard, workflow and project controls.
// Prerequisites/order: auth -> fixtures -> users -> this script.
// The main runner owns integration, execution and cleanup. This draft does not mutate f.uiProjects.a/b.
const { chromium } = require('playwright');
const crypto = require('node:crypto');
const {
  assert, OUT, s, f, record, login, api, projectMetadata, action, track,
} = require(process.env.YF_BROWSER_SUPPORT_DIR + '/ui-lib.cjs');
const {
  returnSupplierMaterial, uploadFixture: uploadBytes,
} = require(process.env.YF_BROWSER_SUPPORT_DIR + '/upload-contract.cjs');

const apiPath = value => new URL(value.url()).pathname;

async function loadProjectDefaults(context, token, supplierId) {
  return {
    workOrderNos: ['WO-' + crypto.randomBytes(4).toString('hex')],
    machineModel: '项目控件机型',
    ...await projectMetadata(context, token, supplierId),
  };
}

async function createProject(context, token, supplierId, groupName, defaults) {
  const project = await (await api(context, 'POST', '/projects', {
    name: groupName,
    description: '独立工作台、权限和控件验收夹具',
    supplierId,
    ...defaults,
  }, token)).json();
  assert.equal(project.status, 'IN_PROGRESS', 'creating a project starts it');
  return project;
}

async function waitForCopyJob(context, token, jobId, timeout = 120000) {
  const deadline = Date.now() + timeout;
  let latest;
  for (;;) {
    const list = await (await api(
      context, 'GET', '/project-copy-jobs', undefined, token,
    )).json();
    assert(Array.isArray(list.jobs), 'copy job list must contain jobs');
    latest = list.jobs.find(item => item.jobId === jobId);
    assert(latest, 'copy job ' + jobId + ' must remain visible to its requester');
    if (latest.status === 'succeeded') break;
    assert.notEqual(latest.status, 'failed', latest.error || 'copy job failed');
    assert(['pending', 'running'].includes(latest.status), 'unexpected copy job status ' + latest.status);
    assert(Date.now() < deadline, 'copy job ' + jobId + ' timed out');
    await new Promise(resolve => setTimeout(resolve, 500));
  }
  const detail = await (await api(
    context, 'GET', '/project-copy-jobs/' + jobId, undefined, token,
  )).json();
  assert.equal(detail.jobId, jobId);
  assert.equal(detail.status, 'succeeded');
  assert.deepEqual(detail.result, latest.result);
  return detail;
}

const dashboardProjectLabel = project => project.name;

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
    assert(f.users?.a?.uiFirstChanged && f.users?.a?.changedPassword,
      'run users before business-controls: supplier A must have completed first password change');
    assert(f.suppliers?.a?.id, 'fixtures must contain supplier A');

    browser = await require(process.env.YF_BROWSER_SUPPORT_DIR + '/shared-browser.cjs')(chromium);
    const adminContext = await browser.newContext({ viewport: { width: 1440, height: 1000 } });
    page = await adminContext.newPage();
    track(page, 'business-controls-admin');
    const admin = await login(page, 'admin', s.adminPassword);
    await page.waitForURL(s.base + '/');
    const adminToken = admin.accessToken;
    const supplierContext = await browser.newContext({ viewport: { width: 1440, height: 1000 } });
    const supplierPage = await supplierContext.newPage();
    track(supplierPage, 'business-controls-supplier-a');
    const supplier = await login(supplierPage, f.users.a.username, f.users.a.changedPassword);
    await supplierPage.waitForURL(s.base + '/');
    const supplierToken = supplier.accessToken;
    const marker = crypto.randomBytes(5).toString('hex');
    const prefix = '业务控件补验-' + marker;
    const tinyFile = Buffer.from('browser-dashboard-fixture|' + marker);
    const pendingProjects = [];
    const messageText = '工作台未读键盘跳转-' + marker;
    const defaults = await loadProjectDefaults(adminContext, adminToken, f.suppliers.a.id);
    const childControlSource = await createProject(
      adminContext, adminToken, f.suppliers.a.id, prefix + '-复制源', defaults);
    const copiedFileName = prefix + '-复制源文件.pdf';
    const sourceOnlyMessage = '只属于复制源项目的留言-' + marker;
    await uploadBytes(adminContext, adminToken, childControlSource.id, copiedFileName, tinyFile, { ensureStep: true });
    await api(adminContext, 'POST', '/projects/' + childControlSource.id + '/messages',
      { content: sourceOnlyMessage }, adminToken);

    // API-only fixture setup: 11 projects with supplier-returned material awaiting the responsible user (admin)
    // make the dashboard paginator real.
    for (let index = 0; index < 11; index += 1) {
      const project = await createProject(
        adminContext, adminToken, f.suppliers.a.id,
        prefix + '-' + String(index + 1).padStart(2, '0'), defaults);
      await uploadBytes(
        adminContext, adminToken, project.id, prefix + '-' + index + '.zip', tinyFile, { ensureStep: true },
      );
      await returnSupplierMaterial(supplierContext, supplierToken, project.id, prefix + '-' + index + '-回传.pdf');
      if (index === 0) {
        await api(supplierContext, 'POST', '/projects/' + project.id + '/messages', {
          content: messageText,
        }, supplierToken);
      }
      pendingProjects.push(project);
    }

    await record('其他内部账号可见非本人负责的项目但只读且无系统管理菜单', async () => {
      const managerContext = await browser.newContext({ viewport: { width: 1440, height: 1000 } });
      const managerPage = await managerContext.newPage();
      track(managerPage, 'business-controls-manager');
      try {
        const manager = await login(managerPage, f.users.manager.username, f.users.manager.changedPassword);
        assert.equal(typeof manager.user.realName, 'string');
        await managerPage.waitForURL(s.base + '/');
        for (const label of ['供应商管理', '用户管理', '组织架构', '角色与权限', '操作日志', '系统参数']) {
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
        const listedProject = body.list.find(item => item.id === pendingProjects[0].id);
        assert(listedProject, 'every internal account must see a project it is not responsible for');
        assert.equal(listedProject.canManage, false);
        await managerPage.getByRole('link', { name: pendingProjects[0].name, exact: true }).click();
        await managerPage.waitForURL(s.base + '/projects/' + pendingProjects[0].id);
        await managerPage.getByRole('heading', { name: pendingProjects[0].name, exact: true }).waitFor();
        await managerPage.getByRole('tab', { name: '文件', exact: true }).waitFor();
        for (const label of ['编辑', '上传文件', '资料确认 OK', '退回资料', '终止']) {
          assert.equal(await managerPage.getByRole('button', { name: label, exact: true }).count(), 0,
            'a non-responsible account must not see ' + label);
        }
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
      assert(summary.pendingTasks >= 11);
      assert(summary.unreadMessages >= 1);
      for (const [title, value] of [['待审核资料项目', summary.pendingTasks], ['未读留言', summary.unreadMessages]]) {
        const card = page.locator('.dashboard-stat-card').filter({ hasText: title });
        await card.getByText(String(value), { exact: true }).waitFor();
      }
      assert.equal(pendingFirst.list.length, 10);
      for (const project of pendingFirst.list) {
        await page.getByRole('link', { name: dashboardProjectLabel(project), exact: true }).waitFor();
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
        await page.getByRole('link', { name: dashboardProjectLabel(project), exact: true }).waitFor();
      }
    });

    await record('工作台待确认链接和未读留言键盘操作进入真实目标', async () => {
      const currentPending = await (await api(adminContext, 'GET',
        '/dashboard/pending-projects?page=2&pageSize=10', undefined, adminToken)).json();
      const target = currentPending.list.find(item => pendingProjects.some(project => project.id === item.id));
      assert(target, 'page 2 must contain a dedicated pending fixture');
      await page.getByRole('link', { name: dashboardProjectLabel(target), exact: true }).click();
      await page.waitForURL(s.base + '/projects/' + target.id);
      await page.getByRole('button', { name: '返回项目列表', exact: true }).waitFor();

      await page.goto(s.base + '/');
      const message = page.locator('.dashboard-message-item').filter({ hasText: messageText });
      await message.waitFor();
      await message.locator('.dashboard-message-unread').waitFor();
      await message.focus();
      await page.keyboard.press('Enter');
      await page.waitForURL(url => url.pathname === '/projects/' + pendingProjects[0].id
        && url.searchParams.get('tab') === 'messages');
      await page.getByText(messageText, { exact: true }).waitFor();
    });

    const flowProject = pendingProjects[0];
    const returnPath = '/api/v1/projects/' + flowProject.id + '/materials/return';
    const approvePath = '/api/v1/projects/' + flowProject.id + '/materials/approve';
    await record('退回资料必填校验和取消草稿不产生写入', async () => {
      let returnWrites = 0;
      const countReturn = request => {
        if (apiPath(request) === returnPath && request.method() === 'POST') returnWrites += 1;
      };
      page.on('request', countReturn);
      await page.goto(s.base + '/projects/' + flowProject.id);
      await page.getByRole('button', { name: '退回资料', exact: true }).click();
      let dialog = page.getByRole('dialog');
      await dialog.getByRole('button', { name: '确认退回', exact: true }).click();
      await dialog.getByText('请填写退回原因', { exact: true }).waitFor();
      assert.equal(returnWrites, 0);
      await dialog.getByPlaceholder('请说明需要厂商补充或修改的资料', { exact: true }).fill('应在取消后丢弃');
      await dialog.getByRole('button', { name: '取消', exact: true }).click();
      await dialog.waitFor({ state: 'hidden' });
      await page.getByRole('button', { name: '退回资料', exact: true }).click();
      dialog = page.getByRole('dialog');
      assert.equal(await dialog.getByPlaceholder('请说明需要厂商补充或修改的资料', { exact: true }).inputValue(), '');
      await dialog.getByRole('button', { name: '取消', exact: true }).click();
      assert.equal(returnWrites, 0);
      page.off('request', countReturn);
    });

    await record('浏览器保留旧回传版本时确认409且不会误完成新资料', async () => {
      await page.goto(s.base + '/projects/' + flowProject.id);
      await page.getByRole('button', { name: '资料确认 OK', exact: true }).waitFor();
      const staleDetail = await (await api(
        adminContext, 'GET', '/projects/' + flowProject.id, undefined, adminToken)).json();
      const staleSupplierFileId = staleDetail.latestSupplierFileId;
      const detailPath = '/api/v1/projects/' + flowProject.id;
      const keepStaleDetail = route => route.request().method() === 'GET'
        ? route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(staleDetail) })
        : route.continue();
      await page.route('**' + detailPath, keepStaleDetail);
      try {
        await returnSupplierMaterial(supplierContext, supplierToken, flowProject.id, prefix + '-新回传.pdf');
        await page.getByRole('button', { name: '资料确认 OK', exact: true }).click();
        const dialog = page.getByRole('dialog', { name: '资料确认 OK' });
        await dialog.getByText('厂商回传文件', { exact: false }).waitFor();
        const conflict = page.waitForResponse(response => apiPath(response) === approvePath
          && response.request().method() === 'POST' && response.status() === 409);
        await dialog.getByRole('button', { name: '确认并完成', exact: true }).click();
        const response = await conflict;
        assert.equal(response.request().postDataJSON().expectedSupplierFileId, staleSupplierFileId);
      } finally {
        await page.unroute('**' + detailPath, keepStaleDetail);
      }
      const persisted = await (await api(
        adminContext, 'GET', '/projects/' + flowProject.id, undefined, adminToken)).json();
      assert.equal(persisted.status, 'IN_PROGRESS');
      assert.equal(persisted.materialReviewStatus, 'PENDING');
      assert(persisted.latestSupplierFileId > staleSupplierFileId);
      await page.reload();
      await page.getByRole('button', { name: '资料确认 OK', exact: true }).waitFor();
    });

    await record('退回资料503保留草稿并可单次重试成功', async () => {
      const reason = '退回失败后保留并重试-' + marker;
      page.expectedServerErrors = new Set([returnPath]);
      await page.route('**' + returnPath, route => route.fulfill({
        status: 503,
        contentType: 'application/json',
        body: JSON.stringify({ code: 50301, message: '退回测试暂时不可用' }),
      }), { times: 1 });
      await page.getByRole('button', { name: '退回资料', exact: true }).click();
      const dialog = page.getByRole('dialog');
      const reasonInput = dialog.getByPlaceholder('请说明需要厂商补充或修改的资料', { exact: true });
      await reasonInput.fill(reason);
      const failed = page.waitForResponse(response => apiPath(response) === returnPath && response.status() === 503);
      await dialog.getByRole('button', { name: '确认退回', exact: true }).click();
      await failed;
      assert.equal(await dialog.isVisible(), true);
      assert.equal(await reasonInput.inputValue(), reason);
      const returned = await action(page, '/projects/' + flowProject.id + '/materials/return', 'POST', () => (
        dialog.getByRole('button', { name: '确认退回', exact: true }).click()
      ));
      assert.equal(returned.materialReviewStatus, 'RETURNED');
      const persisted = await (await api(adminContext, 'GET', '/projects/' + flowProject.id, undefined, adminToken)).json();
      assert.equal(persisted.materialReviewStatus, 'RETURNED');
      assert.equal(persisted.materialReviewReason, reason);
      page.expectedServerErrors.clear();
    });

    await record('项目详情显示服务端负责人且资料待确认时仍可编辑公共资料', async () => {
      const pendingProject = pendingProjects[1];
      const current = await (await api(
        adminContext, 'GET', '/projects/' + pendingProject.id, undefined, adminToken)).json();
      assert.equal(current.responsibleUserId, admin.user.id);
      assert.equal(current.materialReviewStatus, 'PENDING');
      assert.equal(current.canManage, true);
      await api(adminContext, 'PUT', '/projects/' + pendingProject.id, {
        name: current.name,
        description: '资料待确认时编辑-' + marker,
        supplierId: current.supplierId,
        workOrderNos: current.workOrderNos,
        machineModel: current.machineModel,
        robotPartId: current.robotPartId,
        priorityId: current.priorityId,
        robotTypeId: current.robotTypeId,
        robotOwnerContactIds: current.robotOwnerContactIds,
        expectedCompletionDate: current.expectedCompletionDate,
        expectedUpdatedAt: current.updatedAt,
      }, adminToken);
    });

    await record('项目通过页面复制、编辑、终止后删除且复制件含文件不含留言并显示履历', async () => {
      const copyName = prefix + '-页面复制件';
      const editedName = copyName + '-已编辑';

      await page.goto(s.base + '/projects/' + childControlSource.id);
      await page.getByRole('heading', { name: childControlSource.name, exact: true }).waitFor();
      await page.locator('.detail-actions').getByRole('button', { name: '复制', exact: true }).click();
      let dialog = page.getByRole('dialog', { name: '复制项目' });
      await dialog.getByPlaceholder('新项目名称', { exact: true }).fill(copyName);
      const acceptedJob = await action(page, '/projects/' + childControlSource.id + '/copy', 'POST',
        () => dialog.getByRole('button', { name: '创建复制任务', exact: true }).click(), 202);
      assert(Number.isSafeInteger(acceptedJob.jobId) && acceptedJob.jobId > 0);
      assert.equal(acceptedJob.sourceProjectId, childControlSource.id);
      assert(!('projectGroupId' in acceptedJob));
      assert.equal(acceptedJob.targetName, copyName);
      assert(['pending', 'running', 'succeeded'].includes(acceptedJob.status));
      assert.equal(acceptedJob.filesTotal, 3); // The internal-upload fixture also includes its required STEP and Excel.
      assert.equal(acceptedJob.result, null);
      const jobCard = page.locator('[data-copy-job-id="' + acceptedJob.jobId + '"]');
      await jobCard.waitFor();
      await jobCard.getByText(copyName, { exact: true }).waitFor();
      const copied = await waitForCopyJob(adminContext, adminToken, acceptedJob.jobId);
      assert.equal(copied.result.copyFileCount, 3);
      assert(Number.isSafeInteger(copied.result.projectId) && copied.result.projectId > 0);
      await jobCard.getByText('已完成', { exact: true }).waitFor();
      await jobCard.getByText('已复制 3 个文件', { exact: true }).waitFor();
      await jobCard.getByRole('button', { name: '进入副本', exact: true }).click();
      await page.waitForURL(s.base + '/projects/' + copied.result.projectId + '?tab=files');
      const copiedProject = await (await api(
        adminContext, 'GET', '/projects/' + copied.result.projectId, undefined, adminToken)).json();
      assert.equal(copiedProject.status, 'IN_PROGRESS', 'a copy starts at once');
      assert.equal(copiedProject.responsibleUserId, admin.user.id, 'whoever copies owns the copy');
      await page.getByRole('row').filter({ hasText: copiedFileName }).waitFor();
      await page.getByRole('row').filter({ hasText: 'fixture-assembly.step' }).waitFor();
      await page.getByRole('row').filter({ hasText: 'fixture-motion-flow.xlsx' }).waitFor();
      await page.getByRole('button', { name: '查看复制履历', exact: true }).click();
      const history = page.locator('.project-copy-history-drawer');
      await history.getByRole('heading', { name: '复制来源', exact: true }).waitFor();
      await history.getByRole('button', { name: childControlSource.name, exact: true }).waitFor();
      await history.getByRole('button', { name: '查看文件映射', exact: true }).click();
      await history.getByText(copiedFileName, { exact: true }).first().waitFor();
      await history.getByRole('button', { name: '关闭抽屉', exact: true }).click();

      await page.getByRole('tab', { name: '留言', exact: true }).click();
      await page.getByText('暂无留言', { exact: true }).waitFor();
      assert.equal(await page.getByText(sourceOnlyMessage, { exact: true }).count(), 0);
      const copiedMessages = await (await api(
        adminContext, 'GET', '/projects/' + copied.result.projectId + '/messages?page=1&pageSize=20',
        undefined, adminToken)).json();
      assert.equal(copiedMessages.total, 0);

      await page.locator('.detail-actions').getByRole('button', { name: '编辑', exact: true }).click();
      dialog = page.getByRole('dialog', { name: '编辑项目' });
      await dialog.getByPlaceholder('项目名称', { exact: true }).fill(editedName);
      const edited = await action(page, '/projects/' + copied.result.projectId, 'PUT',
        () => dialog.getByRole('button', { name: '保存', exact: true }).click());
      assert.equal(edited.name, editedName);
      await page.getByRole('heading', { name: editedName, exact: true }).waitFor();

      // Only terminated projects can be deleted.
      assert.equal(await page.locator('.detail-actions').getByRole('button', { name: '删除', exact: true }).count(), 0);
      await api(adminContext, 'PUT', '/projects/' + copied.result.projectId + '/status',
        { status: 'TERMINATED' }, adminToken);
      await page.reload();
      await page.locator('.detail-actions').getByRole('button', { name: '删除', exact: true }).click();
      await action(page, '/projects/' + copied.result.projectId, 'DELETE', () =>
        page.locator('.arco-popconfirm:visible').last()
          .getByRole('button', { name: '确定', exact: true }).click());
      await page.waitForURL(s.base + '/projects');
      await api(adminContext, 'GET', '/projects/' + copied.result.projectId, undefined, adminToken, 404);
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
      await choose(page, '具体操作', '开始项目');
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
          && url.searchParams.get('action') === 'PROJECT_START'
          && url.searchParams.get('targetType') === 'project'
          && !!url.searchParams.get('start') && !!url.searchParams.get('end')
          && response.status() === 200;
      });
      await page.getByRole('button', { name: '查询', exact: true }).click();
      const result = await (await filtered).json();
      assert(result.total >= 11);
      assert(result.list.every(item => item.action === 'PROJECT_START' && item.targetType === 'project'));
      const row = page.getByRole('row').filter({ hasText: 'PROJECT_START' }).first();
      await row.getByText('开始项目', { exact: true }).waitFor();
      await row.getByRole('button', { name: '查看', exact: true }).click();
      const drawer = page.locator('.audit-detail-drawer');
      await drawer.getByText('PROJECT_START', { exact: true }).waitFor();
      await drawer.getByRole('button', { name: '关闭抽屉', exact: true }).click();
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
