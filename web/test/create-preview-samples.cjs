const XLSX=require('xlsx')
const path=require('node:path')
const folder=path.resolve(__dirname,'../output/playwright')
const book=XLSX.utils.book_new()
const sheet=XLSX.utils.aoa_to_sheet([['完整预览验收',''],['中文','<img src=x onerror=alert(1)>'],[42,3]])
sheet['!merges']=[{s:{r:0,c:0},e:{r:0,c:1}}]
sheet.B3.f='1+2'
XLSX.utils.book_append_sheet(book,sheet,'验收')
XLSX.utils.book_append_sheet(book,XLSX.utils.aoa_to_sheet([['第二工作表']]),'第二页')
XLSX.writeFile(book,path.join(folder,'full-legacy.xls'),{bookType:'biff8'})
const large=XLSX.utils.book_new(), wide={}
XLSX.utils.sheet_add_aoa(wide,Array.from({length:501},(_,r)=>Array.from({length:101},(_,c)=>`${r+1}:${c+1}`)),{origin:'C3'})
wide['!ref']='C3:CY503'
wide['!merges']=[{s:{r:500,c:101},e:{r:502,c:102}}]
XLSX.utils.book_append_sheet(large,wide,'边界501行101列')
XLSX.writeFile(large,path.join(folder,'full-boundaries-offset.xlsx'))
console.log('Created legacy XLS and 501 x 101 XLSX at non-A1 origin')
