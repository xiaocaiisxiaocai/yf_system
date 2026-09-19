// OEM file transfer end to end: staff enter from the collaboration header, the admin maintains a
// section leader and an OEM vendor, a sender uploads and sends, the leader approves after the
// (fake, TestHost-driven) scan, the vendor downloads from the separate portal, and the sender sees
// the receipt. Also proves the vendor realm cannot reach collaboration APIs.
const {chromium}=require('playwright');
const crypto=require('node:crypto');
const {fs,assert,OUT,s,record,login,reserveLoginBudget,api,track}=require(process.env.YF_BROWSER_SUPPORT_DIR+'/ui-lib.cjs');
const tag=crypto.randomBytes(3).toString('hex');
const password=()=>'Oem!'+crypto.randomBytes(6).toString('base64url');
const COMPANY='自动验收代工厂-'+tag,VENDOR='auto_oem_vendor_'+tag,TITLE='自动验收底盘图纸-'+tag;
const VENDOR_INITIAL=password(),VENDOR_CHANGED=password();
const pdf=fs.readFileSync(OUT+'/valid-preview.pdf');
(async()=>{let b;const pages=[];try{
 b=await chromium.launch({channel:'chrome',headless:true});
 const open=async label=>{const c=await b.newContext({viewport:{width:1440,height:1000},acceptDownloads:true});const p=await c.newPage();track(p,label);pages.push(p);return {c,p};};

 const {c:adminContext,p:admin}=await open('oem-admin');
 const auth=await login(admin,'admin',s.adminPassword);await admin.waitForURL(s.base+'/');
 const token=auth.accessToken;
 const permissions=await(await api(adminContext,'GET','/permissions',undefined,token)).json();
 const permissionIds=codes=>codes.map(code=>{const item=permissions.find(x=>x.code===code);assert.ok(item,'permission '+code);return item.id;});
 const role=async(name,codes)=>{const created=await(await api(adminContext,'POST','/admin/roles',{name:name+tag,description:'OEM 浏览器验收'},token)).json();
  await api(adminContext,'PUT','/admin/roles/'+created.id+'/permissions',{permissionIds:permissionIds(codes)},token);return created.id;};
 const senderRole=await role('OEM验收发送人',['oem','oem:transfer_create','oem:transfer_view','oem:file_download']);
 const leaderRole=await role('OEM验收主管',['oem','oem:flow_approve','oem:transfer_view','oem:file_download']);
 const accountRole=await role('OEM验收账号管理员',['oem','oem:account_manage']);
 const auditRole=await role('OEM验收审计员',['oem','oem:audit_view']);
 const filePolicyRole=await role('OEM验收文件策略管理员',['oem','oem:file_policy_manage']);
 const org=async(name,parentId,sortNo)=>(await(await api(adminContext,'POST','/admin/departments',{name:name+tag,parentId,sortNo},token)).json());
 const division=await org('OEM验收事业部-',null,950);const department=await org('OEM验收部门-',division.id,951);const section=await org('OEM验收课别-',department.id,952);
 const person=async(key,realName,roleId)=>{const pwd=password();
  const user=await(await api(adminContext,'POST','/admin/users',{employeeNo:'auto_oem_'+key+'_'+tag,password:pwd,realName,email:key+tag+'@example.invalid',departmentId:section.id,roleId},token)).json();
  return {id:user.id,username:user.employeeNo,realName,password:pwd,changed:password()};};
 const sender=await person('sender','OEM验收发送人'+tag,senderRole);
 const leader=await person('leader','OEM验收主管'+tag,leaderRole);
 const accountManager=await person('account_mgr','OEM验收账号管理员'+tag,accountRole);
 const auditor=await person('auditor','OEM验收审计员'+tag,auditRole);
 const filePolicyManager=await person('file_policy','OEM验收文件策略管理员'+tag,filePolicyRole);

 // Fresh internal accounts must change their initial password before they can use OEM.
 const firstLogin=async(page,user)=>{
  const result=await login(page,user.username,user.password);assert.equal(result.mustChangePassword,true);
  await page.waitForURL('**/change-password');
  await page.getByRole('textbox',{name:'原密码',exact:true}).fill(user.password);
  await page.getByRole('textbox',{name:'新密码',exact:true}).fill(user.changed);
  await page.getByRole('textbox',{name:'确认新密码',exact:true}).fill(user.changed);
  await page.getByRole('button',{name:'确认修改',exact:true}).click();await page.waitForURL('**/login');
  const again=await login(page,user.username,user.changed);assert.equal(again.mustChangePassword,false);
  await page.waitForURL(url=>!new URL(url).pathname.startsWith('/login'));};

 await record('OEM 入口从协作平台页头进入独立布局',async()=>{
  await admin.getByRole('button',{name:'OEM 文件传递'}).click();await admin.waitForURL('**/oem/transfers');
  await admin.getByText('文件传递单').first().waitFor();
 });
 await record('OEM 管理员在界面设置课别主管',async()=>{
  await admin.goto(s.base+'/oem/admin/leaders');
  const row=admin.getByRole('row').filter({hasText:section.name});await row.waitFor();
  await row.getByRole('button',{name:'设置主管',exact:true}).click();
  const modal=admin.locator('.arco-modal:visible');await modal.locator('.arco-select').click();
  await admin.keyboard.type(leader.username);
  await admin.getByRole('option').filter({hasText:leader.username}).first().click();
  const saved=admin.waitForResponse(r=>new URL(r.url()).pathname===`/api/v1/admin/departments/${section.id}/leader`&&r.request().method()==='PUT');
  await modal.getByRole('button',{name:'保存',exact:true}).click();assert.equal((await saved).status(),200);
  await row.getByText(leader.realName).waitFor();
 });
 await record('OEM 管理员在界面新建厂商和厂商账号',async()=>{
  await admin.goto(s.base+'/oem/admin/companies');
  await admin.getByRole('button',{name:'新增厂商'}).click();
  await admin.locator('.arco-modal:visible input').first().fill(COMPANY);
  await admin.locator('.arco-modal:visible').getByRole('button',{name:'确定'}).click();
  await admin.getByRole('row',{name:new RegExp(COMPANY)}).getByRole('button',{name:'账号'}).click();
  await admin.getByRole('button',{name:'新增账号'}).click();
  const inputs=admin.locator('.arco-modal:visible input');
  await inputs.nth(0).fill(VENDOR);await inputs.nth(1).fill('自动验收厂商代表');
  await inputs.nth(2).fill(VENDOR+'@example.invalid');await inputs.nth(3).fill(VENDOR_INITIAL);
  await admin.locator('.arco-modal:visible').getByRole('button',{name:'确定'}).click();
  await admin.getByText(VENDOR,{exact:true}).waitFor();
 });
 await record('OEM 管理页面可加载（审批模板、删除策略、参数、日志）',async()=>{
  for(const [url,text] of [['/oem/admin/flow-templates','默认审批模板'],['/oem/admin/retention','不自动删除'],['/oem/admin/settings','单文件大小上限'],['/oem/admin/audit','OEM_ACCOUNT_CREATE']]){
   await admin.goto(s.base+url);await admin.getByText(text).first().waitFor({timeout:15000});
  }
 });
 await record('OEM 窄屏内部导航可打开并切换页面',async()=>{
  await admin.setViewportSize({width:390,height:844});
  await admin.goto(s.base+'/oem/admin/audit');
  await admin.getByRole('button',{name:'打开导航菜单',exact:true}).click();
  const drawer=admin.locator('.mobile-nav-drawer:visible');await drawer.waitFor();
  await drawer.getByText('文件策略与提醒',{exact:true}).click();
  await admin.waitForURL('**/oem/admin/settings');
  await admin.getByText('单文件大小上限').first().waitFor();
 });

 const {p:accountManagerPage}=await open('oem-account-manager');
 await record('OEM 账号管理员可列出厂商并进入账号管理',async()=>{
  await firstLogin(accountManagerPage,accountManager);
  await accountManagerPage.goto(s.base+'/oem');await accountManagerPage.waitForURL('**/oem/admin/companies');
  const row=accountManagerPage.getByRole('row').filter({hasText:COMPANY});await row.waitFor();
  assert.equal(await accountManagerPage.getByRole('button',{name:'新增厂商',exact:true}).count(),0);
  assert.equal(await row.getByRole('button',{name:'编辑',exact:true}).count(),0);
  await row.getByRole('button',{name:'账号',exact:true}).click();
  await accountManagerPage.getByRole('button',{name:'新增账号',exact:true}).waitFor();
  await accountManagerPage.getByText(VENDOR,{exact:true}).waitFor();
 });

 const {p:auditorPage}=await open('oem-auditor');
 await record('OEM 审计员从 /oem 落到审计页',async()=>{
  await firstLogin(auditorPage,auditor);
  await auditorPage.goto(s.base+'/oem');await auditorPage.waitForURL('**/oem/admin/audit');
   await auditorPage.getByRole('main').getByText('OEM 审计日志',{exact:true}).waitFor();
 });

 const {p:filePolicyPage}=await open('oem-file-policy');
 await record('OEM 文件策略管理员从 /oem 落到设置页',async()=>{
  await firstLogin(filePolicyPage,filePolicyManager);
  await filePolicyPage.goto(s.base+'/oem');await filePolicyPage.waitForURL('**/oem/admin/settings');
  await filePolicyPage.getByText('单文件大小上限').first().waitFor();
 });

 const {p:senderPage}=await open('oem-sender');let detailUrl;
 await record('OEM 发送人新建传递单、上传并发送',async()=>{
  await firstLogin(senderPage,sender);
  await senderPage.goto(s.base+'/oem/transfers');
  await senderPage.getByRole('button',{name:'新建传递单'}).click();
  const modal=senderPage.locator('.arco-modal:visible');
  await modal.locator('input').first().fill(TITLE);
  await modal.locator('.arco-select').nth(0).click();await senderPage.getByRole('option',{name:COMPANY,exact:true}).click();
  await modal.locator('.arco-select').nth(1).click();await senderPage.getByRole('option',{name:'不自动删除'}).click();
  await modal.getByRole('button',{name:'确定'}).click();
  await senderPage.waitForURL(/\/oem\/transfers\/\d+$/);detailUrl=senderPage.url();
  await senderPage.locator('input[type=file]').setInputFiles({name:'chassis.pdf',mimeType:'application/pdf',buffer:pdf});
  await senderPage.getByText('已上传，等待安全扫描').waitFor({timeout:30000});
  await senderPage.getByRole('button',{name:'发送'}).click();
  await senderPage.locator('.arco-popconfirm:visible, .arco-modal:visible').last().getByRole('button',{name:'确定',exact:true}).click();
  await senderPage.getByText('已发送').first().waitFor();
 });

 const {p:leaderPage}=await open('oem-leader');
 await record('OEM 课别主管在扫描完成后审批通过',async()=>{
  await firstLogin(leaderPage,leader);
  let found=false;
  for(let i=0;i<30&&!found;i++){
   await leaderPage.goto(s.base+'/oem/approvals');
   found=await leaderPage.getByText(TITLE).waitFor({timeout:2000}).then(()=>true).catch(()=>false);
  }
  assert.ok(found,'approval task never appeared');
  await leaderPage.getByText(TITLE).click();
  await leaderPage.getByRole('button',{name:'审批通过'}).click();
  await leaderPage.getByText('已发布').first().waitFor();
 });

 const {c:vendorContext,p:vendor}=await open('oem-vendor');
 const portalLogin=async pwd=>{
  await vendor.locator('input').nth(0).fill(VENDOR);await vendor.locator('input').nth(1).fill(pwd);
  await reserveLoginBudget(VENDOR);
  const done=vendor.waitForResponse(r=>new URL(r.url()).pathname==='/api/v1/oem/auth/login'&&r.request().method()==='POST');
  await vendor.getByRole('button',{name:'登录'}).click();const response=await done;assert.equal(response.status(),200);return response.json();};
 let vendorToken;
 await record('OEM 厂商在独立门户首次改密后下载文件',async()=>{
  await vendor.goto(s.base+'/oem-portal/transfers');await vendor.waitForURL('**/oem-portal/login');
  await portalLogin(VENDOR_INITIAL);await vendor.waitForURL('**/oem-portal/change-password');
  const fields=vendor.locator('input');
  await fields.nth(0).fill(VENDOR_INITIAL);await fields.nth(1).fill(VENDOR_CHANGED);await fields.nth(2).fill(VENDOR_CHANGED);
  await vendor.getByRole('button',{name:'保存'}).click();await vendor.waitForURL('**/oem-portal/login');
  vendorToken=(await portalLogin(VENDOR_CHANGED)).accessToken;await vendor.waitForURL('**/oem-portal/transfers');
  await vendor.getByText(TITLE).click();
  const downloading=vendor.waitForEvent('download');
  await vendor.getByRole('button',{name:'下载'}).click();
  const download=await downloading;const saved=OUT+'/oem-downloaded.pdf';await download.saveAs(saved);
  assert.equal(crypto.createHash('sha256').update(fs.readFileSync(saved)).digest('hex'),crypto.createHash('sha256').update(pdf).digest('hex'));
  await vendor.setViewportSize({width:390,height:844});
  await vendor.getByRole('button',{name:'打开导航菜单',exact:true}).click();
  const drawer=vendor.locator('.mobile-nav-drawer:visible');await drawer.waitFor();
  await drawer.getByText('文件传递单',{exact:true}).click();
  await vendor.waitForURL('**/oem-portal/transfers');
 });
 await record('OEM 厂商令牌不能访问协作平台接口',async()=>{
  assert.ok(vendorToken,'vendor token');
  for(const url of ['/project-groups','/auth/profile','/departments','/admin/users']){
   const response=await api(vendorContext,'GET',url,undefined,vendorToken,403);assert.equal((await response.json()).code,40304,url);
  }
 });
 await record('OEM 发送人看到首次接收回执',async()=>{
  await senderPage.goto(detailUrl);await senderPage.getByText('首次接收').first().waitFor({timeout:15000});
  await senderPage.screenshot({path:OUT+'/oem-sender-receipt.png',fullPage:true});
 });
}catch(e){for(const p of pages){await p.screenshot({path:OUT+'/oem-failure-'+pages.indexOf(p)+'.png',fullPage:true}).catch(()=>{});}
 if(pages.length)console.log((await pages[pages.length-1].locator('body').innerText().catch(()=>'')).slice(-4000));
 console.error(e.stack);process.exitCode=1;}finally{if(b)await b.close();}})();
