const fs=require('fs');const path=require('path');const assert=require('assert/strict');
const OUT=process.env.YF_BROWSER_EVIDENCE_DIR;
const s=JSON.parse(fs.readFileSync(OUT+'/state.private.json','utf8'));
const f=fs.existsSync(OUT+'/fixtures.private.json')?JSON.parse(fs.readFileSync(OUT+'/fixtures.private.json','utf8')):{};
function save(){fs.writeFileSync(OUT+'/fixtures.private.json',JSON.stringify(f));}
let activeOperation=null;
async function record(name,fn){
 const file=OUT+'/browser-operations.json';
 const previousOperation=activeOperation;activeOperation=name;
 try{await fn();const items=fs.existsSync(file)?JSON.parse(fs.readFileSync(file,'utf8')):[];items.push({name,status:'pass',at:new Date().toISOString()});fs.writeFileSync(file,JSON.stringify(items,null,2));console.log('PASS '+name);}
 catch(e){const items=fs.existsSync(file)?JSON.parse(fs.readFileSync(file,'utf8')):[];items.push({name,status:'fail',at:new Date().toISOString(),error:e.message});fs.writeFileSync(file,JSON.stringify(items,null,2));throw e;}
 finally{activeOperation=previousOperation;}
}
async function login(p,user,password){
 if(p.url()!==s.base+'/login')await p.goto(s.base+'/login');
 await p.getByRole('textbox',{name:'工号',exact:true}).fill(user);
 await p.getByRole('textbox',{name:'密码',exact:true}).fill(password);
 await reserveLoginBudget(user);
 const logged=p.waitForResponse(r=>r.url().endsWith('/api/v1/auth/login')&&r.request().method()==='POST');
 await p.getByRole('button',{name:'登录',exact:true}).click();const result=await logged;assert.equal(result.status(),200);return result.json();
}
async function reserveLoginBudget(user,count=1){
 assert.ok(Number.isInteger(count)&&count>0&&count<=10,'login budget count');
 const budgetFile=OUT+'/login-budget.json';
 const account=String(user).trim().toLowerCase();
 for(;;){
  const now=Date.now();
  let issued=fs.existsSync(budgetFile)?JSON.parse(fs.readFileSync(budgetFile,'utf8')):[];
  issued=issued.filter(item=>item&&Number.isFinite(item.at)&&now-item.at<61000);
  const accountIssued=issued.filter(item=>item.account===account);
  const ipOverflow=issued.length+count-60;
  const accountOverflow=accountIssued.length+count-10;
  let readyAt=now;
  if(ipOverflow>0)readyAt=Math.max(readyAt,issued.slice().sort((a,b)=>a.at-b.at)[ipOverflow-1].at+61000);
  if(accountOverflow>0)readyAt=Math.max(readyAt,accountIssued.slice().sort((a,b)=>a.at-b.at)[accountOverflow-1].at+61000);
  if(readyAt>now){
   const delay=readyAt-now;
   console.log('Waiting for production login budget: '+Math.ceil(delay/1000)+'s');
   await new Promise(resolve=>setTimeout(resolve,delay));
   continue;
  }
  issued.push(...Array.from({length:count},()=>({account,at:Date.now()})));
  fs.writeFileSync(budgetFile,JSON.stringify(issued));
  return;
 }
}
async function api(c,method,url,data,token,expected=200){
 const r=await c.request.fetch(s.base+'/api/v1'+url,{method,data,headers:{Origin:s.base,Authorization:'Bearer '+token}});
 if(r.status()!==expected){
  const body=await r.json().catch(()=>({}));
  const message=String(body.message||'non-JSON error').replace(/Bearer\s+\S+/gi,'Bearer [redacted]').replace(/eyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+/g,'[redacted-token]').slice(0,400);
  assert.equal(r.status(),expected,method+' '+url+': '+message);
 }
 return r;
}
async function navigate(p,url){await p.goto(s.base+url);await p.getByText('收起导航',{exact:true}).waitFor();}
async function action(p,suffix,method,fn){const [r]=await Promise.all([p.waitForResponse(r=>new URL(r.url()).pathname.endsWith(suffix)&&r.request().method()===method),Promise.resolve().then(fn)]);assert.equal(r.status(),200,suffix);return r.json();}
function track(p,label){
 const diagnostic=(kind,detail)=>fs.appendFileSync(OUT+'/browser-diagnostics.jsonl',JSON.stringify({at:new Date().toISOString(),label,operation:activeOperation,kind,...detail})+'\n');
 const safeText=text=>String(text).replace(/Bearer\s+\S+/gi,'Bearer [redacted]').replace(/eyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+/g,'[redacted-token]').slice(0,1200);
 p.on('pageerror',e=>fs.appendFileSync(OUT+'/page-errors.jsonl',JSON.stringify({label,error:safeText(e.message)})+'\n'));
 p.on('console',message=>{if(message.type()==='error')diagnostic('console-error',{message:safeText(message.text())});});
 p.on('requestfailed',request=>diagnostic('request-failed',{method:request.method(),path:new URL(request.url()).pathname,error:request.failure()?.errorText}));
 p.on('response',r=>{
  if(r.status()<400)return;
  const entry={label,status:r.status(),method:r.request().method(),path:new URL(r.url()).pathname};
  diagnostic('http-error',{...entry,expectedServerError:p.expectedServerErrors?.has(entry.path)||false});
  if(r.status()>=500&&!p.expectedServerErrors?.has(entry.path))fs.appendFileSync(OUT+'/http-errors.jsonl',JSON.stringify(entry)+'\n');
 });
}
module.exports={fs,path,assert,OUT,s,f,save,record,login,reserveLoginBudget,api,navigate,action,track};
