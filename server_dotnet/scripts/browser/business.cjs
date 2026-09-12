const {chromium}=require('playwright');const crypto=require('crypto');
const {fs,assert,OUT,s,f,save,record,login,api,navigate,action,track}=require(process.env.YF_BROWSER_SUPPORT_DIR+'/ui-lib.cjs');
const sha=p=>crypto.createHash('sha256').update(fs.readFileSync(p)).digest('hex');
let browser,p;const sessions={};
async function openUser(key){const c=await browser.newContext({viewport:{width:1440,height:1000},acceptDownloads:true});const page=await c.newPage();track(page,key);const u=key==='admin'?{username:'admin',changedPassword:s.adminPassword}:f.users[key];const r=await login(page,u.username,u.changedPassword);await page.waitForURL(s.base+'/');sessions[key]={c,page,token:r.accessToken};return page;}
async function confirm(p,label,suffix){await p.getByRole('button',{name:label,exact:true}).click();return action(p,suffix,'POST',()=>p.locator('.arco-popconfirm:visible').last().getByRole('button',{name:'确定',exact:true}).click());}
async function upload(p,pid,file){await p.getByRole('tab',{name:'文件',exact:true}).click();await p.getByRole('button',{name:'上传文件',exact:true}).click();await p.getByLabel('选择上传文件').setInputFiles(file);const result=await action(p,'/merge','POST',()=>p.getByRole('button',{name:'上传所选文件',exact:true}).click());await p.getByRole('row').filter({hasText:require('path').basename(file)}).waitFor();return result.id;}
async function download(p,name,target){const row=p.getByRole('row').filter({hasText:name});const ready=p.waitForEvent('download');await row.getByRole('button',{name:'下载文件',exact:true}).click();const item=await ready;await item.saveAs(target);assert.equal(await item.failure(),null);}
async function preview(p,name,kind,label){const row=p.getByRole('row').filter({hasText:name});await row.getByRole('button',{name:'预览文件',exact:true}).click();const modal=p.getByRole('dialog');if(kind==='pdf'){await modal.locator('canvas').waitFor();await p.waitForFunction(()=>{const c=document.querySelector('[role=dialog] canvas');return c&&c.width>0&&c.height>0;});}else{await modal.getByText('甲公司',{exact:true}).waitFor();await modal.getByText('PASS',{exact:true}).waitFor();}await p.screenshot({path:OUT+'/'+label+'.png',fullPage:true});await modal.getByRole('button',{name:'关闭弹窗',exact:true}).click();await modal.waitFor({state:'hidden'});}
(async()=>{try{browser=await chromium.launch({channel:'chrome',headless:true});p=await openUser('admin');const admin=sessions.admin;const projects={};
for(const key of ['a','b'])await record(key+' 项目通过页面创建、启动并配置内部成员',async()=>{
 await navigate(p,'/projects');await p.getByRole('button',{name:'新建项目',exact:true}).click();const modal=p.getByRole('dialog');const name='自动验收'+key+'项目-'+Date.now();
 await modal.getByPlaceholder('项目名称',{exact:true}).fill(name);await modal.getByRole('combobox').click();await p.getByRole('option',{name:f.suppliers[key].name,exact:true}).click();
 const project=await action(p,'/projects','POST',()=>modal.getByRole('button',{name:'创建项目',exact:true}).click());projects[key]={id:project.id,name};f.uiProjects=projects;save();
 await p.getByRole('link',{name,exact:true}).click();await p.getByRole('button',{name:'开始',exact:true}).waitFor();await action(p,'/projects/'+project.id+'/status','PUT',()=>p.getByRole('button',{name:'开始',exact:true}).click());
 await p.getByRole('tab',{name:'成员',exact:true}).click();await p.getByRole('button',{name:'设置公司成员',exact:true}).click();
 const picker=p.getByRole('dialog');await picker.getByRole('checkbox',{name:/自动验收内部员工/}).locator('..').click();
 await action(p,'/projects/'+project.id+'/members','PUT',()=>picker.getByRole('button',{name:'保存成员',exact:true}).click());await p.getByText('自动验收内部员工',{exact:true}).waitFor();
});
const member=await openUser('member'),vendorA=await openUser('a'),vendorB=await openUser('b');
const pdf=OUT+'/valid-preview.pdf',xlsx=OUT+'/vendor-response.xlsx';
for(const key of ['a','b']){const vendor=key==='a'?vendorA:vendorB;const pr=projects[key];
 await navigate(member,'/projects/'+pr.id);await member.getByRole('tab',{name:'文件',exact:true}).waitFor();
 await record(key+' 内部员工上传PDF，供应商实际预览及下载SHA一致',async()=>{
  pr.pdfId=await upload(member,pr.id,pdf);await navigate(vendor,'/projects/'+pr.id);await vendor.getByRole('row').filter({hasText:'valid-preview.pdf'}).waitFor();
  await preview(vendor,'valid-preview.pdf','pdf',key+'-vendor-pdf');await download(vendor,'valid-preview.pdf',OUT+'/'+key+'-received.pdf');assert.equal(sha(pdf),sha(OUT+'/'+key+'-received.pdf'));
 });
 await record(key+' 供应商上传Excel，内部员工及供应商实际预览、下载SHA一致',async()=>{
  pr.excelId=await upload(vendor,pr.id,xlsx);await preview(vendor,'vendor-response.xlsx','excel',key+'-vendor-excel');
  await member.reload();await member.getByRole('row').filter({hasText:'vendor-response.xlsx'}).waitFor();await preview(member,'vendor-response.xlsx','excel',key+'-member-excel');
  await download(member,'vendor-response.xlsx',OUT+'/'+key+'-received.xlsx');assert.equal(sha(xlsx),sha(OUT+'/'+key+'-received.xlsx'));
 });
 await record(key+' 内部和供应商双向留言、已读回执与刷新持久化',async()=>{
  await member.getByRole('tab',{name:/^留言/}).click();const text=key+'公司内部自动验收留言';await member.getByPlaceholder('输入留言，Ctrl+Enter 发送').fill(text);
  const sent=await action(member,'/projects/'+pr.id+'/messages','POST',()=>member.getByRole('button',{name:'发送',exact:true}).click());
  await vendor.getByRole('tab',{name:/^留言/}).click();await vendor.getByText(text,{exact:true}).waitFor();
  const response=key+'公司供应商自动验收回复';await vendor.getByPlaceholder('输入留言，Ctrl+Enter 发送').fill(response);await action(vendor,'/projects/'+pr.id+'/messages','POST',()=>vendor.getByRole('button',{name:'发送',exact:true}).click());
  await member.reload();await member.getByText(response,{exact:true}).waitFor();await member.getByRole('button',{name:'回执详情',exact:true}).click();
  await member.locator('.arco-drawer').getByText(f.suppliers[key].name+'代表',{exact:false}).waitFor();await member.locator('.arco-drawer').getByRole('button',{name:'关闭抽屉',exact:true}).click();
  const reads=await(await api(admin.c,'GET','/messages/'+sent.id+'/reads',undefined,admin.token)).json();assert(reads.readers.some(x=>x.userId===f.users[key].id));
 });
}
save();
await record('甲乙公司无法读取对方项目、文件或留言',async()=>{
 for(const [key,other] of [['a','b'],['b','a']]){const q=sessions[key],pr=projects[other];await navigate(q.page,'/projects/'+pr.id);await q.page.getByText('项目加载失败或没有访问权限',{exact:true}).waitFor();
  for(const suffix of ['/projects/'+pr.id,'/projects/'+pr.id+'/messages','/files/'+pr.pdfId+'/content','/files/'+pr.excelId+'/download'])await api(q.c,'GET',suffix,undefined,q.token,403);
 }
});
await record('公司提交、撤回、供应商驳回、重新提交并确认完成',async()=>{
 const pr=projects.a;await navigate(p,'/projects/'+pr.id);await confirm(p,'提交确认','/projects/'+pr.id+'/submit');await p.getByText('确认方：供应商',{exact:true}).waitFor();assert.equal(await p.getByRole('button',{name:'上传文件',exact:true}).count(),0);
 await confirm(p,'撤回','/projects/'+pr.id+'/withdraw');await p.getByRole('button',{name:'提交确认',exact:true}).waitFor();await confirm(p,'提交确认','/projects/'+pr.id+'/submit');
 await navigate(vendorA,'/projects/'+pr.id);await vendorA.getByRole('button',{name:'驳回',exact:true}).click();await vendorA.getByPlaceholder('请填写驳回原因').fill('自动验收：请补充确认');
 await action(vendorA,'/projects/'+pr.id+'/reject','POST',()=>vendorA.getByRole('button',{name:'确认驳回',exact:true}).click());await vendorA.getByText('上次驳回：自动验收：请补充确认',{exact:true}).waitFor();
 await p.reload();await confirm(p,'提交确认','/projects/'+pr.id+'/submit');await vendorA.reload();await confirm(vendorA,'确认','/projects/'+pr.id+'/confirm');
 await vendorA.getByText('已完成',{exact:true}).first().waitFor();const done=await(await api(admin.c,'GET','/projects/'+pr.id,undefined,admin.token)).json();assert.equal(done.status,'COMPLETED');await vendorA.screenshot({path:OUT+'/a-completed.png',fullPage:true});
});
await record('供应商提交、公司确认完成及公司项目动态',async()=>{
 const pr=projects.b;await navigate(vendorB,'/projects/'+pr.id);await confirm(vendorB,'提交确认','/projects/'+pr.id+'/submit');await vendorB.getByText('确认方：公司',{exact:true}).waitFor();
 await navigate(p,'/projects/'+pr.id);await confirm(p,'确认','/projects/'+pr.id+'/confirm');await p.getByText('已完成',{exact:true}).first().waitFor();await p.getByRole('tab',{name:'项目动态',exact:true}).click();await p.getByText('确认项目',{exact:false}).first().waitFor();await p.screenshot({path:OUT+'/b-completed-activity.png',fullPage:true});
});
await record('窄桌面项目详情可用且无页面横向溢出',async()=>{await p.setViewportSize({width:1024,height:900});assert(await p.getByRole('button',{name:'返回项目列表',exact:true}).isVisible());const width=await p.evaluate(()=>({scroll:document.documentElement.scrollWidth,view:innerWidth}));assert(width.scroll<=width.view+1);await p.screenshot({path:OUT+'/compact-project.png',fullPage:true});});
await record('浏览器运行无未捕获脚本异常和服务端500',async()=>{for(const name of ['page-errors.jsonl','http-errors.jsonl'])assert(!fs.existsSync(OUT+'/'+name)||fs.readFileSync(OUT+'/'+name,'utf8').trim()==='');});
}catch(e){if(p){await p.screenshot({path:OUT+'/business-failure.png',fullPage:true}).catch(()=>{});console.log((await p.locator('body').innerText()).slice(-4000));}console.error(e.stack);process.exitCode=1;}finally{if(browser)await browser.close();}})();
