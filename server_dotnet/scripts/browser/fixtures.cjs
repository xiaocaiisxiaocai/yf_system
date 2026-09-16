const {chromium}=require('playwright');
const crypto=require('node:crypto');
const {fs,OUT,s,api}=require(process.env.YF_BROWSER_SUPPORT_DIR+'/ui-lib.cjs');
const XLSX=require(process.env.YF_PROJECT_ROOT+'/web/node_modules/xlsx');
(async()=>{let b;try{
 b=await chromium.launch({channel:'chrome',headless:true});const c=await b.newContext();
 const roles=(await(await api(c,'GET','/admin/roles',undefined,s.adminToken)).json()).list;
 const fixtures={users:{},suppliers:{},roles:Object.fromEntries(roles.map(r=>[r.name,r.id]))};
 const password=()=> 'Browser!'+crypto.randomBytes(6).toString('base64url');
 for(const [key,label] of [['a','自动验收甲公司'],['b','自动验收乙公司']]){
  const supplier=await(await api(c,'POST','/admin/suppliers',{name:label,remark:'本轮独立测试库'},s.adminToken)).json();
  fixtures.suppliers[key]=supplier;const initial=password();
 const user=await(await api(c,'POST','/admin/suppliers/'+supplier.id+'/accounts',{employeeNo:'auto_supplier_'+key,password:initial,realName:label+'代表',email:key+'@example.invalid'},s.adminToken)).json();
 fixtures.users[key]={id:user.id,username:user.employeeNo,password:initial,changedPassword:password()};
 }
 const division=await(await api(c,'POST','/admin/departments',{name:'自动验收事业部-'+crypto.randomBytes(4).toString('hex'),parentId:null,sortNo:900},s.adminToken)).json();
 const department=await(await api(c,'POST','/admin/departments',{name:'自动验收部门-'+crypto.randomBytes(4).toString('hex'),parentId:division.id,sortNo:901},s.adminToken)).json();
 const section=await(await api(c,'POST','/admin/departments',{name:'自动验收课别-'+crypto.randomBytes(4).toString('hex'),parentId:department.id,sortNo:902},s.adminToken)).json();
 for(const [key,label,role] of [['member','自动验收内部员工','内部成员'],['manager','自动验收项目经理','项目管理员']]){
  const initial=password();const user=await(await api(c,'POST','/admin/users',{employeeNo:'auto_'+key,password:initial,realName:label,email:key+'@example.invalid',departmentId:section.id,roleId:fixtures.roles[role]},s.adminToken)).json();
  fixtures.users[key]={id:user.id,username:user.employeeNo,password:initial,changedPassword:password()};
 }
 fs.writeFileSync(OUT+'/fixtures.private.json',JSON.stringify(fixtures));
 for(const [type,name,parent] of [['ROBOT_VENDOR','自动验收 Robot 厂商',null],['ROBOT_MODEL','自动验收 Robot 型号','vendor']]){
  const item=await(await api(c,'POST','/project-dictionaries',{type,name,parentId:parent?fixtures.vendorId:null,sortNo:10,enabled:true},s.adminToken)).json();
  if(type==='ROBOT_VENDOR')fixtures.vendorId=item.id;
 }
 fs.writeFileSync(OUT+'/fixtures.private.json',JSON.stringify(fixtures));
 fs.copyFileSync(process.env.YF_PROJECT_ROOT+'/web/test/fixtures/pdf-compatibility.pdf',OUT+'/valid-preview.pdf');
 const book=XLSX.utils.book_new();XLSX.utils.book_append_sheet(book,XLSX.utils.aoa_to_sheet([['公司','验收结果'],['甲公司','PASS']]),'验收');
 XLSX.writeFile(book,OUT+'/vendor-response.xlsx');
 console.log('Created disposable API fixtures and valid document samples.');
}finally{if(b)await b.close();}})().catch(e=>{console.error(e.message);process.exitCode=1;});
