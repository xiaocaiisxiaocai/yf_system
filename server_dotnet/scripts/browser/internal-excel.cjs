const { chromium } = require('playwright');
const crypto = require('node:crypto');
const XLSX = require(process.env.YF_PROJECT_ROOT + '/web/node_modules/xlsx');
const { assert, OUT, s, f, api, record, login, navigate, track } =
  require(process.env.YF_BROWSER_SUPPORT_DIR + '/ui-lib.cjs');

const fileType = 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet';
const marker = crypto.randomBytes(5).toString('hex');

(async () => {
  let browser;
  let context;
  let page;
  let token;
  let user;
  let section;
  let department;
  let division;
  const read = async (method, url, body, expected = 200) =>
    (await api(context, method, url, body, token, expected)).json();
  try {
    browser = await require(process.env.YF_BROWSER_SUPPORT_DIR + '/shared-browser.cjs')(chromium);
    context = await browser.newContext({ viewport: { width: 1440, height: 1000 }, acceptDownloads: true });
    page = await context.newPage();
    track(page, 'internal-excel');
    const auth = await login(page, 'admin', s.adminPassword);
    token = auth.accessToken;
    await page.waitForURL(s.base + '/');
    const sectionName = 'Excel 验收课别-' + marker;
    const userName = 'Excel 内部用户-' + marker;
    const employeeNo = 'excel_user_' + marker;
    const email = 'internal-excel-' + marker + '@example.invalid';
    const initialPassword = 'I5nt#A9q';
    const roleName = '内部成员';

    await record('公司内部账号模板按组织名称（任一层级）和角色选择组织，不含路径或编号', async () => {
      division = await read('POST', '/admin/departments', {
        name: 'Excel 验收事业部-' + marker, parentId: null, sortNo: 970,
      });
      department = await read('POST', '/admin/departments', {
        name: 'Excel 验收部门-' + marker, parentId: division.id, sortNo: 971,
      });
      section = await read('POST', '/admin/departments', {
        name: sectionName, parentId: department.id, sortNo: 972,
      });
      assert.equal(section.kind, 'SECTION');
      await navigate(page, '/org/users');
      await page.getByRole('heading', { name: '用户管理', exact: true }).waitFor();
      await page.getByRole('button', { name: '导入 Excel', exact: true }).waitFor();
      await page.getByRole('button', { name: '导入 Excel', exact: true }).click();
      const modal = page.locator('.arco-modal:visible').last();
      await modal.getByText('导入公司内部账号', { exact: true }).waitFor();
      const download = page.waitForEvent('download');
      await modal.getByRole('button', { name: '下载模板', exact: true }).click();
      const file = await download;
      assert.equal(await file.failure(), null);
      const target = OUT + '/company-internal-account-template.xlsx';
      await file.saveAs(target);
      const workbook = XLSX.read(require('node:fs').readFileSync(target), { type: 'buffer' });
      const dataHeaders = XLSX.utils.sheet_to_json(workbook.Sheets.数据, { header: 1 })[0];
      assert.deepEqual(dataHeaders, ['账号', '姓名', '邮箱', '所属组织', '角色', '初始密码']);
      const optionRows = XLSX.utils.sheet_to_json(workbook.Sheets.选项, { header: 1 });
      assert.deepEqual(optionRows[0], ['所属组织', '组织层级', '角色名称']);
      assert(optionRows.some(row => row[0] === sectionName && row[1] === '课别'));
      await modal.getByRole('button', { name: '关闭', exact: true }).click();
    });

    await record('内部账号 Excel 四位密码被拒，启用账号默认需改密且无最近登录', async () => {
      const makeWorkbook = password => {
        const book = XLSX.utils.book_new();
        XLSX.utils.book_append_sheet(book, XLSX.utils.aoa_to_sheet([
          ['账号', '姓名', '邮箱', '所属组织', '角色', '初始密码'],
          [employeeNo, userName, email, sectionName, roleName, password],
        ]), '数据');
        XLSX.utils.book_append_sheet(book, XLSX.utils.aoa_to_sheet([
          ['所属组织', '组织层级', '角色名称'], [sectionName, '课别', roleName],
        ]), '选项');
        return XLSX.write(book, { type: 'buffer', bookType: 'xlsx' });
      };
      const openImport = async () => {
        await page.getByRole('button', { name: '导入 Excel', exact: true }).click();
        const modal = page.locator('.arco-modal:visible').last();
        await modal.getByText('导入公司内部账号', { exact: true }).waitFor();
        return modal;
      };
      let modal = await openImport();
      await modal.getByLabel('选择 Excel 文件', { exact: true }).setInputFiles({
        name: 'internal-account-short-password.xlsx', mimeType: fileType, buffer: makeWorkbook('Ab1!'),
      });
      await modal.getByRole('row').filter({ hasText: employeeNo }).getByText('有错误', { exact: true }).waitFor();
      assert.equal(await modal.getByRole('button', { name: '确认导入', exact: true }).isDisabled(), true);
      assert.equal((await read('GET', '/admin/users?keyword=' + encodeURIComponent(employeeNo))).total, 0);
      await modal.getByRole('button', { name: '关闭', exact: true }).click();

      modal = await openImport();
      await modal.getByLabel('选择 Excel 文件', { exact: true }).setInputFiles({
        name: 'internal-account.xlsx', mimeType: fileType, buffer: makeWorkbook(initialPassword),
      });
      await modal.getByRole('row').filter({ hasText: employeeNo }).getByText('待导入', { exact: true }).waitFor();
      await modal.getByRole('button', { name: '确认导入', exact: true }).click();
      await modal.getByRole('row').filter({ hasText: employeeNo }).getByText('已导入', { exact: true }).waitFor();
      const found = await read('GET', '/admin/users?keyword=' + encodeURIComponent(employeeNo));
      assert.equal(found.total, 1);
      user = found.list[0];
      assert.equal(user.employeeNo, employeeNo);
      assert.equal(user.departmentId, section.id);
      assert.equal(user.departmentName, sectionName);
      assert.equal(user.status, 'ACTIVE');
      assert.equal(user.mustChangePassword, true);
      assert.equal(user.lastLoginAt, null);
      assert.equal(user.email, email);
      await modal.getByRole('button', { name: '关闭', exact: true }).click();
    });

    await record('内部账号导出包含状态结果但不含初始密码，并按所属组织名称导出', async () => {
      const ready = page.waitForEvent('download');
      await page.getByRole('button', { name: '导出 Excel', exact: true }).click();
      const file = await ready;
      assert.equal(await file.failure(), null);
      const target = OUT + '/company-internal-accounts.xlsx';
      await file.saveAs(target);
      const book = XLSX.read(require('node:fs').readFileSync(target), { type: 'buffer' });
      const rows = XLSX.utils.sheet_to_json(book.Sheets.数据, { header: 1 });
      assert.deepEqual(rows[0], [
        '账号', '姓名', '邮箱', '所属组织', '角色', '初始密码', '状态', '需改密', '最近登录',
      ]);
      const exported = rows.find(row => row[0] === employeeNo);
      assert(exported);
      assert.equal(exported[3], sectionName);
      assert.equal(exported[5] || '', '', 'export never includes the password');
      assert.equal(exported[6], 'ACTIVE');
      assert.equal(exported[7], '是');
      assert.equal(exported[8], '');
      assert.equal(rows.some(row => row[0] === employeeNo && String(row[3]).includes('事业部')), false);
    });
  } catch (error) {
    if (page) {
      await page.screenshot({ path: OUT + '/internal-excel-failure.png', fullPage: true }).catch(() => {});
      console.log((await page.locator('body').innerText()).slice(-4000));
    }
    console.error(error.stack);
    process.exitCode = 1;
  } finally {
    if (user?.id && token) await api(context, 'DELETE', '/admin/users/' + user.id, undefined, token, 200).catch(() => {});
    if (section?.id && token) await api(context, 'DELETE', '/admin/departments/' + section.id, undefined, token, 200).catch(() => {});
    if (department?.id && token) await api(context, 'DELETE', '/admin/departments/' + department.id, undefined, token, 200).catch(() => {});
    if (division?.id && token) await api(context, 'DELETE', '/admin/departments/' + division.id, undefined, token, 200).catch(() => {});
    if (browser) await browser.close();
  }
})();
