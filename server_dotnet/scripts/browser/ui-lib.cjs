const fs=require('fs');const path=require('path');const assert=require('assert/strict');
const OUT=process.env.YF_BROWSER_EVIDENCE_DIR;
const s=JSON.parse(fs.readFileSync(OUT+'/state.private.json','utf8'));
const f=fs.existsSync(OUT+'/fixtures.private.json')?JSON.parse(fs.readFileSync(OUT+'/fixtures.private.json','utf8')):{};
function save(){fs.writeFileSync(OUT+'/fixtures.private.json',JSON.stringify(f));}
async function record(name,fn){
 const file=OUT+'/browser-operations.json';
 try{await fn();const items=fs.existsSync(file)?JSON.parse(fs.readFileSync(file,'utf8')):[];items.push({name,status:'pass',at:new Date().toISOString()});fs.writeFileSync(file,JSON.stringify(items,null,2));console.log('PASS '+name);}
 catch(e){const items=fs.existsSync(file)?JSON.parse(fs.readFileSync(file,'utf8')):[];items.push({name,status:'fail',at:new Date().toISOString(),error:e.message});fs.writeFileSync(file,JSON.stringify(items,null,2));throw e;}
}
async function login(p,user,password){
 // Production rate limits are retained. The shared budget includes the login
 // page's initial image and the explicit refresh used to observe its challenge.
 const budgetFile=OUT+'/captcha-budget.json';
 let issued=fs.existsSync(budgetFile)?JSON.parse(fs.readFileSync(budgetFile,'utf8')):[];
 issued=issued.filter(t=>Date.now()-t<61000);
 if(issued.length>=20){const delay=61000-(Date.now()-issued[0]);console.log('Waiting for production CAPTCHA budget: '+Math.ceil(delay/1000)+'s');await new Promise(r=>setTimeout(r,delay));issued=issued.filter(t=>Date.now()-t<61000);}
 issued.push(Date.now(),Date.now());fs.writeFileSync(budgetFile,JSON.stringify(issued));
 if(p.url()!==s.base+'/login')await p.goto(s.base+'/login');
 await p.getByRole('textbox',{name:'验证码',exact:true}).waitFor();
 const challenge=p.waitForResponse(r=>new URL(r.url()).pathname==='/api/v1/auth/captcha');
 await p.getByRole('button',{name:'刷新验证码',exact:true}).click();const response=await challenge;
 assert.equal(response.status(),200,'CAPTCHA request');const data=await response.json();
 const observed=await p.request.get(s.base+'/__test/captcha-answer/'+data.captchaId,{headers:{'X-Test-Host-Key':s.key}});
 assert.equal(observed.status(),200,'isolated challenge observer');const answer=await observed.json();
 await p.getByRole('textbox',{name:'工号',exact:true}).fill(user);
 await p.getByRole('textbox',{name:'密码',exact:true}).fill(password);
 await p.getByRole('textbox',{name:'验证码',exact:true}).fill(answer.answer);
 const logged=p.waitForResponse(r=>r.url().endsWith('/api/v1/auth/login')&&r.request().method()==='POST');
 await p.getByRole('button',{name:'登录',exact:true}).click();const result=await logged;assert.equal(result.status(),200);return result.json();
}
async function api(c,method,url,data,token,expected=200){const r=await c.request.fetch(s.base+'/api/v1'+url,{method,data,headers:{Origin:s.base,Authorization:'Bearer '+token}});assert.equal(r.status(),expected,method+' '+url);return r;}
async function navigate(p,url){await p.goto(s.base+url);await p.getByText('收起导航',{exact:true}).waitFor();}
async function action(p,suffix,method,fn){const [r]=await Promise.all([p.waitForResponse(r=>new URL(r.url()).pathname.endsWith(suffix)&&r.request().method()===method),Promise.resolve().then(fn)]);assert.equal(r.status(),200,suffix);return r.json();}
function track(p,label){p.on('pageerror',e=>fs.appendFileSync(OUT+'/page-errors.jsonl',JSON.stringify({label,error:e.message})+'\n'));p.on('response',r=>{if(r.status()>=500&&!p.expectedServerErrors?.has(new URL(r.url()).pathname))fs.appendFileSync(OUT+'/http-errors.jsonl',JSON.stringify({label,status:r.status(),path:new URL(r.url()).pathname})+'\n');});}
module.exports={fs,path,assert,OUT,s,f,save,record,login,api,navigate,action,track};
