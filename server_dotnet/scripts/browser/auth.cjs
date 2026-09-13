const {chromium}=require('playwright');
const fs=require('fs');
const path=require('path');
const assert=require('assert/strict');
const ROOT=process.env.YF_PROJECT_ROOT;
const OUT=process.env.YF_BROWSER_EVIDENCE_DIR;
const state=JSON.parse(fs.readFileSync(path.join(OUT,'state.private.json'),'utf8'));
const checks=[];
let browser,page;
async function check(name,action){await action();checks.push({name,status:'pass'});console.log('PASS '+name);fs.writeFileSync(path.join(OUT,'browser-results.json'),JSON.stringify({checks},null,2));}
const {login}=require(process.env.YF_BROWSER_SUPPORT_DIR+'/ui-lib.cjs');
(async()=>{
 try{
  browser=await chromium.launch({channel:'chrome',headless:true});
  const context=await browser.newContext({viewport:{width:1440,height:1000},acceptDownloads:true});
  page=await context.newPage();
  const errors=[];page.on('pageerror',e=>errors.push(e.message));
  await check('管理员工号密码直登并进入首次改密',async()=>{
   await login(page,'admin',state.initialPassword);await page.waitForURL('**/change-password');
   await page.getByText('首次登录，请先修改初始密码',{exact:true}).waitFor();
  });
  const changed=state.adminChangedPassword;
  await check('首次改密、旧会话撤销和新密码重新登录',async()=>{
   await page.getByRole('textbox',{name:'原密码',exact:true}).fill(state.initialPassword);
   await page.getByRole('textbox',{name:'新密码',exact:true}).fill(changed);
   await page.getByRole('textbox',{name:'确认新密码',exact:true}).fill(changed);
   await page.getByRole('button',{name:'确认修改',exact:true}).click();
   await page.waitForURL('**/login');
   const result=await login(page,'admin',changed);assert.equal(result.mustChangePassword,false);
   state.adminPassword=changed;state.adminToken=result.accessToken;
   fs.writeFileSync(path.join(OUT,'state.private.json'),JSON.stringify(state));
   await page.waitForURL(state.base+'/');
  });
  await page.getByText('暂无待确认项目',{exact:true}).waitFor();
  await page.getByText('暂无未读留言',{exact:true}).waitFor();
  await page.screenshot({path:path.join(OUT,'dashboard.png'),fullPage:true});
  assert.deepEqual(errors,[],'No uncaught browser errors during authentication');
  await context.storageState({path:path.join(OUT,'admin.storage.private.json')});
  console.log((await page.locator('body').innerText()).slice(0,6000));
  fs.writeFileSync(path.join(OUT,'browser-errors.json'),JSON.stringify(errors));
 }catch(e){
  if(page){await page.screenshot({path:path.join(OUT,'failure.png'),fullPage:true}).catch(()=>{});console.log((await page.locator('body').innerText()).slice(0,5000));}
  console.error(e.message);process.exitCode=1;
 }finally{if(browser)await browser.close();}
})();
