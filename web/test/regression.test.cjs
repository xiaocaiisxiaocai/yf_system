const test = require('node:test')
const assert = require('node:assert/strict')
const fs = require('node:fs')
const path = require('node:path')
const vm = require('node:vm')
const ts = require('typescript')
const React = require('react')
const { create, act } = require('react-test-renderer')

const component = (name) => Object.assign((props) => React.createElement(name, props), {
  Search: (props) => React.createElement(`${name}.Search`, props),
  Option: (props) => React.createElement(`${name}.Option`, props),
  TextArea: (props) => React.createElement(`${name}.TextArea`, props),
  Password: (props) => React.createElement(`${name}.Password`, props),
  RangePicker: (props) => React.createElement(`${name}.RangePicker`, props),
  TabPane: (props) => React.createElement(`${name}.TabPane`, props),
})
const arco = new Proxy({
  Form: Object.assign(component('Form'), { useForm: () => [{}], Item: component('Form.Item') }),
  Typography: { Text: component('Text'), Title: component('Title') },
  Message: { error() {}, warning() {}, success() {}, info() {} },
}, { get: (obj, key) => obj[key] ?? component(key) })

const actionSlotsModule = {
  actionSlots: (slots, variant) => React.createElement(
    'div',
    { className: `action-slots action-slots--${variant}` },
    slots.map((slot, index) => React.createElement(
      'span',
      { key: index, className: slot ? 'action-slot' : 'action-slot action-slot--empty' },
      slot || null,
    )),
  ),
}

function findElement(node, predicate) {
  if (!React.isValidElement(node)) return undefined
  if (predicate(node)) return node
  for (const child of React.Children.toArray(node.props.children)) {
    const found = findElement(child, predicate)
    if (found) return found
  }
  return undefined
}

function findActionButton(node, text) {
  return findElement(node, (item) => item.props.children === text)
}

test('table action slots keep empty action positions for row alignment', () => {
  const { actionSlots } = loadTs('src/components/ActionSlots.tsx', {})
  const node = actionSlots([
    React.createElement('Button', null, '进入'),
    false,
    React.createElement('Button', null, '状态'),
  ], 'project')
  const slots = React.Children.toArray(node.props.children)
  assert.equal(slots.length, 3)
  assert.match(slots[0].props.className, /action-slot/)
  assert.match(slots[1].props.className, /action-slot--empty/)
  assert.equal(slots[2].props.children.props.children, '状态')
})

function loadTs(relativePath, mocks, globals = {}) {
  const filename = path.resolve(__dirname, '..', relativePath)
  const source = ts.transpileModule(fs.readFileSync(filename, 'utf8'), {
    compilerOptions: { module: ts.ModuleKind.CommonJS, jsx: ts.JsxEmit.ReactJSX, target: ts.ScriptTarget.ES2022, esModuleInterop: true },
  }).outputText
  const exports = {}
  vm.runInNewContext(source, {
    exports, module: { exports }, console, setTimeout, clearTimeout, URL, AbortController, ...globals,
    require: (name) => {
      if (name in mocks) return mocks[name]
      if (name.endsWith('/ActionSlots')) return actionSlotsModule
      return require(name)
    },
  }, { filename })
  return exports
}

for (const [page, api, props] of [
  ['pages/project/ProjectList.tsx', '/projects', {}],
  ['pages/supplier/SupplierList.tsx', '/admin/suppliers', {}],
  ['pages/org/UserList.tsx', '/admin/users', {}],
  ['components/FileTable.tsx', '/projects/1/files', { projectId: 1, projectStatus: 'IN_PROGRESS', rounds: [] }],
]) {
  test(`${page}: repeated identical searches finish and reload`, async () => {
    let requests = 0
    const http = { get: async (url) => {
      if (url === api) { requests++; return { data: { list: [], total: 0, page: 1, pageSize: 10 } } }
      return { data: [] }
    } }
    const auth = { useAuth: () => ({ hasPerm: () => true, user: { id: 1, userType: 'INTERNAL' } }) }
    const mocks = {
      '@arco-design/web-react': arco,
      '@arco-design/web-react/icon': new Proxy({}, { get: (_, name) => component(name) }),
      'react-router-dom': { useNavigate: () => () => {} },
      '../../api/client': http, '../api/client': http,
      '../../store/auth': auth, '../store/auth': auth,
      '../../api/types': { PROJECT_STATUS: {}, fmtTime: String },
      '../api/types': { fmtTime: String, fmtSize: String },
      './ChunkUploader': component('Uploader'), './PdfPreview': component('PDF'),
    }
    const Page = loadTs(`src/${page}`, mocks).default
    let renderer
    await act(async () => { renderer = create(React.createElement(Page, props)) })
    const search = () => renderer.root.findByType('Input.Search').props.onSearch('steel')
    await act(async () => search())
    const before = requests
    await act(async () => search())
    const table = renderer.root.findAllByType('Table')[0]
    assert.equal(table.props.loading, false, 'same search must not leave a permanent spinner')
    assert.equal(requests, before + 1, 'an explicit repeated search should re-fetch')
    await act(async () => renderer.unmount())
  })
}

test('supplier account permission revocation removes the open account drawer', async () => {
  let allowed=false, accountRequests=0
  const supplier={id:8,name:'fixture',code:'FIXTURE',status:'ACTIVE'}
  const Page=loadTs('src/pages/supplier/SupplierList.tsx',{
    '@arco-design/web-react':arco,'@arco-design/web-react/icon':new Proxy({},{get:(_,n)=>component(n)}),
    '../../store/auth':{useAuth:()=>({hasPerm:()=>allowed})},
    '../../api/types':{fmtTime:String},
    '../../api/client':{get:async url=>{
      if(url.endsWith('/accounts')){accountRequests++;return {data:[]}}
      return {data:{list:[supplier],total:1}}
    }},
  }).default
  let renderer
  await act(async()=>{renderer=create(React.createElement(Page))})
  const accountAction=()=>{
    const actions=renderer.root.findAllByType('Table')[0].props.columns.at(-1).render(null,supplier)
    return findActionButton(actions, '账号管理')
  }
  assert.equal(accountAction(),undefined)
  assert.equal(renderer.root.findAllByType('Drawer').length,0)
  allowed=true
  await act(async()=>renderer.update(React.createElement(Page)))
  await act(async()=>accountAction().props.onClick())
  assert.equal(renderer.root.findByType('Drawer').props.visible,true)
  assert.equal(accountRequests,1)
  allowed=false
  await act(async()=>renderer.update(React.createElement(Page)))
  assert.equal(accountAction(),undefined)
  assert.equal(renderer.root.findAllByType('Drawer').length,0,'revocation must remove the already-open drawer')
  assert.equal(accountRequests,1,'revocation cannot issue another account read')
  await act(async()=>renderer.unmount())
})

test('round cancellation is shown only to its creator or a viewer of all projects', async () => {
  for (const [userId,viewAll,expected] of [[2,false,false],[1,false,true],[2,true,true]]) {
    const round={id:8,createdBy:1,status:'PENDING',confirmSide:'COMPANY'}
    const Page=loadTs('src/components/RoundPanel.tsx',{
      '@arco-design/web-react':arco,'@arco-design/web-react/icon':new Proxy({},{get:(_,n)=>component(n)}),
      '../api/client':{get:async()=>({data:[round]})},
      '../store/auth':{useAuth:()=>({user:{id:userId,userType:'INTERNAL'},hasPerm:p=>p==='round:cancel'||(viewAll&&p==='project:view_all')})},
      '../api/types':{ROUND_STATUS:{},fmtTime:String},
    }).default
    let renderer
    await act(async()=>{renderer=create(React.createElement(Page,{projectId:1,projectStatus:'IN_PROGRESS',onChanged(){}}))})
    const actions=renderer.root.findByType('Table').props.columns.at(-1).render(null,round)
    const allowed=!!findElement(actions, (node) => node.props.title === '撤销该轮次？关联文件将一并锁定')
    assert.equal(allowed,expected,`user ${userId}, viewAll ${viewAll}`)
    await act(async()=>renderer.unmount())
  }
})

test('supplier account loading failures stop spinning and can retry', async () => {
  let fail=true
  const supplier={id:8,name:'fixture',code:'FIXTURE',status:'ACTIVE'}
  const Page=loadTs('src/pages/supplier/SupplierList.tsx',{
    '@arco-design/web-react':arco,'@arco-design/web-react/icon':new Proxy({},{get:(_,n)=>component(n)}),
    '../../store/auth':{useAuth:()=>({hasPerm:()=>true})},'../../api/types':{fmtTime:String},
    '../../api/client':{get:async url=>{
      if(url.endsWith('/accounts')){if(fail)throw Error('simulated failure');return {data:[{id:16,username:'fixture-account'}]}}
      return {data:{list:[supplier],total:1}}
    }},
  }).default
  let renderer
  await act(async()=>{renderer=create(React.createElement(Page))})
  const actions=renderer.root.findAllByType('Table')[0].props.columns.at(-1).render(null,supplier)
  await act(async()=>findActionButton(actions, '账号管理').props.onClick())
  assert.equal(renderer.root.findByType('Drawer').findByType('Table').props.loading,false)
  fail=false
  await act(async()=>renderer.root.findAllByType('Button').find(n=>n.props.children==='重试').props.onClick())
  assert.equal(renderer.root.findByType('Drawer').findByType('Table').props.data[0].id,16)
  await act(async()=>renderer.unmount())
})

test('closing or switching PDF previews before download completion does not leak blob URLs', async () => {
  const pending=new Map(),created=[],revoked=[]
  const Page=loadTs('src/components/PdfPreview.tsx',{
    '@arco-design/web-react':arco,
    '../api/client':{get:url=>new Promise(resolve=>pending.set(url,resolve))},
  },{Blob,URL:{createObjectURL:()=>{const u=`blob:test-${created.length}`;created.push(u);return u},revokeObjectURL:u=>revoked.push(u)}}).default
  let renderer
  await act(async()=>{renderer=create(React.createElement(Page,{fileId:1}))})
  await act(async()=>renderer.update(React.createElement(Page,{fileId:2})))
  await act(async()=>pending.get('/files/1/content')({data:'old pdf'}))
  await act(async()=>pending.get('/files/2/content')({data:'current pdf'}))
  assert.equal(renderer.root.findByType('iframe').props.src,created.at(-1))
  await act(async()=>renderer.update(React.createElement(Page,{fileId:3})))
  await act(async()=>renderer.unmount())
  await act(async()=>pending.get('/files/3/content')({data:'closed pdf'}))
  assert.deepEqual(new Set(revoked),new Set(created),'every created URL must be released, including late responses')
})

test('file filtering clears a selection that is no longer visible', async () => {
  const Page=loadTs('src/components/FileTable.tsx',{
    '@arco-design/web-react':arco,'@arco-design/web-react/icon':new Proxy({},{get:(_,n)=>component(n)}),
    '../api/client':{get:async()=>({data:{list:[],total:0}})},
    '../store/auth':{useAuth:()=>({hasPerm:()=>true})},'../api/types':{fmtSize:String,fmtTime:String},
    './ChunkUploader':component('Uploader'),'./PdfPreview':component('PDF'),
  }).default
  let renderer
  await act(async()=>{renderer=create(React.createElement(Page,{projectId:1,projectStatus:'IN_PROGRESS',rounds:[]}))})
  await act(async()=>renderer.root.findByType('Table').props.rowSelection.onChange([23]))
  await act(async()=>renderer.root.findByType('Input.Search').props.onSearch('different-file'))
  assert.equal(renderer.root.findByType('Table').props.rowSelection.selectedRowKeys.length,0,'hidden selected files must not remain in the download batch')
  await act(async()=>renderer.unmount())
})

test('invalid project route identifiers render a recoverable error without API calls', async () => {
  for(const id of ['not-a-number','0','-1','1.5','9007199254740993']) {
    let calls=0
    const Page=loadTs('src/pages/project/ProjectDetail.tsx',{
      '@arco-design/web-react':arco,
      'react-router-dom':{useParams:()=>({id}),useSearchParams:()=>[new URLSearchParams(),()=>{}],useNavigate:()=>()=>{}},
      '../../api/client':{get:async()=>{calls++;throw Error('invalid request')}},
      '../../api/types':{PROJECT_STATUS:{},fmtTime:String},
      '../../components/RoundPanel':component('Rounds'),'../../components/FileTable':component('Files'),
      '../../components/MessagePanel':component('Messages'),'../../components/MemberPanel':component('Members'),
    }).default
    let renderer
    await act(async()=>{renderer=create(React.createElement(Page))})
    assert.equal(calls,0,'invalid route must be rejected before loading')
    assert.equal(renderer.root.findByType('Empty').props.description,'项目地址无效')
    assert.ok(renderer.root.findAllByType('Button').some(n=>n.props.children==='返回项目列表'))
    await act(async()=>renderer.unmount())
  }
})

test('project navigation ignores late responses across valid and invalid routes', async () => {
  let id='1'
  const pending=new Map()
  const Page=loadTs('src/pages/project/ProjectDetail.tsx',{
    '@arco-design/web-react':arco,
    'react-router-dom':{useParams:()=>({id}),useSearchParams:()=>[new URLSearchParams(),()=>{}],useNavigate:()=>()=>{}},
    '../../api/client':{get:url=>new Promise(resolve=>pending.set(url,resolve))},
    '../../api/types':{PROJECT_STATUS:{IN_PROGRESS:{text:'进行中'}},fmtTime:String},
    '../../components/RoundPanel':component('Rounds'),'../../components/FileTable':component('Files'),
    '../../components/MessagePanel':component('Messages'),'../../components/MemberPanel':component('Members'),
  }).default
  let renderer
  await act(async()=>{renderer=create(React.createElement(Page))})
  id='invalid'
  await act(async()=>renderer.update(React.createElement(Page)))
  assert.equal(renderer.root.findByType('Empty').props.description,'项目地址无效')
  id='2'
  await act(async()=>renderer.update(React.createElement(Page)))
  const project=n=>({id:n,name:`项目${n}`,status:'IN_PROGRESS'})
  await act(async()=>pending.get('/projects/2')({data:project(2)}))
  await act(async()=>pending.get('/projects/1')({data:project(1)}))
  assert.equal(renderer.root.findByType('Rounds').props.projectId,2)
  await act(async()=>renderer.root.findByType('Rounds').props.onChanged())
  id='3'
  await act(async()=>renderer.update(React.createElement(Page)))
  await act(async()=>pending.get('/projects/3')({data:project(3)}))
  await act(async()=>pending.get('/projects/2')({data:project(2)}))
  assert.equal(renderer.root.findByType('Rounds').props.projectId,3,'a late refresh after a round operation must not replace the new project')
  await act(async()=>renderer.unmount())
})

test('two tabs can restore a shared rotating refresh cookie', async () => {
  let queue = Promise.resolve()
  const locks = { request: (_name, callback) => {
    const result = queue.then(callback)
    queue = result.catch(() => {})
    return result
  } }
  let cookie = 0
  const consumed = new Set()
  const axios = {
    create: () => ({ interceptors: { request: { use() {} }, response: { use() {} } } }),
    post: async () => {
      const sent = cookie
      await Promise.resolve()
      if (consumed.has(sent)) throw new Error('refresh cookie was already rotated')
      consumed.add(sent)
      cookie++
      return { data: { accessToken: `test-${cookie}` } }
    },
    get: async () => ({ data: { user: { id: 1 }, permissions: [], menus: [], mustChangePassword: false } }),
  }
  function tab() {
    const state = {
      user: { id: 1 }, token: null, booted: false,
      setLogin(p) { state.user = p.user; state.token = p.accessToken },
      logout() { state.user = null; state.token = null },
      setBooted(value) { state.booted = value },
    }
    const mod = loadTs('src/api/client.ts', {
      axios, '@arco-design/web-react': arco, '../store/auth': { useAuth: { getState: () => state } },
    }, { navigator: { locks }, location: { pathname: '/' } })
    return { state, boot: mod.bootAuth }
  }
  const a = tab(), b = tab()
  await Promise.all([a.boot(), b.boot()])
  assert.ok(a.state.token && b.state.token, 'neither legitimate tab should lose its session')
})

test('an in-flight refresh cannot restore a logged-out session', async () => {
  let release, profileStarted
  const started = new Promise(resolve => { profileStarted = resolve })
  const profile = new Promise(resolve => { release = resolve })
  const state = {
    user: { id: 1 }, token: null, booted: false, generation: 0,
    setLogin(p) { this.user = p.user; this.token = p.accessToken; this.generation++ },
    logout() { this.user = null; this.token = null; this.generation++ },
    setBooted(v) { this.booted = v },
  }
  const axios = {
    create: () => ({ interceptors: { request: { use() {} }, response: { use() {} } } }),
    post: async () => ({ data: { accessToken: 'stale' } }),
    get: async () => { profileStarted(); return profile },
  }
  const mod = loadTs('src/api/client.ts', { axios, '@arco-design/web-react': arco, '../store/auth': { useAuth: { getState: () => state } } }, { location: { pathname: '/' } })
  const boot = mod.bootAuth()
  await started
  state.logout()
  release({ data: { user: { id: 1 }, permissions: [], menus: [] } })
  await boot
  assert.equal(state.user, null, 'late profile must not undo logout')
  assert.equal(state.token, null)
})

test('queued stale writes cannot refresh or replay as a newly logged-in account', async () => {
  let request, failure, releaseLock, refreshCalls=0, replays=0
  const locked=new Promise(resolve=>{releaseLock=resolve})
  const state={user:{id:1},token:'account-a',generation:0,logout(){throw new Error('must not log out account B')},setLogin(p){this.token=p.accessToken;this.user=p.user}}
  const client=()=>{replays++;return Promise.resolve({})}
  client.interceptors={request:{use(fn){request=fn}},response:{use(_ok,fn){failure=fn}}}
  const axios={create:()=>client,post:async()=>{refreshCalls++;return {data:{accessToken:'account-b-refreshed'}}},get:async()=>({data:{user:{id:2}}})}
  loadTs('src/api/client.ts',{axios,'@arco-design/web-react':arco,'../store/auth':{useAuth:{getState:()=>state}}},{navigator:{locks:{request:(_name,fn)=>locked.then(fn)}},location:{pathname:'/'}})
  const config=request({url:'/projects/1/messages',method:'POST',headers:{}})
  const error={config,response:{status:401},message:'expired'}
  const rejected=assert.rejects(failure(error))
  state.generation++;state.user={id:2};state.token='account-b';releaseLock()
  await rejected
  assert.equal(refreshCalls,0,'queued stale request must recheck its session after acquiring the lock')
  assert.equal(replays,0,'old write must never be replayed as another account')
  assert.equal(state.token,'account-b')
})

test('simultaneous 401 responses share one refresh and replay each request once', async () => {
  let request, failure, refreshCalls=0
  const replayed=[]
  const state={
    user:{id:1},token:'old-token',generation:0,
    setLogin(p){this.user=p.user;this.token=p.accessToken},
    logout(){throw new Error('refreshable 401s must not log out')},
  }
  const client=config=>{
    const next=request(config)
    replayed.push({url:next.url, retried:next._retried, authorization:next.headers.Authorization})
    return Promise.resolve({data:{ok:true}})
  }
  client.interceptors={request:{use(fn){request=fn}},response:{use(_ok,fn){failure=fn}}}
  const axios={
    create:()=>client,
    post:async()=>{refreshCalls++;await Promise.resolve();return {data:{accessToken:'new-token'}}},
    get:async()=>({data:{user:{id:1},permissions:[],menus:[],mustChangePassword:false}}),
  }
  loadTs('src/api/client.ts',{axios,'@arco-design/web-react':arco,'../store/auth':{useAuth:{getState:()=>state}}},{location:{pathname:'/'}})
  const configs=Array.from({length:5},(_,i)=>request({url:`/resource-${i}`,method:'GET',headers:{}}))
  await Promise.all(configs.map(config=>failure({config,response:{status:401},message:'expired'})))
  assert.equal(refreshCalls,1,'concurrent 401s must share exactly one refresh request')
  assert.equal(replayed.length,5,'each failed request should replay exactly once')
  assert.deepEqual(replayed.map(x=>x.url).sort(),configs.map(x=>x.url).sort())
  assert.ok(replayed.every(x=>x.retried===true && x.authorization==='Bearer new-token'))
})

for (const profileFirst of [true,false]) test(`permission profile and token refresh preserve one session (profile first: ${profileFirst})`, async () => {
  let request, failure, releaseProfile, releaseRefresh, replayToken
  const profile=new Promise(resolve=>{releaseProfile=resolve}), refresh=new Promise(resolve=>{releaseRefresh=resolve})
  const auth=loadTs('src/store/auth.ts',{'zustand/middleware':{persist:fn=>fn}}).useAuth
  const payload={user:{id:1},accessToken:'old-token',permissions:[],menus:[],mustChangePassword:false}
  auth.getState().setLogin(payload)
  const epoch=auth.getState().generation
  const client=config=>{request(config);replayToken=config.headers.Authorization;return Promise.resolve({})}
  client.interceptors={request:{use(fn){request=fn}},response:{use(_ok,fn){failure=fn}}}
  const axios={create:()=>client,post:async()=>refresh,get:async(_url,cfg)=>cfg.headers.Authorization==='Bearer old-token'?profile:{data:payload}}
  loadTs('src/api/client.ts',{axios,'@arco-design/web-react':arco,'../store/auth':{useAuth:auth}},{location:{pathname:'/'}})
  const denied=assert.rejects(failure({config:request({url:'/projects',headers:{}}),response:{status:403}}))
  const expired=failure({config:request({url:'/projects',headers:{}}),response:{status:401}})
  if(profileFirst){releaseProfile({data:payload});await denied;releaseRefresh({data:{accessToken:'new-token'}});await expired}
  else {releaseRefresh({data:{accessToken:'new-token'}});await expired;releaseProfile({data:payload});await denied}
  assert.equal(auth.getState().generation,epoch,'profile/token updates do not replace a session')
  assert.equal(auth.getState().token,'new-token','late permission profile must not restore the old access token')
  assert.equal(replayToken,'Bearer new-token')
})

for (const deleteCount of [1,20]) test(`deleting ${deleteCount} loaded messages keeps older messages reachable`, async () => {
  let rows = Array.from({length:25},(_,i)=>({id:25-i,senderId:2,senderName:'成员',senderType:'INTERNAL',content:`message ${25-i}`,readByMe:true,readCount:0,totalCount:1}))
  const http = {
    get: async (_url,{params}) => ({ data: { total:rows.length,list:params.beforeId ? rows.filter(r=>r.id<params.beforeId).slice(0,20) : rows.slice((params.page-1)*20,params.page*20) } }),
    delete: async url => { rows=rows.filter(r=>r.id!==Number(url.split('/').pop())) },
  }
  const Page=loadTs('src/components/MessagePanel.tsx',{
    '@arco-design/web-react':arco,
    '@arco-design/web-react/icon':new Proxy({},{get:(_,name)=>component(name)}),
    '../api/client':http,'../store/auth':{useAuth:()=>({hasPerm:()=>true,user:{id:1}})},'../api/types':{fmtTime:String},
  }).default
  let renderer
  await act(async()=>{renderer=create(React.createElement(Page,{projectId:1,projectStatus:'IN_PROGRESS',rounds:[]}))})
  for(let i=0;i<deleteCount;i++) await act(async()=>renderer.root.findAllByType('Popconfirm')[0].props.onOk())
  const more=renderer.root.findAllByType('Button').find(n=>JSON.stringify(n.props.children)?.includes('加载更多'))
  assert.ok(more,'older messages must remain reachable after deleting the loaded batch')
  await act(async()=>more.props.onClick())
  const ids=renderer.root.findAll(n=>n.props['data-message-id']!==undefined).map(n=>n.props['data-message-id'])
  assert.deepEqual(ids,rows.map(r=>r.id),'all remaining messages must be reachable exactly once')
  await act(async()=>renderer.unmount())
})

test('a failed second message page can be retried without losing or duplicating messages', async () => {
  const rows=Array.from({length:41},(_,i)=>({id:41-i,senderId:2,senderName:'成员',senderType:'INTERNAL',content:`message ${41-i}`,readByMe:true,readCount:0,totalCount:1}))
  let appendFailures=1
  const http = {
    get: async (_url,{params}) => {
      if(params.beforeId && appendFailures>0){appendFailures--;throw Error('transient page failure')}
      const list=params.beforeId ? rows.filter(r=>r.id<params.beforeId).slice(0,20) : rows.slice(0,20)
      return { data: { total: rows.length, list } }
    },
  }
  const Page=loadTs('src/components/MessagePanel.tsx',{
    '@arco-design/web-react':arco,
    '@arco-design/web-react/icon':new Proxy({},{get:(_,name)=>component(name)}),
    '../api/client':http,'../store/auth':{useAuth:()=>({hasPerm:()=>false,user:{id:1}})},'../api/types':{fmtTime:String},
  }).default
  let renderer
  await act(async()=>{renderer=create(React.createElement(Page,{projectId:1,projectStatus:'IN_PROGRESS',rounds:[]}))})
  let more=renderer.root.findAllByType('Button').find(n=>JSON.stringify(n.props.children)?.includes('加载更多'))
  let rejected=false
  try { await act(async()=>{await more.props.onClick()}) } catch { rejected=true }
  assert.equal(rejected,true,'the first failed append should surface as a rejected load')
  more=renderer.root.findAllByType('Button').find(n=>JSON.stringify(n.props.children)?.includes('加载更多'))
  await act(async()=>more.props.onClick())
  more=renderer.root.findAllByType('Button').find(n=>JSON.stringify(n.props.children)?.includes('加载更多'))
  await act(async()=>more.props.onClick())
  const ids=renderer.root.findAll(n=>n.props['data-message-id']!==undefined).map(n=>n.props['data-message-id'])
  assert.equal(new Set(ids).size,41)
  assert.deepEqual(ids,rows.map(r=>r.id))
  await act(async()=>renderer.unmount())
})

test('menu-only role grants remain selected when another permission is edited', async () => {
  const role={id:7,code:'TEST',name:'测试',permissionIds:[5],assignedUserCount:0,status:'ACTIVE'}
  const perms=[{id:1,code:'dashboard',name:'工作台',type:'MENU',parentId:null},{id:5,code:'org:dept',name:'部门',type:'MENU',parentId:null},{id:24,code:'dept:manage',name:'管理部门',type:'ACTION',parentId:5}]
  const Page=loadTs('src/pages/rbac/RoleList.tsx',{
    '@arco-design/web-react':arco,'@arco-design/web-react/icon':new Proxy({},{get:(_,n)=>component(n)}),
    '../../api/client':{get:async url=>({data:url==='/permissions'?perms:{list:[role],total:1}})},
  }).default
  let renderer
  await act(async()=>{renderer=create(React.createElement(Page))})
  const actions=renderer.root.findByType('Table').props.columns.at(-1).render(null,role)
  const assign=findActionButton(actions, '分配权限')
  await act(async()=>assign.props.onClick())
  const tree=renderer.root.findByType('Tree')
  assert.ok(tree.props.checkedKeys.includes('5'),'standalone menu must visibly remain granted')
  assert.ok(!tree.props.checkedKeys.includes('24'),'menu alone must not grant its action')
  await act(async()=>tree.props.onCheck([...tree.props.checkedKeys,'1'],{checked:true,node:{key:'1'},halfCheckedKeys:[]}))
  assert.ok(renderer.root.findByType('Tree').props.checkedKeys.includes('5'))
  await act(async()=>renderer.unmount())
})

test('workbook parser version includes published security fixes', () => {
  const version = require('xlsx').version.split('.').map(Number)
  assert.ok(version[0] > 0 || version[1] > 20 || (version[1] === 20 && version[2] >= 2), 'xlsx must be at least 0.20.2')
})

test('audit time filtering preserves an explicitly selected midnight endpoint', async () => {
  let query
  const Page=loadTs('src/pages/system/AuditLog.tsx',{
    '@arco-design/web-react':arco,'@arco-design/web-react/icon':new Proxy({},{get:(_,n)=>component(n)}),
    '../../api/client':{get:async(_url,{params})=>{query=params;return {data:{list:[],total:0,page:1,pageSize:20}}}},'../../api/types':{fmtTime:String},
  }).default
  let renderer
  await act(async()=>{renderer=create(React.createElement(Page))})
  await act(async()=>renderer.root.findByType('DatePicker.RangePicker').props.onChange(['2026-09-04 12:00:00','2026-09-05 00:00:00']))
  await act(async()=>renderer.root.findAllByType('Button').find(n=>n.props.children==='查询').props.onClick())
  assert.equal(query.end,new Date('2026-09-05 00:00:00').toISOString(),'midnight must not silently include the following entire day')
  await act(async()=>renderer.unmount())
})

test('workbook parser retains merged cells and Chinese text', () => {
  const XLSX = require('xlsx')
  const sheet = XLSX.utils.aoa_to_sheet([['中文图纸', ''], [42, '供应商']])
  sheet['!merges'] = [{ s: { r: 0, c: 0 }, e: { r: 0, c: 1 } }]
  const workbook = XLSX.utils.book_new()
  XLSX.utils.book_append_sheet(workbook, sheet, '评审')
  const parsed = XLSX.read(XLSX.write(workbook, { type: 'buffer', bookType: 'xlsx' }), { type: 'buffer', cellStyles: true })
  assert.equal(parsed.Sheets['评审'].A1.v, '中文图纸')
  assert.equal(parsed.Sheets['评审']['!merges'][0].e.c, 1)
})

test('upload identity is based on bytes rather than filename and size', async () => {
  const { fileMd5 } = loadTs('src/api/file-hash.ts', {})
  const a = new Blob(['one!']), b = new Blob(['two!'])
  assert.equal(await fileMd5(new Blob(['test'])), '098f6bcd4621d373cade4e832627b4f6')
  assert.notEqual(await fileMd5(a), await fileMd5(b))
  await assert.rejects(fileMd5(a, () => true), /取消/)
})

test('a failed upload drains in-flight chunks before enabling retry', async () => {
  let release, settled=false, calls=0, merges=0
  const pending=new Promise(resolve=>{release=resolve})
  const Uploader=loadTs('src/components/ChunkUploader.tsx',{
    '@arco-design/web-react':arco,'@arco-design/web-react/icon':new Proxy({},{get:(_,n)=>component(n)}),
    '../api/types':{fmtSize:String},'../api/file-hash':{fileMd5:async()=> 'test-hash'},
    '../api/client':{post:async url=>{if(url.endsWith('/merge'))merges++;return {data:{sessionId:'test-session',chunkSize:1,totalChunks:3,uploadedChunks:[]}}},put:async()=>{if(calls++===0)throw new Error('network failed');await pending}},
  }).default
  let renderer,start
  await act(async()=>{renderer=create(React.createElement(Uploader,{projectId:1,roundId:1,visible:true,onClose(){},onDone(){}}))})
  await act(async()=>renderer.root.findByType('input').props.onChange({target:{files:[{name:'sample.pdf',size:3,slice:()=>new Blob(['x'])}]}}))
  await act(async()=>{start=renderer.root.findByType('Modal').props.footer.props.children[1].props.onClick().then(()=>{settled=true});await new Promise(resolve=>setImmediate(resolve))})
  const retriedEarly=settled
  await act(async()=>{release();await start})
  assert.equal(retriedEarly,false,'one failed worker must not allow retry while sibling chunks are in flight')
  assert.equal(merges,0)
  await act(async()=>renderer.unmount())
})

test('empty Excel sheets retain the selector and can switch to populated sheets', async () => {
  const XLSX = require('xlsx')
  const book = XLSX.utils.book_new()
  XLSX.utils.book_append_sheet(book, {}, '空白')
  XLSX.utils.book_append_sheet(book, XLSX.utils.aoa_to_sheet([['可见内容']]), '内容')
  const Preview = loadTs('src/components/ExcelPreview.tsx', {
    '@arco-design/web-react': arco,
    '../api/client': { get: async () => ({ data: XLSX.write(book, { type: 'buffer', bookType: 'xlsx' }) }) },
  }).default
  let renderer
  await act(async () => { renderer = create(React.createElement(Preview, {fileId:1})) })
  assert.equal(renderer.root.findAllByType('Select').length, 1, 'blank first sheet must not hide navigation')
  await act(async () => renderer.root.findByType('Select').props.onChange('内容'))
  assert.ok(JSON.stringify(renderer.toJSON()).includes('可见内容'))
  await act(async () => renderer.root.findByType('Select').props.onChange('空白'))
  assert.equal(renderer.root.findAllByType('Select').length, 1)
  await act(async () => renderer.unmount())
})

test('closing waits for old chunks to settle before another upload can start', async () => {
  let releaseOld, releaseNew, inits=0, done=0, closed=0
  const oldChunk=new Promise(resolve=>{releaseOld=resolve}), newChunk=new Promise(resolve=>{releaseNew=resolve})
  const merges=[]
  const Uploader=loadTs('src/components/ChunkUploader.tsx',{
    '@arco-design/web-react':arco,'@arco-design/web-react/icon':new Proxy({},{get:(_,n)=>component(n)}),
    '../api/types':{fmtSize:String},'../api/file-hash':{fileMd5:async()=> 'test-hash'},
    '../api/client':{post:async url=>{if(url.endsWith('/merge')){merges.push(url);return {data:{}}}return {data:{sessionId:`session-${++inits}`,chunkSize:1,totalChunks:1,uploadedChunks:[]}}},put:async url=>url.includes('session-1')?oldChunk:newChunk,delete:async()=>({})},
  }).default
  let renderer,first,second,closing
  await act(async()=>{renderer=create(React.createElement(Uploader,{projectId:1,roundId:1,visible:true,onClose(){closed++},onDone(){done++}}))})
  const pick=()=>renderer.root.findByType('input').props.onChange({target:{files:[{name:'sample.pdf',size:1,slice:()=>new Blob(['x'])}]}})
  const start=()=>renderer.root.findByType('Modal').props.footer.props.children[1].props.onClick()
  await act(async()=>pick())
  await act(async()=>{first=start();await new Promise(resolve=>setImmediate(resolve))})
  await act(async()=>{closing=renderer.root.findByType('Modal').props.onCancel()})
  assert.equal(closed,0,'dialog must stay open until old attempt cleanup completes')
  await act(async()=>{releaseOld();await first;await closing})
  await act(async()=>pick())
  await act(async()=>{second=start();await new Promise(resolve=>setImmediate(resolve))})
  const stale={merges:[...merges],done,closed}
  await act(async()=>{releaseNew();await second})
  assert.deepEqual(stale,{merges:[],done:0,closed:1},'late cancelled chunks cannot finish or close the new dialog')
  assert.deepEqual(merges,['/uploads/session-2/merge'])
  assert.equal(done,1)
  await act(async()=>renderer.unmount())
})

test('merging upload cannot be reported cancelled while its file commits', async () => {
  let release,done=0,closed=0,mergeStarted
  const merged=new Promise(resolve=>{release=resolve}), started=new Promise(resolve=>{mergeStarted=resolve})
  const Uploader=loadTs('src/components/ChunkUploader.tsx',{
    '@arco-design/web-react':arco,'@arco-design/web-react/icon':new Proxy({},{get:(_,n)=>component(n)}),
    '../api/types':{fmtSize:String},'../api/file-hash':{fileMd5:async()=> 'test-hash'},
    '../api/client':{post:async url=>{if(url.endsWith('/merge')){mergeStarted();return merged}return {data:{sessionId:'session-1',chunkSize:1,totalChunks:1,uploadedChunks:[0]}}},delete:async()=>{throw new Error('409 merge in progress')}},
  }).default
  let renderer,upload
  await act(async()=>{renderer=create(React.createElement(Uploader,{projectId:1,roundId:1,visible:true,onClose(){closed++},onDone(){done++}}))})
  await act(async()=>renderer.root.findByType('input').props.onChange({target:{files:[{name:'sample.pdf',size:1}]}}))
  await act(async()=>{upload=renderer.root.findByType('Modal').props.footer.props.children[1].props.onClick();await started})
  await act(async()=>{const footer=renderer.root.findByType('Modal').props.footer;if(footer.props.onClick)await footer.props.onClick();renderer.root.findByType('Modal').props.onCancel()})
  await act(async()=>{release({data:{id:1}});await upload})
  assert.equal(done,1,'committed merge must refresh file list, not disappear as cancelled')
  assert.equal(closed,1,'dialog closes exactly once after completion')
  await act(async()=>renderer.unmount())
})

test('late initialization settles and cancels before reopening can reuse its session', async () => {
  let release,closed=0,initStarted,initCount=0
  const late=new Promise(resolve=>{release=resolve}), started=new Promise(resolve=>{initStarted=resolve})
  const events=[]
  const Uploader=loadTs('src/components/ChunkUploader.tsx',{
    '@arco-design/web-react':arco,'@arco-design/web-react/icon':new Proxy({},{get:(_,n)=>component(n)}),
    '../api/types':{fmtSize:String},'../api/file-hash':{fileMd5:async()=> 'test-hash'},
    '../api/client':{post:async url=>{if(url.endsWith('/merge')){events.push('merge');return {data:{id:1}}}events.push('init');if(++initCount===1){initStarted();return late}return {data:{sessionId:'shared-session',chunkSize:1,totalChunks:1,uploadedChunks:[0]}}},delete:async()=>{events.push('delete')}},
  }).default
  let renderer,first,closing
  await act(async()=>{renderer=create(React.createElement(Uploader,{projectId:1,roundId:1,visible:true,onClose(){closed++},onDone(){}}))})
  const pick=()=>renderer.root.findByType('input').props.onChange({target:{files:[{name:'sample.pdf',size:1}]}})
  await act(async()=>pick())
  await act(async()=>{first=renderer.root.findByType('Modal').props.footer.props.children[1].props.onClick();await started})
  await act(async()=>{closing=renderer.root.findByType('Modal').props.onCancel()})
  assert.equal(closed,0,'cannot reopen while init response is pending')
  await act(async()=>{release({data:{sessionId:'shared-session',chunkSize:1,totalChunks:1,uploadedChunks:[]}});await first;await closing})
  await act(async()=>pick())
  await act(async()=>renderer.root.findByType('Modal').props.footer.props.children[1].props.onClick())
  assert.deepEqual(events,['init','delete','init','merge'],'old cleanup must finish before new init')
  await act(async()=>renderer.unmount())
})
