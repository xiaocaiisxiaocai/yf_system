const {chromium}=require('playwright');
const {fs,assert,OUT,s,record,login,api,navigate,action,track}=require(process.env.YF_BROWSER_SUPPORT_DIR+'/ui-lib.cjs');
(async()=>{let b,p;try{b=await chromium.launch({channel:'chrome',headless:true});const c=await b.newContext({viewport:{width:1440,height:1000}});p=await c.newPage();track(p,'management');const auth=await login(p,'admin',s.adminPassword);await p.waitForURL(s.base+'/');
const roleName='UI自动验收角色-'+Date.now();let role;
await navigate(p,'/rbac/roles');
await record('角色新增必填校验、取消和创建持久化',async()=>{
 await p.getByRole('button',{name:'新增角色',exact:true}).click();await p.getByRole('button',{name:'创建角色',exact:true}).click();await p.getByText('请输入名称',{exact:true}).waitFor();
 await p.getByRole('button',{name:'取消',exact:true}).click();await p.getByRole('button',{name:'新增角色',exact:true}).click();
 await p.getByPlaceholder('角色名称',{exact:true}).fill(roleName);await p.getByPlaceholder('选填',{exact:true}).fill('浏览器创建，准备变更');
 role=await action(p,'/admin/roles','POST',()=>p.getByRole('button',{name:'创建角色',exact:true}).click());await p.getByRole('row').filter({hasText:roleName}).waitFor();
});
const row=()=>p.getByRole('row').filter({hasText:roleName});
await record('角色编辑保存并重新读取',async()=>{
 await row().getByRole('button',{name:'编辑',exact:true}).click();await p.getByPlaceholder('选填',{exact:true}).fill('已通过浏览器编辑');
 await action(p,'/admin/roles/'+role.id,'PUT',()=>p.getByRole('button',{name:'保存角色',exact:true}).click());await row().getByText('已通过浏览器编辑').waitFor();
});
await row().getByRole('button',{name:'分配权限',exact:true}).click();

await record('角色菜单权限授予和撤销保存',async()=>{
 const drawer=p.locator('.arco-drawer');await drawer.getByRole('treeitem',{name:'工作台',exact:true}).getByRole('checkbox').locator('..').click();
 await action(p,'/admin/roles/'+role.id+'/permissions','PUT',()=>p.getByRole('button',{name:'保存权限',exact:true}).click());
 let r=await(await api(c,'GET','/admin/roles',undefined,auth.accessToken)).json();assert.equal(r.list.find(x=>x.id===role.id).permissionIds.length,1);
 await row().getByRole('button',{name:'分配权限',exact:true}).click();await drawer.getByRole('treeitem',{name:'工作台',exact:true}).getByRole('checkbox').locator('..').click();
 await action(p,'/admin/roles/'+role.id+'/permissions','PUT',()=>p.getByRole('button',{name:'保存权限',exact:true}).click());
 r=await(await api(c,'GET','/admin/roles',undefined,auth.accessToken)).json();assert.equal(r.list.find(x=>x.id===role.id).permissionIds.length,0);
});
for(const label of ['禁用','启用'])await record('角色'+label+'和状态持久化',async()=>{
 await row().getByRole('button',{name:label,exact:true}).click();await action(p,'/admin/roles/'+role.id+'/status','PUT',()=>p.locator('.arco-popconfirm:visible').last().getByRole('button',{name:'确定',exact:true}).click());
 await row().getByRole('button',{name:label==='禁用'?'启用':'禁用',exact:true}).waitFor();
});
await record('角色删除和列表移除',async()=>{await row().getByRole('button',{name:'删除',exact:true}).click();await action(p,'/admin/roles/'+role.id,'DELETE',()=>p.locator('.arco-popconfirm:visible').last().getByRole('button',{name:'确定',exact:true}).click());await row().waitFor({state:'detached'});});
await navigate(p,'/org/depts');const deptName='UI验收事业部-'+Date.now();let dept;
await record('组织架构创建事业部和编辑',async()=>{
 await p.getByRole('button',{name:'新增事业部',exact:true}).click();await p.getByPlaceholder('请输入事业部名称').fill(deptName);
 dept=await action(p,'/admin/departments','POST',()=>p.getByRole('button',{name:'创建事业部',exact:true}).click());
 await p.getByText(deptName,{exact:true}).first().click();await p.getByRole('button',{name:'编辑事业部',exact:true}).click();
 await p.getByPlaceholder('请输入事业部名称').fill(deptName+'已编辑');await action(p,'/admin/departments/'+dept.id,'PUT',()=>p.getByRole('button',{name:'保存事业部',exact:true}).click());
 await p.getByText(deptName+'已编辑',{exact:true}).first().waitFor();
});
await record('组织架构禁用、启用和无引用删除',async()=>{
 for(const label of ['禁用事业部','启用事业部']){await p.getByRole('button',{name:label,exact:true}).click();await action(p,'/admin/departments/'+dept.id+'/status','PUT',()=>p.locator('.arco-popconfirm:visible').last().getByRole('button',{name:'确定',exact:true}).click());}
 await p.getByRole('button',{name:'删除事业部',exact:true}).click();await action(p,'/admin/departments/'+dept.id,'DELETE',()=>p.locator('.arco-popconfirm:visible').last().getByRole('button',{name:'确定',exact:true}).click());await p.getByText(deptName+'已编辑',{exact:true}).waitFor({state:'detached'});
});
await navigate(p,'/logs');await p.getByRole('heading',{name:'操作日志',exact:true}).waitFor();await p.getByText('创建角色',{exact:true}).first().waitFor();
await p.screenshot({path:OUT+'/audit-crud.png',fullPage:true});await record('角色及组织操作在日志页面显示',async()=>{});
}catch(e){if(p){await p.screenshot({path:OUT+'/management-failure.png',fullPage:true}).catch(()=>{});console.log((await p.locator('body').innerText()).slice(-4500));}console.error(e.message);process.exitCode=1;}finally{if(b)await b.close();}})();
