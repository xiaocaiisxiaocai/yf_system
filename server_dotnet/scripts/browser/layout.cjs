const {chromium}=require('playwright');
const {fs,assert,OUT,s,f,record,login,track}=require(process.env.YF_BROWSER_SUPPORT_DIR+'/ui-lib.cjs');
(async()=>{let b,p;try{
 b=await require(process.env.YF_BROWSER_SUPPORT_DIR + '/shared-browser.cjs')(chromium);const c=await b.newContext({viewport:{width:1440,height:1000}});p=await c.newPage();track(p,'layout');
 await login(p,'admin',s.adminPassword);await p.waitForURL(s.base+'/');
 const detail=f.uiProjects?.a?.id;assert(detail,'Run business before layout to cover a real detail route');
 const routes=[['dashboard','/','工作台'],['profile','/profile','个人资料'],['projects','/projects','Robot仿真协作'],['detail','/projects/'+detail,f.uiProjects.a.name],['suppliers','/suppliers','供应商管理'],['users','/org/users','用户管理'],['depts','/org/depts','组织架构'],['roles','/rbac/roles','角色与权限'],['logs','/logs','操作日志'],['config','/system/config','系统参数'],['password','/change-password','确认修改'],['not-found','/does-not-exist','页面不存在或已被移除']];
 routes.push(['dictionaries','/system/dictionaries','数据字典']);
 routes.push(['oem-transfers','/oem/transfers','文件传递单'],['oem-approvals','/oem/approvals','审批'],['oem-companies','/oem/admin/companies','厂商与账号'],['oem-audit','/oem/admin/audit','审计日志']);
 const listPages=new Set(['projects','suppliers','users','roles','logs','dictionaries','oem-transfers','oem-approvals','oem-companies','oem-audit']);
 const results=[];
 // 整页 scrollWidth 查不到卡片内部的裁切（卡片 overflow 会把溢出藏起来）：窄屏下逐个核对标题、表格和操作按钮没有超出所在容器。
 const clippedInNarrowCards=name=>p.evaluate(page=>{
  const bad=[],box=element=>element.getBoundingClientRect();
  const inside=(label,element,container)=>{if(!element||!container)return bad.push(label+' missing');const a=box(element),b=box(container);if(a.right>b.right+1||a.left<b.left-1)bad.push(label+' outside container '+Math.round(a.left)+'-'+Math.round(a.right)+' vs '+Math.round(b.left)+'-'+Math.round(b.right))};
  const fits=(label,element)=>{if(!element)return bad.push(label+' missing');if(element.scrollWidth>element.clientWidth+1)bad.push(label+' scrollWidth '+element.scrollWidth+' > clientWidth '+element.clientWidth)};
  const q=selector=>document.querySelector(selector);
  const card=q('.project-detail-summary-card');
  fits('detail summary card',card);fits('detail heading',q('.project-detail-heading'));
  inside('detail title block',q('.project-detail-title'),card);inside('detail title text',q('.project-detail-name h1'),card);
  // 文件表：窄屏取舍列后不应再需要横向滚动，操作按钮必须落在自己的单元格内。
  const scroller=q('.file-table .arco-table-content-inner');if(scroller)fits('file table',scroller);
  for(const slots of document.querySelectorAll('.file-table .action-slots')){
   const cell=slots.closest('.arco-table-td');
   for(const button of slots.querySelectorAll('button'))inside('file action button',button,cell);
  }
  return bad;
 },name);
 for(const [width,height] of [[1440,1000],[1024,900],[390,844],[1920,1080],[390,600]]){
  await p.setViewportSize({width,height});
  for(const [name,route,label] of routes){
   await record('页面布局 '+name+' '+width+'x'+height,async()=>{
     await p.goto(s.base+route);await p.getByText(label,{exact:false}).last().waitFor();
    if(name!=='password')await p.getByRole('button',{name:'账号菜单：系统管理员',exact:true}).waitFor();
    await p.waitForFunction(()=>![...document.querySelectorAll('.arco-spin-loading')].some(element=>element.getClientRects().length>0));
    const geometry=await p.evaluate(()=>({width:innerWidth,scroll:document.documentElement.scrollWidth}));assert(geometry.scroll<=geometry.width+1,name+' overflows page');
    if(width<=720&&name==='detail')assert.deepStrictEqual(await clippedInNarrowCards(name),[],name+' has content clipped inside its cards at '+width+'px');
    // 列表页：筛选栏贴卡片顶部，分页贴卡片底部，即使只有几行数据也不跟着表格内容上移。
    if(width>720&&height>700&&listPages.has(name)){
     const pinned=await p.evaluate(()=>{const card=document.querySelector('.page-card--table');const pager=card?.querySelector('.arco-table-pagination');const bar=card?.querySelector('.oem-vendor-tabs,.page-toolbar,.audit-filter-panel,.dictionary-tabs');const empty=Boolean(card?.querySelector('.arco-table-no-data'));if(!card||!bar||(!pager&&!empty))return {missing:true};const c=card.getBoundingClientRect(),t=bar.getBoundingClientRect();return {bottomGap:pager?Math.round(c.bottom-pager.getBoundingClientRect().bottom):null,topGap:Math.round(t.top-c.top)}});
     assert(!pinned.missing,name+' list page lost its toolbar or pagination');
     // An empty list renders no pagination; only the toolbar position applies then.
     if(pinned.bottomGap!==null)assert(pinned.bottomGap<=40,name+' pagination is not pinned to the card bottom (gap '+pinned.bottomGap+'px)');
     assert(pinned.topGap<=40,name+' filter bar is not at the card top (gap '+pinned.topGap+'px)');
    }
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
   await q.setViewportSize({width,height});await q.goto(s.base+'/projects');await q.waitForURL('**/login');await q.getByRole('textbox',{name:'账号',exact:true}).waitFor();
  assert(await q.evaluate(()=>document.documentElement.scrollWidth<=innerWidth+1));await q.screenshot({path:OUT+'/layout-login-'+width+'x'+height+'.png'});results.push({name:'login',route:'/login',width,height,status:'pass'});
 });
 fs.writeFileSync(OUT+'/layout-results.json',JSON.stringify(results,null,2));
}catch(e){if(p){await p.screenshot({path:OUT+'/layout-failure.png',fullPage:true}).catch(()=>{});console.log((await p.locator('body').innerText()).slice(-3500));}console.error(e.stack);process.exitCode=1;}finally{if(b)await b.close();}})();
