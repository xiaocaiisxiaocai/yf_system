const { chromium } = require('playwright');
const XLSX = require(process.env.YF_PROJECT_ROOT + '/web/node_modules/xlsx');
const { fs, assert, OUT, s, record, login, api, navigate, action, track } =
  require(process.env.YF_BROWSER_SUPPORT_DIR + '/ui-lib.cjs');

const marker = require('node:crypto').randomBytes(5).toString('hex');
const emailType = 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet';
let accountSpecs = [];

(async () => {
  let browser;
  let context;
  let page;
  let supplier;
  let authToken;
  let accounts = [];
  try {
    browser = await require(process.env.YF_BROWSER_SUPPORT_DIR + '/shared-browser.cjs')(chromium);
    context = await browser.newContext({ viewport: { width: 1440, height: 1000 }, acceptDownloads: true });
    page = await context.newPage();
    track(page, 'supplier-excel');
    const auth = await login(page, 'admin', s.adminPassword);
    authToken = auth.accessToken;
    await page.waitForURL(s.base + '/');
    const read = async (method, url, body, expected = 200) =>
      (await api(context, method, url, body, auth.accessToken, expected)).json();
    const supplierName = 'Excel 验收供应商-' + marker;

    await navigate(page, '/suppliers');
    await page.getByRole('heading', { name: '供应商管理', exact: true }).waitFor();
    await record('已存在但无账号和联络人的 Robot 供应商可以从当前页面建立档案', async () => {
      await page.getByRole('button', { name: '新增供应商', exact: true }).click();
      const modal = page.locator('.arco-modal:visible').last();
      await modal.getByPlaceholder('公司全称', { exact: true }).fill(supplierName);
      supplier = await action(page, '/admin/suppliers', 'POST',
        () => modal.getByRole('button', { name: '创建供应商', exact: true }).click());
      assert(supplier.id > 0);
      assert.equal((await read('GET', `/admin/suppliers/${supplier.id}/accounts`)).length, 0);
      assert.equal((await read('GET', `/admin/suppliers/${supplier.id}/contacts`)).length, 0);
      await page.getByRole('row').filter({ hasText: supplierName }).waitFor();
    });

    await record('Robot 账号模板仅含新增必需字段，短密码拦截，重复填写厂商可批量新增账号', async () => {
      const makeFile = (headers, rows) => {
        const book = XLSX.utils.book_new();
        XLSX.utils.book_append_sheet(book, XLSX.utils.aoa_to_sheet([headers, ...rows]), '数据');
        return XLSX.write(book, { type: 'buffer', bookType: 'xlsx' });
      };
      const openAccountImport = async () => {
        await page.getByRole('button', { name: '导入账号', exact: true }).click();
        const modal = page.locator('.arco-modal:visible').last();
        await modal.getByText('导入 Robot 供应商账号', { exact: true }).waitFor();
        return modal;
      };
      let modal = await openAccountImport();
      const templateDownload = page.waitForEvent('download');
      await modal.getByRole('button', { name: '下载模板', exact: true }).click();
      const template = await templateDownload;
      const templatePath = OUT + '/robot-account-template.xlsx';
      await template.saveAs(templatePath);
      const templateBook = XLSX.read(fs.readFileSync(templatePath), { type: 'buffer' });
      assert.deepEqual(XLSX.utils.sheet_to_json(templateBook.Sheets.数据, { header: 1 })[0],
        ['供应商名称', '账号', '角色', '初始密码']);

      const rejectedName = 'robot_short_' + marker;
      await modal.getByLabel('选择 Excel 文件', { exact: true }).setInputFiles({
        name: 'robot-account-short-password.xlsx', mimeType: emailType,
        buffer: makeFile(['供应商名称', '账号', '角色', '初始密码'], [[supplierName, rejectedName, '', 'Ab1!']]),
      });
      await modal.getByRole('row').filter({ hasText: rejectedName }).getByText('有错误', { exact: true }).waitFor();
      assert.equal(await modal.getByRole('button', { name: '确认导入', exact: true }).isDisabled(), true);
      assert.equal((await read('GET', `/admin/suppliers/${supplier.id}/accounts`)).length, 0);
      await modal.getByRole('button', { name: '关闭', exact: true }).click();

      accountSpecs = [
        { employeeNo: 'robot_excel_a_' + marker, password: 'T3st#A9q' },
        { employeeNo: 'robot_excel_b_' + marker, password: 'T3st#B9q' },
      ];
      modal = await openAccountImport();
      await modal.getByLabel('选择 Excel 文件', { exact: true }).setInputFiles({
        name: 'robot-accounts.xlsx', mimeType: emailType,
        buffer: makeFile(['供应商名称', '账号', '角色', '初始密码'], [
          [supplierName, accountSpecs[0].employeeNo, '供应商人员', accountSpecs[0].password],
          [supplierName, accountSpecs[1].employeeNo, '', accountSpecs[1].password],
        ]),
      });
      for (const account of accountSpecs)
        await modal.getByRole('row').filter({ hasText: account.employeeNo }).getByText('待导入', { exact: true }).waitFor();
      await modal.getByRole('button', { name: '确认导入', exact: true }).click();
      for (const account of accountSpecs)
        await modal.getByRole('row').filter({ hasText: account.employeeNo }).getByText('已导入', { exact: true }).waitFor();
      accounts = await read('GET', `/admin/suppliers/${supplier.id}/accounts`);
      assert.equal(accounts.length, 2);
      for (const account of accountSpecs) {
        const saved = accounts.find(item => item.employeeNo === account.employeeNo);
        assert(saved, 'imported account exists: ' + account.employeeNo);
        assert.equal(saved.status, 'ACTIVE', 'new account defaults to active');
        assert.equal(saved.lastLoginAt, null, 'new account has not logged in');
      }
      const vendorContext = await browser.newContext({ viewport: { width: 1440, height: 1000 } });
      const vendorPage = await vendorContext.newPage();
      track(vendorPage, 'supplier-excel-first-login');
      const firstLogin = await login(vendorPage, accountSpecs[0].employeeNo, accountSpecs[0].password);
      assert.equal(firstLogin.mustChangePassword, true, 'new account requires first-login password change');
      await vendorPage.waitForURL('**/change-password');
      await vendorContext.close();
      await modal.getByRole('button', { name: '关闭', exact: true }).click();
    });

    await record('Robot 联络人模板不包含状态，多位联络人批量导入并从当前目录导出', async () => {
      const makeFile = rows => {
        const book = XLSX.utils.book_new();
        XLSX.utils.book_append_sheet(book, XLSX.utils.aoa_to_sheet([
          ['供应商名称', '联络人姓名', '邮箱', '排序'], ...rows,
        ]), '数据');
        return XLSX.write(book, { type: 'buffer', bookType: 'xlsx' });
      };
      await page.getByRole('button', { name: '导入联络人', exact: true }).click();
      let modal = page.locator('.arco-modal:visible').last();
      await modal.getByText('导入 Robot 供应商联络人', { exact: true }).waitFor();
      const templateDownload = page.waitForEvent('download');
      await modal.getByRole('button', { name: '下载模板', exact: true }).click();
      const template = await templateDownload;
      const templatePath = OUT + '/robot-contact-template.xlsx';
      await template.saveAs(templatePath);
      const templateBook = XLSX.read(fs.readFileSync(templatePath), { type: 'buffer' });
      assert.deepEqual(XLSX.utils.sheet_to_json(templateBook.Sheets.数据, { header: 1 })[0],
        ['供应商名称', '联络人姓名', '邮箱', '排序']);

      const contactSpecs = [1, 2].map(index => ({
        name: `Robot Excel 联络人 ${index}-${marker}`,
        email: `robot-excel-${index}-${marker}@vendor.invalid`,
      }));
      await modal.getByLabel('选择 Excel 文件', { exact: true }).setInputFiles({
        name: 'robot-contacts.xlsx', mimeType: emailType,
        buffer: makeFile(contactSpecs.map((contact, index) =>
          [supplierName, contact.name, contact.email, String(index + 1)])),
      });
      for (const contact of contactSpecs)
        await modal.getByRole('row').filter({ hasText: contact.email }).getByText('待导入', { exact: true }).waitFor();
      await modal.getByRole('button', { name: '确认导入', exact: true }).click();
      for (const contact of contactSpecs)
        await modal.getByRole('row').filter({ hasText: contact.email }).getByText('已导入', { exact: true }).waitFor();
      const contacts = await read('GET', `/admin/suppliers/${supplier.id}/contacts`);
      assert.equal(contacts.length, 2);
      for (const contact of contactSpecs) {
        const saved = contacts.find(item => item.email === contact.email);
        assert(saved && saved.name === contact.name);
        assert.equal(saved.status, 'ACTIVE');
      }
      await modal.getByRole('button', { name: '关闭', exact: true }).click();

      const exportWorkbook = async buttonName => {
        const ready = page.waitForEvent('download');
        await page.getByRole('button', { name: buttonName, exact: true }).click();
        const file = await ready;
        assert.equal(await file.failure(), null);
        const target = OUT + '/robot-' + buttonName + '.xlsx';
        await file.saveAs(target);
        return XLSX.read(fs.readFileSync(target), { type: 'buffer' });
      };
      const accountBook = await exportWorkbook('导出账号');
      const accountRows = XLSX.utils.sheet_to_json(accountBook.Sheets.数据, { header: 1 });
      assert.deepEqual(accountRows[0], ['供应商名称', '账号', '角色', '状态', '最近登录']);
      const exportedAccounts = accountRows.filter(row => row[0] === supplierName);
      assert.equal(exportedAccounts.length, 2);
      assert.equal(exportedAccounts.some(row => accountSpecs.some(item => row.includes(item.password))), false,
        'account export never includes initial passwords');

      const contactBook = await exportWorkbook('导出联络人');
      const contactRows = XLSX.utils.sheet_to_json(contactBook.Sheets.数据, { header: 1 });
      assert.deepEqual(contactRows[0], ['供应商名称', '联络人姓名', '邮箱', '排序', '状态']);
      const exportedContacts = contactRows.filter(row => row[0] === supplierName);
      assert.equal(exportedContacts.length, 2);
      assert(exportedContacts.some(row => row.includes(contactSpecs[0].email)));
      assert(exportedContacts.some(row => row.includes(contactSpecs[1].email)));
    });
  } catch (error) {
    if (page) {
      await page.screenshot({ path: OUT + '/supplier-excel-failure.png', fullPage: true }).catch(() => {});
      console.log((await page.locator('body').innerText()).slice(-4000));
    }
    console.error(error.stack);
    process.exitCode = 1;
  } finally {
    if (supplier?.id && authToken) {
      for (const item of accounts) {
        await api(context, 'DELETE', '/admin/supplier-accounts/' + item.id, undefined, authToken, 200).catch(() => {});
      }
      await api(context, 'DELETE', '/admin/suppliers/' + supplier.id, undefined, authToken, 200).catch(() => {});
    }
    if (browser) await browser.close();
  }
})();
