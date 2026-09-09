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
  Typography: { Text: component('Text'), Title: component('Title'), Ellipsis: component('Ellipsis') },
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

function authModule(user, permissions = []) {
  const state = { user, hasPerm: (code) => permissions.includes(code) }
  return { useAuth: (selector) => selector ? selector(state) : state }
}

test('SAA branding is wired to the application logo and favicon', () => {
  const root = path.resolve(__dirname, '..')
  const index = fs.readFileSync(path.join(root, 'index.html'), 'utf8')
  const layout = fs.readFileSync(path.join(root, 'src/layouts/AdminLayout.tsx'), 'utf8')
  const login = fs.readFileSync(path.join(root, 'src/pages/Login.tsx'), 'utf8')

  assert.match(index, /rel="icon"[^>]+href="\/saa-logo\.svg"/)
  assert.match(index, /rel="alternate icon"[^>]+href="\/favicon\.ico"/)
  assert.match(layout, /src="\/saa-logo\.svg"/)
  assert.match(login, /src="\/saa-logo\.svg"/)
  assert.ok(fs.statSync(path.join(root, 'public/saa-logo.svg')).size > 0)
  assert.ok(fs.statSync(path.join(root, 'public/saa-logo.png')).size > 0)
  assert.ok(fs.statSync(path.join(root, 'public/favicon.ico')).size > 0)
})

test('sidebar keeps overflow inside the menu instead of the sider shell', () => {
  const css = fs.readFileSync(path.resolve(__dirname, '..', 'src/index.css'), 'utf8')
  assert.match(css, /\.layout-sider > \.arco-layout-sider-children\s*\{[\s\S]*?overflow: hidden;/)
  assert.match(css, /\.layout-sider \.arco-menu\s*\{[\s\S]*?flex: 1;[\s\S]*?height: auto;/)
  assert.match(css, /\.layout-logo\s*\{[\s\S]*?box-sizing: border-box;/)
})

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

test('paginated list pages constrain table body scrolling to keep pagination visible', async () => {
  const pageData = { list: [], total: 0, page: 1, pageSize: 10 }
  const iconMock = new Proxy({}, { get: (_, name) => component(name) })
  const auth = { useAuth: () => ({ hasPerm: () => true, user: { id: 1, userType: 'INTERNAL' } }) }
  const cases = [
    {
      page: 'src/pages/project/ProjectList.tsx',
      cardClass: 'page-card page-card--table',
      mocks: {
        '@arco-design/web-react': arco,
        '@arco-design/web-react/icon': iconMock,
        'react-router-dom': { useNavigate: () => () => {} },
        '../../api/client': { get: async (url) => ({ data: url === '/supplier-options' ? [] : pageData }) },
        '../../store/auth': auth,
        '../../api/types': { PROJECT_STATUS: {}, fmtTime: String },
      },
    },
    {
      page: 'src/pages/supplier/SupplierList.tsx',
      cardClass: 'page-card page-card--table',
      mocks: {
        '@arco-design/web-react': arco,
        '@arco-design/web-react/icon': iconMock,
        '../../api/client': { get: async () => ({ data: pageData }) },
        '../../store/auth': auth,
        '../../api/types': { fmtTime: String },
      },
    },
    {
      page: 'src/pages/org/UserList.tsx',
      cardClass: 'page-card page-card--table',
      mocks: {
        '@arco-design/web-react': arco,
        '@arco-design/web-react/icon': iconMock,
        '../../api/client': { get: async (url) => ({ data: url === '/departments' || url === '/admin/user-role-options' ? [] : pageData }) },
        '../../store/auth': auth,
        '../../api/types': { fmtTime: String },
      },
    },
    {
      page: 'src/pages/rbac/RoleList.tsx',
      cardClass: 'page-card page-card--table',
      mocks: {
        '@arco-design/web-react': arco,
        '@arco-design/web-react/icon': iconMock,
        '../../api/client': { get: async (url) => ({ data: url === '/permissions' ? [] : { ...pageData, pageSize: 20 } }) },
        '../../store/auth': auth,
        '../../api/types': { PageResp: {} },
      },
    },
    {
      page: 'src/pages/system/AuditLog.tsx',
      cardClass: 'page-card page-card--table audit-page',
      mocks: {
        '@arco-design/web-react': arco,
        '@arco-design/web-react/icon': iconMock,
        '../../api/client': { get: async () => ({ data: { ...pageData, pageSize: 20 } }) },
        '../../store/auth': auth,
        '../../api/types': { fmtTime: String },
      },
    },
  ]

  for (const item of cases) {
    const Page = loadTs(item.page, item.mocks).default
    let renderer
    await act(async () => { renderer = create(React.createElement(Page)) })
    assert.ok(renderer.root.findByProps({ className: item.cardClass }))
    const table = renderer.root.findAllByType('Table')[0]
    assert.equal(table.props.className, 'page-table', item.page)
    assert.equal(table.props.scroll.y, 'var(--page-table-scroll-y)', item.page)
    await act(async () => renderer.unmount())
  }
})

test('organization structure uses division, department and section levels', async () => {
  const layout = fs.readFileSync(path.resolve(__dirname, '..', 'src/layouts/AdminLayout.tsx'), 'utf8')
  assert.match(layout, /label: '组织架构'/)
  const departments = [{
    id: 1,
    name: '事业一部',
    kind: 'DIVISION',
    sortNo: 1,
    status: 'ACTIVE',
    children: [{
      id: 2,
      name: '研发部',
      kind: 'DEPARTMENT',
      parentId: 1,
      sortNo: 3,
      status: 'DISABLED',
      children: [{ id: 3, name: '开发课', kind: 'SECTION', parentId: 2, sortNo: 1, status: 'ACTIVE' }],
    }],
  }]
  const Page = loadTs('src/pages/org/DeptManage.tsx', {
    '@arco-design/web-react': arco,
    '@arco-design/web-react/icon': new Proxy({}, { get: (_, name) => component(name) }),
    '../../api/client': { get: async () => ({ data: departments }) },
    '../../store/auth': authModule({ id: 1, userType: 'INTERNAL', isSystemAdmin: true }, ['dept:manage']),
  }).default
  let renderer
  await act(async () => { renderer = create(React.createElement(Page)) })
  assert.ok(renderer.root.findAll((node) => node.props.children === '组织架构').length > 0)
  assert.ok(renderer.root.findAll((node) => node.props.children === '新增事业部').length > 0)
  const tree = renderer.root.findByType('Tree')
  assert.equal(tree.props.blockNode, true)
  assert.equal(tree.props.showLine, true)
  await act(async () => renderer.root.findByType('Tree').props.onSelect(['1']))
  assert.ok(renderer.root.findAll((node) => node.props.children === '新增部门').length > 0)
  await act(async () => renderer.root.findByType('Tree').props.onSelect(['2']))
  assert.ok(renderer.root.findAll((node) => node.props.children === '研发部').length > 0)
  assert.ok(renderer.root.findAll((node) => node.props.children === '层级路径').length > 0)
  assert.ok(renderer.root.findAll((node) => node.props.children === '编辑部门').length > 0)
  assert.ok(renderer.root.findAll((node) => node.props.children === '新增课别').length > 0)
  assert.equal(renderer.root.findAll((node) => node.props.children === '新增子部门').length, 0)
  await act(async () => renderer.root.findByType('Tree').props.onSelect(['3']))
  assert.ok(renderer.root.findAll((node) => node.props.children === '编辑课别').length > 0)
  assert.equal(renderer.root.findAll((node) => node.props.children === '新增课别').length, 0)
  assert.ok(renderer.root.findAll((node) => node.props.children === '事业一部 > 研发部 > 开发课').length > 0)
  assert.ok(renderer.root.findAll((node) => node.props.children === '删除课别').length > 0)
  await act(async () => renderer.unmount())
})

test('hard delete actions require both system-admin identity and the page management permission', async () => {
  const iconMock = new Proxy({}, { get: (_, name) => component(name) })
  const pageData = { list: [], total: 0, page: 1, pageSize: 10 }
  const cases = [
    {
      page: 'src/pages/project/ProjectList.tsx',
      permission: 'project:update',
      row: { id: 1, name: 'p', status: 'DRAFT' },
      mocks: (auth) => ({
        '@arco-design/web-react': arco,
        '@arco-design/web-react/icon': iconMock,
        'react-router-dom': { useNavigate: () => () => {} },
        '../../api/client': { get: async (url) => ({ data: url === '/supplier-options' ? [] : pageData }) },
        '../../store/auth': auth,
        '../../api/types': { PROJECT_STATUS: {}, fmtTime: String },
      }),
    },
    {
      page: 'src/pages/supplier/SupplierList.tsx',
      permission: 'supplier:manage',
      row: { id: 8, name: 's', status: 'ACTIVE' },
      mocks: (auth) => ({
        '@arco-design/web-react': arco,
        '@arco-design/web-react/icon': iconMock,
        '../../api/client': { get: async () => ({ data: pageData }) },
        '../../store/auth': auth,
        '../../api/types': { fmtTime: String },
      }),
    },
    {
      page: 'src/pages/org/UserList.tsx',
      permission: 'user:manage',
      row: { id: 2, employeeNo: 'staff', status: 'ACTIVE' },
      mocks: (auth) => ({
        '@arco-design/web-react': arco,
        '@arco-design/web-react/icon': iconMock,
        '../../api/client': { get: async (url) => ({ data: url === '/departments' || url === '/admin/user-role-options' ? [] : pageData }) },
        '../../store/auth': auth,
        '../../api/types': { fmtTime: String },
      }),
    },
    {
      page: 'src/pages/rbac/RoleList.tsx',
      permission: 'role:manage',
      row: { id: 9, name: '自定义', isBuiltIn: false, permissionIds: [], assignedUserCount: 0, status: 'ACTIVE' },
      mocks: (auth) => ({
        '@arco-design/web-react': arco,
        '@arco-design/web-react/icon': iconMock,
        '../../api/client': { get: async (url) => ({ data: url === '/permissions' ? [] : { list: [], total: 0, page: 1, pageSize: 20 } }) },
        '../../store/auth': auth,
        '../../api/types': { PageResp: {} },
      }),
    },
  ]
  for (const item of cases) {
    for (const [label, user, permissions, expected] of [
      ['ordinary manager', { id: 1, userType: 'INTERNAL', isSystemAdmin: false }, [item.permission], false],
      ['admin without page permission', { id: 1, userType: 'INTERNAL', isSystemAdmin: true }, [], false],
      ['system admin manager', { id: 1, userType: 'INTERNAL', isSystemAdmin: true }, [item.permission], true],
    ]) {
      const Page = loadTs(item.page, item.mocks(authModule(user, permissions))).default
      let renderer
      await act(async () => { renderer = create(React.createElement(Page)) })
      const actions = renderer.root.findAllByType('Table')[0].props.columns.at(-1).render(null, item.row)
      assert.equal(!!findActionButton(actions, '删除'), expected, `${item.page}: ${label}`)
      await act(async () => renderer.unmount())
    }
  }

  const Project = loadTs('src/pages/project/ProjectList.tsx', cases[0].mocks(authModule(
    { id: 1, userType: 'INTERNAL', isSystemAdmin: true },
    ['project:update'],
  ))).default
  let renderer
  await act(async () => { renderer = create(React.createElement(Project)) })
  const busy = renderer.root.findAllByType('Table')[0].props.columns.at(-1).render(null, { id: 2, name: 'busy', status: 'IN_PROGRESS' })
  assert.equal(findActionButton(busy, '删除'), undefined, 'in-progress projects must not offer delete')
  await act(async () => renderer.unmount())
})

test('department hard delete is hidden from ordinary department managers', async () => {
  const departments = [{ id: 3, name: '开发课', kind: 'SECTION', parentId: 2, sortNo: 1, status: 'ACTIVE' }]
  for (const [isSystemAdmin, expected] of [[false, false], [true, true]]) {
    const Page = loadTs('src/pages/org/DeptManage.tsx', {
      '@arco-design/web-react': arco,
      '@arco-design/web-react/icon': new Proxy({}, { get: (_, name) => component(name) }),
      '../../api/client': { get: async () => ({ data: departments }) },
      '../../store/auth': authModule({ id: 1, userType: 'INTERNAL', isSystemAdmin }, ['dept:manage']),
    }).default
    let renderer
    await act(async () => { renderer = create(React.createElement(Page)) })
    await act(async () => renderer.root.findByType('Tree').props.onSelect(['3']))
    assert.equal(renderer.root.findAll((node) => node.props.children === '删除课别').length > 0, expected)
    await act(async () => renderer.unmount())
  }
})

function loadTs(relativePath, mocks, globals = {}) {
  const filename = path.resolve(__dirname, '..', relativePath)
  const source = ts.transpileModule(fs.readFileSync(filename, 'utf8'), {
    compilerOptions: { module: ts.ModuleKind.CommonJS, jsx: ts.JsxEmit.ReactJSX, target: ts.ScriptTarget.ES2022, esModuleInterop: true },
  }).outputText
  const exports = {}
  vm.runInNewContext(source, {
    exports, module: { exports }, console, setTimeout, clearTimeout, URL, URLSearchParams, AbortController, ...globals,
    require: (name) => {
      if (typeof name === 'string' && name in mocks) return mocks[name]
      if (typeof name === 'string' && name.replace(/\\/g, '/').endsWith('/ActionSlots')) return actionSlotsModule
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
  const supplier={id:8,name:'fixture',status:'ACTIVE'}
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

test('round history requests ignore late responses and closing invalidates them', async () => {
  const rounds = [
    { id: 1, roundNo: 1, status: 'CONFIRMED', confirmSide: 'COMPANY', createdBy: 1, createdAt: '' },
    { id: 2, roundNo: 2, status: 'CONFIRMED', confirmSide: 'COMPANY', createdBy: 1, createdAt: '' },
  ]
  const pending = new Map()
  const http = {
    get: async (url) => {
      if (url === '/projects/1/rounds') return { data: rounds }
      const id = Number(url.match(/rounds\/(\d+)$/)[1])
      return new Promise((resolve) => {
        const queue = pending.get(id) || []
        queue.push(resolve)
        pending.set(id, queue)
      })
    },
  }
  const timeline = Object.assign(component('Timeline'), { Item: component('Timeline.Item') })
  const roundArco = new Proxy({}, { get: (_, key) => key === 'Timeline' ? timeline : arco[key] })
  const Page = loadTs('src/components/RoundPanel.tsx', {
    '@arco-design/web-react': roundArco,
    '@arco-design/web-react/icon': new Proxy({}, { get: (_, n) => component(n) }),
    '../api/client': http,
    '../store/auth': authModule({ id: 1, userType: 'INTERNAL' }),
    '../api/types': { ROUND_STATUS: {}, fmtTime: String },
  }).default
  const historyData = (round) => ({ ...round, logs: [{ id: round.id, toStatus: 'CONFIRMED', operatorName: `操作人${round.id}`, createdAt: '' }] })
  const release = async (id, round) => {
    const queue = pending.get(id)
    assert.ok(queue && queue.length > 0, `history request ${id} must be pending`)
    queue.shift()({ data: historyData(round) })
  }
  let renderer
  await act(async () => { renderer = create(React.createElement(Page, { projectId: 1, projectStatus: 'IN_PROGRESS', onChanged() {} })) })
  const historyButton = (round) => {
    const actions = renderer.root.findByType('Table').props.columns.at(-1).render(null, round)
    return findElement(actions, (node) => node.props.title === '历史')
  }

  let firstRequest
  let secondRequest
  await act(async () => {
    firstRequest = historyButton(rounds[0]).props.onClick()
    secondRequest = historyButton(rounds[1]).props.onClick()
    await Promise.resolve()
  })
  await act(async () => { await release(2, rounds[1]); await secondRequest })
  await act(async () => { await release(1, rounds[0]); await firstRequest })
  let drawer = renderer.root.findByType('Drawer')
  assert.equal(drawer.props.visible, true)
  assert.match(drawer.props.title, /第 2 轮/)
  assert.equal(drawer.findByType('Timeline').props.children[0].props.children[0].props.children[1].props.children, '操作人2')

  let lateRequest
  await act(async () => {
    lateRequest = historyButton(rounds[0]).props.onClick()
    await Promise.resolve()
  })
  drawer = renderer.root.findByType('Drawer')
  await act(async () => drawer.props.onCancel())
  await act(async () => { await release(1, rounds[0]); await lateRequest })
  assert.equal(renderer.root.findByType('Drawer').props.visible, false, 'closing must invalidate a late history response')

  let unmountedRequest
  await act(async () => {
    unmountedRequest = historyButton(rounds[1]).props.onClick()
    await Promise.resolve()
  })
  await act(async () => renderer.unmount())
  await act(async () => { await release(2, rounds[1]); await unmountedRequest })
})

test('member picker waits for a successful member load and guards loading/error handlers', async () => {
  const memberRequests = []
  let optionCalls = 0
  let releaseOptions
  let saved
  const existing = { userId: 42, employeeNo: 'E42', realName: '已有成员', deptName: '研发部', status: 'ACTIVE', createdAt: '' }
  const disabled = { userId: 99, employeeNo: 'E99', realName: '停用成员', deptName: '旧部门', status: 'DISABLED', createdAt: '' }
  const options = [{ id: 7, employeeNo: 'E7', realName: '当前用户', deptName: '管理部' }]
  const supplierMembers = [{ userId: 88, employeeNo: 'S88', realName: '供应商成员', status: 'ACTIVE' }]
  const http = {
    get: async (url) => {
      if (url === '/projects/1/members') {
        return new Promise((resolve, reject) => memberRequests.push({ resolve, reject }))
      }
      if (url === '/projects/1/supplier-members') return { data: supplierMembers }
      if (url === '/internal-user-options') {
        optionCalls++
        return new Promise((resolve) => { releaseOptions = resolve })
      }
      throw new Error(`unexpected GET ${url}`)
    },
    put: async (_url, body) => { saved = body }
  }
  const Page = loadTs('src/components/MemberPanel.tsx', {
    '@arco-design/web-react': arco,
    '@arco-design/web-react/icon': new Proxy({}, { get: (_, n) => component(n) }),
    '../api/client': http,
    '../store/auth': authModule({ id: 7, userType: 'INTERNAL' }, ['project:member']),
    '../api/types': { fmtTime: String },
  }).default
  let renderer
  await act(async () => { renderer = create(React.createElement(Page, { projectId: 1, supplierName: '供应商' })) })
  assert.equal(memberRequests.length, 1)
  let picker = renderer.root.findAllByType('Button').find((node) => node.props.children === '设置公司成员')
  assert.equal(picker.props.disabled, true, 'picker must stay disabled before members load')
  await act(async () => { await picker.props.onClick() })
  assert.equal(optionCalls, 0, 'the handler must reject calls while the member list is loading')

  await act(async () => { memberRequests.shift().reject(new Error('members unavailable')); await Promise.resolve() })
  picker = renderer.root.findAllByType('Button').find((node) => node.props.children === '设置公司成员')
  assert.equal(picker.props.disabled, true, 'picker must stay disabled after a member load error')
  await act(async () => { await picker.props.onClick() })
  assert.equal(optionCalls, 0, 'the handler must reject calls while the member list is in an error state')

  const retry = renderer.root.findAllByType('Button').find((node) => node.props.children === '重试')
  await act(async () => { retry.props.onClick(); await Promise.resolve() })
  assert.equal(memberRequests.length, 1)
  await act(async () => { memberRequests.shift().resolve({ data: [existing, disabled] }); await Promise.resolve() })
  picker = renderer.root.findAllByType('Button').find((node) => node.props.children === '设置公司成员')
  assert.equal(picker.props.disabled, false, 'picker should enable after a successful member load')
  const headings = renderer.root.findAllByType('h3').map((node) => node.props.children)
  assert.deepEqual(headings.map((children) => Array.isArray(children) ? children.join('') : String(children)), ['公司成员 2', '供应商成员 1'])
  assert.equal(renderer.root.findAllByType('Text').some((node) => node.props.children === '关联供应商的启用账号自动参与'), true)
  let openRequest
  let duplicateOpen
  await act(async () => {
    openRequest = picker.props.onClick()
    duplicateOpen = picker.props.onClick()
    await Promise.resolve()
  })
  assert.equal(optionCalls, 1, 'double clicking the picker must issue one options request')
  await act(async () => renderer.root.findByType('Modal').props.onCancel())
  await act(async () => { releaseOptions({ data: options }); await openRequest; await duplicateOpen })
  assert.equal(renderer.root.findByType('Modal').props.visible, false, 'closing must invalidate a late options response')

  picker = renderer.root.findAllByType('Button').find((node) => node.props.children === '设置公司成员')
  await act(async () => { openRequest = picker.props.onClick(); await Promise.resolve() })
  assert.equal(optionCalls, 2)
  await act(async () => { releaseOptions({ data: options }); await openRequest })
  const modal = renderer.root.findByType('Modal')
  assert.equal(modal.props.visible, true)
  assert.equal(modal.props.className, 'form-dialog member-picker-dialog')
  assert.equal(renderer.root.findByType('Input.Search').props.placeholder, '搜索姓名、工号或部门')
  let checkboxes = renderer.root.findAllByType('Checkbox')
  assert.equal(checkboxes.length, 3, 'current members must remain visible in the fixed picker list')
  assert.equal(checkboxes.filter((checkbox) => checkbox.props.checked).length, 2)
  const disabledCheckbox = checkboxes.find((checkbox) => findElement(checkbox.props.children, (node) => node.props.children === '停用成员'))
  assert.ok(disabledCheckbox)
  assert.equal(disabledCheckbox.props.disabled, false, 'disabled members require an explicit user deselection')
  await act(async () => renderer.root.findByType('Input.Search').props.onChange('E99'))
  assert.equal(renderer.root.findAllByType('Checkbox').length, 1, 'search must match employee number')
  await act(async () => renderer.root.findByType('Input.Search').props.onChange(''))
  checkboxes = renderer.root.findAllByType('Checkbox')
  await act(async () => renderer.root.findByType('Modal').props.onOk())
  assert.equal(saved, undefined, 'saving with a disabled member must require explicit deselection')
  const currentDisabledCheckbox = renderer.root.findAllByType('Checkbox').find((checkbox) => findElement(checkbox.props.children, (node) => node.props.children === '停用成员'))
  await act(async () => currentDisabledCheckbox.props.onChange(false))
  await act(async () => renderer.root.findByType('Modal').props.onOk())
  assert.deepEqual(Array.from(saved.userIds), [42, 7], 'saving must preserve the loaded members and the operator')
  await act(async () => renderer.unmount())
})

test('supplier account loading failures stop spinning and can retry', async () => {
  let fail=true
  const supplier={id:8,name:'fixture',status:'ACTIVE'}
  const Page=loadTs('src/pages/supplier/SupplierList.tsx',{
    '@arco-design/web-react':arco,'@arco-design/web-react/icon':new Proxy({},{get:(_,n)=>component(n)}),
    '../../store/auth':{useAuth:()=>({hasPerm:()=>true})},'../../api/types':{fmtTime:String},
    '../../api/client':{get:async url=>{
      if(url.endsWith('/accounts')){if(fail)throw Error('simulated failure');return {data:[{id:16,employeeNo:'fixture-account'}]}}
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

test('disabled supplier account drawer disables new account creation and rejects stale submit', async () => {
  let createCalls = 0
  const supplier = { id: 8, name: 'FULL_PAGE_SUP00', status: 'DISABLED', createdAt: '' }
  const form = {
    validate: async () => ({ employeeNo: 'new-account', password: 'secret1', realName: '新账号', email: 'new@example.invalid' }),
    resetFields() {},
    setFieldsValue() {},
  }
  const testArco = new Proxy({
    Form: Object.assign(component('Form'), { useForm: () => [form], Item: component('Form.Item') }),
    Typography: arco.Typography,
    Message: arco.Message,
  }, { get: (obj, key) => obj[key] ?? component(key) })
  const Page = loadTs('src/pages/supplier/SupplierList.tsx', {
    '@arco-design/web-react': testArco,
    '@arco-design/web-react/icon': new Proxy({}, { get: (_, n) => component(n) }),
    '../../store/auth': { useAuth: () => ({ hasPerm: () => true, user: { id: 1, userType: 'INTERNAL', isSystemAdmin: true } }) },
    '../../api/types': { fmtTime: String },
    '../../api/client': {
      get: async (url) => url.endsWith('/accounts') ? { data: [] } : { data: { list: [supplier], total: 1, page: 1, pageSize: 10 } },
      post: async (url) => { if (url.endsWith('/accounts')) createCalls++ ; return { data: {} } },
    },
  }).default
  let renderer
  await act(async () => { renderer = create(React.createElement(Page)) })
  const actions = renderer.root.findAllByType('Table')[0].props.columns.at(-1).render(null, supplier)
  await act(async () => findActionButton(actions, '账号管理').props.onClick())
  const add = renderer.root.findAllByType('Button').find((node) => node.props.children === '新增账号')
  assert.ok(add, 'the account entry remains discoverable so the disabled state is explicit')
  assert.equal(add.props.disabled, true)
  // Even if a stale UI event reaches the handler, the submit path must enforce the same contract.
  await act(async () => add.props.onClick())
  const modal = renderer.root.findAllByType('Modal').find((node) => node.props.visible)
  await act(async () => modal.props.onOk())
  assert.equal(createCalls, 0)
  await act(async () => renderer.unmount())
})

test('paginated lists and file table expose a retry state after the main GET fails', async () => {
  const iconMock = new Proxy({}, { get: (_, name) => component(name) })
  const auth = authModule({ id: 1, userType: 'INTERNAL', isSystemAdmin: true }, [
    'project:update', 'supplier:account', 'user:manage', 'role:manage', 'file:download',
  ])
  const cases = [
    {
      file: 'src/pages/project/ProjectList.tsx', api: '/projects', props: {},
      mocks: (http) => ({
        '@arco-design/web-react': arco, '@arco-design/web-react/icon': iconMock,
        'react-router-dom': { useNavigate: () => () => {} }, '../../api/client': http,
        '../../store/auth': auth, '../../api/types': { PROJECT_STATUS: {}, fmtTime: String },
      }),
    },
    {
      file: 'src/pages/supplier/SupplierList.tsx', api: '/admin/suppliers', props: {},
      mocks: (http) => ({
        '@arco-design/web-react': arco, '@arco-design/web-react/icon': iconMock,
        '../../api/client': http, '../../store/auth': auth, '../../api/types': { fmtTime: String },
      }),
    },
    {
      file: 'src/pages/org/UserList.tsx', api: '/admin/users', props: {},
      mocks: (http) => ({
        '@arco-design/web-react': arco, '@arco-design/web-react/icon': iconMock,
        '../../api/client': http, '../../store/auth': auth, '../../api/types': { fmtTime: String },
      }),
    },
    {
      file: 'src/pages/rbac/RoleList.tsx', api: '/admin/roles', props: {},
      mocks: (http) => ({
        '@arco-design/web-react': arco, '@arco-design/web-react/icon': iconMock,
        '../../api/client': http, '../../store/auth': auth, '../../api/types': { PageResp: {} },
      }),
    },
    {
      file: 'src/pages/system/AuditLog.tsx', api: '/admin/audit-logs', props: {},
      mocks: (http) => ({
        '@arco-design/web-react': arco, '@arco-design/web-react/icon': iconMock,
        '../../api/client': http, '../../store/auth': auth, '../../api/types': { fmtTime: String },
      }),
    },
    {
      file: 'src/components/FileTable.tsx', api: '/projects/1/files', props: { projectId: 1, projectStatus: 'IN_PROGRESS', rounds: [] },
      mocks: (http) => ({
        '@arco-design/web-react': arco, '@arco-design/web-react/icon': iconMock,
        '../api/client': http, '../store/auth': auth, '../api/types': { fmtTime: String, fmtSize: String },
        './ChunkUploader': component('Uploader'), './PdfPreview': component('PDF'),
      }),
    },
  ]

  for (const item of cases) {
    let fail = true
    const http = {
      get: async (url) => {
        if (url === item.api) {
          if (fail) throw new Error('simulated list failure')
          return { data: { list: [{ id: 1 }], total: 1, page: 1, pageSize: 10 } }
        }
        return { data: url === '/permissions' || url === '/departments' || url === '/admin/user-role-options' || url === '/supplier-options' ? [] : { list: [], total: 0 } }
      },
    }
    const Page = loadTs(item.file, item.mocks(http)).default
    let renderer
    await act(async () => { renderer = create(React.createElement(Page, item.props)) })
    assert.ok(renderer.root.findAll((node) => node.props && node.props.children === '加载失败').length > 0, item.file)
    const retry = renderer.root.findAllByType('Button').find((node) => node.props.children === '重试')
    assert.ok(retry, `${item.file} must offer retry`)
    fail = false
    await act(async () => retry.props.onClick())
    assert.equal(renderer.root.findAll((node) => node.props && node.props.children === '加载失败').length, 0, item.file)
    await act(async () => renderer.unmount())
  }
})

test('list actions cannot let an old refresh overwrite a newer filter result', async () => {
  const iconMock = new Proxy({}, { get: (_, name) => component(name) })
  const auth = authModule({ id: 1, userType: 'INTERNAL', isSystemAdmin: true }, [
    'project:status', 'project:update', 'supplier:account', 'supplier:manage', 'user:manage', 'role:manage',
  ])
  const cases = [
    {
      file: 'src/pages/project/ProjectList.tsx', api: '/projects',
      row: { id: 1, name: '旧项目', status: 'DRAFT', supplierId: 1 },
      action: (actions) => findElement(actions, (node) => node.props.placeholder === '状态' && typeof node.props.onChange === 'function'),
      invoke: (node) => node.props.onChange('IN_PROGRESS'),
      filter: (renderer) => renderer.root.findByType('Input.Search').props.onSearch('new-filter'),
      matches: (request) => request.params.keyword === 'new-filter',
      mocks: (http) => ({
        '@arco-design/web-react': arco, '@arco-design/web-react/icon': iconMock,
        'react-router-dom': { useNavigate: () => () => {} }, '../../api/client': http,
        '../../store/auth': auth, '../../api/types': { PROJECT_STATUS: {}, fmtTime: String },
      }),
    },
    {
      file: 'src/pages/supplier/SupplierList.tsx', api: '/admin/suppliers',
      row: { id: 1, name: '旧供应商', status: 'ACTIVE', createdAt: '' },
      action: (actions) => findElement(actions, (node) => node.props.title === '禁用后其所有账号无法登录，确认？'),
      invoke: (node) => node.props.onOk(),
      filter: (renderer) => renderer.root.findByType('Input.Search').props.onSearch('new-filter'),
      matches: (request) => request.params.keyword === 'new-filter',
      mocks: (http) => ({
        '@arco-design/web-react': arco, '@arco-design/web-react/icon': iconMock,
        '../../api/client': http, '../../store/auth': auth, '../../api/types': { fmtTime: String },
      }),
    },
    {
      file: 'src/pages/org/UserList.tsx', api: '/admin/users',
      row: { id: 1, employeeNo: 'u1', realName: '旧用户', email: '', status: 'ACTIVE', createdAt: '' },
      action: (actions) => findElement(actions, (node) => node.props.title === '禁用后立即无法登录，确认？'),
      invoke: (node) => node.props.onOk(),
      filter: (renderer) => renderer.root.findByType('Input.Search').props.onSearch('new-filter'),
      matches: (request) => request.params.keyword === 'new-filter',
      mocks: (http) => ({
        '@arco-design/web-react': arco, '@arco-design/web-react/icon': iconMock,
        '../../api/client': http, '../../store/auth': auth, '../../api/types': { fmtTime: String },
      }),
    },
    {
      file: 'src/pages/rbac/RoleList.tsx', api: '/admin/roles',
      row: { id: 1, name: '旧角色', status: 'ACTIVE', isBuiltIn: false, permissionsLocked: false, permissionIds: [], assignedUserCount: 0 },
      action: (actions) => findElement(actions, (node) => node.props.title === '确认禁用该角色？'),
      invoke: (node) => node.props.onOk(),
      filter: (renderer) => renderer.root.findAllByType('Table').find((node) => String(node.props.className || '').includes('page-table')).props.pagination.onChange(2, 20),
      matches: (request) => request.params.page === 2,
      mocks: (http) => ({
        '@arco-design/web-react': arco, '@arco-design/web-react/icon': iconMock,
        '../../api/client': http, '../../store/auth': auth, '../../api/types': { PageResp: {} },
      }),
    },
  ]

  for (const item of cases) {
    const requests = []
    const http = {
      get: async (url, config = {}) => {
        if (url !== item.api) return { data: [] }
        return new Promise((resolve) => requests.push({ params: config.params || {}, resolve }))
      },
      put: async () => ({}),
    }
    const Page = loadTs(item.file, item.mocks(http)).default
    let renderer
    await act(async () => { renderer = create(React.createElement(Page)) })
    const first = requests.shift()
    await act(async () => first.resolve({ data: { list: [item.row], total: 1, page: 1, pageSize: 10 } }))
    const mainTable = () => renderer.root.findAllByType('Table').find((node) => String(node.props.className || '').includes('page-table')) || renderer.root.findAllByType('Table')[0]
    const actions = mainTable().props.columns.at(-1).render(null, item.row)
    const action = item.action(actions)
    assert.ok(action, `${item.file} must expose a refreshable action`)
    await act(async () => { await item.invoke(action) })
    const stale = requests.shift()
    assert.ok(stale, `${item.file} must start an action refresh`)
    await act(async () => item.filter(renderer))
    const filtered = requests.find(item.matches)
    assert.ok(filtered, `${item.file} must fetch the newer filter`)
    await act(async () => filtered.resolve({ data: { list: [{ ...item.row, id: 2, name: '新筛选结果' }], total: 1, page: 1, pageSize: 10 } }))
    await act(async () => stale.resolve({ data: { list: [item.row], total: 1, page: 1, pageSize: 10 } }))
    assert.equal(mainTable().props.data[0].id, 2, item.file)
    await act(async () => renderer.unmount())
  }
})

test('dashboard and project detail panels expose retry instead of a false empty state', async () => {
  const iconMock = new Proxy({}, { get: (_, name) => component(name) })
  const state = { user: { id: 1, realName: '管理员', userType: 'INTERNAL' }, hasPerm: () => false }
  const auth = { useAuth: (selector) => selector ? selector(state) : state }
  const cases = [
    {
      file: 'src/components/RoundPanel.tsx', api: '/projects/1/rounds', props: { projectId: 1, projectStatus: 'IN_PROGRESS', onChanged() {} },
      data: [{ id: 1, roundNo: 1, status: 'CONFIRMED', confirmSide: 'COMPANY', createdBy: 1, createdAt: '' }],
      mocks: (http) => ({
        '@arco-design/web-react': arco, '@arco-design/web-react/icon': iconMock,
        '../api/client': http, '../store/auth': auth, '../api/types': { ROUND_STATUS: {}, fmtTime: String },
      }),
    },
    {
      file: 'src/components/MemberPanel.tsx', api: '/projects/1/members', props: { projectId: 1, supplierName: '供应商' },
      data: [{ userId: 1, employeeNo: 'A1', realName: '管理员', createdAt: '' }],
      mocks: (http) => ({
        '@arco-design/web-react': arco, '@arco-design/web-react/icon': iconMock,
        '../api/client': http, '../store/auth': auth, '../api/types': { fmtTime: String },
      }),
    },
    {
      file: 'src/components/MessagePanel.tsx', api: '/projects/1/messages', props: { projectId: 1, projectStatus: 'IN_PROGRESS', rounds: [] },
      data: { list: [{ id: 1, senderId: 2, senderName: '成员', senderType: 'INTERNAL', content: '消息', readByMe: true, readCount: 1, totalCount: 1, createdAt: '' }], total: 1 },
      mocks: (http) => ({
        '@arco-design/web-react': arco, '@arco-design/web-react/icon': iconMock,
        '../api/client': http, '../store/auth': auth, '../api/types': { fmtTime: String },
      }),
    },
    {
      file: 'src/pages/org/DeptManage.tsx', api: '/departments', props: {},
      data: [{ id: 1, name: '事业部', kind: 'DIVISION', sortNo: 1, status: 'ACTIVE', children: [] }],
      mocks: (http) => ({
        '@arco-design/web-react': arco, '@arco-design/web-react/icon': iconMock,
        '../../api/client': http, '../../store/auth': auth,
      }),
    },
    {
      file: 'src/pages/system/SysConfig.tsx', api: '/admin/system/configs', props: {}, data: [],
      mocks: (http) => ({
        '@arco-design/web-react': arco, '@arco-design/web-react/icon': iconMock,
        '../../api/client': http, '../../api/types': { fmtTime: String, fmtSize: String },
      }),
    },
    {
      file: 'src/pages/Dashboard.tsx', api: '/dashboard/summary', props: {},
      data: { projectCount: 1, activeProjectCount: 1, pendingRounds: 0, unreadMessages: 0, recentMessages: [] },
      mocks: (http) => ({
        '@arco-design/web-react': new Proxy({
          Grid: Object.assign(component('Grid'), { Row: component('Grid.Row'), Col: component('Grid.Col') }),
          List: Object.assign(component('List'), { Item: Object.assign(component('List.Item'), { Meta: component('List.Item.Meta') }) }),
          Typography: arco.Typography,
          Message: arco.Message,
        }, { get: (obj, key) => obj[key] ?? component(key) }),
        '@arco-design/web-react/icon': iconMock,
        '../api/client': http, '../store/auth': auth, '../api/types': { fmtTime: String },
        'react-router-dom': { useNavigate: () => () => {} },
      }),
    },
  ]

  for (const item of cases) {
    let fail = true
    const http = {
      get: async (url) => {
        if (url === item.api) {
          if (fail) throw new Error('simulated panel failure')
          return { data: item.data }
        }
        if (url.endsWith('/messages')) return { data: { list: [], total: 0 } }
        if (url === '/admin/system/storage') return { data: { totalBytes: 1, availableBytes: 1, usedPercent: 0, warnPercent: 80, warning: false, root: '/' } }
        return { data: [] }
      },
    }
    const Page = loadTs(item.file, item.mocks(http)).default
    let renderer
    await act(async () => { renderer = create(React.createElement(Page, item.props)) })
    assert.ok(renderer.root.findAll((node) => node.props && node.props.children === '加载失败').length > 0, item.file)
    const retry = renderer.root.findAllByType('Button').find((node) => node.props.children === '重试')
    assert.ok(retry, `${item.file} must offer retry`)
    fail = false
    await act(async () => retry.props.onClick())
    assert.equal(renderer.root.findAll((node) => node.props && node.props.children === '加载失败').length, 0, item.file)
    await act(async () => renderer.unmount())
  }
})

test('message receipt requests ignore late responses and closing invalidates them', async () => {
  const pending = new Map()
  const messages = [
    { id: 1, projectId: 1, senderId: 1, senderName: '我', senderType: 'INTERNAL', content: '一', readCount: 0, totalCount: 1, readByMe: true, createdAt: '' },
    { id: 2, projectId: 1, senderId: 1, senderName: '我', senderType: 'INTERNAL', content: '二', readCount: 0, totalCount: 1, readByMe: true, createdAt: '' },
  ]
  const Page = loadTs('src/components/MessagePanel.tsx', {
    '@arco-design/web-react': arco,
    '@arco-design/web-react/icon': new Proxy({}, { get: (_, n) => component(n) }),
    '../store/auth': { useAuth: () => ({ user: { id: 1 }, hasPerm: () => false }) },
    '../api/types': { fmtTime: String },
    '../api/client': {
      get: async (url) => {
        if (url === '/projects/1/messages') return { data: { list: messages, total: messages.length } }
        const id = Number(url.match(/messages\/(\d+)\/reads$/)[1])
        return new Promise((resolve) => pending.set(id, resolve))
      },
    },
  }).default
  let renderer
  await act(async () => { renderer = create(React.createElement(Page, { projectId: 1, projectStatus: 'COMPLETED', rounds: [] })) })
  const receiptButtons = renderer.root.findAllByType('Button').filter((node) => node.props.children === '回执详情')
  assert.equal(receiptButtons.length, 2)
  await act(async () => {
    receiptButtons[0].props.onClick()
    receiptButtons[1].props.onClick()
    await Promise.resolve()
  })
  await act(async () => pending.get(2)({ data: { readers: [{ userId: 2, realName: '读者二', userType: 'INTERNAL' }], unread: [] } }))
  await act(async () => pending.get(1)({ data: { readers: [{ userId: 3, realName: '读者一', userType: 'INTERNAL' }], unread: [] } }))
  let drawer = renderer.root.findByType('Drawer')
  assert.equal(drawer.findAllByType('List')[0].props.dataSource[0].userId, 2)
  let oldRequest
  await act(async () => {
    oldRequest = renderer.root.findAllByType('Button').find((node) => node.props.children === '回执详情').props.onClick()
    await Promise.resolve()
  })
  const releaseOld = pending.get(1)
  drawer = renderer.root.findByType('Drawer')
  await act(async () => drawer.props.onCancel())
  await act(async () => { releaseOld({ data: { readers: [{ userId: 4, realName: '关闭后返回', userType: 'INTERNAL' }], unread: [] } }); await oldRequest })
  assert.equal(renderer.root.findByType('Drawer').props.visible, false)
  await act(async () => renderer.unmount())
})

test('lost upload merge response retries only merge on the same session', async () => {
  let initCalls = 0
  let chunkCalls = 0
  let mergeCalls = 0
  let deleteCalls = 0
  let done = 0
  let closed = 0
  const Uploader = loadTs('src/components/ChunkUploader.tsx', {
    '@arco-design/web-react': arco,
    '@arco-design/web-react/icon': new Proxy({}, { get: (_, n) => component(n) }),
    '../api/types': { fmtSize: String },
    '../api/file-hash': { fileMd5: async () => 'test-hash' },
    '../api/client': {
      post: async (url) => {
        if (url === '/uploads/init') {
          initCalls++
          return { data: { sessionId: 'same-session', chunkSize: 1, totalChunks: 1, uploadedChunks: [] } }
        }
        mergeCalls++
        if (mergeCalls === 1) throw new Error('response lost after commit')
        return { data: { id: 99 } }
      },
      put: async () => { chunkCalls++ },
      delete: async () => { deleteCalls++ },
    },
  }).default
  let renderer
  await act(async () => { renderer = create(React.createElement(Uploader, { projectId: 1, roundId: 1, visible: true, onClose: () => { closed++ }, onDone: () => { done++ } })) })
  await act(async () => renderer.root.findByType('input').props.onChange({ target: { files: [{ name: 'sample.pdf', size: 1, slice: () => new Blob(['x']) }] } }))
  let footer
  await act(async () => {
    footer = renderer.root.findByType('Modal').props.footer
    await footer.props.children[1].props.onClick()
  })
  const retry = findActionButton(renderer.root.findByType('Modal').props.footer, '重试确认')
  assert.ok(retry, 'an uncertain merge must offer an explicit confirmation retry')
  await act(async () => retry.props.onClick())
  assert.equal(initCalls, 1)
  assert.equal(chunkCalls, 1)
  assert.equal(mergeCalls, 2)
  assert.equal(deleteCalls, 0)
  assert.equal(done, 1)
  assert.equal(closed, 1)
  await act(async () => renderer.unmount())
})

test('concurrent merge confirmation clicks issue only one retry request', async () => {
  let mergeCalls = 0
  let initCalls = 0
  let chunkCalls = 0
  let done = 0
  let closed = 0
  let releaseRetry
  const retryMerge = new Promise((resolve) => { releaseRetry = resolve })
  const Uploader = loadTs('src/components/ChunkUploader.tsx', {
    '@arco-design/web-react': arco,
    '@arco-design/web-react/icon': new Proxy({}, { get: (_, n) => component(n) }),
    '../api/types': { fmtSize: String },
    '../api/file-hash': { fileMd5: async () => 'test-hash' },
    '../api/client': {
      post: async (url) => {
        if (url === '/uploads/init') {
          initCalls++
          return { data: { sessionId: 'same-session', chunkSize: 1, totalChunks: 1, uploadedChunks: [] } }
        }
        mergeCalls++
        if (mergeCalls === 1) throw new Error('response lost after commit')
        return retryMerge
      },
      put: async () => { chunkCalls++ },
      delete: async () => { throw new Error('retry must not cancel a committed upload') },
    },
  }).default
  let renderer
  await act(async () => { renderer = create(React.createElement(Uploader, { projectId: 1, roundId: 1, visible: true, onClose: () => { closed++ }, onDone: () => { done++ } })) })
  await act(async () => renderer.root.findByType('input').props.onChange({ target: { files: [{ name: 'sample.pdf', size: 1, slice: () => new Blob(['x']) }] } }))
  await act(async () => {
    const start = renderer.root.findByType('Modal').props.footer.props.children[1].props.onClick()
    await start
  })
  const retry = findActionButton(renderer.root.findByType('Modal').props.footer, '重试确认')
  assert.ok(retry)
  let firstRetry
  let secondRetry
  await act(async () => {
    firstRetry = retry.props.onClick()
    secondRetry = retry.props.onClick()
    await Promise.resolve()
  })
  assert.equal(mergeCalls, 2, 'double clicking confirmation must keep one merge retry in flight')
  releaseRetry({ data: { id: 99 } })
  await act(async () => { await Promise.all([firstRetry, secondRetry]) })
  assert.equal(initCalls, 1)
  assert.equal(chunkCalls, 1)
  assert.equal(done, 1)
  assert.equal(closed, 1)
  await act(async () => renderer.unmount())
})

test('supplier account drawer fits on desktop and keeps columns reachable on narrow screens without phone fields', async () => {
  const css = fs.readFileSync(path.resolve(__dirname, '..', 'src/index.css'), 'utf8')
  assert.doesNotMatch(css, /\.account-drawer[^{}]*\{[^}]*overflow-x:\s*hidden/)

  const supplier = { id: 8, name: 'fixture', status: 'ACTIVE' }
  const Page = loadTs('src/pages/supplier/SupplierList.tsx', {
    '@arco-design/web-react': arco,
    '@arco-design/web-react/icon': new Proxy({}, { get: (_, n) => component(n) }),
    '../../store/auth': { useAuth: () => ({ hasPerm: () => true }) },
    '../../api/types': { fmtTime: String },
    '../../api/client': { get: async (url) => {
      if (url.endsWith('/accounts')) return { data: [] }
      return { data: { list: [supplier], total: 1 } }
    } },
  }).default
  let renderer
  await act(async () => { renderer = create(React.createElement(Page)) })
  const actions = renderer.root.findAllByType('Table')[0].props.columns.at(-1).render(null, supplier)
  await act(async () => findActionButton(actions, '账号管理').props.onClick())
  const drawer = renderer.root.findByType('Drawer')
  assert.equal(drawer.props.className, 'account-drawer')
  const table = drawer.findByType('Table')
  assert.equal(table.props.className, 'account-table')
  assert.equal(table.props.scroll.x, 840)
  const widthSum = table.props.columns.reduce((sum, col) => sum + (col.width || 0), 0)
  assert.ok(widthSum <= table.props.scroll.x, `account column widths ${widthSum} must fit the scrollable table`)
  assert.ok(table.props.scroll.x <= drawer.props.width - 48, 'desktop table must fit inside the drawer padding')
  assert.equal(table.props.columns.some((col) => col.dataIndex === 'phone' || col.title === '电话'), false)
  const phoneField = renderer.root.findAll((node) => node.props && node.props.field === 'phone')
  assert.equal(phoneField.length, 0, 'account form must not collect phone')
  await act(async () => renderer.unmount())

  const userSrc = fs.readFileSync(path.resolve(__dirname, '..', 'src/pages/org/UserList.tsx'), 'utf8')
  assert.doesNotMatch(userSrc, /field="phone"/)
})

test('supplier form and table drop contact address and email fields', async () => {
  const gone = ['contactName', 'contactPhone', 'contactEmail', 'address']
  const supplier = { id: 8, name: 'fixture', status: 'ACTIVE' }
  const Page = loadTs('src/pages/supplier/SupplierList.tsx', {
    '@arco-design/web-react': arco,
    '@arco-design/web-react/icon': new Proxy({}, { get: (_, n) => component(n) }),
    '../../store/auth': { useAuth: () => ({ hasPerm: () => true }) },
    '../../api/types': { fmtTime: String },
    '../../api/client': { get: async (url) => {
      if (url.endsWith('/accounts')) return { data: [] }
      return { data: { list: [supplier], total: 1 } }
    } },
  }).default
  let renderer
  await act(async () => { renderer = create(React.createElement(Page)) })
  const table = renderer.root.findAllByType('Table')[0]
  const dataIndexes = table.props.columns.map((col) => col.dataIndex)
  for (const field of gone) {
    assert.equal(dataIndexes.includes(field), false, `table must not show ${field}`)
  }
  const formFields = renderer.root.findAll((node) => node.props && typeof node.props.field === 'string').map((node) => node.props.field)
  for (const field of gone) {
    assert.equal(formFields.includes(field), false, `form must not collect ${field}`)
  }
  assert.ok(formFields.includes('name'))
  assert.ok(formFields.includes('remark'))
  await act(async () => renderer.unmount())
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

test('file preview, download and delete icon actions expose accessible names', async () => {
  const row = {
    id: 1, originalName: '图纸.pdf', ext: 'pdf', sizeBytes: 1, roundId: 2, direction: 'C2S',
    createdAt: '', canDelete: true,
  }
  const Page = loadTs('src/components/FileTable.tsx', {
    '@arco-design/web-react': arco,
    '@arco-design/web-react/icon': new Proxy({}, { get: (_, name) => component(name) }),
    '../api/client': { get: async () => ({ data: { list: [row], total: 1 } }) },
    '../store/auth': { useAuth: () => ({ hasPerm: (permission) => ['file:preview', 'file:download'].includes(permission) }) },
    '../api/types': { fmtSize: String, fmtTime: String },
    './ChunkUploader': component('Uploader'), './PdfPreview': component('PDF'),
  }).default
  let renderer
  await act(async () => { renderer = create(React.createElement(Page, { projectId: 1, projectStatus: 'IN_PROGRESS', rounds: [] })) })
  const table = renderer.root.findByType('Table')
  const columns = table.props.columns
  const nameCell = columns[0].render(row.originalName, row)
  const actions = columns.at(-1).render(null, row)
  assert.equal(findElement(nameCell, (node) => node.props['aria-label'] === '预览文件').props['aria-label'], '预览文件')
  assert.equal(findElement(actions, (node) => node.props['aria-label'] === '下载文件').props['aria-label'], '下载文件')
  assert.equal(findElement(actions, (node) => node.props['aria-label'] === '删除文件').props['aria-label'], '删除文件')
  await act(async () => renderer.unmount())
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
      '../../components/ProjectActivityPanel':component('Activities'),
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
    '../../components/ProjectActivityPanel':component('Activities'),
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

test('unknown project tab query falls back to rounds and keeps description expansion keyboard accessible', async () => {
  const project = {
    id: 1,
    name: '项目 1',
    description: '一段较长的项目说明',
    status: 'IN_PROGRESS',
    supplierName: '供应商',
    updatedAt: '2026-09-09T00:00:00Z',
  }
  const Page = loadTs('src/pages/project/ProjectDetail.tsx', {
    '@arco-design/web-react': arco,
    'react-router-dom': {
      useParams: () => ({ id: '1' }),
      useSearchParams: () => [new URLSearchParams('tab=unknown'), () => {}],
      useNavigate: () => () => {},
    },
    '../../api/client': {
      get: async (url) => ({ data: url.endsWith('/rounds') ? [] : url.endsWith('/summary') ? { unreadMessages: 0, pendingRounds: 0 } : project }),
    },
    '../../api/types': { PROJECT_STATUS: { IN_PROGRESS: { text: '进行中', color: 'blue' } }, fmtTime: String },
    '../../components/RoundPanel': component('Rounds'),
    '../../components/FileTable': component('Files'),
    '../../components/MessagePanel': component('Messages'),
    '../../components/MemberPanel': component('Members'),
    '../../components/ProjectActivityPanel': component('Activities'),
  }).default
  let renderer
  await act(async () => { renderer = create(React.createElement(Page)) })
  const tabs = renderer.root.findByType('Tabs')
  assert.equal(tabs.props.activeTab, 'rounds')
  assert.ok(renderer.root.findByType('Rounds'))
  const ellipsis = renderer.root.findByType('Descriptions').props.data.find((item) => item.label === '项目说明').value
  const expand = ellipsis.props.expandRender(false)
  assert.equal(expand.props['aria-expanded'], false)
  assert.equal(expand.props.children, '展开')
  await act(async () => renderer.unmount())
})

function activityRow(id, type = 'ROUND', overrides = {}) {
  return {
    id,
    type,
    action: 'CREATED',
    actorName: `操作人${id}`,
    occurredAt: `2026-09-09T00:00:${String(id).padStart(2, '0')}Z`,
    title: type === 'ROUND' ? '发起轮次' : `${type}动作`,
    summary: type === 'PROJECT' ? '项目摘要' : `${type}对象`,
    roundId: type === 'ROUND' ? id : null,
    roundNo: type === 'ROUND' ? id : null,
    targetId: id,
    targetAvailable: type === 'ROUND',
    ...overrides,
  }
}

function activityArco() {
  const timeline = Object.assign(component('Timeline'), { Item: component('Timeline.Item') })
  return new Proxy({ Timeline: timeline }, { get: (obj, key) => obj[key] ?? arco[key] })
}

test('project activity waits for its tab, filters, refreshes and ignores late responses', async () => {
  const pending = []
  const http = {
    get: async (_url, config) => new Promise((resolve) => pending.push({ params: config.params, resolve })),
  }
  const Page = loadTs('src/components/ProjectActivityPanel.tsx', {
    '@arco-design/web-react': activityArco(),
    '../api/client': http,
    '../api/types': { PROJECT_STATUS: { IN_PROGRESS: { text: '进行中' } }, fmtTime: String },
  }).default
  const response = (list, nextCursor = null) => ({
    data: {
      list,
      nextCursor,
      summary: { status: 'IN_PROGRESS', pendingRounds: 2, lastActivityAt: '2026-09-09T00:00:01Z' },
    },
  })
  let renderer
  await act(async () => { renderer = create(React.createElement(Page, { projectId: 1, active: false })) })
  assert.equal(pending.length, 0, 'inactive activity tab must not request')
  await act(async () => { renderer.update(React.createElement(Page, { projectId: 1, active: true })) })
  assert.deepEqual(JSON.parse(JSON.stringify(pending[0].params)), { pageSize: 20 })

  const first = activityRow(1)
  const filtered = activityRow(2, 'ROUND', { summary: '<驳回原因>', title: '<发起轮次>' })
  await act(async () => {
    renderer.root.findByType('Select').props.onChange('ROUND')
    await Promise.resolve()
  })
  assert.deepEqual(JSON.parse(JSON.stringify(pending[1].params)), { pageSize: 20, type: 'ROUND' })
  await act(async () => pending[1].resolve(response([filtered])))
  await act(async () => pending[0].resolve(response([first], 'late-cursor')))
  const renderedRows = renderer.root.findAll((node) => node.props['data-activity-type'] !== undefined)
  assert.deepEqual(renderedRows.map((node) => node.props['data-activity-type']), ['ROUND'])
  assert.equal(renderer.root.findAll((node) => node.props.dangerouslySetInnerHTML !== undefined).length, 0)
  assert.ok(JSON.stringify(renderer.toJSON()).includes('<驳回原因>'), 'server text must remain text content')
  assert.ok(renderer.root.findAll((node) => node.props['data-activity-id'] === 2)[0])
  assert.ok(renderer.root.findAll((node) => node.props.label === filtered.occurredAt).length > 0, 'each event must expose its occurrence time')

  await act(async () => {
    const refresh = renderer.root.findAllByType('Button').find((node) => node.props.children === '刷新')
    refresh.props.onClick()
    refresh.props.onClick()
    await Promise.resolve()
  })
  assert.equal(pending.length, 3)
  await act(async () => pending[2].resolve(response([activityRow(3, 'PROJECT', { summary: null })])))
  assert.equal(renderer.root.findAll((node) => node.props['data-activity-id'] === 3).length, 1)
  assert.equal(renderer.root.findAll((node) => node.props.className === 'project-activity-target').length, 0, 'empty project summary omits the object row')
  await act(async () => renderer.update(React.createElement(Page, { projectId: 2, active: true })))
  assert.equal(pending.length, 4, 'switching projects must issue a fresh request')
  await act(async () => pending[3].resolve(response([activityRow(4)])))
  assert.equal(renderer.root.findAll((node) => node.props['data-activity-id'] === 4).length, 1)
  await act(async () => renderer.unmount())
})

test('project activity exposes malformed initial responses as a retryable failure', async () => {
  let attempts = 0
  const http = {
    get: async () => {
      attempts += 1
      if (attempts === 1) return { data: { list: [] } }
      return {
        data: {
          list: [activityRow(6)],
          nextCursor: null,
          summary: { status: 'IN_PROGRESS', pendingRounds: 0, lastActivityAt: null },
        },
      }
    },
  }
  const Page = loadTs('src/components/ProjectActivityPanel.tsx', {
    '@arco-design/web-react': activityArco(),
    '../api/client': http,
    '../api/types': { PROJECT_STATUS: { IN_PROGRESS: { text: '进行中' } }, fmtTime: String },
  }).default
  let renderer
  await act(async () => { renderer = create(React.createElement(Page, { projectId: 1 })) })
  assert.ok(renderer.root.findAll((node) => node.props.children === '项目动态加载失败').length > 0)
  const retry = renderer.root.findAllByType('Button').find((node) => node.props.children === '重试')
  await act(async () => retry.props.onClick())
  assert.equal(attempts, 2)
  assert.equal(renderer.root.findAll((node) => node.props['data-activity-id'] === 6).length, 1)
  await act(async () => renderer.unmount())
})

test('project activity keeps loaded rows when an append fails, retries the cursor and exposes target navigation', async () => {
  const calls = []
  let appendFailures = 1
  const http = {
    get: async (_url, config) => {
      calls.push(config.params)
      if (config.params.cursor && appendFailures > 0) {
        appendFailures -= 1
        throw Error('temporary activity failure')
      }
      return {
        data: {
          list: config.params.cursor
            ? [
              activityRow(2, 'FILE', { targetAvailable: false, summary: '图纸.pdf' }),
              activityRow(3, 'PROJECT', { summary: null }),
              activityRow(4, 'MESSAGE', { targetAvailable: true, summary: '留言内容' }),
            ]
            : [activityRow(1, 'ROUND', { summary: '驳回原因：需要补充资料' })],
          nextCursor: config.params.cursor ? null : 'cursor-1',
          summary: { status: 'IN_PROGRESS', pendingRounds: 1, lastActivityAt: '2026-09-09T00:00:01Z' },
        },
      }
    },
  }
  const Page = loadTs('src/components/ProjectActivityPanel.tsx', {
    '@arco-design/web-react': activityArco(),
    '../api/client': http,
    '../api/types': { PROJECT_STATUS: { IN_PROGRESS: { text: '进行中' } }, fmtTime: String },
  }).default
  const navigated = []
  let renderer
  await act(async () => { renderer = create(React.createElement(Page, { projectId: 1, onNavigate: (...args) => navigated.push(args) })) })
  assert.deepEqual(JSON.parse(JSON.stringify(calls[0])), { pageSize: 20 })
  let more = renderer.root.findAllByType('Button').find((node) => node.props.children === '加载更多')
  assert.ok(more)
  await act(async () => { await more.props.onClick() })
  assert.ok(renderer.root.findAll((node) => node.props.children === '加载失败').length > 0, 'append failure must expose a retry state')
  assert.equal(renderer.root.findAll((node) => node.props['data-activity-id'] === 1).length, 1)
  const retry = renderer.root.findAllByType('Button').find((node) => node.props.children === '重试')
  assert.ok(retry)
  await act(async () => retry.props.onClick())
  assert.deepEqual(JSON.parse(JSON.stringify(calls[1])), { pageSize: 20, cursor: 'cursor-1' })
  assert.deepEqual(JSON.parse(JSON.stringify(calls[2])), { pageSize: 20, cursor: 'cursor-1' })
  const ids = renderer.root.findAll((node) => node.props['data-activity-id'] !== undefined).map((node) => node.props['data-activity-id'])
  assert.deepEqual(ids, [1, 2, 3, 4])
  const roundTargetLinks = renderer.root.findAllByProps({ 'aria-label': '查看第 1 轮 · 驳回原因：需要补充资料' })
  assert.ok(roundTargetLinks.length > 0)
  await act(async () => roundTargetLinks[0].props.onClick())
  assert.deepEqual(navigated, [['ROUND', 1, 1]])
  assert.equal(renderer.root.findAllByProps({ 'aria-label': '查看图纸.pdf' }).length, 0, 'unavailable file target must not be a link')
  assert.equal(renderer.root.findAllByProps({ 'aria-label': '查看项目摘要' }).length, 0, 'project target must never be a link')
  const messageTargetLinks = renderer.root.findAllByProps({ 'aria-label': '查看留言内容' })
  assert.ok(messageTargetLinks.length > 0, 'available message target should be a link')
  await act(async () => messageTargetLinks[0].props.onClick())
  assert.deepEqual(navigated, [['ROUND', 1, 1], ['MESSAGE', 4, null]])
  await act(async () => renderer.unmount())
})

test('project detail activity URL tab and target navigation preserve valid tab state', async () => {
  let query = new URLSearchParams('tab=activity')
  const updates = []
  const project = { id: 1, name: '项目 1', status: 'IN_PROGRESS', supplierName: '供应商', updatedAt: '2026-09-09T00:00:00Z' }
  const Page = loadTs('src/pages/project/ProjectDetail.tsx', {
    '@arco-design/web-react': arco,
    'react-router-dom': {
      useParams: () => ({ id: '1' }),
      useSearchParams: () => [query, (next) => { updates.push(next); query = new URLSearchParams(next) }],
      useNavigate: () => () => {},
    },
    '../../api/client': { get: async (url) => ({ data: url.endsWith('/rounds') ? [] : url.endsWith('/summary') ? { unreadMessages: 0, pendingRounds: 0 } : project }) },
    '../../api/types': { PROJECT_STATUS: { IN_PROGRESS: { text: '进行中', color: 'blue' } }, fmtTime: String },
    '../../components/RoundPanel': component('Rounds'),
    '../../components/FileTable': component('Files'),
    '../../components/MessagePanel': component('Messages'),
    '../../components/MemberPanel': component('Members'),
    '../../components/ProjectActivityPanel': component('Activities'),
  }).default
  let renderer
  await act(async () => { renderer = create(React.createElement(Page)) })
  assert.equal(renderer.root.findByType('Tabs').props.activeTab, 'activity')
  const activities = renderer.root.findByType('Activities')
  assert.equal(activities.props.active, true)
  await act(async () => activities.props.onNavigate('ROUND', 7, 7))
  assert.equal(updates.at(-1).get('tab'), 'rounds')
  assert.equal(updates.at(-1).get('target'), '7')

  await act(async () => renderer.update(React.createElement(Page)))
  assert.equal(renderer.root.findByType('Tabs').props.activeTab, 'rounds')
  assert.equal(renderer.root.findByType('Rounds').props.targetId, 7)
  assert.ok(renderer.root.findAll((node) => node.props.children === '显示全部').length > 0)
  await act(async () => renderer.root.findAllByType('Tabs')[0].props.onChange('files'))
  assert.equal(updates.at(-1).get('tab'), 'files')
  assert.equal(updates.at(-1).get('target'), null)
  await act(async () => renderer.unmount())
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
  const role={id:7,name:'测试',permissionIds:[5],assignedUserCount:0,status:'ACTIVE'}
  const perms=[{id:1,code:'dashboard',name:'工作台',type:'MENU',parentId:null},{id:5,code:'org:dept',name:'部门',type:'MENU',parentId:null},{id:24,code:'dept:manage',name:'管理部门',type:'ACTION',parentId:5}]
  const Page=loadTs('src/pages/rbac/RoleList.tsx',{
    '@arco-design/web-react':arco,'@arco-design/web-react/icon':new Proxy({},{get:(_,n)=>component(n)}),
    '../../api/client':{get:async url=>({data:url==='/permissions'?perms:{list:[role],total:1}})},
    '../../store/auth':authModule({id:1,userType:'INTERNAL',isSystemAdmin:false},['role:manage']),
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
    '../../api/client':{get:async(_url,{params})=>{query=params;return {data:{list:[],total:0,page:1,pageSize:20}}}},
    '../../store/auth':authModule({id:1,userType:'INTERNAL',isSystemAdmin:false},['log:view']),'../../api/types':{fmtTime:String},
  }).default
  let renderer
  await act(async()=>{renderer=create(React.createElement(Page))})
  await act(async()=>renderer.root.findByType('DatePicker.RangePicker').props.onChange(['2026-09-04 12:00:00','2026-09-05 00:00:00']))
  await act(async()=>renderer.root.findAllByType('Button').find(n=>n.props.children==='查询').props.onClick())
  assert.equal(query.end,new Date('2026-09-05 00:00:00').toISOString(),'midnight must not silently include the following entire day')
  await act(async()=>renderer.unmount())
})

test('audit hard-delete controls require a system administrator with log access', async () => {
  const row = { id: 101, action: 'LOGIN', createdAt: '2026-09-09T00:00:00Z' }
  const cleanupAudit = { id: 102, action: 'AUDIT_LOG_DELETE', createdAt: '2026-09-09T00:00:01Z' }
  for (const [label, user, permissions, expected] of [
    ['ordinary log viewer', { id: 1, userType: 'INTERNAL', isSystemAdmin: false }, ['log:view'], false],
    ['admin without log access', { id: 1, userType: 'INTERNAL', isSystemAdmin: true }, [], false],
    ['system admin log viewer', { id: 1, userType: 'INTERNAL', isSystemAdmin: true }, ['log:view'], true],
  ]) {
    const Page = loadTs('src/pages/system/AuditLog.tsx', {
      '@arco-design/web-react': arco,
      '@arco-design/web-react/icon': new Proxy({}, { get: (_, name) => component(name) }),
      '../../api/client': { get: async () => ({ data: { list: [row, cleanupAudit], total: 2, page: 1, pageSize: 20 } }) },
      '../../store/auth': authModule(user, permissions),
      '../../api/types': { fmtTime: String },
    }).default
    let renderer
    await act(async () => { renderer = create(React.createElement(Page)) })
    const table = renderer.root.findByType('Table')
    const ordinaryActions = table.props.columns.at(-1).render(null, row)
    assert.equal(!!findActionButton(ordinaryActions, '删除'), expected, label)
    assert.equal(renderer.root.findAllByType('Button').some((node) => node.props.children === '删除所选'), expected, label)
    if (expected) {
      const cleanupActions = table.props.columns.at(-1).render(null, cleanupAudit)
      assert.equal(findActionButton(cleanupActions, '删除'), undefined, 'cleanup audit must remain immutable')
      assert.equal(table.props.rowSelection.checkboxProps(cleanupAudit).disabled, true)
    }
    await act(async () => renderer.unmount())
  }
})

test('audit selection is cleared across filtering, paging and refresh, and reports the returned delete count', async () => {
  const rows = {
    initial: [{ id: 101, action: 'LOGIN', createdAt: '2026-09-09T00:00:00Z' }],
    filtered: [{ id: 202, action: 'LOGIN_FAILED', createdAt: '2026-09-09T00:01:00Z' }],
    page2: [
      { id: 301, action: 'LOGIN', createdAt: '2026-09-09T00:02:00Z' },
      { id: 302, action: 'LOGIN_FAILED', createdAt: '2026-09-09T00:03:00Z' },
      { id: 303, action: 'AUDIT_LOG_DELETE', createdAt: '2026-09-09T00:04:00Z' },
    ],
  }
  let holdNext = false
  let releaseFetch
  const postCalls = []
  const deleteCalls = []
  const successes = []
  const pageData = (params) => params.page === 2 ? rows.page2 : params.keyword === 'next' ? rows.filtered : rows.initial
  const http = {
    get: async (_url, { params }) => {
      const response = { data: { list: pageData(params), total: pageData(params).length, page: params.page, pageSize: params.pageSize } }
      if (!holdNext) return response
      holdNext = false
      return new Promise((resolve) => { releaseFetch = () => resolve(response) })
    },
    post: async (url, body) => { postCalls.push({ url, body }); return { data: { deleted: 1 } } },
    delete: async (url) => { deleteCalls.push(url); return { data: { deleted: 1 } } },
  }
  const auditArco = new Proxy({}, {
    get: (_, key) => key === 'Message' ? { ...arco.Message, success: (message) => successes.push(message) } : arco[key],
  })
  const Page = loadTs('src/pages/system/AuditLog.tsx', {
    '@arco-design/web-react': auditArco,
    '@arco-design/web-react/icon': new Proxy({}, { get: (_, name) => component(name) }),
    '../../api/client': http,
    '../../store/auth': authModule({ id: 1, userType: 'INTERNAL', isSystemAdmin: true }, ['log:view']),
    '../../api/types': { fmtTime: String },
  }).default
  let renderer
  await act(async () => { renderer = create(React.createElement(Page)) })

  let table = renderer.root.findByType('Table')
  await act(async () => table.props.rowSelection.onChange([101]))
  const staleBatchOk = renderer.root.findAllByType('Popconfirm').find((node) => String(node.props.title).includes('选中的')).props.onOk
  await act(async () => renderer.root.findByProps({ placeholder: '操作人 / 动作编码 / 对象' }).props.onChange('next'))
  await act(async () => renderer.root.findAllByType('Button').find((node) => node.props.children === '查询').props.onClick())
  table = renderer.root.findByType('Table')
  assert.deepEqual(Array.from(table.props.rowSelection.selectedRowKeys), [])
  assert.deepEqual(table.props.data.map((row) => row.id), [202])
  await act(async () => staleBatchOk())
  assert.equal(postCalls.length, 0, 'a stale selection must not submit hidden log 101')

  table = renderer.root.findByType('Table')
  await act(async () => table.props.rowSelection.onChange([202]))
  table = renderer.root.findByType('Table')
  await act(async () => table.props.pagination.onChange(2, 20))
  table = renderer.root.findByType('Table')
  assert.deepEqual(Array.from(table.props.rowSelection.selectedRowKeys), [])
  assert.deepEqual(table.props.data.map((row) => row.id), [301, 302, 303])

  await act(async () => table.props.rowSelection.onChange([301, 302, 303]))
  table = renderer.root.findByType('Table')
  assert.deepEqual(Array.from(table.props.rowSelection.selectedRowKeys), [301, 302], 'cleanup audits must be excluded from batch selection')
  const preRefreshBatchOk = renderer.root.findAllByType('Popconfirm').find((node) => String(node.props.title).includes('选中的')).props.onOk
  holdNext = true
  act(() => renderer.root.findAllByType('Button').find((node) => node.props.children === '刷新').props.onClick())
  table = renderer.root.findByType('Table')
  assert.deepEqual(Array.from(table.props.rowSelection.selectedRowKeys), [])
  assert.equal(renderer.root.findAllByType('Button').find((node) => node.props.children === '删除所选').props.disabled, true)
  const loadingActions = table.props.columns.at(-1).render(null, rows.page2[0])
  assert.equal(findActionButton(loadingActions, '删除').props.disabled, true)
  await act(async () => preRefreshBatchOk())
  assert.equal(postCalls.length, 0, 'refresh must invalidate the previous batch callback')
  await act(async () => releaseFetch())

  table = renderer.root.findByType('Table')
  let rowActions = table.props.columns.at(-1).render(null, rows.page2[0])
  await act(async () => findActionButton(rowActions, '查看').props.onClick())
  assert.equal(renderer.root.findByType('Drawer').props.visible, true)
  table = renderer.root.findByType('Table')
  rowActions = table.props.columns.at(-1).render(null, rows.page2[0])
  const deleteOneOk = findElement(rowActions, (node) => node.props.title === '确认删除这条日志？').props.onOk
  await act(async () => deleteOneOk())
  assert.deepEqual(deleteCalls, ['/admin/audit-logs/301'])
  assert.equal(renderer.root.findByType('Drawer').props.visible, false, 'deleting the open detail row must close its stale drawer')

  table = renderer.root.findByType('Table')
  await act(async () => table.props.rowSelection.onChange([301, 302, 303]))
  const batchOk = renderer.root.findAllByType('Popconfirm').find((node) => String(node.props.title).includes('选中的')).props.onOk
  const successCountBeforeBatch = successes.length
  await act(async () => batchOk())
  assert.deepEqual(postCalls[0].body.ids, [301, 302])
  assert.equal(successes[successCountBeforeBatch], '已删除 1 条', 'batch success must use the server-reported count instead of the two requested ids')
  await act(async () => renderer.unmount())
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
