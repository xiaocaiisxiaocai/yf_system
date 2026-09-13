const {chromium}=require('playwright');
const {assert,OUT,s,record,login,api,navigate,action,track}=require(process.env.YF_BROWSER_SUPPORT_DIR+'/ui-lib.cjs');
const pathOf=r=>new URL(r.url()).pathname;
(async()=>{let b,p;try{
 b=await chromium.launch({channel:'chrome',headless:true});const c=await b.newContext({viewport:{width:1440,height:1000}});p=await c.newPage();track(p,'system');
 const auth=await login(p,'admin',s.adminPassword);await p.waitForURL(s.base+'/');
 const configs=async()=>Object.fromEntries((await(await api(c,'GET','/admin/system/configs',undefined,auth.accessToken)).json()).map(x=>[x.key,x.value]));
 const original=await configs();await navigate(p,'/system/config');
 await p.getByLabel('允许上传类型',{exact:true}).waitFor();
 const threshold=original['upload.allowed_exts'].split(',').includes('cfgtest') ? original['upload.allowed_exts'].split(',').filter(x=>x!=='cfgtest').sort().join(',') : [...original['upload.allowed_exts'].split(','),'cfgtest'].sort().join(',');
 await record('系统参数编辑重置不写入、保存后刷新及恢复原值',async()=>{
  await p.getByLabel('允许上传类型',{exact:true}).fill(threshold);await p.getByRole('button',{name:'重置',exact:true}).click();
  assert.deepEqual(await configs(),original);assert.equal(await p.getByLabel('允许上传类型',{exact:true}).inputValue(),original['upload.allowed_exts']);
  await p.getByLabel('允许上传类型',{exact:true}).fill(threshold);
  await action(p,'/admin/system/configs','PUT',()=>p.getByRole('button',{name:'保存',exact:true}).click());
  await p.reload();await p.getByLabel('允许上传类型',{exact:true}).waitFor();assert.equal(await p.getByLabel('允许上传类型',{exact:true}).inputValue(),threshold);
  assert.equal((await configs())['upload.allowed_exts'],threshold);
  await p.getByLabel('允许上传类型',{exact:true}).fill(original['upload.allowed_exts']);
  await action(p,'/admin/system/configs','PUT',()=>p.getByRole('button',{name:'保存',exact:true}).click());
  assert.deepEqual(await configs(),original);
 });
 await record('系统参数非法扩展名失败后保留编辑并可重置',async()=>{
  await p.getByLabel('允许上传类型',{exact:true}).fill('../invalid');
  const failed=p.waitForResponse(r=>pathOf(r)==='/api/v1/admin/system/configs'&&r.request().method()==='PUT');
  await p.getByRole('button',{name:'保存',exact:true}).click();assert.equal((await failed).status(),400);
  assert.deepEqual(await configs(),original);assert.equal(await p.getByLabel('允许上传类型',{exact:true}).inputValue(),'../invalid');
  await p.getByRole('button',{name:'重置',exact:true}).click();assert.equal(await p.getByLabel('允许上传类型',{exact:true}).inputValue(),original['upload.allowed_exts']);
 });
 await record('系统参数读取失败显示重试且重试恢复真实数据',async()=>{
  p.expectedServerErrors=new Set(['/api/v1/admin/system/configs']);
  await p.route('**/api/v1/admin/system/configs',r=>r.fulfill({status:503,contentType:'application/json',body:JSON.stringify({code:50301,message:'验收模拟暂时不可用'})}),{times:1});
  await p.reload();await p.getByText('加载失败',{exact:true}).waitFor();await p.getByRole('button',{name:'重试',exact:true}).click();
  await p.getByLabel('允许上传类型',{exact:true}).waitFor();assert.equal(await p.getByLabel('允许上传类型',{exact:true}).inputValue(),original['upload.allowed_exts']);p.expectedServerErrors.clear();
 });
 await navigate(p,'/logs');await p.getByRole('button',{name:'查询',exact:true}).waitFor();
 const query=async(expected={})=>{const keyword=(await p.getByPlaceholder('操作人 / 动作编码 / 对象').inputValue()).trim();const ready=p.waitForResponse(r=>{const url=new URL(r.url());return url.pathname==='/api/v1/admin/audit-logs'&&r.status()===200&&(url.searchParams.get('keyword')||'')===keyword&&Object.entries(expected).every(([key,value])=>url.searchParams.get(key)===value);});await p.getByRole('button',{name:'查询',exact:true}).click();return(await ready).json();};
 await record('审计搜索详情与单条删除持久化',async()=>{
  await p.getByPlaceholder('操作人 / 动作编码 / 对象').fill('CONFIG_UPDATE');const found=await query();assert(found.total>=2);
  const first=found.list[0];const row=p.getByRole('row').filter({hasText:'CONFIG_UPDATE'}).first();await row.getByRole('button',{name:'查看',exact:true}).click();
  const drawer=p.locator('.arco-drawer:visible');await drawer.getByText('CONFIG_UPDATE',{exact:true}).waitFor();await drawer.getByText('upload.allowed_exts',{exact:false}).last().waitFor();
  await drawer.locator('.arco-drawer-close-icon').click();
  await row.getByRole('button',{name:'删除',exact:true}).click();await action(p,'/admin/audit-logs/'+first.id,'DELETE',()=>p.locator('.arco-popconfirm:visible').last().getByRole('button',{name:'确定',exact:true}).click());
  const after=await(await api(c,'GET','/admin/audit-logs?action=CONFIG_UPDATE',undefined,auth.accessToken)).json();assert(!after.list.some(x=>x.id===first.id));assert.equal(after.total,found.total-1);
 });
 await record('审计批量删除及清理记录保护',async()=>{
  const remaining=await query();assert(remaining.total>0);await p.getByRole('checkbox').first().locator('..').click();
  await p.getByRole('button',{name:'删除所选',exact:true}).click();await action(p,'/admin/audit-logs/batch-delete','POST',()=>p.locator('.arco-popconfirm:visible').last().getByRole('button',{name:'确定',exact:true}).click());
  assert.equal((await(await api(c,'GET','/admin/audit-logs?action=CONFIG_UPDATE',undefined,auth.accessToken)).json()).total,0);
  await p.getByPlaceholder('操作人 / 动作编码 / 对象').fill('AUDIT_LOG_DELETE');const protectedRows=await query();assert(protectedRows.total>0);
  const rows=p.getByRole('row').filter({hasText:'AUDIT_LOG_DELETE'});assert.equal(await rows.getByRole('button',{name:'删除',exact:true}).count(),0);
  await api(c,'DELETE','/admin/audit-logs/'+protectedRows.list[0].id,undefined,auth.accessToken,403);
 });
 await record('审计分类筛选查询和重置',async()=>{
  const category=p.locator('.arco-select').filter({has:p.getByText('业务分类',{exact:true})});
  await p.getByRole('button',{name:'重置',exact:true}).click();await category.click();await p.getByRole('option',{name:'认证安全',exact:true}).click();
  const filtered=await query({category:'AUTH'});assert(filtered.total>0);assert(filtered.list.every(x=>/^(AUTH_|LOGIN|LOGOUT|PASSWORD|PROFILE)/.test(x.action)));
  await p.getByRole('button',{name:'重置',exact:true}).click();await category.waitFor();
 });
 await p.screenshot({path:OUT+'/system-audit-final.png',fullPage:true});
}catch(e){if(p){await p.screenshot({path:OUT+'/system-failure.png',fullPage:true}).catch(()=>{});console.log((await p.locator('body').innerText()).slice(-4000));}console.error(e.stack);process.exitCode=1;}finally{if(b)await b.close();}})();
