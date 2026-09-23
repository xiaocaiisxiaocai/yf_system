const { chromium } = require('playwright');
const crypto = require('node:crypto');
const {
  assert, OUT, s, f, record, login, api, navigate, track,
} = require(process.env.YF_BROWSER_SUPPORT_DIR + '/ui-lib.cjs');

(async () => {
  let browser;
  let page;
  try {
    browser = await chromium.launch({ channel: 'chrome', headless: true });
    const context = await browser.newContext({ viewport: { width: 1440, height: 1000 } });
    page = await context.newPage();
    track(page, 'project-metadata');
    const auth = await login(page, 'admin', s.adminPassword);
    await page.waitForURL(s.base + '/');
    const token = auth.accessToken;
    const suffix = crypto.randomBytes(5).toString('hex');
    const request = async (method, path, body, expected = 200) => (
      await api(context, method, path, body, token, expected)
    ).json();

    await navigate(page, '/system/dictionaries');
    await record('项目基础数据页面显示 Robot 厂商、料号、型号和优先级合同', async () => {
      await page.getByText('Robot 厂商', { exact: true }).first().waitFor();
      await page.getByText('Robot 料号', { exact: true }).first().waitFor();
      await page.getByText('Robot 型号', { exact: true }).first().waitFor();
      await page.getByText('优先级', { exact: true }).first().waitFor();
    });

    const partNumber = 'BROWSER-' + suffix.toUpperCase();
    let part;
    await record('Robot 料号 API 按供应商创建、筛选和更新', async () => {
      part = await request('POST', '/robot-parts', {
        supplierId: f.suppliers.a.id,
        partNumber,
        model: '浏览器目录型号-' + suffix,
        sortNo: 901,
        enabled: true,
      });
      const listed = await request(
        'GET', '/robot-parts?supplierId=' + f.suppliers.a.id + '&enabledOnly=true');
      const saved = listed.find(item => item.id === part.id);
      assert(saved, 'created robot part must be returned by supplier filter');
      assert.equal(saved.supplierId, f.suppliers.a.id);
      assert.equal(saved.supplierName, f.suppliers.a.name);
      assert.equal(saved.partNumber, partNumber);
      assert.equal(saved.inUse, false);
      part = await request('PUT', '/robot-parts/' + part.id, {
        supplierId: f.suppliers.a.id,
        partNumber: partNumber + '-EDITED',
        model: '浏览器目录型号已编辑-' + suffix,
        sortNo: 902,
        enabled: false,
      });
      assert.equal(part.enabled, false);
      assert.equal(part.sortNo, 902);
    });

    await record('未引用 Robot 料号可删除且不再出现在目录', async () => {
      await api(context, 'DELETE', '/robot-parts/' + part.id, undefined, token);
      const listed = await request('GET', '/robot-parts?supplierId=' + f.suppliers.a.id);
      assert.equal(listed.some(item => item.id === part.id), false);
    });

    await page.screenshot({ path: OUT + '/project-metadata.png', fullPage: true });
    await context.close();
  } catch (error) {
    if (page) {
      await page.screenshot({ path: OUT + '/project-metadata-failure.png', fullPage: true }).catch(() => {});
      console.log((await page.locator('body').innerText()).slice(-5000));
    }
    console.error(error.stack || error.message);
    process.exitCode = 1;
  } finally {
    if (browser) await browser.close();
  }
})();
