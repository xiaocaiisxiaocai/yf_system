// OEM file transfer end to end: staff enter from the collaboration header, the admin maintains a
// section leader and an OEM vendor, a sender uploads and sends, the leader approves after the
// TestHost-driven scan (Fake by default, or explicitly selected real ClamAV), the vendor downloads from the separate portal, and the sender sees
// the receipt. Also proves the vendor realm cannot reach collaboration APIs.
const {chromium}=require('playwright');
const crypto=require('node:crypto');
const JSZip=require(process.env.YF_PROJECT_ROOT+'/web/node_modules/jszip');
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
  await page.waitForURL(url=>!new URL(url).pathname.startsWith('/login'));return again;};

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

 const {p:senderPage}=await open('oem-sender');let detailUrl,senderToken;
 if(s.oemScanner==='ClamAV') await record('OEM ClamAV 病毒库门禁可在页面启用并持久化',async()=>{
  await filePolicyPage.getByText(/ClamAV 单文件扫描上限为 1024 MiB/).waitFor();
  const gate=filePolicyPage.getByRole('switch',{name:'病毒库过期时暂停放行',exact:true});
  assert.equal(await gate.isEnabled(),true);
  if(await gate.getAttribute('aria-checked')!=='true') await gate.click();
  const saved=filePolicyPage.waitForResponse(r=>new URL(r.url()).pathname==='/api/v1/oem/file-policies'&&r.request().method()==='PUT');
  await filePolicyPage.getByRole('button',{name:'保存',exact:true}).click();
  const response=await saved;assert.equal(response.status(),200);
  assert.equal((await response.json()).find(x=>x.key==='oem.scan.block_on_stale_signatures').value,'true');
  await filePolicyPage.reload();
  await filePolicyPage.waitForFunction(()=>document.querySelector('[role="switch"][aria-label="病毒库过期时暂停放行"]')?.getAttribute('aria-checked')==='true');
 });
 await record('OEM 发送人新建传递单、上传并发送',async()=>{
  senderToken=(await firstLogin(senderPage,sender)).accessToken;
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


 // ---- oem.next.cjs supplemental minimum coverage ----
 const pathOfNext=response=>new URL(response.url()).pathname;
 const getJsonNext=async(method,url,data,expected=200)=>(await api(adminContext,method,url,data,token,expected)).json();
 const getSenderJsonNext=async(method,url,data,expected=200)=>(await api(senderPage.context(),method,url,data,senderToken,expected)).json();
 const companyListNext=()=>getJsonNext('GET','/oem/companies?page=1&pageSize=20&keyword='+encodeURIComponent(COMPANY));
 const waitNoSpinNext=page=>page.waitForFunction(()=>![...document.querySelectorAll('.arco-spin-loading')].some(element=>element.getClientRects().length>0),null,{timeout:20000});
 const rootFitsNext=async(page,label)=>{const size=await page.evaluate(()=>({inner:innerWidth,scroll:document.documentElement.scrollWidth}));assert.ok(size.scroll<=size.inner+1,label+' root overflow '+JSON.stringify(size));};
 const FLOW_NAME_NEXT='自动验收审批模板-'+tag,FLOW_EDITED_NEXT=FLOW_NAME_NEXT+'-已编辑';
 const RETENTION_NAME_NEXT='自动验收删除策略-'+tag,RETENTION_EDITED_NEXT=RETENTION_NAME_NEXT+'-已编辑';

 let companyIdNext,vendorAccountIdNext,flowIdNext,retentionIdNext;
 const companyResultNext=await companyListNext();
 const companyNext=companyResultNext.list.find(item=>item.name===COMPANY);assert.ok(companyNext,'created OEM company');companyIdNext=companyNext.id;
 const accountsNext=await getJsonNext('GET','/oem/companies/'+companyIdNext+'/accounts');
 const accountNext=accountsNext.find(item=>item.employeeNo===VENDOR);assert.ok(accountNext,'created OEM account');vendorAccountIdNext=accountNext.id;

 await record('OEM 厂商表单必填、取消均不写入',async()=>{
  await admin.setViewportSize({width:1440,height:1000});await admin.goto(s.base+'/oem/admin/companies');
  await admin.getByText(COMPANY,{exact:true}).waitFor();
  let writes=0;const countWrite=request=>{if(pathOfNext(request)==='/api/v1/oem/companies'&&request.method()==='POST')writes++;};admin.on('request',countWrite);
  try{
   await admin.getByRole('button',{name:'新增厂商',exact:true}).click();let modal=admin.locator('.arco-modal:visible');
   await modal.getByRole('button',{name:'确定',exact:true}).click();await admin.waitForTimeout(150);
   assert.equal(writes,0,'required validation must prevent company POST');assert.equal(await modal.isVisible(),true);
   await modal.locator('input').first().fill('自动验收取消厂商-'+tag);
   await modal.getByRole('button',{name:'取消',exact:true}).click();await modal.waitFor({state:'hidden'});
   assert.equal(writes,0,'cancelled company must not be created');
  }finally{admin.off('request',countWrite);}
 });

 await record('OEM 厂商编辑503保留表单，重试后持久化并可停启',async()=>{
  const row=()=>admin.getByRole('row').filter({hasText:COMPANY});await row().getByRole('button',{name:'编辑',exact:true}).click();
  const modal=admin.locator('.arco-modal:visible');const inputs=modal.locator('input');
  await inputs.nth(1).fill('验收联系人-'+tag);await inputs.nth(2).fill('1380000'+tag.slice(0,4));await inputs.nth(3).fill('contact-'+tag+'@example.invalid');
  const endpoint='/api/v1/oem/companies/'+companyIdNext;
  const previousExpected=admin.expectedServerErrors;admin.expectedServerErrors=new Set([...(previousExpected||[]),endpoint]);
  const failOnce=route=>route.fulfill({status:503,contentType:'application/json',body:'{"code":50301,"message":"OEM 厂商保存暂时不可用"}'});
  await admin.route('**'+endpoint,failOnce,{times:1});
  try{
   const failed=admin.waitForResponse(response=>pathOfNext(response)===endpoint&&response.request().method()==='PUT'&&response.status()===503);
   await modal.getByRole('button',{name:'确定',exact:true}).click();await failed;
   assert.equal(await modal.isVisible(),true);assert.equal(await inputs.nth(1).inputValue(),'验收联系人-'+tag);
  }finally{await admin.unroute('**'+endpoint,failOnce);admin.expectedServerErrors=previousExpected;}
  const saved=admin.waitForResponse(response=>pathOfNext(response)===endpoint&&response.request().method()==='PUT'&&response.status()===200);
  await modal.getByRole('button',{name:'确定',exact:true}).click();await saved;await modal.waitFor({state:'hidden'});
  let persisted=(await companyListNext()).list.find(item=>item.id===companyIdNext);assert.equal(persisted.contactName,'验收联系人-'+tag);
  const statusPath=endpoint+'/status';
  let changed=admin.waitForResponse(response=>pathOfNext(response)===statusPath&&response.request().method()==='PUT');
  await row().getByRole('button',{name:'停用',exact:true}).click();await admin.locator('.arco-popconfirm:visible').getByRole('button',{name:'确定',exact:true}).click();assert.equal((await changed).status(),200);
  persisted=(await companyListNext()).list.find(item=>item.id===companyIdNext);assert.equal(persisted.status,'DISABLED');
  changed=admin.waitForResponse(response=>pathOfNext(response)===statusPath&&response.request().method()==='PUT');
  await row().getByRole('button',{name:'启用',exact:true}).click();await admin.locator('.arco-popconfirm:visible').getByRole('button',{name:'确定',exact:true}).click();assert.equal((await changed).status(),200);
  persisted=(await companyListNext()).list.find(item=>item.id===companyIdNext);assert.equal(persisted.status,'ACTIVE');
 });

 await record('OEM 厂商账号编辑和停启均持久化',async()=>{
  const companyRow=admin.getByRole('row').filter({hasText:COMPANY});await companyRow.getByRole('button',{name:'账号',exact:true}).click();
  const drawer=admin.locator('.arco-drawer:visible');const accountRow=()=>drawer.getByRole('row').filter({hasText:VENDOR});await accountRow().waitFor();
  await accountRow().getByRole('button',{name:'编辑',exact:true}).click();const modal=admin.locator('.arco-modal:visible');const fields=modal.locator('input');
  await fields.nth(0).fill('自动验收厂商代表-'+tag);await fields.nth(1).fill('vendor-'+tag+'@example.invalid');
  const editPath='/api/v1/oem/accounts/'+vendorAccountIdNext;let response=admin.waitForResponse(r=>pathOfNext(r)===editPath&&r.request().method()==='PUT');
  await modal.getByRole('button',{name:'确定',exact:true}).click();assert.equal((await response).status(),200);
  let savedAccounts=await getJsonNext('GET','/oem/companies/'+companyIdNext+'/accounts');let savedAccount=savedAccounts.find(item=>item.id===vendorAccountIdNext);
  assert.equal(savedAccount.realName,'自动验收厂商代表-'+tag);assert.equal(savedAccount.email,'vendor-'+tag+'@example.invalid');
  const statusPath=editPath+'/status';response=admin.waitForResponse(r=>pathOfNext(r)===statusPath&&r.request().method()==='PUT');
  await accountRow().getByRole('button',{name:'停用',exact:true}).click();assert.equal((await response).status(),200);
  savedAccounts=await getJsonNext('GET','/oem/companies/'+companyIdNext+'/accounts');assert.equal(savedAccounts.find(item=>item.id===vendorAccountIdNext).status,'DISABLED');
  response=admin.waitForResponse(r=>pathOfNext(r)===statusPath&&r.request().method()==='PUT');
  await accountRow().getByRole('button',{name:'启用',exact:true}).click();assert.equal((await response).status(),200);
  savedAccounts=await getJsonNext('GET','/oem/companies/'+companyIdNext+'/accounts');assert.equal(savedAccounts.find(item=>item.id===vendorAccountIdNext).status,'ACTIVE');
  await admin.keyboard.press('Escape');await drawer.waitFor({state:'hidden'});
 });

 await record('OEM 审批模板取消、服务端必填校验、创建编辑和持久化',async()=>{
  await admin.goto(s.base+'/oem/admin/flow-templates');await admin.getByRole('button',{name:'新建模板',exact:true}).click();
  let modal=admin.locator('.arco-modal:visible');await modal.getByPlaceholder('模板名称').fill('自动验收取消模板-'+tag);await modal.getByRole('button',{name:'取消',exact:true}).click();await modal.waitFor({state:'hidden'});
  let templates=await getJsonNext('GET','/oem/flow-templates');assert.equal(templates.some(item=>item.name==='自动验收取消模板-'+tag),false);
  await admin.getByRole('button',{name:'新建模板',exact:true}).click();modal=admin.locator('.arco-modal:visible');
  const invalid=admin.waitForResponse(r=>pathOfNext(r)==='/api/v1/oem/flow-templates'&&r.request().method()==='POST'&&r.status()===400);
  await modal.getByRole('button',{name:'确定',exact:true}).click();await invalid;assert.equal(await modal.isVisible(),true);
  await modal.getByPlaceholder('模板名称').fill(FLOW_NAME_NEXT);await modal.getByRole('button',{name:'添加节点',exact:true}).click();
  const secondNode=modal.locator('.arco-card').filter({hasText:'节点 2'}).first();await secondNode.locator('input').first().fill('二级课别主管-'+tag);
  const created=admin.waitForResponse(r=>pathOfNext(r)==='/api/v1/oem/flow-templates'&&r.request().method()==='POST'&&r.status()===200);
  await modal.getByRole('button',{name:'确定',exact:true}).click();const createdBody=await (await created).json();flowIdNext=createdBody.id;await modal.waitFor({state:'hidden'});
  templates=await getJsonNext('GET','/oem/flow-templates');let flow=templates.find(item=>item.id===flowIdNext);assert.equal(flow.name,FLOW_NAME_NEXT);assert.equal(flow.nodes.length,2);
  await admin.getByText(FLOW_NAME_NEXT,{exact:true}).click();modal=admin.locator('.arco-modal:visible');await modal.getByPlaceholder('模板名称').fill(FLOW_EDITED_NEXT);await modal.locator('.arco-switch').first().click();
  const updated=admin.waitForResponse(r=>pathOfNext(r)==='/api/v1/oem/flow-templates/'+flowIdNext+'/definition'&&r.request().method()==='PUT'&&r.status()===200);
  await modal.getByRole('button',{name:'确定',exact:true}).click();await updated;await modal.waitFor({state:'hidden'});
  templates=await getJsonNext('GET','/oem/flow-templates');flow=templates.find(item=>item.id===flowIdNext);assert.equal(flow.name,FLOW_EDITED_NEXT);assert.equal(flow.status,'DISABLED');assert.equal(flow.nodes.length,2);
 });

 await record('OEM 删除策略取消、必填、创建编辑停用和持久化',async()=>{
  await admin.goto(s.base+'/oem/admin/retention');await admin.getByRole('button',{name:'新建策略',exact:true}).click();let modal=admin.locator('.arco-modal:visible');
  await modal.locator('input').first().fill('自动验收取消策略-'+tag);await modal.getByRole('button',{name:'取消',exact:true}).click();await modal.waitFor({state:'hidden'});
  let policies=await getJsonNext('GET','/oem/retention-templates');assert.equal(policies.some(item=>item.name==='自动验收取消策略-'+tag),false);
  await admin.getByRole('button',{name:'新建策略',exact:true}).click();modal=admin.locator('.arco-modal:visible');let writes=0;const count=request=>{if(pathOfNext(request)==='/api/v1/oem/retention-templates'&&request.method()==='POST')writes++;};admin.on('request',count);
  try{await modal.getByRole('button',{name:'确定',exact:true}).click();await admin.waitForTimeout(150);assert.equal(writes,0);assert.equal(await modal.isVisible(),true);}finally{admin.off('request',count);}
  await modal.locator('input').first().fill(RETENTION_NAME_NEXT);
  const created=admin.waitForResponse(r=>pathOfNext(r)==='/api/v1/oem/retention-templates'&&r.request().method()==='POST'&&r.status()===200);
  await modal.getByRole('button',{name:'确定',exact:true}).click();const createdBody=await (await created).json();retentionIdNext=createdBody.id;await modal.waitFor({state:'hidden'});
  policies=await getJsonNext('GET','/oem/retention-templates');let policy=policies.find(item=>item.id===retentionIdNext);assert.equal(policy.name,RETENTION_NAME_NEXT);assert.equal(policy.status,'ACTIVE');
  const policyRow=admin.getByRole('row').filter({hasText:RETENTION_NAME_NEXT});await policyRow.getByRole('button',{name:'编辑',exact:true}).click();modal=admin.locator('.arco-modal:visible');
  await modal.locator('input').first().fill(RETENTION_EDITED_NEXT);await modal.locator('.arco-switch').first().click();
  const updated=admin.waitForResponse(r=>pathOfNext(r)==='/api/v1/oem/retention-templates/'+retentionIdNext&&r.request().method()==='PUT'&&r.status()===200);
  await modal.getByRole('button',{name:'确定',exact:true}).click();await updated;await modal.waitFor({state:'hidden'});
  policies=await getJsonNext('GET','/oem/retention-templates');policy=policies.find(item=>item.id===retentionIdNext);assert.equal(policy.name,RETENTION_EDITED_NEXT);assert.equal(policy.status,'DISABLED');
 });

 await record('OEM 文件策略与提醒通过界面保存并刷新持久化',async()=>{
  await admin.goto(s.base+'/oem/admin/settings');
  const retryInput=admin.getByRole('textbox',{name:'扫描错误最大重试次数',exact:true});await retryInput.waitFor();const retryBefore=await retryInput.inputValue();const retryAfter=String(Number(retryBefore)===50?49:Number(retryBefore)+1);
  await retryInput.fill(retryAfter);let saved=admin.waitForResponse(r=>pathOfNext(r)==='/api/v1/oem/file-policies'&&r.request().method()==='PUT');await admin.getByRole('button',{name:'保存',exact:true}).click();assert.equal((await saved).status(),200);
  await admin.reload();assert.equal(await admin.getByRole('textbox',{name:'扫描错误最大重试次数',exact:true}).inputValue(),retryAfter);
  await admin.getByRole('tab',{name:'邮件提醒',exact:true}).click();const receipt=admin.getByRole('switch',{name:'接收与删除进度通知发送人邮件',exact:true});await receipt.waitFor();const before=await receipt.getAttribute('aria-checked');await receipt.click();
  saved=admin.waitForResponse(r=>pathOfNext(r)==='/api/v1/oem/notify-policies'&&r.request().method()==='PUT');await admin.getByRole('button',{name:'保存',exact:true}).click();assert.equal((await saved).status(),200);
  await admin.reload();await admin.getByRole('tab',{name:'邮件提醒',exact:true}).click();assert.equal(await admin.getByRole('switch',{name:'接收与删除进度通知发送人邮件',exact:true}).getAttribute('aria-checked'),before==='true'?'false':'true');
 });

 await record('OEM 账号管理员直达审批模板被403阻断',async()=>{
  await accountManagerPage.goto(s.base+'/oem/admin/flow-templates');await accountManagerPage.getByText('无操作权限',{exact:true}).waitFor();
  assert.equal(await accountManagerPage.getByText('新建模板',{exact:true}).count(),0);
  await accountManagerPage.goto(s.base+'/oem');await accountManagerPage.waitForURL('**/oem/admin/companies');
 });

 await record('OEM 审计按标记内容筛选并可清除恢复',async()=>{
  await admin.goto(s.base+'/oem/admin/audit');const search=admin.getByPlaceholder('搜索账号、动作或内容',{exact:true});
  await search.fill(FLOW_EDITED_NEXT);let loaded=admin.waitForResponse(r=>pathOfNext(r)==='/api/v1/oem/audit-logs'&&new URL(r.url()).searchParams.get('keyword')===FLOW_EDITED_NEXT);
  await search.press('Enter');const result=await (await loaded).json();assert.ok(result.total>=1);await admin.getByRole('row').filter({hasText:'OEM_FLOW_TEMPLATE_UPDATE'}).first().waitFor();
  await search.fill('');loaded=admin.waitForResponse(r=>pathOfNext(r)==='/api/v1/oem/audit-logs'&&!new URL(r.url()).searchParams.has('keyword'));await search.press('Enter');assert.equal((await loaded).status(),200);
 });

 // Company/account status changes revoke the already issued OEM session. Log in
 // again before checking every authenticated portal route.
 await vendor.goto(s.base+'/oem-portal/login');vendorToken=(await portalLogin(VENDOR_CHANGED)).accessToken;await vendor.waitForURL('**/oem-portal/transfers');
 const transferIdNext=Number(new URL(detailUrl).pathname.split('/').pop());assert.ok(Number.isSafeInteger(transferIdNext)&&transferIdNext>0);

 await record('OEM 全部内部和门户路由覆盖1440、1024、390视口',async()=>{
  const viewports=[[1440,1000],[1024,900],[390,844]];
  const internalRoutes=[
   ['internal-root','/oem','文件传递单'],['internal-transfers','/oem/transfers','文件传递单'],['internal-detail','/oem/transfers/'+transferIdNext,TITLE],
   ['internal-approvals','/oem/approvals','待我审批'],['internal-companies','/oem/admin/companies','OEM 厂商与账号'],
   ['internal-flow','/oem/admin/flow-templates','审批模板'],['internal-leaders','/oem/admin/leaders','组织主管'],
   ['internal-retention','/oem/admin/retention','删除策略模板'],['internal-settings','/oem/admin/settings','文件策略与邮件提醒'],
   ['internal-audit','/oem/admin/audit','OEM 审计日志']
  ];
  const portalRoutes=[['portal-root','/oem-portal','文件传递单'],['portal-transfers','/oem-portal/transfers','文件传递单'],['portal-detail','/oem-portal/transfers/'+transferIdNext,TITLE],['portal-password','/oem-portal/change-password','修改密码']];
  for(const [width,height] of viewports){
   await admin.setViewportSize({width,height});
   for(const [name,route,label] of internalRoutes){await admin.goto(s.base+route);await admin.getByText(label,{exact:false}).filter({visible:true}).first().waitFor({timeout:20000});await waitNoSpinNext(admin);await rootFitsNext(admin,name+' '+width);await admin.screenshot({path:OUT+'/oem-next-'+name+'-'+width+'x'+height+'.png',animations:'disabled'});}
   await vendor.setViewportSize({width,height});
   for(const [name,route,label] of portalRoutes){await vendor.goto(s.base+route);await vendor.getByText(label,{exact:false}).filter({visible:true}).first().waitFor({timeout:20000});await waitNoSpinNext(vendor);await rootFitsNext(vendor,name+' '+width);await vendor.screenshot({path:OUT+'/oem-next-'+name+'-'+width+'x'+height+'.png',animations:'disabled'});}
  }
  const {p:publicPortal}=await open('oem-portal-public-layout');
  for(const [width,height] of viewports){await publicPortal.setViewportSize({width,height});await publicPortal.goto(s.base+'/oem-portal/login');await publicPortal.getByText('OEM 文件传递 · 厂商登录',{exact:false}).waitFor();await rootFitsNext(publicPortal,'portal-login '+width);await publicPortal.screenshot({path:OUT+'/oem-next-portal-login-'+width+'x'+height+'.png',animations:'disabled'});}
 });

 // ---- extended real UI state coverage ----
 const INBOUND_TITLE_NEXT='自动验收OEM入站-'+tag,REJECT_TITLE_NEXT='自动验收驳回-'+tag;
 const DRAFT_TITLE_NEXT='自动验收草稿删除-'+tag,CANCEL_TITLE_NEXT='自动验收终止-'+tag,EICAR_TITLE_NEXT='自动验收安全阻断-'+tag;
 const EICAR_NEXT=Buffer.from('X5O!P%@AP[4\\PZX54(P^)7CC)7}$EICAR-STANDARD-ANTIVIRUS-TEST-FILE!$H+H*','ascii');
 const createTransferUiNext=async(page,basePath,title,internal)=>{
  await page.goto(s.base+basePath+'/transfers');await page.getByRole('button',{name:'新建传递单',exact:true}).click();
  const modal=page.locator('.arco-modal:visible');await modal.locator('input').first().fill(title);const selects=modal.locator('.arco-select');
  if(internal){await selects.nth(0).click();await page.getByRole('option',{name:COMPANY,exact:true}).click();await selects.nth(1).click();}else await selects.nth(0).click();
  await page.getByRole('option',{name:'不自动删除',exact:true}).click();
  const created=page.waitForResponse(r=>pathOfNext(r)==='/api/v1/oem/transfers'&&r.request().method()==='POST'&&r.status()===200);
  await modal.getByRole('button',{name:'确定',exact:true}).click();const body=await (await created).json();
  await page.waitForURL(new RegExp(basePath.replace('/','\\/')+'/transfers/'+body.summary.id+'$'));return body.summary.id;
 };
 const uploadUiNext=async(page,name,mime,buffer)=>{await page.locator('input[type=file]').setInputFiles({name,mimeType:mime,buffer});await page.getByText('已上传，等待安全扫描').last().waitFor({timeout:30000});};
 const sendUiNext=async(page,id)=>{const sent=page.waitForResponse(r=>pathOfNext(r)==='/api/v1/oem/transfers/'+id+'/send'&&r.request().method()==='POST'&&r.status()===200);await page.getByRole('button',{name:'发送',exact:true}).click();await page.locator('.arco-popconfirm:visible').getByRole('button',{name:'确定',exact:true}).click();await sent;};
 const waitLifecycleUiNext=async(page,url,label)=>{for(let attempt=0;attempt<40;attempt++){await page.goto(url);if(await page.getByText(label,{exact:true}).filter({visible:true}).first().waitFor({timeout:2000}).then(()=>true).catch(()=>false))return;await page.waitForTimeout(500);}throw new Error('lifecycle did not reach '+label+': '+url);};

 await record('OEM 厂商通过UI创建入站、上传发送，公司通过UI接收并回执',async()=>{
  await vendor.setViewportSize({width:1440,height:1000});const inboundId=await createTransferUiNext(vendor,'/oem-portal',INBOUND_TITLE_NEXT,false);
  await uploadUiNext(vendor,'oem-inbound-'+tag+'.pdf','application/pdf',pdf);await sendUiNext(vendor,inboundId);await waitLifecycleUiNext(vendor,s.base+'/oem-portal/transfers/'+inboundId,'已发布');
  const released=await (await api(vendorContext,'GET','/oem/transfers/'+inboundId,undefined,vendorToken)).json();assert.equal(released.summary.direction,'OEM_TO_INTERNAL');assert.equal(released.summary.lifecycleStatus,'RELEASED');
  await senderPage.setViewportSize({width:1440,height:1000});await senderPage.goto(s.base+'/oem/transfers/'+inboundId);await senderPage.getByText(INBOUND_TITLE_NEXT,{exact:true}).waitFor();
  const downloadStarted=senderPage.waitForEvent('download');await senderPage.getByRole('button',{name:'下载',exact:true}).click();const download=await downloadStarted;const saved=OUT+'/oem-inbound-downloaded-'+tag+'.pdf';await download.saveAs(saved);
  assert.equal(crypto.createHash('sha256').update(fs.readFileSync(saved)).digest('hex'),crypto.createHash('sha256').update(pdf).digest('hex'));
  await vendor.goto(s.base+'/oem-portal/transfers/'+inboundId);await vendor.getByText('首次接收').first().waitFor({timeout:15000});const persisted=await getJsonNext('GET','/oem/transfers/'+inboundId);assert.ok(persisted.files[0].firstRecipientDownloadAt);
 });

 await record('OEM 主管驳回必填、取消不写入、成功后持久化',async()=>{
  await senderPage.setViewportSize({width:1440,height:1000});const rejectId=await createTransferUiNext(senderPage,'/oem',REJECT_TITLE_NEXT,true);await uploadUiNext(senderPage,'reject-review-'+tag+'.pdf','application/pdf',pdf);await sendUiNext(senderPage,rejectId);
  let found=false;for(let attempt=0;attempt<40&&!found;attempt++){await leaderPage.goto(s.base+'/oem/approvals');found=await leaderPage.getByText(REJECT_TITLE_NEXT,{exact:true}).waitFor({timeout:2000}).then(()=>true).catch(()=>false);if(!found)await leaderPage.waitForTimeout(500);}if(!found)throw new Error('reject approval task never appeared: '+JSON.stringify((await getJsonNext('GET','/oem/transfers/'+rejectId)).summary));
  await leaderPage.getByText(REJECT_TITLE_NEXT,{exact:true}).click();await leaderPage.getByRole('button',{name:'驳回',exact:true}).waitFor();
  let rejectWrites=0;const countReject=request=>{const path=pathOfNext(request);if(path.startsWith('/api/v1/oem/approvals/')&&path.endsWith('/reject')&&request.method()==='POST')rejectWrites++;};leaderPage.on('request',countReject);
  try{
   await leaderPage.getByRole('button',{name:'驳回',exact:true}).click();let dialog=leaderPage.locator('.arco-modal:visible').last();await dialog.getByRole('button',{name:'确定',exact:true}).click();await leaderPage.getByText('请填写原因',{exact:true}).waitFor();assert.equal(rejectWrites,0);assert.equal(await dialog.isVisible(),true);
   await dialog.getByPlaceholder('请输入驳回原因（发送人将收到通知）').fill('取消驳回-'+tag);await dialog.getByRole('button',{name:'取消',exact:true}).click();await dialog.waitFor({state:'hidden'});assert.equal(rejectWrites,0);
   await leaderPage.getByRole('button',{name:'驳回',exact:true}).click();dialog=leaderPage.locator('.arco-modal:visible').last();const reason='附件版本不符合要求-'+tag;await dialog.getByPlaceholder('请输入驳回原因（发送人将收到通知）').fill(reason);
   const rejected=leaderPage.waitForResponse(r=>pathOfNext(r).startsWith('/api/v1/oem/approvals/')&&pathOfNext(r).endsWith('/reject')&&r.request().method()==='POST'&&r.status()===200);await dialog.getByRole('button',{name:'确定',exact:true}).click();await rejected;await leaderPage.getByText('已驳回',{exact:true}).first().waitFor();
   const persisted=await getJsonNext('GET','/oem/transfers/'+rejectId);assert.equal(persisted.summary.lifecycleStatus,'REJECTED');assert.equal(persisted.closedReason,reason);assert.equal(rejectWrites,1);
  }finally{leaderPage.off('request',countReject);}
 });

 await record('OEM 草稿附件通过UI移除、管理员不可读取他人草稿，删除后作废并保留原因',async()=>{
  const draftId=await createTransferUiNext(senderPage,'/oem',DRAFT_TITLE_NEXT,true);await uploadUiNext(senderPage,'draft-remove-'+tag+'.pdf','application/pdf',pdf);
  const file=senderPage.getByRole('row').filter({hasText:'draft-remove-'+tag+'.pdf'});await file.getByRole('button',{name:'移除',exact:true}).click();const removed=senderPage.waitForResponse(r=>pathOfNext(r).startsWith('/api/v1/oem/files/')&&r.request().method()==='DELETE'&&r.status()===200);
  await senderPage.locator('.arco-popconfirm:visible').getByRole('button',{name:'确定',exact:true}).click();await removed;await senderPage.getByText('暂无附件',{exact:true}).waitFor();assert.equal((await getSenderJsonNext('GET','/oem/transfers/'+draftId)).files.length,0);
  await api(adminContext,'GET','/oem/transfers/'+draftId,undefined,token,404);
  const deleted=senderPage.waitForResponse(r=>pathOfNext(r)==='/api/v1/oem/transfers/'+draftId&&r.request().method()==='DELETE'&&r.status()===200);await senderPage.getByRole('button',{name:'删除草稿',exact:true}).click();await senderPage.locator('.arco-popconfirm:visible').getByRole('button',{name:'确定',exact:true}).click();await deleted;
  await senderPage.waitForURL('**/oem/transfers');
  const abandoned=await getSenderJsonNext('GET','/oem/transfers/'+draftId);
  assert.equal(abandoned.summary.lifecycleStatus,'ABANDONED');
  assert.equal(abandoned.closedReason,'发送人删除草稿');
 });

 await record('OEM 异常处置通过UI终止已发送传递并持久化原因',async()=>{
  const cancelId=await createTransferUiNext(senderPage,'/oem',CANCEL_TITLE_NEXT,true);await uploadUiNext(senderPage,'cancel-transfer-'+tag+'.pdf','application/pdf',pdf);await sendUiNext(senderPage,cancelId);
  await admin.setViewportSize({width:1440,height:1000});await admin.goto(s.base+'/oem/transfers/'+cancelId);await admin.getByRole('button',{name:'终止传递',exact:true}).waitFor();await admin.getByRole('button',{name:'终止传递',exact:true}).click();
  const dialog=admin.locator('.arco-modal:visible').last();const reason='自动验收主动终止-'+tag;await dialog.getByPlaceholder('请输入终止原因（发送人将收到通知）').fill(reason);
  const cancelled=admin.waitForResponse(r=>pathOfNext(r)==='/api/v1/oem/transfers/'+cancelId+'/cancel'&&r.request().method()==='POST'&&r.status()===200);await dialog.getByRole('button',{name:'确定',exact:true}).click();await cancelled;await admin.getByText('已终止',{exact:true}).first().waitFor();
  const persisted=await getJsonNext('GET','/oem/transfers/'+cancelId);assert.equal(persisted.summary.lifecycleStatus,'CANCELLED');assert.equal(persisted.closedReason,reason);
 });

 const eicarArchiveNext=new JSZip();eicarArchiveNext.file('eicar.com',EICAR_NEXT);
 const eicarZipNext=await eicarArchiveNext.generateAsync({type:'nodebuffer',compression:'STORE'});
 for(const sample of [
  {name:'invalid-pdf',extension:'pdf',mime:'application/pdf',bytes:EICAR_NEXT,status:'UNSCANNABLE',label:'无法扫描'},
  {name:'eicar-archive',extension:'zip',mime:'application/zip',bytes:eicarZipNext,status:'INFECTED',label:'发现威胁'},
 ])await record('OEM '+sample.status+' 附件通过UI阻止发送、下载和预览',async()=>{
  const infectedId=await createTransferUiNext(senderPage,'/oem',EICAR_TITLE_NEXT+'-'+sample.name,true);await uploadUiNext(senderPage,sample.name+'-'+tag+'.'+sample.extension,sample.mime,sample.bytes);
  await senderPage.getByText(sample.label,{exact:true}).waitFor({timeout:60000});await senderPage.getByText('有附件未通过安全检查，请移除后再发送。',{exact:true}).waitFor();assert.equal(await senderPage.getByRole('button',{name:'发送',exact:true}).isDisabled(),true);
  assert.equal(await senderPage.getByRole('button',{name:'下载',exact:true}).count(),0);assert.equal(await senderPage.getByRole('button',{name:'预览',exact:true}).count(),0);
  const persisted=await getSenderJsonNext('GET','/oem/transfers/'+infectedId);assert.equal(persisted.summary.lifecycleStatus,'DRAFT');assert.equal(persisted.files.length,1);assert.equal(persisted.files[0].scanStatus,sample.status);assert.equal(persisted.files[0].downloadable,false);
  await senderPage.screenshot({path:OUT+'/oem-'+sample.status.toLowerCase()+'-blocked-'+tag+'.png',fullPage:true});
 });
 // ---- end extended real UI state coverage ----

 fs.writeFileSync(OUT+'/oem-next-uncovered.json',JSON.stringify({status:'explicitly-untested',items:[
  'approval reassign/recover and concurrency conflicts (reject is covered)',
  'upload abort/resume and transfer metadata edit (draft delete, cancel and removal are covered)',
  'ERROR and APPROVAL_BLOCKED browser states (INFECTED and UNSCANNABLE are covered)',
  'more-than-20-row pagination for OEM lists and audit',
  'portal bad-password, rate-limit, expired-session and disabled-account recovery',
  '390x600 long-form/dialog coverage and keyboard focus trapping'
 ]},null,2));
 // ---- end supplemental coverage ----

}catch(e){for(const p of pages){await p.screenshot({path:OUT+'/oem-failure-'+pages.indexOf(p)+'.png',fullPage:true}).catch(()=>{});}
 if(pages.length)console.log((await pages[pages.length-1].locator('body').innerText().catch(()=>'')).slice(-4000));
 console.error(e.stack);process.exitCode=1;}finally{if(b)await b.close();}})();
