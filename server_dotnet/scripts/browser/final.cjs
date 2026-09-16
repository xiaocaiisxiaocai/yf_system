const { chromium } = require('playwright');
const {
  fs, assert, OUT, s, f, record, login, api, navigate, action, track,
} = require(process.env.YF_BROWSER_SUPPORT_DIR + '/ui-lib.cjs');

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
  let browser;
  let page;
  try {
    browser = await chromium.launch({ channel: 'chrome', headless: true });
    fs.writeFileSync(OUT + '/browser-environment.json', JSON.stringify({
      browser: browser.version(),
      mode: 'headless desktop Chrome',
      viewports: [[1440, 1000], [1024, 900]],
      host: 'separate TestHost with production API',
    }, null, 2));
    const context = await browser.newContext({
      viewport: { width: 1440, height: 1000 },
      acceptDownloads: true,
    });
    page = await context.newPage();
    track(page, 'final');
    const auth = await login(page, 'admin', s.adminPassword);
    await page.waitForURL(s.base + '/');

    await record('完成项目文件仍可批量下载并保存ZIP', async () => {
      await navigate(page, '/projects/' + f.uiProjects.b.id);
      await page.getByRole('row').filter({ hasText: 'vendor-response.xlsx' }).waitFor();
      await page.getByRole('checkbox').first().locator('..').click();
      const download = page.waitForEvent('download');
      await page.getByRole('button', { name: '打包下载（2）', exact: true }).click();
      const item = await download;
      await item.saveAs(OUT + '/browser-batch.zip');
      assert.equal(await item.failure(), null);
    });

    await record('PDF与Excel预览稳定渲染截图', async () => {
      for (const [name, kind] of [['valid-preview.pdf', 'pdf'], ['vendor-response.xlsx', 'excel']]) {
        await page.getByRole('row').filter({ hasText: name })
          .getByRole('button', { name: '预览文件', exact: true }).click();
        const dialog = page.getByRole('dialog', { name: `预览：${name}` });
        if (kind === 'pdf') await dialog.getByRole('img', { name: 'PDF 第 1 页' }).waitFor();
        else await assertExcelGrid(dialog, [['公司', '验收结果'], ['甲公司', 'PASS']]);
        await dialog.screenshot({ path: OUT + '/final-' + kind + '.png', animations: 'disabled' });
        await dialog.getByRole('button', { name: '关闭文件预览', exact: true }).click();
        await dialog.waitFor({ state: 'hidden' });
      }
    });

    await record('个人资料邮箱修改并刷新保留', async () => {
      await navigate(page, '/profile');
      await page.getByPlaceholder('请输入联系邮箱').fill('final.ui@example.invalid');
      await action(page, '/auth/profile', 'PUT',
        () => page.getByRole('button', { name: '保存资料', exact: true }).click());
      await page.reload();
      await page.getByPlaceholder('请输入联系邮箱').waitFor();
      assert.equal(await page.getByPlaceholder('请输入联系邮箱').inputValue(), 'final.ui@example.invalid');
    });

    await record('管理列表和系统参数当前数据加载正常', async () => {
      for (const [route, apiPath] of [
        ['/org/users', '/api/v1/admin/users'],
        ['/suppliers', '/api/v1/admin/suppliers'],
        ['/system/config', '/api/v1/admin/system/configs'],
        ['/logs', '/api/v1/admin/audit-logs'],
      ]) {
        const loaded = page.waitForResponse(response =>
          new URL(response.url()).pathname === apiPath && response.status() === 200);
        await navigate(page, route);
        await loaded;
        assert.equal(await page.getByText('加载失败', { exact: true }).count(), 0);
      }
      await page.screenshot({ path: OUT + '/final-audit.png', animations: 'disabled', fullPage: true });
    });

    await record('同源第二标签会话恢复、退出同步及访问令牌撤销', async () => {
      const second = await context.newPage();
      track(second, 'second-tab');
      await navigate(second, '/projects');
      await second.getByRole('button', { name: '新建主项目', exact: true }).waitFor();
      await page.getByRole('button', { name: '账号菜单：系统管理员', exact: true }).click();
      await page.getByRole('menuitem', { name: '退出登录', exact: true }).click();
      await page.waitForURL('**/login');
      await second.waitForURL('**/login');
      await api(context, 'GET', '/auth/profile', undefined, auth.accessToken, 401);
    });
  } catch (error) {
    if (page) {
      await page.screenshot({
        path: OUT + '/final-ui-failure.png', animations: 'disabled', fullPage: true,
      }).catch(() => {});
      console.log((await page.locator('body').innerText()).slice(-3000));
    }
    console.error(error.stack || error.message);
    process.exitCode = 1;
  } finally {
    if (browser) await browser.close();
  }
})();
