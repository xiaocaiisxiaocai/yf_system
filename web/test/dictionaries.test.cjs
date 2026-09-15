const test = require('node:test')
const assert = require('node:assert/strict')
const fs = require('node:fs')
const path = require('node:path')
const vm = require('node:vm')
const ts = require('typescript')
const React = require('react')
const { create, act } = require('react-test-renderer')
const component = name => Object.assign(props => React.createElement(name, props, props.children), {
  Item: props => React.createElement(`${name}.Item`, props, props.children),
  Search: props => React.createElement(`${name}.Search`, props),
  TabPane: props => React.createElement(`${name}.TabPane`, props),
})
function load(http, form) {
  const arco = new Proxy({ Form: Object.assign(component('Form'), { useForm: () => [form] }),
    Message: { success() {} }, Typography: { Text: component('Text') },
  }, { get: (obj,key) => obj[key] ?? component(key) })
  const filename = path.resolve(__dirname,'../src/pages/system/Dictionaries.tsx')
  const output = ts.transpileModule(fs.readFileSync(filename,'utf8'), { compilerOptions: {
    module:ts.ModuleKind.CommonJS,jsx:ts.JsxEmit.ReactJSX,target:ts.ScriptTarget.ES2022,esModuleInterop:true,
  }}).outputText
  const exports = {}
  vm.runInNewContext(output,{exports,module:{exports},require:name=> name==='@arco-design/web-react'?arco:
    name==='@arco-design/web-react/icon'?{IconPlus:component('IconPlus')}:
    name==='../../api/client'?http:require(name)},{filename})
  return exports.default
}
test('dictionary save blocks duplicates and retains failed form before retry',async()=>{
  let resolve, reject, values
  const writes=[]
  const form={ resetFields(){},setFieldsValue(v){values=v},validate:async()=>values }
  const Page=load({get:async()=>({data:[]}),post:(url,body)=>{writes.push(body);return new Promise((ok,fail)=>{resolve=ok;reject=fail})}},form)
  let renderer,pending
  await act(async()=>{renderer=create(React.createElement(Page))})
  await act(async()=>renderer.root.findAllByType('Button').find(n=>n.props.children?.[0]==='新增').props.onClick())
  const nameRule = renderer.root.findAllByType('Form.Item').find(n=>n.props.field==='name').props.rules[1]
  let validated = false
  nameRule.validator('Valid name', error => { assert.equal(error, undefined); validated = true })
  assert.equal(validated, true, 'successful validation must complete rather than leave save pending')
  values={code:'VENDOR',name:' Vendor ',sortNo:10,enabled:true}
  await act(async()=>{pending=renderer.root.findByType('Modal').props.onOk();await renderer.root.findByType('Modal').props.onOk()})
  assert.equal(writes.length,1)
  assert.equal(writes[0].name,'Vendor')
  await act(async()=>{reject(new Error('conflict'));await pending})
  assert.equal(renderer.root.findByType('Modal').props.visible,true)
  await act(async()=>{pending=renderer.root.findByType('Modal').props.onOk()})
  await act(async()=>{resolve({});await pending})
  assert.equal(renderer.root.findByType('Modal').props.visible,false)
  await act(async()=>renderer.unmount())
})
test('dictionary loading failure is recoverable and cannot create from stale data',async()=>{
  let fail=true
  const Page=load({get:async()=>{if(fail)throw new Error('offline');return {data:[]}}},{resetFields(){},setFieldsValue(){}})
  let renderer
  await act(async()=>{renderer=create(React.createElement(Page))})
  const add=()=>renderer.root.findAllByType('Button').find(n=>n.props.children?.[0]==='新增')
  assert.equal(add().props.disabled,true)
  fail=false
  await act(async()=>renderer.root.findAllByType('Button').find(n=>n.props.children==='重试').props.onClick())
  assert.equal(add().props.disabled,false)
  await act(async()=>renderer.unmount())
})
