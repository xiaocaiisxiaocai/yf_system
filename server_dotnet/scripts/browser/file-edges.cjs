const { chromium } = require('playwright');
const crypto = require('node:crypto');
const XLSX = require(process.env.YF_PROJECT_ROOT + '/web/node_modules/xlsx');
const { fs, assert, OUT, s, f, record, login, api, projectMetadata, track } = require(process.env.YF_BROWSER_SUPPORT_DIR + '/ui-lib.cjs');
const { uploadFixture: uploadApi } = require(process.env.YF_BROWSER_SUPPORT_DIR + '/upload-contract.cjs');

const fileListPath = projectId => '/api/v1/projects/' + projectId + '/files';

async function createProjectGroup(context, token, supplierId, name) {
  const group = await (await api(context, 'POST', '/project-groups', {
    name,
    description: 'O26/O28 独立浏览器验收夹具',
    supplierId,
    workOrderNos: ['WO-' + crypto.randomBytes(4).toString('hex')],
    machineModel: '文件边界机型',
    ...await projectMetadata(context, token, supplierId),
    subprojectNames: [name + ' 子项目'],
  }, token)).json();
  const detail = await (await api(context, 'GET', '/project-groups/' + group.id, undefined, token)).json();
  assert.equal(detail.projects.length, 1, 'file fixture must contain one subproject');
  return detail.projects[0];
}

function workbookBytes(marker) {
  const book = XLSX.utils.book_new();
  XLSX.utils.book_append_sheet(book, XLSX.utils.aoa_to_sheet([
    ['文件边界验收', '第一页'],
    [marker, 'FIRST-SHEET-PASS'],
  ]), '第一页');
  XLSX.utils.book_append_sheet(book, XLSX.utils.aoa_to_sheet([
    ['文件边界验收', '第二页'],
    [marker, 'SECOND-SHEET-PASS'],
  ]), '第二页');
  return XLSX.write(book, { type: 'buffer', bookType: 'xlsx' });
}

async function assertExcelGrid(dialog, expectedRows) {
  const frame = dialog.frameLocator('iframe[title="Excel 预览内容"]');
  const grid = frame.locator('.x-spreadsheet-overlayer');
  const selectionInput = frame.locator('.x-spreadsheet-selector .hide-input input');
  const address = frame.locator('.excel-cell-address');
  const content = frame.getByLabel('单元格完整内容', { exact: true });
  await grid.waitFor({ state: 'visible' });
  await grid.click({ position: { x: 70, y: 35 } });
  for (let row = 0; row < expectedRows.length; row += 1) {
    for (let column = 0; column < expectedRows[row].length; column += 1) {
      const cellAddress = String.fromCharCode(65 + column) + String(row + 1);
      await address.filter({ hasText: new RegExp('^' + cellAddress + '$') }).waitFor();
      assert.equal(await content.inputValue(), expectedRows[row][column], cellAddress + ' rendered value');
      if (column + 1 < expectedRows[row].length) await selectionInput.press('ArrowRight');
    }
    if (row + 1 < expectedRows.length) {
      for (let column = expectedRows[row].length - 1; column > 0; column -= 1) {
        await selectionInput.press('ArrowLeft');
      }
      await selectionInput.press('ArrowDown');
    }
  }
}

(async () => {
  let browser, page;
  try {
    browser = await chromium.launch({ channel: 'chrome', headless: true });
    const admin = await browser.newContext({
      viewport: { width: 1440, height: 1000 },
    });
    const internalApi = await browser.newContext();
    const supplierApi = await browser.newContext();
    page = await admin.newPage();
    track(page, 'file-edges');
    const adminAuth = await login(page, 'admin', s.adminPassword);
    await page.waitForURL(s.base + '/');
    const adminToken = adminAuth.accessToken;

    const suffix = crypto.randomBytes(5).toString('hex');
    const prefix = 'file-edge-' + suffix;
    const configs = await (await api(admin, 'GET', '/admin/system/configs', undefined, adminToken)).json();
    const originalChunkSize = configs.find(item => item.key === 'upload.chunk_size')?.value;
    assert(originalChunkSize, 'upload.chunk_size fixture');
    await api(admin, 'PUT', '/admin/system/configs', {
      items: [{ key: 'upload.chunk_size', value: String(1024 * 1024) }],
    }, adminToken);
    const project = await createProjectGroup(
      internalApi, f.users.manager.token, f.suppliers.a.id, '文件边界验收-' + suffix);
    await api(internalApi, 'PUT', '/projects/' + project.id + '/status',
      { status: 'IN_PROGRESS' }, f.users.manager.token);

    const c2sNames = [];
    const s2cNames = [];
    for (let index = 0; index < 7; index++) {
      const name = prefix + '-c2s-' + String(index).padStart(2, '0') + '.zip';
      const bytes = Buffer.from('C2S|' + suffix + '|' + index);
      await uploadApi(internalApi, f.users.manager.token, project.id, name, bytes);
      c2sNames.push(name);
    }
    for (let index = 0; index < 6; index++) {
      const name = prefix + '-s2c-' + String(index).padStart(2, '0') + '.zip';
      const bytes = Buffer.from('S2C|' + suffix + '|' + index);
      await uploadApi(supplierApi, f.users.a.token, project.id, name, bytes);
      s2cNames.push(name);
    }
    const workbookName = prefix + '-s2c-workbook.xlsx';
    await uploadApi(supplierApi, f.users.a.token, project.id, workbookName, workbookBytes(suffix));
    s2cNames.push(workbookName);

    const waitForList = (expected, status = 200) => page.waitForResponse(response => {
      const url = new URL(response.url());
      return url.pathname === fileListPath(project.id)
        && response.request().method() === 'GET'
        && response.status() === status
        && Object.entries(expected).every(([key, value]) => (url.searchParams.get(key) || '') === String(value));
    });
    const listAction = async (expected, action) => {
      const [response] = await Promise.all([waitForList(expected), Promise.resolve().then(action)]);
      return response.json();
    };
    const search = page.getByPlaceholder('文件名', { exact: true });
    const direction = page.locator('.responsive-toolbar .arco-select').first();
    const row = name => page.getByRole('row').filter({ hasText: name });
    const uploadDiagnostics = OUT + '/file-upload-diagnostics.json';
    const uploadEvents = [];
    const writeUploadEvent = event => {
      uploadEvents.push(event);
      fs.writeFileSync(uploadDiagnostics, JSON.stringify(uploadEvents, null, 2));
    };
    const isUploadPath = path => path.startsWith('/api/v1/uploads/');
    page.on('request', request => {
      const path = new URL(request.url()).pathname;
      if (isUploadPath(path)) writeUploadEvent({ event: 'request', method: request.method(), path, status: null, requestfailed: null });
    });
    page.on('response', response => {
      const path = new URL(response.url()).pathname;
      if (isUploadPath(path)) writeUploadEvent({ event: 'response', method: response.request().method(), path, status: response.status(), requestfailed: null });
    });
    page.on('requestfailed', request => {
      const path = new URL(request.url()).pathname;
      if (isUploadPath(path)) writeUploadEvent({ event: 'requestfailed', method: request.method(), path, status: null, requestfailed: request.failure()?.errorText || 'unknown' });
    });

    const initial = waitForList({ page: 1, pageSize: 10 });
    await page.goto(s.base + '/projects/' + project.id);
    const initialData = await (await initial).json();
    await page.getByRole('tab', { name: '文件', exact: true, selected: true }).waitFor();

    await record('文件列表双向多页总数和分页真实结果', async () => {
      assert.equal(initialData.total, 14);
      assert.equal(initialData.list.length, 10);
      await page.getByText('共 14 条', { exact: true }).waitFor();
      const second = await listAction({ page: 2, pageSize: 10 }, () =>
        page.locator('.arco-pagination-item').filter({ hasText: /^2$/ }).click());
      assert.equal(second.total, 14);
      assert.equal(second.list.length, 4);
      assert(second.list.every(item => item.direction === 'C2S'));
    });

    await record('文件名与方向查询清除且筛选换页清理选择', async () => {
      await row(c2sNames[0]).getByRole('checkbox').locator('..').click();
      await page.getByRole('button', { name: '打包下载（1）', exact: true }).waitFor();

      const supplierFiles = await listAction({ page: 1, pageSize: 10, direction: 'S2C' }, async () => {
        await direction.click();
        await page.getByRole('option', { name: '供应商 → 公司', exact: true }).click();
      });
      assert.equal(supplierFiles.total, 7);
      assert(supplierFiles.list.every(item => item.direction === 'S2C'));
      assert.equal(await page.getByRole('button', { name: '打包下载（1）', exact: true }).count(), 0);

      const byName = await listAction({ page: 1, pageSize: 10, direction: 'S2C', keyword: workbookName }, async () => {
        await search.fill(workbookName);
        await search.press('Enter');
      });
      assert.equal(byName.total, 1);
      assert.equal(byName.list[0].originalName, workbookName);
      await row(workbookName).waitFor();

      const clearedName = await listAction({ page: 1, pageSize: 10, direction: 'S2C', keyword: '' }, async () => {
        await search.hover();
        await page.locator('.responsive-toolbar .arco-input-search .arco-input-clear-icon').click();
        assert.equal(await search.inputValue(), '');
      });
      assert.equal(clearedName.total, 7);

      await direction.hover();
      const clearedDirection = await listAction({ page: 1, pageSize: 10, direction: '' }, () =>
        direction.locator('.arco-select-clear-icon').click());
      assert.equal(clearedDirection.total, 14);
    });

    await record('文件列表错误重试和迟到响应不覆盖新条件', async () => {
      const path = fileListPath(project.id);
      page.expectedServerErrors = new Set([path]);
      await page.route(new RegExp(path.replaceAll('/', '\\/') + '(?:\\?|$)'), route => route.fulfill({
        status: 503,
        contentType: 'application/json',
        body: JSON.stringify({ code: 50301, message: 'temporary file-list failure' }),
      }), { times: 1 });
      await page.reload();
      await page.getByText('加载失败', { exact: true }).waitFor();
      const retried = await listAction({ page: 1, pageSize: 10 }, () =>
        page.getByRole('button', { name: '重试', exact: true }).click());
      assert.equal(retried.total, 14);

      let releaseOld;
      const oldReleased = new Promise(resolve => { releaseOld = resolve; });
      let markOldSeen;
      const oldSeen = new Promise(resolve => { markOldSeen = resolve; });
      let markOldDone;
      const oldDone = new Promise(resolve => { markOldDone = resolve; });
      const oldKeyword = prefix + '-c2s';
      const newestKeyword = workbookName;
      await page.route(new RegExp(path.replaceAll('/', '\\/') + '(?:\\?|$)'), async route => {
        const url = new URL(route.request().url());
        if (url.searchParams.get('keyword') !== oldKeyword) return route.continue();
        const response = await route.fetch();
        markOldSeen();
        await oldReleased;
        await route.fulfill({ response });
        markOldDone();
      });

      await search.fill(oldKeyword);
      await search.press('Enter');
      await oldSeen;
      const newest = await listAction({ page: 1, pageSize: 10, keyword: newestKeyword }, async () => {
        await search.fill(newestKeyword);
        await search.press('Enter');
      });
      assert.equal(newest.total, 1);
      await row(workbookName).waitFor();
      releaseOld();
      await oldDone;
      await page.waitForTimeout(100);
      assert.equal(await row(workbookName).count(), 1);
      assert.equal(await page.getByText(c2sNames[0], { exact: true }).count(), 0);
      await page.unroute(new RegExp(path.replaceAll('/', '\\/') + '(?:\\?|$)'));
      page.expectedServerErrors.clear();
    });

    await record('多工作表Excel预览真实切换', async () => {
      await row(workbookName).getByRole('button', { name: '预览文件', exact: true }).click();
      const dialog = page.getByRole('dialog', { name: `预览：${workbookName}` });
      await assertExcelGrid(dialog, [['文件边界验收', '第一页'], [suffix, 'FIRST-SHEET-PASS']]);
      const frame = dialog.frameLocator('iframe[title="Excel 预览内容"]');
      const secondSheet = frame.locator('.x-spreadsheet-bottombar li').filter({ hasText: /^第二页$/ });
      await secondSheet.click();
      await secondSheet.waitFor();
      await assertExcelGrid(dialog, [['文件边界验收', '第二页'], [suffix, 'SECOND-SHEET-PASS']]);
      assert((await secondSheet.getAttribute('class') || '').split(/\s+/).includes('active'));
      await dialog.getByRole('button', { name: '关闭文件预览', exact: true }).click();
      await dialog.waitFor({ state: 'hidden' });
    });

    await record('真实浏览器上传中断后复用会话断点续传', async () => {
      const interruptedName = prefix + '-interrupted.zip';
      const interruptedBytes = Buffer.alloc(2 * 1024 * 1024 + 257, 0x5a);
      await listAction({ page: 1, pageSize: 10, keyword: '' }, async () => {
        await search.fill('');
        await search.press('Enter');
      });
      await page.getByRole('button', { name: '上传文件', exact: true }).click();
      const dialog = page.getByRole('dialog');
      await dialog.getByLabel('选择上传文件').setInputFiles({
        name: interruptedName,
        mimeType: 'application/zip',
        buffer: interruptedBytes,
      });

      let failedChunkPath;
      // The uploader retries transient chunk failures (3 attempts in total), so the reset must
      // persist across every attempt before the upload is reported as interrupted.
      let abortedAttempts = 0;
      let committed = false;
      let firstChunkCommitted;
      const committedChunk = new Promise(resolve => { firstChunkCommitted = resolve; });
      await page.route('**/api/v1/uploads/*/chunks/*', async route => {
        const chunkPath = new URL(route.request().url()).pathname;
        const index = Number(chunkPath.split('/').at(-1));
        if (index === 0 && !committed) {
          const response = await route.fetch();
          assert.equal(response.status(), 200, 'commit one real chunk before injecting a later failure');
          await route.fulfill({ response });
          committed = true; firstChunkCommitted();
          return;
        }
        if (index === 1 && abortedAttempts < 3) {
          await committedChunk;
          failedChunkPath = chunkPath;
          abortedAttempts++;
          writeUploadEvent({ event: 'route-hit', method: route.request().method(), path: chunkPath, status: null, requestfailed: null });
          try { await route.abort('connectionreset'); }
          finally { writeUploadEvent({ event: 'route-exit', method: route.request().method(), path: chunkPath, status: null, requestfailed: null }); }
          return;
        }
        await route.continue();
      });

      const firstInitReady = page.waitForResponse(response =>
        new URL(response.url()).pathname === '/api/v1/uploads/init'
        && response.request().method() === 'POST' && response.status() === 200);
      await dialog.getByRole('button', { name: '上传所选文件', exact: true }).click();
      const firstInit = await (await firstInitReady).json();
      writeUploadEvent({ event: 'init', method: 'POST', path: '/api/v1/uploads/init', status: 200, requestfailed: null,
        totalChunks: firstInit.totalChunks, chunkSize: firstInit.chunkSize });
      await page.getByText('上传中断，可点击重新上传从断点续传', { exact: true }).waitFor();
      assert(failedChunkPath);
      assert.equal(abortedAttempts, 3, 'a reset chunk is retried automatically before the upload is interrupted');

      const secondInitReady = page.waitForResponse(response =>
        new URL(response.url()).pathname === '/api/v1/uploads/init'
        && response.request().method() === 'POST' && response.status() === 200);
      const mergeReady = page.waitForResponse(response =>
        new URL(response.url()).pathname === '/api/v1/uploads/' + firstInit.sessionId + '/merge'
        && response.request().method() === 'POST');
      await dialog.getByRole('button', { name: '上传所选文件', exact: true }).click();
      const secondInit = await (await secondInitReady).json();
      writeUploadEvent({ event: 'resume-init', method: 'POST', path: '/api/v1/uploads/init', status: 200, requestfailed: null,
        totalChunks: secondInit.totalChunks, chunkSize: secondInit.chunkSize });
      assert.equal(secondInit.sessionId, firstInit.sessionId);
      assert.equal(secondInit.resumed, true);
      assert(secondInit.uploadedChunks.length > 0, 'at least one completed chunk must be resumed');
      assert(secondInit.uploadedChunks.every(chunk => Number.isInteger(chunk.index)
        && /^[0-9a-f]{64}$/.test(chunk.sha256)), 'resumed chunks must include their SHA-256 digests');
      assert.equal((await mergeReady).status(), 200);
      await dialog.waitFor({ state: 'hidden' });
      await row(interruptedName).waitFor();

      const persisted = await (await api(admin, 'GET', '/projects/' + project.id + '/files?keyword=' + encodeURIComponent(interruptedName), undefined, adminToken)).json();
      assert.equal(persisted.total, 1);
      assert.equal(persisted.list[0].originalName, interruptedName);
    });

    await api(admin, 'PUT', '/admin/system/configs', {
      items: [{ key: 'upload.chunk_size', value: originalChunkSize }],
    }, adminToken);
    await page.screenshot({ path: OUT + '/file-edges.png', fullPage: true });
    await admin.close();
    await internalApi.close();
    await supplierApi.close();
  } catch (error) {
    if (page) {
      await page.screenshot({ path: OUT + '/file-edges-failure.png', fullPage: true }).catch(() => {});
      console.log((await page.locator('body').innerText()).slice(-5000));
    }
    console.error(error.stack);
    process.exitCode = 1;
  } finally {
    if (browser) await browser.close();
  }
})();
