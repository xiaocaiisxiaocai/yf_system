const { chromium } = require('playwright');
const crypto = require('node:crypto');
const path = require('node:path');
const {
  fs, assert, OUT, s, f, save, record, login, api, navigate, action, track,
} = require(process.env.YF_BROWSER_SUPPORT_DIR + '/ui-lib.cjs');

const sha = file => crypto.createHash('sha256').update(fs.readFileSync(file)).digest('hex');
let browser;
let adminPage;
const sessions = {};

async function json(context, token, method, url, body, expected = 200) {
  return (await api(context, method, url, body, token, expected)).json();
}

async function openUser(key) {
  const context = await browser.newContext({
    viewport: { width: 1440, height: 1000 },
    acceptDownloads: true,
  });
  const page = await context.newPage();
  track(page, key);
  const user = key === 'admin'
    ? { username: 'admin', changedPassword: s.adminPassword }
    : f.users[key];
  const result = await login(page, user.username, user.changedPassword);
  await page.waitForURL(s.base + '/');
  sessions[key] = { context, page, token: result.accessToken };
  return page;
}

async function confirm(page, label, url) {
  await page.getByRole('button', { name: label, exact: true }).click();
  const result = await action(
    page,
    url,
    'POST',
    () => page.locator('.arco-popconfirm:visible').last()
      .getByRole('button', { name: '确定', exact: true }).click(),
  );
  if (url.endsWith('/submit')) assert(Number.isInteger(result.latestSubmissionId) && result.latestSubmissionId > 0);
  return result;
}

async function addToken(scope, placeholder, value) {
  const input = scope.getByPlaceholder(placeholder, { exact: true });
  await input.fill(value);
  await input.press('Enter');
}

async function choose(scope, placeholder, optionName) {
  await scope.getByPlaceholder(placeholder, { exact: true }).click();
  await adminPage.getByRole('option', { name: optionName, exact: true }).click();
}

async function upload(page, projectId, file) {
  await page.getByRole('tab', { name: '文件', exact: true }).click();
  await page.getByRole('button', { name: '上传文件', exact: true }).click();
  await page.getByLabel('选择上传文件').setInputFiles(file);
  const result = await action(
    page,
    '/merge',
    'POST',
    () => page.getByRole('button', { name: '上传所选文件', exact: true }).click(),
  );
  await page.getByRole('row').filter({ hasText: path.basename(file) }).waitFor();
  return result.id;
}

async function download(page, name, target) {
  const row = page.getByRole('row').filter({ hasText: name });
  const ready = page.waitForEvent('download');
  await row.getByRole('button', { name: '下载文件', exact: true }).click();
  const item = await ready;
  await item.saveAs(target);
  assert.equal(await item.failure(), null);
}

async function preview(page, name, kind, label) {
  const row = page.getByRole('row').filter({ hasText: name });
  await row.getByRole('button', { name: '预览文件', exact: true }).click();
  const modal = page.getByRole('dialog');
  if (kind === 'pdf') {
    await modal.locator('canvas').waitFor();
    await page.waitForFunction(() => {
      const canvas = document.querySelector('[role=dialog] canvas');
      return canvas && canvas.width > 0 && canvas.height > 0;
    });
  } else {
    await modal.getByText('甲公司', { exact: true }).waitFor();
    await modal.getByText('PASS', { exact: true }).waitFor();
  }
  await page.screenshot({ path: OUT + '/' + label + '.png', fullPage: true });
  await modal.getByRole('button', { name: '关闭弹窗', exact: true }).click();
  await modal.waitFor({ state: 'hidden' });
}

(async () => {
  try {
    browser = await chromium.launch({ channel: 'chrome', headless: true });
    adminPage = await openUser('admin');
    const admin = sessions.admin;
    const owners = await json(admin.context, admin.token, 'GET', '/project-owner-options');
    assert(owners.length > 0, 'project owner options must contain an active section owner');
    const memberOwner = owners.find(owner => owner.id === f.users.member?.id);
    const owner = memberOwner || owners[0];
    const vendors = await json(admin.context, admin.token, 'GET', '/project-dictionaries?type=ROBOT_VENDOR&enabledOnly=true');
    const models = await json(admin.context, admin.token, 'GET',
      '/project-dictionaries?type=ROBOT_MODEL&parentId=' + vendors[0].id + '&enabledOnly=true');
    const priorities = await json(admin.context, admin.token, 'GET', '/project-dictionaries?type=PRIORITY&enabledOnly=true');
    assert(vendors.length > 0 && models.length > 0 && priorities.length > 0,
      'project creation options must be available');
    const internal = owner.id === f.users.member?.id ? await openUser('member') : adminPage;
    const projects = {};

    for (const key of ['a', 'b']) {
      await record(key + ' 主项目及子项目通过页面创建、启动并显示负责人', async () => {
        await navigate(adminPage, '/projects');
        await adminPage.getByRole('button', { name: '新建主项目', exact: true }).click();
        const modal = adminPage.getByRole('dialog');
        const groupName = '自动验收' + key + '主项目-' + Date.now();
        const childName = groupName + ' 子项目';
        await modal.getByPlaceholder('主项目名称', { exact: true }).fill(groupName);
        await addToken(modal, '输入子项目名称后按回车，可一次创建多个', childName);
        await addToken(modal, '输入工令号后按回车，可填写多个', 'WO-' + crypto.randomBytes(4).toString('hex'));
        await modal.getByPlaceholder('请输入机型', { exact: true }).fill('自动验收机型');
        await choose(modal, '选择 Robot 厂商', vendors[0].name);
        await choose(modal, '选择 Robot 型号', models[0].name);
        await choose(modal, '选择负责人', owner.realName + '（' + owner.employeeNo + '）');
        await choose(modal, '选择优先级', priorities[0].name);
        const date = modal.getByPlaceholder('选择预计完成日期', { exact: true });
        await date.fill('2099-12-31');
        await date.press('Enter');
        await choose(modal, '选择供应商', f.suppliers[key].name);
        const group = await action(
          adminPage,
          '/project-groups',
          'POST',
          () => modal.getByRole('button', { name: '创建主项目', exact: true }).click(),
        );
        const detail = await json(admin.context, admin.token, 'GET', '/project-groups/' + group.id);
        assert.equal(detail.projects.length, 1);
        const project = detail.projects[0];
        projects[key] = { id: project.id, groupId: group.id, groupName, name: project.name };
        await adminPage.getByRole('link', { name: groupName, exact: true }).waitFor();
        await adminPage.getByRole('link', { name: groupName, exact: true }).click();
        await adminPage.getByRole('button', { name: childName, exact: true }).waitFor();
        await adminPage.getByRole('button', { name: childName, exact: true }).click();
        await adminPage.getByRole('button', { name: '开始', exact: true }).waitFor();
        await action(adminPage, '/projects/' + project.id + '/status', 'PUT',
          () => adminPage.getByRole('button', { name: '开始', exact: true }).click());
        await adminPage.getByText(owner.realName, { exact: false }).waitFor();
        f.uiProjects = projects;
        save();
      });
    }

    const vendorA = await openUser('a');
    const vendorB = await openUser('b');
    const pdf = OUT + '/valid-preview.pdf';
    const xlsx = OUT + '/vendor-response.xlsx';
    for (const key of ['a', 'b']) {
      const vendor = key === 'a' ? vendorA : vendorB;
      const project = projects[key];
      await navigate(internal, '/projects/' + project.id);
      await internal.getByRole('tab', { name: '文件', exact: true }).waitFor();
      await record(key + ' 内部负责人上传PDF，供应商实际预览及下载SHA一致', async () => {
        project.pdfId = await upload(internal, project.id, pdf);
        await navigate(vendor, '/projects/' + project.id);
        await vendor.getByRole('row').filter({ hasText: 'valid-preview.pdf' }).waitFor();
        await preview(vendor, 'valid-preview.pdf', 'pdf', key + '-vendor-pdf');
        await download(vendor, 'valid-preview.pdf', OUT + '/' + key + '-received.pdf');
        assert.equal(sha(pdf), sha(OUT + '/' + key + '-received.pdf'));
      });
      await record(key + ' 供应商上传Excel，双方实际预览及下载SHA一致', async () => {
        project.excelId = await upload(vendor, project.id, xlsx);
        await preview(vendor, 'vendor-response.xlsx', 'excel', key + '-vendor-excel');
        await internal.reload();
        await internal.getByRole('row').filter({ hasText: 'vendor-response.xlsx' }).waitFor();
        await preview(internal, 'vendor-response.xlsx', 'excel', key + '-internal-excel');
        await download(internal, 'vendor-response.xlsx', OUT + '/' + key + '-received.xlsx');
        assert.equal(sha(xlsx), sha(OUT + '/' + key + '-received.xlsx'));
      });
      await record(key + ' 内部和供应商双向留言、已读回执与刷新持久化', async () => {
        await internal.getByRole('tab', { name: /^留言/ }).click();
        const text = key + '公司内部自动验收留言';
        await internal.getByPlaceholder('输入留言，Ctrl+Enter 发送').fill(text);
        const sent = await action(internal, '/projects/' + project.id + '/messages', 'POST',
          () => internal.getByRole('button', { name: '发送', exact: true }).click());
        await vendor.getByRole('tab', { name: /^留言/ }).click();
        await vendor.getByText(text, { exact: true }).waitFor();
        const response = key + '公司供应商自动验收回复';
        await vendor.getByPlaceholder('输入留言，Ctrl+Enter 发送').fill(response);
        await action(vendor, '/projects/' + project.id + '/messages', 'POST',
          () => vendor.getByRole('button', { name: '发送', exact: true }).click());
        await internal.reload();
        await internal.getByText(response, { exact: true }).waitFor();
        await internal.getByRole('button', { name: '回执详情', exact: true }).click();
        await internal.locator('.arco-drawer').getByText(f.suppliers[key].name + '代表', { exact: false }).waitFor();
        await internal.locator('.arco-drawer').getByRole('button', { name: '关闭抽屉', exact: true }).click();
        const reads = await json(admin.context, admin.token, 'GET', '/messages/' + sent.id + '/reads');
        assert(reads.readers.some(reader => reader.userId === f.users[key].id));
      });
    }

    await record('甲乙公司无法读取对方主项目、文件或留言', async () => {
      for (const [key, other] of [['a', 'b'], ['b', 'a']]) {
        const session = sessions[key];
        const project = projects[other];
        await navigate(session.page, '/projects/' + project.id);
        await session.page.getByText('项目加载失败或没有访问权限', { exact: true }).waitFor();
        for (const url of [
          '/projects/' + project.id,
          '/projects/' + project.id + '/messages',
          '/files/' + project.pdfId + '/content',
          '/files/' + project.excelId + '/download',
        ]) await json(session.context, session.token, 'GET', url, undefined, 403);
      }
    });

    await record('供应商提交、公司撤回、内部驳回、重新提交并内部验收完成', async () => {
      const project = projects.a;
      await navigate(vendorA, '/projects/' + project.id);
      const submitted = await confirm(vendorA, '提交公司验收', '/projects/' + project.id + '/submit');
      assert.equal(submitted.confirmSide, 'COMPANY');
      await vendorA.getByText('验收方：公司', { exact: true }).waitFor();
      assert.equal(await vendorA.getByRole('button', { name: '验收通过', exact: true }).count(), 0);
      assert.equal(await vendorA.getByRole('button', { name: '验收驳回', exact: true }).count(), 0);
      await navigate(internal, '/projects/' + project.id);
      await internal.getByText('验收方：公司', { exact: true }).waitFor();
      await json(admin.context, admin.token, 'POST', '/projects/' + project.id + '/withdraw', {
        expectedSubmissionId: submitted.latestSubmissionId,
      });
      await navigate(vendorA, '/projects/' + project.id);
      await confirm(vendorA, '提交公司验收', '/projects/' + project.id + '/submit');
      await navigate(internal, '/projects/' + project.id);
      await internal.getByRole('button', { name: '验收驳回', exact: true }).click();
      await internal.getByPlaceholder('请填写驳回原因').fill('自动验收：请补充确认');
      await action(internal, '/projects/' + project.id + '/reject', 'POST',
        () => internal.getByRole('button', { name: '确认驳回', exact: true }).click());
      await internal.getByText('上次驳回：自动验收：请补充确认', { exact: true }).waitFor();
      await navigate(vendorA, '/projects/' + project.id);
      await confirm(vendorA, '提交公司验收', '/projects/' + project.id + '/submit');
      await navigate(internal, '/projects/' + project.id);
      await confirm(internal, '验收通过', '/projects/' + project.id + '/confirm');
      await internal.getByText('已完成', { exact: true }).first().waitFor();
      const done = await json(admin.context, admin.token, 'GET', '/projects/' + project.id);
      assert.equal(done.status, 'COMPLETED');
      await internal.screenshot({ path: OUT + '/a-completed.png', fullPage: true });
    });

    await record('供应商提交、公司确认完成及公司项目动态', async () => {
      const project = projects.b;
      await navigate(vendorB, '/projects/' + project.id);
      const submitted = await confirm(vendorB, '提交公司验收', '/projects/' + project.id + '/submit');
      assert.equal(submitted.confirmSide, 'COMPANY');
      await vendorB.getByText('验收方：公司', { exact: true }).waitFor();
      assert.equal(await vendorB.getByRole('button', { name: '验收通过', exact: true }).count(), 0);
      assert.equal(await vendorB.getByRole('button', { name: '验收驳回', exact: true }).count(), 0);
      await navigate(internal, '/projects/' + project.id);
      await confirm(internal, '验收通过', '/projects/' + project.id + '/confirm');
      await internal.getByText('已完成', { exact: true }).first().waitFor();
      await internal.getByRole('tab', { name: '项目动态', exact: true }).click();
      await internal.getByText('验收通过', { exact: false }).first().waitFor();
      await internal.screenshot({ path: OUT + '/b-completed-activity.png', fullPage: true });
    });

    await record('窄桌面项目详情可用且无页面横向溢出', async () => {
      await internal.setViewportSize({ width: 1024, height: 900 });
      assert(await internal.getByRole('button', { name: '返回主项目', exact: true }).isVisible());
      const width = await internal.evaluate(() => ({ scroll: document.documentElement.scrollWidth, view: innerWidth }));
      assert(width.scroll <= width.view + 1);
      await internal.screenshot({ path: OUT + '/compact-project.png', fullPage: true });
    });
    await record('浏览器运行无未捕获脚本异常和服务端500', async () => {
      for (const name of ['page-errors.jsonl', 'http-errors.jsonl']) {
        assert(!fs.existsSync(OUT + '/' + name) || fs.readFileSync(OUT + '/' + name, 'utf8').trim() === '');
      }
    });
  } catch (error) {
    if (adminPage) {
      await adminPage.screenshot({ path: OUT + '/business-failure.png', fullPage: true }).catch(() => {});
      console.log((await adminPage.locator('body').innerText()).slice(-4000));
    }
    console.error(error.stack || error.message);
    process.exitCode = 1;
  } finally {
    if (browser) await browser.close();
  }
})();
