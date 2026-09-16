const {chromium}=require('playwright');
const {fs,assert,OUT,s,f,record,login,track}=require(process.env.YF_BROWSER_SUPPORT_DIR+'/ui-lib.cjs');
(async()=>{let b,p;try{
 b=await chromium.launch({channel:'chrome',headless:true});const c=await b.newContext({viewport:{width:1440,height:1000}});p=await c.newPage();track(p,'layout');
 await login(p,'admin',s.adminPassword);await p.waitForURL(s.base+'/');
 const detail=f.uiProjects?.a?.id;assert(detail,'Run business before layout to cover a real detail route');
 const routes=[['dashboard','/','工作台'],['profile','/profile','个人资料'],['projects','/projects','项目协作'],['detail','/projects/'+detail,'返回主项目'],['suppliers','/suppliers','供应商管理'],['users','/org/users','用户管理'],['depts','/org/depts','组织架构'],['roles','/rbac/roles','角色与权限'],['logs','/logs','操作日志'],['config','/system/config','系统参数'],['password','/change-password','确认修改'],['not-found','/does-not-exist','页面不存在或已被移除']];
 const results=[];
 for(const [width,height] of [[1440,1000],[1024,900],[390,844],[1920,1080],[390,600]]){
  await p.setViewportSize({width,height});
  for(const [name,route,label] of routes){
   if(width===1024&&!['projects','detail','suppliers','logs','config'].includes(name))continue;
   if(width===1920&&!['projects','detail'].includes(name))continue;
   if(height===600&&!['password','detail','config'].includes(name))continue;
   await record('页面布局 '+name+' '+width+'x'+height,async()=>{
     await p.goto(s.base+route);await p.getByText(label,{exact:false}).last().waitFor();
    if(name!=='password')await p.getByRole('button',{name:'账号菜单：系统管理员',exact:true}).waitFor();
    await p.waitForFunction(()=>![...document.querySelectorAll('.arco-spin-loading')].some(element=>element.getClientRects().length>0));
    const geometry=await p.evaluate(()=>({width:innerWidth,scroll:document.documentElement.scrollWidth}));assert(geometry.scroll<=geometry.width+1,name+' overflows page');
    await p.screenshot({path:OUT+'/layout-'+name+'-'+width+'x'+height+'.png',animations:'disabled'});results.push({name,route,width,height,status:'pass'});
   });
  }
 }
 await record('移动导航打开和跳转后收起',async()=>{
  await p.goto(s.base+'/');await p.getByRole('button',{name:'打开导航菜单',exact:true}).click();const drawer=p.locator('.arco-drawer:visible');
  await drawer.getByRole('menuitem',{name:'用户管理',exact:true}).click();await p.waitForURL(s.base+'/org/users');await drawer.waitFor({state:'hidden'});
  await p.getByRole('button',{name:'新增用户',exact:true}).click();const dialog=p.getByRole('dialog');await dialog.waitFor();
  for(let i=0;i<12;i++){await p.keyboard.press('Tab');assert(await dialog.evaluate(el=>el.contains(document.activeElement)),'focus left open dialog');}
  await dialog.getByRole('button',{name:'取消',exact:true}).click();await dialog.waitFor({state:'hidden'});
 });
 await record('桌面导航折叠持久化及展开',async()=>{
  await p.setViewportSize({width:1440,height:1000});await p.getByRole('button',{name:'折叠侧边栏',exact:true}).click();await p.reload();await p.getByRole('button',{name:'展开侧边栏',exact:true}).click();await p.getByRole('button',{name:'折叠侧边栏',exact:true}).waitFor();
 });
 const anon=await b.newContext();const q=await anon.newPage();track(q,'anonymous-layout');
 for(const [width,height] of [[1440,1000],[390,844],[390,600]])await record('匿名登录页面 '+width+'x'+height,async()=>{
   await q.setViewportSize({width,height});await q.goto(s.base+'/projects');await q.waitForURL('**/login');await q.getByRole('textbox',{name:'工号',exact:true}).waitFor();
  assert(await q.evaluate(()=>document.documentElement.scrollWidth<=innerWidth+1));await q.screenshot({path:OUT+'/layout-login-'+width+'x'+height+'.png'});results.push({name:'login',route:'/login',width,height,status:'pass'});
 });
 fs.writeFileSync(OUT+'/layout-results.json',JSON.stringify(results,null,2));
}catch(e){if(p){await p.screenshot({path:OUT+'/layout-failure.png',fullPage:true}).catch(()=>{});console.log((await p.locator('body').innerText()).slice(-3500));}console.error(e.stack);process.exitCode=1;}finally{if(b)await b.close();}})();
