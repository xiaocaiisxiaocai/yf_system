const {chromium}=require('playwright');
const {fs,assert,OUT,s,f,save,record,login,api,track}=require(process.env.YF_BROWSER_SUPPORT_DIR+'/ui-lib.cjs');
(async()=>{let b,p;try{
 b=await chromium.launch({channel:'chrome',headless:true});
 for(const [key,u] of Object.entries(f.users)){
  if(u.uiFirstChanged)continue;
  const c=await b.newContext({viewport:{width:1440,height:1000}});p=await c.newPage();track(p,key);
  await record(key+' 首次登录强制改密并撤销旧访问令牌',async()=>{
   const old=await login(p,u.username,u.password);assert.equal(old.mustChangePassword,true);
   await p.waitForURL('**/change-password');
   await p.getByRole('textbox',{name:'原密码',exact:true}).fill(u.password);
   await p.getByRole('textbox',{name:'新密码',exact:true}).fill(u.changedPassword);
   await p.getByRole('textbox',{name:'确认新密码',exact:true}).fill(u.changedPassword);
   await p.getByRole('button',{name:'确认修改',exact:true}).click();await p.waitForURL('**/login');
   await api(c,'GET','/auth/profile',undefined,old.accessToken,401);
   const result=await login(p,u.username,u.changedPassword);assert.equal(result.mustChangePassword,false);u.token=result.accessToken;
   await p.waitForURL(s.base+'/');u.uiFirstChanged=true;save();
   await c.storageState({path:OUT+'/'+key+'.storage.private.json'});
  });await c.close();
 }
 const u=f.users.member;const c=await b.newContext({viewport:{width:1440,height:1000}});p=await c.newPage();track(p,'profile');
 const fresh=await login(p,u.username,u.changedPassword);u.token=fresh.accessToken;save();await p.waitForURL(s.base+'/');
 await p.goto(s.base+'/profile');await p.getByRole('button',{name:'修改密码',exact:true}).waitFor();
 await record('普通改密表单弱密码/不一致校验阻止提交',async()=>{
  let requests=0;const handler=r=>{if(r.method()==='PUT'&&r.url().endsWith('/auth/password'))requests++;};p.on('request',handler);
  await p.getByLabel('当前密码',{exact:true}).fill(u.changedPassword);
  await p.getByLabel('新密码',{exact:true}).fill('short');await p.getByLabel('确认新密码',{exact:true}).fill('short');
  await p.getByRole('button',{name:'修改密码',exact:true}).click();await p.getByText('密码需 6-20 个字符，不能使用常见弱密码或简单重复序列',{exact:true}).waitFor();
  await p.getByLabel('新密码',{exact:true}).fill('123456');await p.getByLabel('确认新密码',{exact:true}).fill('123456');
  await p.getByRole('button',{name:'修改密码',exact:true}).click();await p.getByText('密码需 6-20 个字符，不能使用常见弱密码或简单重复序列',{exact:true}).waitFor();
  await p.getByLabel('新密码',{exact:true}).fill('Normal!Q7v9-2026');
  await p.getByLabel('确认新密码',{exact:true}).fill('Mismatch!Q7v9-26');
  await p.getByRole('button',{name:'修改密码',exact:true}).click();await p.getByText('两次输入的新密码不一致',{exact:true}).waitFor();
  assert.equal(requests,0);p.off('request',handler);
 });
 await record('个人资料普通改密成功并重新登录',async()=>{
  const next='Normal!Q7v9-2026';await p.getByLabel('新密码',{exact:true}).fill(next);await p.getByLabel('确认新密码',{exact:true}).fill(next);
  await p.getByRole('button',{name:'修改密码',exact:true}).click();await p.waitForURL('**/login');
  const result=await login(p,u.username,next);u.changedPassword=next;u.token=result.accessToken;save();await p.waitForURL(s.base+'/');
  await c.storageState({path:OUT+'/member.storage.private.json'});
 });
 await record('内部员工隐藏管理菜单且无权限直达不会泄露管理数据',async()=>{
  assert.equal(await p.getByRole('menuitem',{name:'角色权限',exact:true}).count(),0);
  await p.goto(s.base+'/rbac/roles');await p.waitForURL(s.base+'/');
  await api(c,'GET','/admin/roles',undefined,u.token,403);
 });
 await p.screenshot({path:OUT+'/member-dashboard.png',fullPage:true});await c.close();
}catch(e){if(p){await p.screenshot({path:OUT+'/users-failure.png',fullPage:true}).catch(()=>{});console.log((await p.locator('body').innerText()).slice(0,2500));}console.error(e.message);process.exitCode=1;}finally{if(b)await b.close();}})();
