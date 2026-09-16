/*
 * Isolated browser acceptance for file controls.
 * Prerequisites: YF_BROWSER_SUPPORT_DIR, YF_PROJECT_ROOT and
 * YF_BROWSER_EVIDENCE_DIR point at the prepared full-browser run; ui-lib state
 * contains the temporary admin password and fixture supplier A exists.
 * This script creates its own IN_PROGRESS project and never changes system
 * configuration. API calls are limited to fixture setup and postconditions.
 */
const { chromium } = require('playwright');
const crypto = require('node:crypto');
const XLSX = require(process.env.YF_PROJECT_ROOT + '/web/node_modules/xlsx');
const {
  assert, OUT, s, f, record, login, api, track,
} = require(process.env.YF_BROWSER_SUPPORT_DIR + '/ui-lib.cjs');

const apiPath = path => '/api/v1' + path;
const pathOf = value => new URL(value.url()).pathname;

function twoPagePdf(marker) {
  const streams = [1, 2].map(page =>
    `BT\n/F1 26 Tf\n72 720 Td\n(${marker} PAGE ${page}) Tj\nET\n`);
  const objects = [
    '<< /Type /Catalog /Pages 2 0 R >>',
    '<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>',
    '<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 5 0 R >> >> /Contents 6 0 R >>',
    '<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 5 0 R >> >> /Contents 7 0 R >>',
    '<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>',
    `<< /Length ${Buffer.byteLength(streams[0], 'ascii')} >>\nstream\n${streams[0]}endstream`,
    `<< /Length ${Buffer.byteLength(streams[1], 'ascii')} >>\nstream\n${streams[1]}endstream`,
  ];
  let body = '%PDF-1.4\n%YFQA\n';
  const offsets = [0];
  objects.forEach((object, index) => {
    offsets.push(Buffer.byteLength(body, 'ascii'));
    body += `${index + 1} 0 obj\n${object}\nendobj\n`;
  });
  const xref = Buffer.byteLength(body, 'ascii');
  body += `xref\n0 ${objects.length + 1}\n`;
  body += '0000000000 65535 f \n';
  for (const offset of offsets.slice(1)) body += String(offset).padStart(10, '0') + ' 00000 n \n';
  body += `trailer\n<< /Size ${objects.length + 1} /Root 1 0 R >>\nstartxref\n${xref}\n%%EOF\n`;
  return Buffer.from(body, 'ascii');
}

function workbookBytes(marker) {
  const book = XLSX.utils.book_new();
  XLSX.utils.book_append_sheet(book, XLSX.utils.aoa_to_sheet([
    ['文件控件验收', '结果'],
    [marker, 'DOWNLOAD-FALLBACK'],
  ]), '验收');
  return XLSX.write(book, { type: 'buffer', bookType: 'xlsx' });
}

async function uploadFixture(context, token, projectId, fileName, bytes) {
  const fileMd5 = crypto.createHash('md5').update(bytes).digest('hex');
  const init = await (await api(context, 'POST', '/uploads/init', {
    projectId, fileName, fileSize: bytes.length, fileMd5,
  }, token)).json();
  for (let index = 0; index < init.totalChunks; index += 1) {
    const chunk = bytes.subarray(
      index * init.chunkSize,
      Math.min((index + 1) * init.chunkSize, bytes.length),
    );
    const response = await context.request.fetch(s.base + apiPath(`/uploads/${init.sessionId}/chunks/${index}`), {
      method: 'PUT',
      data: chunk,
      headers: {
        Origin: s.base,
        Authorization: 'Bearer ' + token,
        'Content-Type': 'application/octet-stream',
      },
    });
    assert.equal(response.status(), 200, `fixture chunk ${fileName} ${index}`);
  }
  const merged = await (await api(
    context, 'POST', `/uploads/${init.sessionId}/merge`, undefined, token,
  )).json();
  return { ...merged, sessionId: init.sessionId };
}

async function createProjectGroup(context, token, supplierId, ownerId, name) {
  const vendors = await (await api(
    context, 'GET', '/project-dictionaries?type=ROBOT_VENDOR&enabledOnly=true', undefined, token)).json();
  assert(vendors.length > 0, 'file controls need a Robot vendor');
  const models = await (await api(
    context, 'GET', '/project-dictionaries?type=ROBOT_MODEL&parentId=' + vendors[0].id
      + '&enabledOnly=true', undefined, token)).json();
  const priorities = await (await api(
    context, 'GET', '/project-dictionaries?type=PRIORITY&enabledOnly=true', undefined, token)).json();
  assert(models.length > 0 && priorities.length > 0, 'file controls need model and priority options');
  const group = await (await api(context, 'POST', '/project-groups', {
    name,
    description: '独立文件控件浏览器验收夹具',
    supplierId,
    workOrderNos: ['WO-' + crypto.randomBytes(4).toString('hex')],
    machineModel: '文件控件机型',
    robotVendorId: vendors[0].id,
    robotModelId: models[0].id,
    responsibleUserId: ownerId,
    priorityId: priorities[0].id,
    expectedCompletionDate: '2099-12-31',
    subprojectNames: [name + ' 子项目'],
  }, token)).json();
  const detail = await (await api(
    context, 'GET', '/project-groups/' + group.id, undefined, token)).json();
  assert.equal(detail.projects.length, 1, 'file controls group must contain one subproject');
  return detail.projects[0];
}

async function assertCanvasRendered(canvas) {
  await canvas.waitFor({ state: 'visible' });
  const result = await canvas.evaluate(element => {
    const context = element.getContext('2d');
    if (!context || element.width < 1 || element.height < 1) return { width: 0, height: 0, ink: false };
    const pixels = context.getImageData(0, 0, element.width, element.height).data;
    let ink = false;
    for (let index = 0; index < pixels.length; index += 16) {
      if (pixels[index] < 245 || pixels[index + 1] < 245 || pixels[index + 2] < 245) {
        ink = true;
        break;
      }
    }
    return { width: element.width, height: element.height, ink };
  });
  assert(result.width > 0 && result.height > 0, 'PDF canvas has a backing store');
  assert.equal(result.ink, true, 'PDF canvas contains rendered non-white pixels');
  return result;
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

async function assertInsideViewport(locator, page, label) {
  const box = await locator.boundingBox();
  const viewport = page.viewportSize();
  assert(box && viewport, label + ' has a measurable viewport box');
  assert(box.x >= -1 && box.y >= -1, label + ' starts inside the viewport');
  assert(box.x + box.width <= viewport.width + 1, label + ' fits viewport width');
  assert(box.y + box.height <= viewport.height + 1, label + ' fits viewport height');
}

(async () => {
  let browser;
  let context;
  let page;
  let auth;
  const cleanupSessions = new Set();
  try {
    assert(f.suppliers?.a?.id, 'fixture supplier A is required');
    browser = await chromium.launch({ channel: 'chrome', headless: true });
    context = await browser.newContext({
      viewport: { width: 1440, height: 1000 },
      acceptDownloads: true,
    });
    page = await context.newPage();
    track(page, 'file-controls');
    auth = await login(page, 'admin', s.adminPassword);
    await page.waitForURL(s.base + '/');

    const json = async (method, path, body, expected = 200) =>
      (await api(context, method, path, body, auth.accessToken, expected)).json();
    const suffix = crypto.randomBytes(5).toString('hex');
    const prefix = '文件控件-' + suffix;
    const owners = await json('GET', '/project-owner-options');
    const owner = owners.find(item => item.sectionName?.trim());
    assert(owner, 'file controls need an active project owner with a section');
    const project = await createProjectGroup(
      context, auth.accessToken, f.suppliers.a.id, owner.id, prefix + '-项目');
    await json('PUT', `/projects/${project.id}/status`, { status: 'IN_PROGRESS' });
    assert.equal((await json('GET', `/projects/${project.id}`)).status, 'IN_PROGRESS');

    const deleteName = prefix + '-delete.pdf';
    const pdfName = prefix + '-two-pages.pdf';
    const excelName = prefix + '-fallback.xlsx';
    const deleteFile = await uploadFixture(
      context, auth.accessToken, project.id, deleteName, twoPagePdf('DELETE'),
    );
    const pdfFile = await uploadFixture(
      context, auth.accessToken, project.id, pdfName, twoPagePdf('MULTIPAGE'),
    );
    const excelFile = await uploadFixture(
      context, auth.accessToken, project.id, excelName, workbookBytes(suffix),
    );

    const listFiles = name => json(
      'GET', `/projects/${project.id}/files?keyword=${encodeURIComponent(name)}&page=1&pageSize=10`,
    );
    const row = name => page.getByRole('row').filter({ hasText: name });
    const popconfirm = () => page.locator('.arco-popconfirm:visible').last();
    const openUpload = async () => {
      await page.getByRole('button', { name: '上传文件', exact: true }).click();
      const dialog = page.getByRole('dialog', { name: '上传文件' });
      await dialog.waitFor();
      return dialog;
    };
    const openPreview = async name => {
      await row(name).getByRole('button', { name: '预览文件', exact: true }).click();
      const dialog = page.getByRole('dialog', { name: `预览：${name}` });
      await dialog.waitFor();
      return dialog;
    };
    const closePreview = async dialog => {
      await dialog.getByRole('button', { name: '关闭文件预览', exact: true }).click();
      await dialog.waitFor({ state: 'hidden' });
    };
    const withExpectedServerErrors = async (paths, operation) => {
      const previous = page.expectedServerErrors;
      page.expectedServerErrors = new Set([...(previous || []), ...paths]);
      try {
        return await operation();
      } finally {
        page.expectedServerErrors = previous;
      }
    };

    await page.goto(s.base + `/projects/${project.id}?tab=files`);
    await page.getByRole('tab', { name: '文件', exact: true, selected: true }).waitFor();
    await row(deleteName).waitFor();

    await record('单文件删除可取消，失败保留行，重试后API确认持久删除', async () => {
      let deleteRequests = 0;
      const countDelete = request => {
        if (request.method() === 'DELETE' && pathOf(request) === apiPath(`/files/${deleteFile.id}`)) {
          deleteRequests += 1;
        }
      };
      page.on('request', countDelete);
      try {
        await row(deleteName).getByRole('button', { name: '删除文件', exact: true }).click();
        await popconfirm().getByRole('button', { name: '取消', exact: true }).click();
        assert.equal(deleteRequests, 0, 'cancel must not send a delete request');
        assert.equal((await listFiles(deleteName)).total, 1, 'cancelled file remains persisted');

        const deletePath = apiPath(`/files/${deleteFile.id}`);
        await page.route('**' + deletePath, route => route.fulfill({
          status: 503,
          contentType: 'application/json',
          body: JSON.stringify({ code: 50301, message: '验收模拟文件删除失败' }),
        }), { times: 1 });
        await withExpectedServerErrors([deletePath], async () => {
          const failed = page.waitForResponse(response =>
            pathOf(response) === deletePath && response.request().method() === 'DELETE');
          await row(deleteName).getByRole('button', { name: '删除文件', exact: true }).click();
          await popconfirm().getByRole('button', { name: '确定', exact: true }).click();
          assert.equal((await failed).status(), 503);
          await page.getByText('验收模拟文件删除失败', { exact: true }).waitFor();
        });
        if (await popconfirm().count()) {
          await popconfirm().getByRole('button', { name: '取消', exact: true }).click();
        }
        await row(deleteName).waitFor();
        assert.equal((await listFiles(deleteName)).total, 1, 'failed delete does not change persistence');

        const deleted = page.waitForResponse(response =>
          pathOf(response) === deletePath && response.request().method() === 'DELETE');
        await row(deleteName).getByRole('button', { name: '删除文件', exact: true }).click();
        await popconfirm().getByRole('button', { name: '确定', exact: true }).click();
        assert.equal((await deleted).status(), 200);
        await row(deleteName).waitFor({ state: 'detached' });
        assert.equal((await listFiles(deleteName)).total, 0, 'successful retry persists deletion');
      } finally {
        page.off('request', countDelete);
      }
    });

    await record('选择文件后在上传开始前关闭不创建上传会话或文件', async () => {
      const name = prefix + '-prestart-close.zip';
      let uploadRequests = 0;
      const countUpload = request => {
        if (pathOf(request).startsWith('/api/v1/uploads/')) uploadRequests += 1;
      };
      page.on('request', countUpload);
      try {
        const dialog = await openUpload();
        await dialog.getByLabel('选择上传文件').setInputFiles({
          name,
          mimeType: 'application/zip',
          buffer: Buffer.from('selected-but-never-uploaded|' + suffix),
        });
        await dialog.getByText('文件已选择，点击“上传所选文件”后开始上传。', { exact: true }).waitFor();
        await dialog.getByRole('button', { name: '关闭', exact: true }).click();
        await dialog.waitFor({ state: 'hidden' });
        assert.equal(uploadRequests, 0, 'closing before start must not call upload APIs');
        assert.equal((await listFiles(name)).total, 0, 'closing before start creates no file');
      } finally {
        page.off('request', countUpload);
      }
    });

    await record('上传中取消失败显示重试，重试后会话ABORTED且未生成文件', async () => {
      const name = prefix + '-cancel-in-progress.zip';
      const bytes = Buffer.alloc(1024 * 1024 + 37, 0x43);
      const chunkPattern = '**/api/v1/uploads/*/chunks/*';
      let releaseChunk = () => {};
      let reachChunk = () => {};
      const released = new Promise(resolve => { releaseChunk = resolve; });
      const reached = new Promise(resolve => { reachChunk = resolve; });
      const heldChunk = async route => {
        reachChunk(pathOf(route.request()));
        await released;
        await route.continue().catch(() => undefined);
      };
      await page.route(chunkPattern, heldChunk);
      let sessionId;
      try {
        const dialog = await openUpload();
        await dialog.getByLabel('选择上传文件').setInputFiles({
          name, mimeType: 'application/zip', buffer: bytes,
        });
        const initialized = page.waitForResponse(response =>
          pathOf(response) === '/api/v1/uploads/init'
          && response.request().method() === 'POST' && response.status() === 200);
        await dialog.getByRole('button', { name: '上传所选文件', exact: true }).click();
        sessionId = (await (await initialized).json()).sessionId;
        cleanupSessions.add(sessionId);
        const heldPath = await reached;
        assert(heldPath.startsWith(apiPath(`/uploads/${sessionId}/chunks/`)));

        const abortPath = apiPath(`/uploads/${sessionId}`);
        await page.route('**' + abortPath, route => route.fulfill({
          status: 503,
          contentType: 'application/json',
          body: JSON.stringify({ code: 50301, message: '验收模拟取消失败' }),
        }), { times: 1 });
        await withExpectedServerErrors([abortPath], async () => {
          const failedAbort = page.waitForResponse(response =>
            pathOf(response) === abortPath && response.request().method() === 'DELETE');
          await dialog.getByRole('button', { name: '取消上传', exact: true }).click();
          releaseChunk();
          assert.equal((await failedAbort).status(), 503);
          await dialog.getByRole('button', { name: '重试取消', exact: true }).waitFor();
          await page.getByText('无法确认取消结果，请重试取消', { exact: true }).waitFor();
        });
        assert.equal((await json('GET', `/uploads/${sessionId}`)).status, 'UPLOADING');
        assert.equal((await listFiles(name)).total, 0);

        const retriedAbort = page.waitForResponse(response =>
          pathOf(response) === abortPath && response.request().method() === 'DELETE');
        await dialog.getByRole('button', { name: '重试取消', exact: true }).click();
        assert.equal((await retriedAbort).status(), 200);
        await dialog.waitFor({ state: 'hidden' });
        assert.equal((await json('GET', `/uploads/${sessionId}`)).status, 'ABORTED');
        assert.equal((await listFiles(name)).total, 0, 'aborted session creates no file');
        cleanupSessions.delete(sessionId);
      } finally {
        releaseChunk();
        await page.unroute(chunkPattern, heldChunk);
      }
    });

    await record('分片上传失败后从同一会话重试成功且API确认文件持久化', async () => {
      const name = prefix + '-retry-success.zip';
      const bytes = Buffer.alloc(1024 * 1024 + 91, 0x52);
      const previousExpected = page.expectedServerErrors;
      page.expectedServerErrors = new Set(previousExpected || []);
      let failedChunkPath;
      const failFirstChunk = async route => {
        failedChunkPath = pathOf(route.request());
        page.expectedServerErrors.add(failedChunkPath);
        await route.fulfill({
          status: 503,
          contentType: 'application/json',
          body: JSON.stringify({ code: 50301, message: '验收模拟分片上传失败' }),
        });
      };
      await page.route('**/api/v1/uploads/*/chunks/*', failFirstChunk, { times: 1 });
      try {
        const dialog = await openUpload();
        await dialog.getByLabel('选择上传文件').setInputFiles({
          name, mimeType: 'application/zip', buffer: bytes,
        });
        const firstInitReady = page.waitForResponse(response =>
          pathOf(response) === '/api/v1/uploads/init'
          && response.request().method() === 'POST' && response.status() === 200);
        await dialog.getByRole('button', { name: '上传所选文件', exact: true }).click();
        const firstInit = await (await firstInitReady).json();
        cleanupSessions.add(firstInit.sessionId);
        await page.getByText('上传中断，可点击开始后从断点续传', { exact: true }).waitFor();
        await page.getByText('验收模拟分片上传失败', { exact: true }).waitFor();
        assert(failedChunkPath?.startsWith(apiPath(`/uploads/${firstInit.sessionId}/chunks/`)));

        const secondInitReady = page.waitForResponse(response =>
          pathOf(response) === '/api/v1/uploads/init'
          && response.request().method() === 'POST' && response.status() === 200);
        const mergeReady = page.waitForResponse(response =>
          /^\/api\/v1\/uploads\/[^/]+\/merge$/.test(pathOf(response))
          && response.request().method() === 'POST');
        const refreshedList = page.waitForResponse(response =>
          pathOf(response) === apiPath(`/projects/${project.id}/files`)
          && response.request().method() === 'GET' && response.status() === 200);
        await dialog.getByRole('button', { name: '上传所选文件', exact: true }).click();
        const secondInit = await (await secondInitReady).json();
        assert.equal(secondInit.sessionId, firstInit.sessionId, 'retry reuses interrupted session');
        assert.equal(secondInit.resumed, true);
        assert.equal((await mergeReady).status(), 200);
        await refreshedList;
        await dialog.waitFor({ state: 'hidden' });
        await row(name).waitFor();
        const persisted = await listFiles(name);
        assert.equal(persisted.total, 1);
        assert.equal(persisted.list[0].originalName, name);
        cleanupSessions.delete(firstInit.sessionId);
      } finally {
        page.expectedServerErrors = previousExpected;
        await page.unroute('**/api/v1/uploads/*/chunks/*', failFirstChunk);
      }
    });

    await record('PDF两页、页码、缩放及上一页下一页均触发真实画布渲染', async () => {
      const dialog = await openPreview(pdfName);
      const previous = dialog.getByRole('button', { name: '上一页', exact: true });
      const next = dialog.getByRole('button', { name: '下一页', exact: true });
      const pageNumber = dialog.getByLabel('PDF 页码', { exact: true });
      const zoom = dialog.getByLabel('PDF 缩放', { exact: true });
      await dialog.getByText('/ 2 页', { exact: true }).waitFor();
      assert(await previous.isDisabled());
      await assertCanvasRendered(dialog.getByRole('img', { name: 'PDF 第 1 页', exact: true }));

      await next.click();
      await assertCanvasRendered(dialog.getByRole('img', { name: 'PDF 第 2 页', exact: true }));
      assert(await next.isDisabled());
      assert.equal(await pageNumber.inputValue(), '2');

      await previous.click();
      await assertCanvasRendered(dialog.getByRole('img', { name: 'PDF 第 1 页', exact: true }));
      await pageNumber.fill('2');
      await pageNumber.press('Enter');
      await assertCanvasRendered(dialog.getByRole('img', { name: 'PDF 第 2 页', exact: true }));

      await zoom.click();
      await page.getByRole('option', { name: '150%', exact: true }).click();
      const zoomed = dialog.getByRole('img', { name: 'PDF 第 2 页', exact: true });
      await assertCanvasRendered(zoomed);
      const cssWidth = await zoomed.evaluate(element => Number.parseFloat(element.style.width));
      assert(Math.abs(cssWidth - 918) < 2, '150% zoom renders the 612pt page at about 918 CSS px');
      await dialog.screenshot({ path: OUT + '/pdf-page-two-150-percent.png', animations: 'disabled' });
      await closePreview(dialog);
    });

    await record('PDF读取失败显示重试，恢复渲染且列表仍可下载原文件', async () => {
      const contentPath = apiPath(`/files/${pdfFile.id}/content`);
      await page.route('**' + contentPath, route => route.fulfill({
        status: 503,
        contentType: 'application/json',
        body: JSON.stringify({ code: 50301, message: '验收模拟PDF读取失败' }),
      }), { times: 1 });
      await withExpectedServerErrors([contentPath], async () => {
        const dialog = await openPreview(pdfName);
        await dialog.getByRole('alert').getByText(
          'PDF 加载失败，文件可能损坏或网络暂时不可用，请重试。',
          { exact: true },
        ).waitFor();
        const retry = dialog.getByRole('button', { name: '重试 PDF 预览', exact: true });
        await retry.waitFor();
        await retry.click();
        await assertCanvasRendered(dialog.getByRole('img', { name: 'PDF 第 1 页', exact: true }));
        await closePreview(dialog);
        const downloadReady = page.waitForEvent('download');
        await row(pdfName).getByRole('button', { name: '下载文件', exact: true }).click();
        const download = await downloadReady;
        assert.equal(download.suggestedFilename(), pdfName);
        assert.equal(await download.failure(), null);
      });
    });

    await record('Excel读取失败显示明确警告，重试恢复真实单元格且列表可下载', async () => {
      const contentPath = apiPath(`/files/${excelFile.id}/content`);
      await page.route('**' + contentPath, route => route.fulfill({
        status: 503,
        contentType: 'application/json',
        body: JSON.stringify({ code: 50301, message: '验收模拟Excel读取失败' }),
      }), { times: 1 });
      await withExpectedServerErrors([contentPath], async () => {
        const dialog = await openPreview(excelName);
        await dialog.getByText('Excel 预览失败', { exact: true }).waitFor();
        await dialog.getByText('文件可能损坏、受密码保护，或包含暂不支持的内容。', { exact: true }).waitFor();
        await dialog.getByRole('button', { name: '重试预览', exact: true }).click();
        await assertExcelGrid(dialog, [['文件控件验收', '结果'], [suffix, 'DOWNLOAD-FALLBACK']]);
        await closePreview(dialog);
        const downloadReady = page.waitForEvent('download');
        await row(excelName).getByRole('button', { name: '下载文件', exact: true }).click();
        const download = await downloadReady;
        assert.equal(download.suggestedFilename(), excelName);
        assert.equal(await download.failure(), null);
      });
    });

    await record('390x600上传与PDF预览弹窗不越界且键盘焦点顺序可达', async () => {
      await page.setViewportSize({ width: 390, height: 600 });
      const upload = await openUpload();
      await assertInsideViewport(upload, page, 'upload modal');
      const uploadOverflow = await upload.evaluate(element => ({
        clientWidth: element.clientWidth,
        scrollWidth: element.scrollWidth,
      }));
      assert(uploadOverflow.scrollWidth <= uploadOverflow.clientWidth + 1, 'upload modal has no horizontal overflow');
      // The native file input is intentionally hidden; the visible picker button
      // is the keyboard-operable control in the modal's focus order.
      const picker = upload.getByRole('button', { name: '选择文件', exact: true });
      await picker.focus();
      await page.keyboard.press('Shift+Tab');
      assert(await upload.evaluate(element => element.contains(document.activeElement)), 'Shift+Tab stays in upload dialog');
      await page.keyboard.press('Tab');
      assert(await picker.evaluate(element => element === document.activeElement), 'Tab returns focus to file picker');
      await page.keyboard.press('Escape');
      await upload.waitFor({ state: 'hidden' });

      const preview = await openPreview(pdfName);
      await assertInsideViewport(preview, page, 'PDF preview modal');
      const pdfViewport = preview.getByLabel('PDF 页面内容', { exact: true });
      await pdfViewport.focus();
      assert(await pdfViewport.evaluate(element => element === document.activeElement));
      await assertCanvasRendered(preview.getByRole('img', { name: 'PDF 第 1 页', exact: true }));
      const pageOverflow = await page.evaluate(() => ({
        viewport: document.documentElement.clientWidth,
        documentWidth: document.documentElement.scrollWidth,
      }));
      assert(pageOverflow.documentWidth <= pageOverflow.viewport + 1, '390px page has no root horizontal overflow');
      await preview.screenshot({ path: OUT + '/pdf-preview-390x600.png', animations: 'disabled' });
      await page.keyboard.press('Escape');
      await preview.waitFor({ state: 'hidden' });
      await page.setViewportSize({ width: 1440, height: 1000 });
    });

    await page.waitForFunction(() => document.querySelectorAll('.arco-message').length === 0);
    await page.screenshot({
      path: OUT + '/file-controls-final.png', animations: 'disabled', fullPage: true,
    });
  } catch (error) {
    if (page) {
      await page.screenshot({
        path: OUT + '/file-controls-failure.png', animations: 'disabled', fullPage: true,
      }).catch(() => undefined);
      console.log((await page.locator('body').innerText()).slice(-5000));
    }
    console.error(error.stack);
    process.exitCode = 1;
  } finally {
    if (context && auth?.accessToken) {
      for (const sessionId of cleanupSessions) {
        await context.request.fetch(s.base + apiPath(`/uploads/${sessionId}`), {
          method: 'DELETE',
          headers: { Origin: s.base, Authorization: 'Bearer ' + auth.accessToken },
        }).catch(() => undefined);
      }
    }
    if (browser) await browser.close();
  }
})();
