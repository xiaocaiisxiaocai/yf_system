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

test('account menu opens personal profile maintenance inside the authenticated layout', () => {
  const root = path.resolve(__dirname, '..')
  const layout = fs.readFileSync(path.join(root, 'src/layouts/AdminLayout.tsx'), 'utf8')
  const app = fs.readFileSync(path.join(root, 'src/App.tsx'), 'utf8')

  assert.match(layout, /<Menu\.Item key="profile">[\s\S]*?个人资料维护/)
  assert.match(layout, /nav\('\/profile'\)/)
  assert.doesNotMatch(layout, /<Menu\.Item key="pwd">/)
  assert.match(app, /<Route path="profile" element=\{<Profile \/>\}/)
})

test('personal profile submits only own email and keeps password change in the same page', async () => {
  const calls = []
  let savedUser
  let loggedOut = false
  let navigated
  const profileForm = { setFieldsValue() {} }
  const passwordForm = {}
  let formIndex = 0
  const profileArco = new Proxy({
    ...arco,
    Form: Object.assign(component('Form'), {
      useForm: () => [formIndex++ === 0 ? profileForm : passwordForm],
      Item: component('Form.Item'),
    }),
  }, { get: (obj, key) => obj[key] ?? component(key) })
  const http = {
    put: async (url, body) => {
      calls.push({ url, body })
      if (url === '/auth/profile') {
        return { data: { user: { id: 8, employeeNo: 'supplier8', realName: '供应商人员', email: body.email, userType: 'SUPPLIER', supplierId: 3 } } }
      }
      return { data: {} }
    },
  }
  const Page = loadTs('src/pages/Profile.tsx', {
    '@arco-design/web-react': profileArco,
    '@arco-design/web-react/icon': new Proxy({}, { get: (_, name) => component(name) }),
    'react-router-dom': { useNavigate: () => (path) => { navigated = path } },
    '../api/client': { __esModule: true, default: http, withAuthLock: async (action) => action() },
    '../store/auth': {
      useAuth: () => ({
        user: { id: 8, employeeNo: 'supplier8', realName: '供应商人员', email: 'before@example.invalid', userType: 'SUPPLIER', supplierId: 3 },
        setUser: (user) => { savedUser = user },
        logout: () => { loggedOut = true },
      }),
    },
  }).default
  let renderer
  await act(async () => { renderer = create(React.createElement(Page)) })
  let forms = renderer.root.findAllByType('Form')

  await act(async () => forms[0].props.onSubmit({ email: 'after@example.invalid', id: 999 }))
  assert.equal(calls[0].url, '/auth/profile')
  assert.deepEqual({ ...calls[0].body }, { email: 'after@example.invalid' })
  assert.equal(savedUser.id, 8)
  assert.equal(savedUser.email, 'after@example.invalid')

  forms = renderer.root.findAllByType('Form')
  await act(async () => forms[1].props.onSubmit({ oldPassword: 'old123', newPassword: 'new123', confirm: 'new123' }))
  assert.equal(calls[1].url, '/auth/password')
  assert.deepEqual({ ...calls[1].body }, { oldPassword: 'old123', newPassword: 'new123' })
  assert.equal(loggedOut, true)
  assert.equal(navigated, '/login')
  await act(async () => renderer.unmount())
})

test('SAA branding is wired to the application logo and favicon', () => {
  const root = path.resolve(__dirname, '..')
  const index = fs.readFileSync(path.join(root, 'index.html'), 'utf8')
  const layout = fs.readFileSync(path.join(root, 'src/layouts/AdminLayout.tsx'), 'utf8')
  const login = fs.readFileSync(path.join(root, 'src/pages/Login.tsx'), 'utf8')
  const authShell = fs.readFileSync(path.join(root, 'src/components/AuthShell.tsx'), 'utf8')

  assert.match(index, /rel="icon"[^>]+href="\/saa-logo\.svg"/)
  assert.match(index, /rel="alternate icon"[^>]+href="\/favicon\.ico"/)
  assert.match(layout, /src="\/saa-logo\.svg"/)
  assert.match(login, /<AuthShell/)
  assert.match(authShell, /src="\/saa-logo\.svg"/)
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

test('collapsed sidebar tooltip renders the menu label instead of duplicating its icon', () => {
  const layout = fs.readFileSync(path.resolve(__dirname, '..', 'src/layouts/AdminLayout.tsx'), 'utf8')
  assert.match(layout, /renderItemInTooltip=\{\(\) => m\.label\}/)
  assert.doesNotMatch(layout, /title=\{collapsed \? m\.label/)
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
    '../../store/auth': authModule({ id: 1, userType: 'INTERNAL', isSystemAdmin: true }, ['dept:manage', 'dept:delete']),
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

test('hard delete actions require their dedicated delete permission', async () => {
  const iconMock = new Proxy({}, { get: (_, name) => component(name) })
  const pageData = { list: [], total: 0, page: 1, pageSize: 10 }
  const cases = [
    {
      page: 'src/pages/project/ProjectList.tsx',
      pagePermission: 'project:update',
      deletePermission: 'project:delete',
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
      pagePermission: 'supplier:manage',
      deletePermission: 'supplier:delete',
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
      pagePermission: 'user:manage',
      deletePermission: 'user:delete',
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
      pagePermission: 'role:manage',
      deletePermission: 'role:delete',
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
      ['manager without delete permission', { id: 1, userType: 'INTERNAL', isSystemAdmin: false }, [item.pagePermission], false],
      ['system admin without delete permission', { id: 1, userType: 'INTERNAL', isSystemAdmin: true }, [item.pagePermission], false],
      ['delegated delete permission', { id: 1, userType: 'INTERNAL', isSystemAdmin: false }, [item.pagePermission, item.deletePermission], true],
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
    ['project:update', 'project:delete'],
  ))).default
  let renderer
  await act(async () => { renderer = create(React.createElement(Project)) })
  const busy = renderer.root.findAllByType('Table')[0].props.columns.at(-1).render(null, { id: 2, name: 'busy', status: 'IN_PROGRESS' })
  assert.equal(findActionButton(busy, '删除'), undefined, 'in-progress projects must not offer delete')
  const terminated = renderer.root.findAllByType('Table')[0].props.columns.at(-1).render(null, { id: 3, name: 'terminated', status: 'TERMINATED' })
  assert.ok(findActionButton(terminated, '删除'), 'empty terminated projects may be deleted')
  await act(async () => renderer.unmount())
})

test('department hard delete requires the dedicated delete permission', async () => {
  const departments = [{ id: 3, name: '开发课', kind: 'SECTION', parentId: 2, sortNo: 1, status: 'ACTIVE' }]
  for (const [permissions, expected] of [[['dept:manage'], false], [['dept:manage', 'dept:delete'], true]]) {
    const Page = loadTs('src/pages/org/DeptManage.tsx', {
      '@arco-design/web-react': arco,
      '@arco-design/web-react/icon': new Proxy({}, { get: (_, name) => component(name) }),
      '../../api/client': { get: async () => ({ data: departments }) },
      '../../store/auth': authModule({ id: 1, userType: 'INTERNAL', isSystemAdmin: false }, permissions),
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
      if (typeof name === 'string' && name.startsWith('.') && name.replace(/\\/g, '/').endsWith('/utils/password')) {
        return loadTs('src/utils/password.ts', {})
      }
      return require(name)
    },
  }, { filename })
  return exports
}

for (const [page, api, props] of [
  ['pages/project/ProjectList.tsx', '/projects', {}],
  ['pages/supplier/SupplierList.tsx', '/admin/suppliers', {}],
  ['pages/org/UserList.tsx', '/admin/users', {}],
  ['components/FileTable.tsx', '/projects/1/files', { projectId: 1, projectStatus: 'IN_PROGRESS' }],
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

test('password reset dialogs discard a cancelled password before another account opens', async () => {
  const iconMock = new Proxy({}, { get: (_, name) => component(name) })

  {
    let resetCalls = 0
    const form = {
      resetFields() { resetCalls++ },
      setFieldsValue() {},
      validate: async () => ({ newPassword: 'unused' }),
    }
    const testArco = new Proxy({
      Form: Object.assign(component('Form'), { useForm: () => [form], Item: component('Form.Item') }),
      Typography: arco.Typography,
      Message: arco.Message,
    }, { get: (obj, key) => obj[key] ?? component(key) })
    const rows = [
      { id: 11, employeeNo: 'user-a', realName: '甲', email: 'a@example.invalid', status: 'ACTIVE', createdAt: '' },
      { id: 12, employeeNo: 'user-b', realName: '乙', email: 'b@example.invalid', status: 'ACTIVE', createdAt: '' },
    ]
    const Page = loadTs('src/pages/org/UserList.tsx', {
      '@arco-design/web-react': testArco,
      '@arco-design/web-react/icon': iconMock,
      '../../store/auth': authModule({ id: 1, userType: 'INTERNAL' }, ['user:manage']),
      '../../api/types': { fmtTime: String },
      '../../components/ActionSlots': actionSlotsModule,
      '../../api/client': {
        get: async (url) => ({ data: url === '/admin/users' ? { list: rows, total: rows.length, page: 1, pageSize: 10 } : [] }),
      },
    }).default
    let renderer
    await act(async () => { renderer = create(React.createElement(Page)) })
    const actionsFor = (row) => renderer.root.findByType('Table').props.columns.at(-1).render(null, row)

    await act(async () => findActionButton(actionsFor(rows[0]), '重置密码').props.onClick())
    assert.equal(resetCalls, 1, 'opening a user password reset must start with an empty form')
    await act(async () => renderer.root.findAllByType('Modal').find((node) => node.props.visible).props.onCancel())
    assert.equal(resetCalls, 2, 'cancelling a user password reset must discard the entered password')
    await act(async () => findActionButton(actionsFor(rows[1]), '重置密码').props.onClick())
    assert.equal(resetCalls, 3, 'opening another user cannot reuse the previous password draft')
    await act(async () => renderer.unmount())
  }

  {
    let resetCalls = 0
    const form = {
      resetFields() { resetCalls++ },
      setFieldsValue() {},
      validate: async () => ({ newPassword: 'unused' }),
    }
    const testArco = new Proxy({
      Form: Object.assign(component('Form'), { useForm: () => [form], Item: component('Form.Item') }),
      Typography: arco.Typography,
      Message: arco.Message,
    }, { get: (obj, key) => obj[key] ?? component(key) })
    const supplier = { id: 8, name: 'fixture', status: 'ACTIVE', createdAt: '' }
    const accounts = [
      { id: 21, employeeNo: 'supplier-a', realName: '甲', email: 'a@example.invalid', status: 'ACTIVE', createdAt: '' },
      { id: 22, employeeNo: 'supplier-b', realName: '乙', email: 'b@example.invalid', status: 'ACTIVE', createdAt: '' },
    ]
    const Page = loadTs('src/pages/supplier/SupplierList.tsx', {
      '@arco-design/web-react': testArco,
      '@arco-design/web-react/icon': iconMock,
      '../../store/auth': { useAuth: () => ({ hasPerm: () => true }) },
      '../../api/types': { fmtTime: String },
      '../../components/ActionSlots': actionSlotsModule,
      '../../api/client': {
        get: async (url) => url.endsWith('/accounts')
          ? { data: accounts }
          : { data: { list: [supplier], total: 1, page: 1, pageSize: 10 } },
      },
    }).default
    let renderer
    await act(async () => { renderer = create(React.createElement(Page)) })
    const supplierActions = renderer.root.findAllByType('Table')[0].props.columns.at(-1).render(null, supplier)
    await act(async () => findActionButton(supplierActions, '账号管理').props.onClick())
    const accountActionsFor = (row) => renderer.root.findAllByType('Table')[1].props.columns.at(-1).render(null, row)

    await act(async () => findActionButton(accountActionsFor(accounts[0]), '重置密码').props.onClick())
    assert.equal(resetCalls, 1, 'opening a supplier password reset must start with an empty form')
    await act(async () => renderer.root.findAllByType('Modal').find((node) => node.props.visible).props.onCancel())
    assert.equal(resetCalls, 2, 'cancelling a supplier password reset must discard the entered password')
    await act(async () => findActionButton(accountActionsFor(accounts[1]), '重置密码').props.onClick())
    assert.equal(resetCalls, 3, 'opening another supplier account cannot reuse the previous password draft')
    await act(async () => renderer.unmount())
  }
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
      file: 'src/components/FileTable.tsx', api: '/projects/1/files', props: { projectId: 1, projectStatus: 'IN_PROGRESS' },
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
      file: 'src/components/MemberPanel.tsx', api: '/projects/1/members', props: { projectId: 1, supplierName: '供应商' },
      data: [{ userId: 1, employeeNo: 'A1', realName: '管理员', createdAt: '' }],
      mocks: (http) => ({
        '@arco-design/web-react': arco, '@arco-design/web-react/icon': iconMock,
        '../api/client': http, '../store/auth': auth, '../api/types': { fmtTime: String },
      }),
    },
    {
      file: 'src/components/MessagePanel.tsx', api: '/projects/1/messages', props: { projectId: 1, projectStatus: 'IN_PROGRESS' },
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
        'react-router-dom': { useNavigate: () => () => {} },
      }),
    },
    {
      file: 'src/pages/Dashboard.tsx', api: '/dashboard/summary', props: {},
      data: { projectCount: 1, activeProjectCount: 1, pendingConfirmations: 0, unreadMessages: 0, recentMessages: [] },
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
        if (url === '/dashboard/pending-projects') return { data: { list: [], total: 0, page: 1, pageSize: 10 } }
        if (url === '/admin/system/storage') return { data: { totalBytes: 1, availableBytes: 1, usedPercent: 0, warnPercent: 80, warning: false, root: '/' } }
        if (url === '/admin/system/mail-status') return { data: { configured: false, notificationsEnabled: true, queue: { pending: 0, sending: 0, sent: 0, failed: 0 }, missingEmailCount: 0, missingEmailAccounts: [], recent: [] } }
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

test('dashboard pending projects can retry, navigate, refresh, and recover from an expired last page', async () => {
  const iconMock = new Proxy({}, { get: (_, name) => component(name) })
  const pendingRequests = []
  const summary = {
    projectCount: 12,
    activeProjectCount: 3,
    pendingConfirmations: 11,
    unreadMessages: 0,
    recentMessages: [],
  }
  const http = {
    get: async (url, config = {}) => {
      if (url === '/dashboard/summary') return { data: summary }
      if (url === '/dashboard/pending-projects') {
        return new Promise((resolve, reject) => pendingRequests.push({ params: config.params, resolve, reject }))
      }
      throw new Error(`unexpected request ${url}`)
    },
  }
  const Page = loadTs('src/pages/Dashboard.tsx', {
    '@arco-design/web-react': new Proxy({
      Grid: Object.assign(component('Grid'), { Row: component('Grid.Row'), Col: component('Grid.Col') }),
      List: Object.assign(component('List'), { Item: Object.assign(component('List.Item'), { Meta: component('List.Item.Meta') }) }),
      Typography: arco.Typography,
      Message: arco.Message,
    }, { get: (obj, key) => obj[key] ?? component(key) }),
    '@arco-design/web-react/icon': iconMock,
    '../api/client': http,
    '../store/auth': authModule({ id: 1, realName: '管理员', userType: 'INTERNAL' }, ['project:confirm']),
    '../api/types': { fmtTime: String },
    'react-router-dom': { Link: component('Link'), useNavigate: () => () => {} },
  }).default
  let renderer
  await act(async () => { renderer = create(React.createElement(Page)) })

  assert.deepEqual(JSON.parse(JSON.stringify(pendingRequests[0].params)), { page: 1, pageSize: 10 })
  await act(async () => pendingRequests[0].reject(new Error('pending projects unavailable')))
  assert.ok(renderer.root.findAll((node) => node.props.children === '待确认项目加载失败').length > 0)
  const retry = renderer.root.findAllByType('Button').find((node) => node.props.children === '重试')
  assert.ok(retry)
  await act(async () => retry.props.onClick())
  const firstPage = [{ id: 11, name: '待确认项目 A', status: 'PENDING_CONFIRMATION', confirmSide: 'COMPANY', updatedAt: '2026-09-10T01:00:00Z' }]
  await act(async () => pendingRequests[1].resolve({ data: { list: firstPage, total: 11, page: 1, pageSize: 10 } }))

  const pendingList = renderer.root.findAllByType('List').find((node) => node.props.dataSource?.[0]?.id === 11)
  assert.ok(pendingList)
  const pendingRow = pendingList.props.render(firstPage[0])
  const projectLink = pendingRow.props.children.props.title
  assert.equal(projectLink.props.to, '/projects/11')
  assert.equal(projectLink.props.children, '待确认项目 A')
  const pagination = renderer.root.findByType('Pagination')
  assert.equal(pagination.props.current, 1)
  assert.equal(pagination.props.total, 11)

  await act(async () => pagination.props.onChange(2, 10))
  assert.deepEqual(JSON.parse(JSON.stringify(pendingRequests[2].params)), { page: 2, pageSize: 10 })
  await act(async () => pendingRequests[2].resolve({ data: { list: [], total: 10, page: 2, pageSize: 10 } }))
  assert.deepEqual(JSON.parse(JSON.stringify(pendingRequests[3].params)), { page: 1, pageSize: 10 })
  await act(async () => pendingRequests[3].resolve({ data: { list: firstPage, total: 10, page: 1, pageSize: 10 } }))
  assert.equal(renderer.root.findAllByType('Pagination').length, 0, 'a single valid page must not retain a stale page-two control')

  const refresh = renderer.root.findAllByType('Card').find((node) => node.props.className === 'dashboard-pending').props.extra
  assert.ok(refresh, 'a workflow status change must have a reachable list refresh')
  await act(async () => refresh.props.onClick())
  await act(async () => pendingRequests[4].resolve({ data: { list: [], total: 0, page: 1, pageSize: 10 } }))
  assert.ok(renderer.root.findAllByType('Empty').some((node) => node.props.description === '暂无待确认项目'))
  await act(async () => renderer.unmount())
})

test('system config exposes mail configuration, queue outcomes and missing mailbox hints', async () => {
  const calls = []
  const Page = loadTs('src/pages/system/SysConfig.tsx', {
    '@arco-design/web-react': arco,
    '@arco-design/web-react/icon': new Proxy({}, { get: (_, name) => component(name) }),
    '../../api/client': {
      get: async (url) => {
        calls.push(url)
        if (url === '/admin/system/configs') return { data: [] }
        if (url === '/admin/system/storage') return { data: { totalBytes: 100, availableBytes: 40, usedPercent: 60, warnPercent: 80, warning: false, root: '/' } }
        return { data: {
          configured: false,
          notificationsEnabled: true,
          queue: { pending: 2, sending: 1, sent: 8, failed: 3 },
          latestSentAt: '2026-09-09T01:00:00Z',
          latestFailedAt: '2026-09-09T02:00:00Z',
          missingEmailCount: 1,
          missingEmailAccounts: [{ userId: 7, employeeNo: 'E7', realName: '未填邮箱', userType: 'INTERNAL', status: 'ACTIVE' }],
          recent: [{ id: 11, action: 'EMAIL_FAILED', targetType: 'email_outbox', targetId: '11', detail: { error: 'SMTP 连接失败' }, createdAt: '2026-09-09T02:00:00Z' }],
        } }
      },
    },
    '../../api/types': { fmtTime: String, fmtSize: String },
    'react-router-dom': { useNavigate: () => () => {} },
  }).default
  let renderer
  await act(async () => { renderer = create(React.createElement(Page)) })
  await act(async () => { await Promise.resolve(); await Promise.resolve() })
  assert.ok(calls.includes('/admin/system/mail-status'))
  assert.ok(renderer.root.findAll((node) => node.props.title === '邮件发送').length > 0)
  const tables = renderer.root.findAllByType('Table')
  assert.ok(tables.some((table) => table.props.data?.some((row) => row.action === 'EMAIL_FAILED')))
  await act(async () => renderer.unmount())
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
  await act(async () => { renderer = create(React.createElement(Page, { projectId: 1, projectStatus: 'COMPLETED' })) })
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

test('completed and terminated projects hide message delete controls', async () => {
  const Page = loadTs('src/components/MessagePanel.tsx', {
    '@arco-design/web-react': arco,
    '@arco-design/web-react/icon': new Proxy({}, { get: (_, n) => component(n) }),
    '../store/auth': { useAuth: () => ({ user: { id: 1 }, hasPerm: () => true }) },
    '../api/types': { fmtTime: String },
    '../api/client': {
      get: async () => ({
        data: {
          list: [{ id: 1, projectId: 1, senderId: 2, senderName: '成员', senderType: 'INTERNAL', content: '历史留言', readCount: 0, totalCount: 1, readByMe: true, createdAt: '' }],
          total: 1,
        },
      }),
    },
  }).default
  let renderer
  await act(async () => { renderer = create(React.createElement(Page, { projectId: 1, projectStatus: 'COMPLETED' })) })
  assert.equal(renderer.root.findAllByType('Popconfirm').length, 0)
  await act(async () => renderer.update(React.createElement(Page, { projectId: 1, projectStatus: 'TERMINATED' })))
  assert.equal(renderer.root.findAllByType('Popconfirm').length, 0)
  await act(async () => renderer.update(React.createElement(Page, { projectId: 1, projectStatus: 'IN_PROGRESS' })))
  assert.equal(renderer.root.findAllByType('Popconfirm').length, 1)
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
  await act(async () => { renderer = create(React.createElement(Uploader, { projectId: 1, visible: true, onClose: () => { closed++ }, onDone: () => { done++ } })) })
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

test('a definitive upload integrity failure can discard the damaged session and retry cleanup', async () => {
  let initCalls = 0
  const merges = []
  const deletes = []
  let deleteFailures = 1
  let done = 0
  let closed = 0
  const file = { name: 'damaged.pdf', size: 1, slice: () => new Blob(['x']) }
  const fileInput = { value: 'damaged.pdf' }
  const Uploader = loadTs('src/components/ChunkUploader.tsx', {
    '@arco-design/web-react': arco,
    '@arco-design/web-react/icon': new Proxy({}, { get: (_, name) => component(name) }),
    '../api/types': { fmtSize: String },
    '../api/file-hash': { fileMd5: async () => 'test-hash' },
    '../api/client': {
      post: async (url) => {
        if (url === '/uploads/init') {
          initCalls++
          return { data: { sessionId: initCalls === 1 ? 'damaged-session' : 'fresh-session', chunkSize: 1, totalChunks: 1, uploadedChunks: [] } }
        }
        const sid = url.split('/')[2]
        merges.push(sid)
        if (sid === 'damaged-session') {
          throw { response: { status: 400, data: { message: '文件 MD5 校验失败，请重新上传' } } }
        }
        return { data: { id: 99 } }
      },
      put: async () => {},
      delete: async (url) => {
        deletes.push(url)
        if (deleteFailures-- > 0) throw new Error('temporary cleanup failure')
        return { data: {} }
      },
    },
  }).default
  let renderer
  await act(async () => { renderer = create(React.createElement(Uploader, { projectId: 1, visible: true, onClose: () => { closed++ }, onDone: () => { done++ } }), {
    createNodeMock: (element) => element.type === 'input' ? fileInput : null,
  }) })
  await act(async () => renderer.root.findByType('input').props.onChange({ target: { files: [file] } }))
  await act(async () => renderer.root.findByType('Modal').props.footer.props.children[1].props.onClick())
  let discard = findActionButton(renderer.root.findByType('Modal').props.footer, '清理并重新选择')
  assert.ok(discard, 'an explicit integrity failure must not be mislabeled as an uncertain commit')
  await act(async () => discard.props.onClick())
  assert.deepEqual(deletes, ['/uploads/damaged-session'])
  discard = findActionButton(renderer.root.findByType('Modal').props.footer, '清理并重新选择')
  assert.ok(discard, 'failed cleanup must retain the damaged session for retry')
  assert.equal(fileInput.value, 'damaged.pdf', 'failed cleanup keeps the selected file until cleanup succeeds')
  await act(async () => discard.props.onClick())
  assert.deepEqual(deletes, ['/uploads/damaged-session', '/uploads/damaged-session'])
  assert.equal(renderer.root.findByType('input').props.disabled, false)
  assert.equal(fileInput.value, '', 'cleanup must clear the native input so choosing the same file fires change again')
  await act(async () => renderer.root.findByType('input').props.onChange({ target: { files: [file] } }))
  await act(async () => renderer.root.findByType('Modal').props.footer.props.children[1].props.onClick())
  assert.deepEqual(merges, ['damaged-session', 'fresh-session'])
  assert.equal(initCalls, 2, 'an aborted damaged session must restart through init')
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
  await act(async () => { renderer = create(React.createElement(Uploader, { projectId: 1, visible: true, onClose: () => { closed++ }, onDone: () => { done++ } })) })
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
  await act(async()=>{renderer=create(React.createElement(Page,{projectId:1,projectStatus:'IN_PROGRESS'}))})
  await act(async()=>renderer.root.findByType('Table').props.rowSelection.onChange([23]))
  await act(async()=>renderer.root.findByType('Input.Search').props.onSearch('different-file'))
  assert.equal(renderer.root.findByType('Table').props.rowSelection.selectedRowKeys.length,0,'hidden selected files must not remain in the download batch')
  await act(async()=>renderer.unmount())
})

test('file preview, download and delete icon actions expose accessible names', async () => {
  const row = {
    id: 1, originalName: '图纸.pdf', ext: 'pdf', sizeBytes: 1, direction: 'C2S',
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
  await act(async () => { renderer = create(React.createElement(Page, { projectId: 1, projectStatus: 'IN_PROGRESS' })) })
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
       '../../components/FileTable':component('Files'),
       '../../components/MessagePanel':component('Messages'),'../../components/MemberPanel':component('Members'),
       '../../components/ProjectActivityPanel':component('Activities'),'../../components/ProjectWorkflowPanel':component('Workflow'),
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
     '../../components/FileTable':component('Files'),
     '../../components/MessagePanel':component('Messages'),'../../components/MemberPanel':component('Members'),
     '../../components/ProjectActivityPanel':component('Activities'),'../../components/ProjectWorkflowPanel':component('Workflow'),
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
  assert.equal(renderer.root.findByType('Files').props.projectId,2)
  id='3'
  await act(async()=>renderer.update(React.createElement(Page)))
  await act(async()=>pending.get('/projects/3')({data:project(3)}))
  await act(async()=>pending.get('/projects/2')({data:project(2)}))
  assert.equal(renderer.root.findByType('Files').props.projectId,3,'a late refresh after a project operation must not replace the new project')
  await act(async()=>renderer.unmount())
})

test('unknown project tab query falls back to files and keeps description expansion keyboard accessible', async () => {
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
      get: async (url) => ({ data: url.endsWith('/summary') ? { unreadMessages: 0 } : project }),
    },
    '../../api/types': { PROJECT_STATUS: { IN_PROGRESS: { text: '进行中', color: 'blue' } }, fmtTime: String },
    '../../components/FileTable': component('Files'),
    '../../components/MessagePanel': component('Messages'),
    '../../components/MemberPanel': component('Members'),
    '../../components/ProjectActivityPanel': component('Activities'),
    '../../components/ProjectWorkflowPanel': component('Workflow'),
  }).default
  let renderer
  await act(async () => { renderer = create(React.createElement(Page)) })
  const tabs = renderer.root.findByType('Tabs')
  assert.equal(tabs.props.activeTab, 'files')
  assert.ok(renderer.root.findByType('Files'))
  const ellipsis = renderer.root.findByType('Descriptions').props.data.find((item) => item.label === '项目说明').value
  const expand = ellipsis.props.expandRender(false)
  assert.equal(expand.props['aria-expanded'], false)
  assert.equal(expand.props.children, '展开')
  await act(async () => renderer.unmount())
})

test('project workflow exposes only permissioned project-level actions and matches the confirmer type', async () => {
  const calls = []
  const http = {
    put: async (url, body) => { calls.push({ method: 'put', url, body }); return { data: {} } },
    post: async (url, body) => { calls.push({ method: 'post', url, body }); return { data: {} } },
  }
  const Page = loadTs('src/components/ProjectWorkflowPanel.tsx', {
    '@arco-design/web-react': arco,
    '@arco-design/web-react/icon': new Proxy({}, { get: (_, name) => component(name) }),
    '../api/client': http,
    '../api/types': { PROJECT_STATUS: { PENDING_CONFIRMATION: { text: '待确认', color: 'orange' } } },
    '../store/auth': authModule({ id: 1, userType: 'INTERNAL' }, ['project:confirm', 'project:withdraw']),
  }).default
  let renderer
  const changed = []
  await act(async () => {
    renderer = create(React.createElement(Page, {
      project: { id: 1, status: 'PENDING_CONFIRMATION', confirmSide: 'COMPANY', latestSubmitterId: 1 },
      onChanged: () => changed.push(true),
    }))
  })
  assert.ok(renderer.root.findAllByType('Button').some((node) => node.props.children === '确认'))
  assert.ok(renderer.root.findAllByType('Button').some((node) => node.props.children === '驳回'))
  assert.ok(renderer.root.findAllByType('Button').some((node) => node.props.children === '撤回'))

  await act(async () => renderer.update(React.createElement(Page, {
    project: { id: 1, status: 'PENDING_CONFIRMATION', confirmSide: 'SUPPLIER', latestSubmitterId: 2 },
    onChanged: () => changed.push(true),
  })))
  assert.equal(renderer.root.findAllByType('Button').some((node) => node.props.children === '确认'), false)
  assert.equal(renderer.root.findAllByType('Button').some((node) => node.props.children === '驳回'), false)
  assert.equal(renderer.root.findAllByType('Button').some((node) => node.props.children === '撤回'), false)
  await act(async () => renderer.unmount())

  const AllViewPage = loadTs('src/components/ProjectWorkflowPanel.tsx', {
    '@arco-design/web-react': arco,
    '@arco-design/web-react/icon': new Proxy({}, { get: (_, name) => component(name) }),
    '../api/client': http,
    '../api/types': { PROJECT_STATUS: { PENDING_CONFIRMATION: { text: '待确认', color: 'orange' } } },
    '../store/auth': authModule({ id: 1, userType: 'INTERNAL' }, ['project:withdraw', 'project:view_all']),
  }).default
  await act(async () => {
    renderer = create(React.createElement(AllViewPage, {
      project: { id: 1, status: 'PENDING_CONFIRMATION', confirmSide: 'SUPPLIER', latestSubmitterId: 2 },
      onChanged: () => changed.push(true),
    }))
  })
  assert.ok(renderer.root.findAllByType('Button').some((node) => node.props.children === '撤回'))
  await act(async () => renderer.unmount())
})

test('project workflow status commands are limited to start, terminate and restart', async () => {
  const calls = []
  const http = { put: async (url, body) => { calls.push({ url, body }); return { data: {} } }, post: async () => ({ data: {} }) }
  const Page = loadTs('src/components/ProjectWorkflowPanel.tsx', {
    '@arco-design/web-react': arco,
    '@arco-design/web-react/icon': new Proxy({}, { get: (_, name) => component(name) }),
    '../api/client': http,
    '../api/types': { PROJECT_STATUS: { DRAFT: { text: '草稿' }, IN_PROGRESS: { text: '进行中' }, TERMINATED: { text: '已终止' }, COMPLETED: { text: '已完成' } } },
    '../store/auth': authModule({ id: 1, userType: 'INTERNAL' }, ['project:status']),
  }).default
  let renderer
  await act(async () => { renderer = create(React.createElement(Page, { project: { id: 1, status: 'DRAFT' }, onChanged() {} })) })
  await act(async () => renderer.root.findAllByType('Button').find((node) => node.props.children === '开始').props.onClick())
  assert.equal(calls.at(-1).url, '/projects/1/status')
  assert.equal(calls.at(-1).body.status, 'IN_PROGRESS')

  await act(async () => { renderer.update(React.createElement(Page, { project: { id: 1, status: 'IN_PROGRESS' }, onChanged() {} })) })
  const terminate = renderer.root.findAllByType('Popconfirm').find((node) => String(node.props.title).includes('终止项目'))
  await act(async () => terminate.props.onOk())
  assert.equal(calls.at(-1).url, '/projects/1/status')
  assert.equal(calls.at(-1).body.status, 'TERMINATED')

  await act(async () => { renderer.update(React.createElement(Page, { project: { id: 1, status: 'TERMINATED' }, onChanged() {} })) })
  await act(async () => renderer.root.findAllByType('Button').find((node) => node.props.children === '重新开始').props.onClick())
  assert.equal(calls.at(-1).url, '/projects/1/status')
  assert.equal(calls.at(-1).body.status, 'IN_PROGRESS')
  await act(async () => { renderer.update(React.createElement(Page, { project: { id: 1, status: 'COMPLETED' }, onChanged() {} })) })
  assert.equal(renderer.root.findAllByType('Button').some((node) => ['开始', '终止', '重新开始'].includes(node.props.children)), false)
  await act(async () => renderer.unmount())
})

test('project submission sends only the opposite organization as confirmer', async () => {
  const calls = []
  const http = { post: async (url, body) => { calls.push({ url, body }); return { data: {} } } }
  const render = (user) => loadTs('src/components/ProjectWorkflowPanel.tsx', {
    '@arco-design/web-react': arco,
    '@arco-design/web-react/icon': new Proxy({}, { get: (_, name) => component(name) }),
    '../api/client': http,
    '../api/types': { PROJECT_STATUS: { IN_PROGRESS: { text: '进行中' } } },
    '../store/auth': authModule(user, ['project:submit']),
  }).default

  for (const [user, expectedSide, title] of [
    [{ id: 1, userType: 'INTERNAL' }, 'SUPPLIER', '提交给供应商确认？'],
    [{ id: 2, userType: 'SUPPLIER' }, 'COMPANY', '提交给公司确认？'],
  ]) {
    const Page = render(user)
    let renderer
    await act(async () => {
      renderer = create(React.createElement(Page, { project: { id: 1, status: 'IN_PROGRESS' }, onChanged() {} }))
    })
    const submit = renderer.root.findAllByType('Popconfirm').find((node) => node.props.title === title)
    assert.ok(submit, title)
    await act(async () => submit.props.onOk())
    assert.equal(calls.at(-1).url, '/projects/1/submit')
    assert.equal(calls.at(-1).body.confirmSide, expectedSide)
    await act(async () => renderer.unmount())
  }
})

test('project rejection discards a cancelled reason but retains it after a failed submit', async () => {
  let reason = ''
  const form = {
    resetFields() { reason = '' },
    validate: async () => ({ reason }),
  }
  const localArco = new Proxy({
    ...arco,
    Form: Object.assign(component('Form'), { useForm: () => [form], Item: component('Form.Item') }),
  }, { get: (obj, key) => obj[key] ?? component(key) })
  const calls = []
  const Page = loadTs('src/components/ProjectWorkflowPanel.tsx', {
    '@arco-design/web-react': localArco,
    '@arco-design/web-react/icon': new Proxy({}, { get: (_, name) => component(name) }),
    '../api/client': { post: async (url, body) => { calls.push({ url, body }); throw new Error('offline') } },
    '../api/types': { PROJECT_STATUS: { PENDING_CONFIRMATION: { text: '待确认' } } },
    '../store/auth': authModule({ id: 1, userType: 'INTERNAL' }, ['project:confirm']),
  }).default
  let renderer
  await act(async () => {
    renderer = create(React.createElement(Page, {
      project: { id: 17, status: 'PENDING_CONFIRMATION', confirmSide: 'COMPANY' },
      onChanged() { assert.fail('a cancelled or failed rejection must not report success') },
    }))
  })
  const open = () => renderer.root.findAllByType('Button').find((node) => node.props.children === '驳回').props.onClick()
  const modal = () => renderer.root.findByType('Modal')
  await act(async () => open())
  reason = 'cancelled draft'
  await act(async () => modal().props.onCancel())
  assert.equal(modal().props.visible, false)
  await act(async () => open())
  assert.equal(reason, '', 'reopening must not reuse a cancelled rejection reason')
  assert.equal(calls.length, 0)
  reason = 'keep this reason while retrying'
  await act(async () => assert.rejects(modal().props.onOk(), /offline/))
  assert.equal(modal().props.visible, true)
  assert.equal(reason, 'keep this reason while retrying')
  assert.equal(calls.length, 1)
  assert.equal(calls[0].url, '/projects/17/reject')
  assert.equal(calls[0].body.reason, reason)
  await act(async () => modal().props.onCancel())
  assert.equal(reason, '')
  await act(async () => renderer.unmount())
})

test('workflow actions ignore duplicate invocations while validation or the request is pending', async () => {
  for (const action of ['start', 'submit', 'confirm', 'reject', 'withdraw']) {
    const calls = []
    let settle
    let validate
    let resets = 0
    const form = {
      resetFields() { resets += 1 },
      validate: () => new Promise((resolve) => { validate = resolve }),
    }
    const localArco = new Proxy({
      ...arco,
      Form: Object.assign(component('Form'), { useForm: () => [form], Item: component('Form.Item') }),
    }, { get: (obj, key) => obj[key] ?? component(key) })
    const request = (url) => { calls.push(url); return new Promise((resolve) => { settle = resolve }) }
    const Page = loadTs('src/components/ProjectWorkflowPanel.tsx', {
      '@arco-design/web-react': localArco,
      '@arco-design/web-react/icon': new Proxy({}, { get: (_, name) => component(name) }),
      '../api/client': { post: request, put: request },
      '../api/types': { PROJECT_STATUS: {} },
      '../store/auth': authModule({ id: 1, userType: 'INTERNAL' }, ['project:status', 'project:submit', 'project:confirm', 'project:withdraw']),
    }).default
    let renderer
    let changed = 0
    await act(async () => {
      renderer = create(React.createElement(Page, {
        project: { id: 17, status: action === 'start' ? 'DRAFT' : action === 'submit' ? 'IN_PROGRESS' : 'PENDING_CONFIRMATION', confirmSide: 'COMPANY', latestSubmitterId: 1 },
        onChanged() { changed += 1 },
      }))
    })
    const modal = () => renderer.root.findByType('Modal')
    let invoke
    if (action === 'start') invoke = renderer.root.findAllByType('Button').find((node) => node.props.children === '开始').props.onClick
    else if (action === 'reject') {
      await act(async () => renderer.root.findAllByType('Button').find((node) => node.props.children === '驳回').props.onClick())
      invoke = modal().props.onOk
    } else {
      const prefix = { submit: '提交给', confirm: '确认通过', withdraw: '撤回本次' }[action]
      invoke = renderer.root.findAllByType('Popconfirm').find((node) => String(node.props.title).startsWith(prefix)).props.onOk
    }
    let first
    let second
    await act(async () => { first = invoke(); second = invoke(); await Promise.resolve() })
    if (action === 'reject') {
      await act(async () => modal().props.onCancel())
      assert.equal(modal().props.visible, true, 'cancel must not close a rejection while validation is in flight')
      assert.equal(resets, 0, 'pending validation must retain the submitted draft')
      await act(async () => { validate({ reason: 'review reason' }); await Promise.resolve() })
    }
    assert.equal(calls.length, 1, `${action} must send only one request`)
    await act(async () => { settle({ data: {} }); await Promise.all([first, second]) })
    assert.equal(changed, 1)
    await act(async () => renderer.unmount())
  }
})

test('project pages contain no round entry points or project round selectors', () => {
  const root = path.resolve(__dirname, '..')
  const files = [
    'src/pages/project/ProjectDetail.tsx',
    'src/pages/project/ProjectList.tsx',
    'src/components/FileTable.tsx',
    'src/components/MessagePanel.tsx',
    'src/components/ChunkUploader.tsx',
    'src/components/ProjectActivityPanel.tsx',
  ]
  for (const file of files) {
    const source = fs.readFileSync(path.join(root, file), 'utf8')
    assert.doesNotMatch(source, /轮次|roundId|roundNo|pendingRounds|RoundPanel/, file)
  }
})

function activityRow(id, type = 'PROJECT', overrides = {}) {
  return {
    id,
    type,
    action: 'CREATED',
    actorName: `操作人${id}`,
    occurredAt: `2026-09-09T00:00:${String(id).padStart(2, '0')}Z`,
    title: type === 'PROJECT' ? '项目动作' : `${type}动作`,
    summary: type === 'PROJECT' ? '项目摘要' : `${type}对象`,
    targetId: id,
    targetAvailable: type !== 'PROJECT',
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
        summary: { status: 'IN_PROGRESS', pendingConfirmation: true, confirmSide: 'COMPANY', lastActivityAt: '2026-09-09T00:00:01Z' },
    },
  })
  let renderer
  await act(async () => { renderer = create(React.createElement(Page, { projectId: 1, active: false })) })
  assert.equal(pending.length, 0, 'inactive activity tab must not request')
  await act(async () => { renderer.update(React.createElement(Page, { projectId: 1, active: true })) })
  assert.deepEqual(JSON.parse(JSON.stringify(pending[0].params)), { pageSize: 20 })

  const first = activityRow(1)
  const filtered = activityRow(2, 'PROJECT', { summary: '<驳回原因>', title: '<驳回项目>', action: 'REJECT', targetAvailable: false })
  await act(async () => {
    renderer.root.findByType('Select').props.onChange('PROJECT')
    await Promise.resolve()
  })
  assert.deepEqual(JSON.parse(JSON.stringify(pending[1].params)), { pageSize: 20, type: 'PROJECT' })
  await act(async () => pending[1].resolve(response([filtered])))
  await act(async () => pending[0].resolve(response([first], 'late-cursor')))
  const renderedRows = renderer.root.findAll((node) => node.props['data-activity-type'] !== undefined)
  assert.deepEqual(renderedRows.map((node) => node.props['data-activity-type']), ['PROJECT'])
  assert.equal(renderer.root.findAll((node) => node.props.dangerouslySetInnerHTML !== undefined).length, 0)
  assert.ok(JSON.stringify(renderer.toJSON()).includes('<驳回原因>'), 'server text must remain text content')
  assert.ok(renderer.root.findAll((node) => node.props['data-activity-id'] === 2)[0])
  assert.ok(renderer.root.findAll((node) => node.props.dateTime === filtered.occurredAt).length > 0, 'each event must expose its occurrence time')

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
          summary: { status: 'IN_PROGRESS', pendingConfirmation: false, confirmSide: null, lastActivityAt: null },
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
            : [activityRow(1, 'PROJECT', { summary: '驳回原因：需要补充资料', action: 'REJECT' })],
          nextCursor: config.params.cursor ? null : 'cursor-1',
          summary: { status: 'IN_PROGRESS', pendingConfirmation: true, confirmSide: 'SUPPLIER', lastActivityAt: '2026-09-09T00:00:01Z' },
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
  assert.equal(renderer.root.findAllByProps({ 'aria-label': '查看图纸.pdf' }).length, 0, 'unavailable file target must not be a link')
  assert.equal(renderer.root.findAllByProps({ 'aria-label': '查看项目摘要' }).length, 0, 'project target must never be a link')
  const messageTargetLinks = renderer.root.findAllByProps({ 'aria-label': '查看留言内容' })
  assert.ok(messageTargetLinks.length > 0, 'available message target should be a link')
  await act(async () => messageTargetLinks[0].props.onClick())
  assert.deepEqual(navigated, [['MESSAGE', 4]])
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
    '../../api/client': { get: async (url) => ({ data: url.endsWith('/summary') ? { unreadMessages: 0 } : project }) },
    '../../api/types': { PROJECT_STATUS: { IN_PROGRESS: { text: '进行中', color: 'blue' } }, fmtTime: String },
    '../../components/FileTable': component('Files'),
    '../../components/MessagePanel': component('Messages'),
    '../../components/MemberPanel': component('Members'),
    '../../components/ProjectActivityPanel': component('Activities'),
    '../../components/ProjectWorkflowPanel': component('Workflow'),
  }).default
  let renderer
  await act(async () => { renderer = create(React.createElement(Page)) })
  assert.equal(renderer.root.findByType('Tabs').props.activeTab, 'activity')
  const activities = renderer.root.findByType('Activities')
  assert.equal(activities.props.active, true)
  await act(async () => activities.props.onNavigate('FILE', 7))
  assert.equal(updates.at(-1).get('tab'), 'files')
  assert.equal(updates.at(-1).get('target'), '7')

  await act(async () => renderer.update(React.createElement(Page)))
  assert.equal(renderer.root.findByType('Tabs').props.activeTab, 'files')
  assert.equal(renderer.root.findByType('Files').props.targetId, 7)
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
  await act(async()=>{renderer=create(React.createElement(Page,{projectId:1,projectStatus:'IN_PROGRESS'}))})
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
  let appendFailures=2
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
  await act(async()=>{renderer=create(React.createElement(Page,{projectId:1,projectStatus:'IN_PROGRESS'}))})
  let more=renderer.root.findAllByType('Button').find(n=>JSON.stringify(n.props.children)?.includes('加载更多'))
  const visibleIds=()=>renderer.root.findAll(n=>n.props['data-message-id']!==undefined).map(n=>n.props['data-message-id'])
  const retry=()=>renderer.root.findAllByType('Button').find(n=>n.props.children==='重试')
  await act(async()=>assert.doesNotReject(more.props.onClick(), 'a handled page failure must not escape the UI event handler'))
  assert.deepEqual(visibleIds(),rows.slice(0,20).map(r=>r.id),'failed append keeps the first page intact')
  assert.ok(retry(),'the failure must expose an explicit retry action')
  await act(async()=>assert.doesNotReject(retry().props.onClick(), 'a failed retry must also stay handled'))
  assert.deepEqual(visibleIds(),rows.slice(0,20).map(r=>r.id))
  await act(async()=>retry().props.onClick())
  assert.equal(retry(),undefined,'successful retry clears the failure state')
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

test('deleting the only row on a controlled last page reloads the last page allowed by the response total', async () => {
  const iconMock = new Proxy({}, { get: (_, name) => component(name) })
  const cases = [
    {
      label: 'projects', page: 'src/pages/project/ProjectList.tsx', pageSize: 10,
      row: { id: 91, name: '末页项目', status: 'DRAFT' },
      props: {},
      mocks: (http) => ({
        '@arco-design/web-react': arco, '@arco-design/web-react/icon': iconMock,
        'react-router-dom': { Link: component('Link'), useNavigate: () => () => {} },
        '../../api/client': http,
        '../../store/auth': authModule({ id: 1, userType: 'INTERNAL' }, ['project:update', 'project:delete']),
        '../../api/types': { PROJECT_STATUS: { DRAFT: { text: '草稿' } }, fmtTime: String },
        '../../components/ActionSlots': actionSlotsModule,
      }),
    },
    {
      label: 'users', page: 'src/pages/org/UserList.tsx', pageSize: 10,
      row: { id: 91, employeeNo: 'last-user', realName: '末页用户', email: 'last@example.invalid', status: 'ACTIVE', createdAt: '' },
      props: {},
      mocks: (http) => ({
        '@arco-design/web-react': arco, '@arco-design/web-react/icon': iconMock,
        '../../api/client': http,
        '../../store/auth': authModule({ id: 1, userType: 'INTERNAL' }, ['user:manage', 'user:delete']),
        '../../api/types': { fmtTime: String }, '../../components/ActionSlots': actionSlotsModule,
      }),
    },
    {
      label: 'suppliers', page: 'src/pages/supplier/SupplierList.tsx', pageSize: 10,
      row: { id: 91, name: '末页供应商', status: 'ACTIVE', createdAt: '' },
      props: {},
      mocks: (http) => ({
        '@arco-design/web-react': arco, '@arco-design/web-react/icon': iconMock,
        '../../api/client': http,
        '../../store/auth': authModule({ id: 1, userType: 'INTERNAL' }, ['supplier:manage', 'supplier:delete']),
        '../../api/types': { fmtTime: String }, '../../components/ActionSlots': actionSlotsModule,
      }),
    },
    {
      label: 'roles', page: 'src/pages/rbac/RoleList.tsx', pageSize: 20,
      row: { id: 91, name: '末页角色', isBuiltIn: false, permissionIds: [], assignedUserCount: 0, status: 'ACTIVE' },
      props: {},
      mocks: (http) => ({
        '@arco-design/web-react': arco, '@arco-design/web-react/icon': iconMock,
        '../../api/client': http,
        '../../store/auth': authModule({ id: 1, userType: 'INTERNAL' }, ['role:manage', 'role:delete']),
        '../../api/types': { PageResp: {} }, '../../components/ActionSlots': actionSlotsModule,
      }),
    },
    {
      label: 'files', page: 'src/components/FileTable.tsx', pageSize: 10,
      row: { id: 91, originalName: '末页文件.pdf', ext: 'pdf', sizeBytes: 1, direction: 'C2S', createdAt: '', canDelete: true },
      props: { projectId: 1, projectStatus: 'IN_PROGRESS' },
      mocks: (http) => ({
        '@arco-design/web-react': arco, '@arco-design/web-react/icon': iconMock,
        '../api/client': http, '../store/auth': authModule({ id: 1, userType: 'INTERNAL' }, ['file:delete']),
        '../api/types': { fmtSize: String, fmtTime: String }, '../components/ActionSlots': actionSlotsModule,
        './ActionSlots': actionSlotsModule, './ChunkUploader': component('Uploader'), './PdfPreview': component('PDF'),
      }),
    },
  ]

  for (const item of cases) {
    let deleted = false
    const pages = []
    const http = {
      get: async (url, config = {}) => {
        if (url === '/supplier-options' || url === '/departments' || url === '/admin/user-role-options' || url === '/permissions') return { data: [] }
        const requested = Number(config.params?.page || 1)
        pages.push(requested)
        const total = deleted ? item.pageSize : item.pageSize + 1
        const list = requested === 2 ? (deleted ? [] : [item.row]) : [item.row]
        return { data: { list, total, page: requested, pageSize: item.pageSize } }
      },
      delete: async () => { deleted = true; return { data: {} } },
    }
    const Page = loadTs(item.page, item.mocks(http)).default
    let renderer
    await act(async () => { renderer = create(React.createElement(Page, item.props)) })
    await act(async () => renderer.root.findByType('Table').props.pagination.onChange(2, item.pageSize))
    const table = renderer.root.findByType('Table')
    const actions = table.props.columns.at(-1).render(null, item.row)
    const confirm = findElement(actions, (node) => typeof node.props.onOk === 'function' && String(node.props.title).includes('删除'))
    assert.ok(confirm, `${item.label} must expose the fixture delete action`)
    await act(async () => confirm.props.onOk())
    assert.equal(renderer.root.findByType('Table').props.pagination.current, 1, `${item.label} must correct controlled current`)
    assert.equal(pages.at(-1), 1, `${item.label} must fetch the valid page after observing the new total`)
    await act(async () => renderer.unmount())
  }
})

test('message receipt popover distinguishes failure, retries, and ignores an older response', async () => {
  const pending = []
  const message = { id: 7, senderId: 2, senderName: '留言人', senderType: 'INTERNAL', content: '测试', createdAt: '', readCount: 0, totalCount: 1, readByMe: true }
  const http = { get: async (url) => {
    if (url.includes('/projects/')) return { data: { list: [message], total: 1 } }
    return new Promise((resolve, reject) => pending.push({ url, resolve, reject }))
  } }
  const Page = loadTs('src/components/MessagePanel.tsx', {
    '@arco-design/web-react': arco, '@arco-design/web-react/icon': new Proxy({}, { get: (_, name) => component(name) }),
    '../api/client': http, '../store/auth': authModule({ id: 1, userType: 'INTERNAL' }, []), '../api/types': { fmtTime: String },
  }).default
  let pageRenderer
  await act(async () => { pageRenderer = create(React.createElement(Page, { projectId: 1, projectStatus: 'IN_PROGRESS' })) })
  const receiptElement = React.Children.toArray(pageRenderer.root.findByType('Popover').props.content.props.children)[1]
  const Receipt = receiptElement.type
  let renderer
  await act(async () => { renderer = create(React.createElement(Receipt, { id: 7 })) })
  await act(async () => { pending[0].reject(new Error('reads unavailable')); await Promise.resolve() })
  assert.match(JSON.stringify(renderer.toJSON()), /回执加载失败/)
  assert.doesNotMatch(JSON.stringify(renderer.toJSON()), /暂无/)
  const retry = renderer.root.findAllByType('Button').find((node) => node.props.children === '重试')
  assert.ok(retry)
  await act(async () => { retry.props.onClick(); await Promise.resolve() })
  await act(async () => { renderer.update(React.createElement(Receipt, { id: 8 })); await Promise.resolve() })
  await act(async () => pending[1].resolve({ data: { readers: [{ realName: '旧回执' }] } }))
  assert.doesNotMatch(JSON.stringify(renderer.toJSON()), /旧回执/)
  await act(async () => pending[2].resolve({ data: { readers: [{ realName: '新回执' }] } }))
  assert.match(JSON.stringify(renderer.toJSON()), /新回执/)
  await act(async () => renderer.unmount())
  await act(async () => pageRenderer.unmount())
})

test('required option sources expose loading and retry states and block submits until ready', async () => {
  const iconMock = new Proxy({}, { get: (_, name) => component(name) })

  {
    let posted = 0
    let optionAttempt = 0
    let rejectFirst
    const form = { resetFields() {}, validate: async () => ({ name: '项目', supplierId: 8 }) }
    const testArco = new Proxy({
      Form: Object.assign(component('Form'), { useForm: () => [form], Item: component('Form.Item') }),
      Typography: arco.Typography, Message: arco.Message,
    }, { get: (obj, key) => obj[key] ?? component(key) })
    const http = {
      get: async (url) => {
        if (url === '/projects') return { data: { list: [], total: 0, page: 1, pageSize: 10 } }
        optionAttempt++
        if (optionAttempt === 1) return new Promise((_resolve, reject) => { rejectFirst = reject })
        return { data: [{ id: 8, name: '可用供应商' }] }
      },
      post: async () => { posted++ },
    }
    const Page = loadTs('src/pages/project/ProjectList.tsx', {
      '@arco-design/web-react': testArco, '@arco-design/web-react/icon': iconMock,
      'react-router-dom': { Link: component('Link'), useNavigate: () => () => {} },
      '../../api/client': http, '../../store/auth': authModule({ id: 1, userType: 'INTERNAL' }, ['project:create']),
      '../../api/types': { PROJECT_STATUS: {}, fmtTime: String }, '../../components/ActionSlots': actionSlotsModule,
    }).default
    let renderer
    await act(async () => { renderer = create(React.createElement(Page)) })
    const createButton = renderer.root.findAllByType('Button').find((node) => node.props.children === '新建项目')
    assert.equal(createButton.props.disabled, true, 'project creation must wait for supplier options')
    await act(async () => { rejectFirst(new Error('supplier options unavailable')); await Promise.resolve() })
    assert.ok(renderer.root.findAllByType('Button').some((node) => String(node.props.children).includes('重试')))
    const retry = renderer.root.findAllByType('Button').find((node) => String(node.props.children).includes('重试'))
    await act(async () => retry.props.onClick())
    assert.equal(optionAttempt, 2)
    assert.equal(renderer.root.findAllByType('Button').find((node) => node.props.children === '新建项目').props.disabled, false)
    await act(async () => renderer.root.findAllByType('Button').find((node) => node.props.children === '新建项目').props.onClick())
    await act(async () => renderer.root.findByType('Modal').props.onOk())
    assert.equal(posted, 1)
    await act(async () => renderer.unmount())
  }

  {
    let posted = 0
    let optionRound = 0
    const first = []
    const form = { resetFields() {}, setFieldsValue() {}, validate: async () => ({ employeeNo: 'new_user', password: '123456', realName: '新用户', email: 'new@example.invalid', roleId: 3 }) }
    const pwdForm = { resetFields() {}, validate: async () => ({ newPassword: '123456' }) }
    let formIndex = 0
    const testArco = new Proxy({
      Form: Object.assign(component('Form'), { useForm: () => [formIndex++ % 2 === 0 ? form : pwdForm], Item: component('Form.Item') }),
      Typography: arco.Typography, Message: arco.Message,
    }, { get: (obj, key) => obj[key] ?? component(key) })
    const http = {
      get: async (url) => {
        if (url === '/admin/users') return { data: { list: [], total: 0, page: 1, pageSize: 10 } }
        if (optionRound === 0) return new Promise((resolve, reject) => first.push({ resolve, reject }))
        return { data: url === '/departments' ? [] : [{ id: 3, name: '可用角色' }] }
      },
      post: async () => { posted++ },
    }
    const Page = loadTs('src/pages/org/UserList.tsx', {
      '@arco-design/web-react': testArco, '@arco-design/web-react/icon': iconMock,
      '../../api/client': http, '../../store/auth': authModule({ id: 1, userType: 'INTERNAL' }, ['user:manage']),
      '../../api/types': { fmtTime: String }, '../../components/ActionSlots': actionSlotsModule,
    }).default
    let renderer
    await act(async () => { renderer = create(React.createElement(Page)) })
    const createButton = renderer.root.findAllByType('Button').find((node) => node.props.children === '新增用户')
    assert.equal(createButton.props.disabled, true, 'user creation must wait for role options')
    optionRound = 1
    await act(async () => { first.forEach((request) => request.reject(new Error('user options unavailable'))); await Promise.resolve() })
    const retry = renderer.root.findAllByType('Button').find((node) => String(node.props.children).includes('重试'))
    assert.ok(retry)
    await act(async () => retry.props.onClick())
    assert.equal(renderer.root.findAllByType('Button').find((node) => node.props.children === '新增用户').props.disabled, false)
    await act(async () => renderer.root.findAllByType('Button').find((node) => node.props.children === '新增用户').props.onClick())
    await act(async () => renderer.root.findAllByType('Modal').find((node) => node.props.visible).props.onOk())
    assert.equal(posted, 1)
    await act(async () => renderer.unmount())
  }

  {
    const role = { id: 7, name: '测试角色', permissionIds: [1], assignedUserCount: 0, isBuiltIn: false, status: 'ACTIVE' }
    let permissionRound = 0
    let rejectPermissions
    const http = { get: async (url) => {
      if (url === '/admin/roles') return { data: { list: [role], total: 1, page: 1, pageSize: 20 } }
      permissionRound++
      if (permissionRound === 1) return new Promise((_resolve, reject) => { rejectPermissions = reject })
      return { data: [{ id: 1, name: '菜单', type: 'MENU', parentId: null }] }
    } }
    const Page = loadTs('src/pages/rbac/RoleList.tsx', {
      '@arco-design/web-react': arco, '@arco-design/web-react/icon': iconMock,
      '../../api/client': http, '../../store/auth': authModule({ id: 1, userType: 'INTERNAL' }, ['role:manage']),
      '../../api/types': { PageResp: {} }, '../../components/ActionSlots': actionSlotsModule,
    }).default
    let renderer
    await act(async () => { renderer = create(React.createElement(Page)) })
    const actions = renderer.root.findByType('Table').props.columns.at(-1).render(null, role)
    await act(async () => findActionButton(actions, '分配权限').props.onClick())
    const savePermissions = () => findActionButton(renderer.root.findByType('Drawer').props.footer, '保存权限')
    assert.equal(savePermissions().props.disabled, true)
    await act(async () => { rejectPermissions(new Error('permissions unavailable')); await Promise.resolve() })
    const retry = renderer.root.findAllByType('Button').find((node) => String(node.props.children).includes('重试'))
    assert.ok(retry)
    await act(async () => retry.props.onClick())
    assert.equal(permissionRound, 2)
    assert.equal(savePermissions().props.disabled, false)
    await act(async () => renderer.unmount())
  }
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

test('project workflow audit actions expose precise labels and state or rejection summaries', async () => {
  const row = {
    id: 201,
    action: 'PROJECT_REJECT',
    targetType: 'project',
    targetId: '7',
    detail: { from: 'PENDING_CONFIRMATION', to: 'IN_PROGRESS', reason: '需要补充资料' },
    createdAt: '2026-09-09T00:00:00Z',
  }
  const Page = loadTs('src/pages/system/AuditLog.tsx', {
    '@arco-design/web-react': arco,
    '@arco-design/web-react/icon': new Proxy({}, { get: (_, name) => component(name) }),
    '../../api/client': { get: async () => ({ data: { list: [row], total: 1, page: 1, pageSize: 20 } }) },
    '../../store/auth': authModule({ id: 1, userType: 'INTERNAL' }, ['log:view']),
    '../../api/types': { fmtTime: String },
  }).default
  let renderer
  await act(async () => { renderer = create(React.createElement(Page)) })
  const table = renderer.root.findByType('Table')
  const action = table.props.columns.find((column) => column.dataIndex === 'action').render(row.action, row)
  assert.match(JSON.stringify(action), /驳回项目/)
  const summary = table.props.columns.find((column) => column.title === '内容摘要').render(null, row)
  assert.match(JSON.stringify(summary), /待确认/)
  assert.match(JSON.stringify(summary), /进行中/)
  assert.match(JSON.stringify(summary), /驳回原因：需要补充资料/)

  const actionSelect = renderer.root.findAllByType('Select').find((select) => select.props.placeholder === '具体操作')
  const values = React.Children.toArray(actionSelect.props.children).map((option) => option.props.value)
  for (const value of ['PROJECT_START', 'PROJECT_SUBMIT', 'PROJECT_CONFIRM', 'PROJECT_REJECT', 'PROJECT_WITHDRAW', 'PROJECT_TERMINATE', 'PROJECT_RESTART']) {
    assert.ok(values.includes(value), `项目日志筛选缺少 ${value}`)
  }
  assert.ok(!values.includes('PROJECT_STATUS'))
  await act(async () => renderer.unmount())
})

test('audit hard-delete controls require the dedicated delete permission', async () => {
  const row = { id: 101, action: 'LOGIN', createdAt: '2026-09-09T00:00:00Z' }
  const cleanupAudit = { id: 102, action: 'AUDIT_LOG_DELETE', createdAt: '2026-09-09T00:00:01Z' }
  for (const [label, user, permissions, expected] of [
    ['ordinary log viewer', { id: 1, userType: 'INTERNAL', isSystemAdmin: false }, ['log:view'], false],
    ['system admin without delete permission', { id: 1, userType: 'INTERNAL', isSystemAdmin: true }, ['log:view'], false],
    ['delegated delete permission', { id: 1, userType: 'INTERNAL', isSystemAdmin: false }, ['log:view', 'log:delete'], true],
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
      const response = { data: { list: pageData(params), total: params.page === 2 ? 23 : pageData(params).length, page: params.page, pageSize: params.pageSize } }
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
    '../../store/auth': authModule({ id: 1, userType: 'INTERNAL', isSystemAdmin: true }, ['log:view', 'log:delete']),
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
  await act(async()=>{renderer=create(React.createElement(Uploader,{projectId:1,visible:true,onClose(){},onDone(){}}))})
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
  await act(async()=>{renderer=create(React.createElement(Uploader,{projectId:1,visible:true,onClose(){closed++},onDone(){done++}}))})
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
  await act(async()=>{renderer=create(React.createElement(Uploader,{projectId:1,visible:true,onClose(){closed++},onDone(){done++}}))})
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
  await act(async()=>{renderer=create(React.createElement(Uploader,{projectId:1,visible:true,onClose(){closed++},onDone(){}}))})
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

test('message detail replaces stale content with loading, failure and retry states for the requested message', async () => {
  const messages = [
    { id: 1, senderId: 1, senderName: '我', senderType: 'INTERNAL', content: 'A', readCount: 1, totalCount: 1, readByMe: true, createdAt: '' },
    { id: 2, senderId: 1, senderName: '我', senderType: 'INTERNAL', content: 'B', readCount: 1, totalCount: 1, readByMe: true, createdAt: '' },
  ]
  let bAttempts = 0
  let rejectFirstB
  const Page = loadTs('src/components/MessagePanel.tsx', {
    '@arco-design/web-react': arco,
    '@arco-design/web-react/icon': new Proxy({}, { get: (_, name) => component(name) }),
    '../api/client': {
      get: async (url) => {
        if (url === '/projects/1/messages') return { data: { list: messages, total: messages.length } }
        if (url === '/messages/1/reads') {
          return { data: { readers: [{ userId: 11, realName: '读者 A', userType: 'INTERNAL' }], unread: [] } }
        }
        bAttempts++
        if (bAttempts === 1) return new Promise((_resolve, reject) => { rejectFirstB = reject })
        return { data: { readers: [{ userId: 22, realName: '读者 B', userType: 'INTERNAL' }], unread: [] } }
      },
    },
    '../store/auth': authModule({ id: 1, userType: 'INTERNAL' }, []),
    '../api/types': { fmtTime: String },
  }).default
  let renderer
  await act(async () => { renderer = create(React.createElement(Page, { projectId: 1, projectStatus: 'IN_PROGRESS' })) })
  const detailButtons = () => renderer.root.findAllByType('Button').filter((node) => node.props.children === '回执详情')

  await act(async () => detailButtons()[0].props.onClick())
  let drawer = renderer.root.findByType('Drawer')
  assert.equal(drawer.findAllByType('List')[0].props.dataSource[0].realName, '读者 A')

  let firstB
  await act(async () => {
    firstB = detailButtons()[1].props.onClick()
    await Promise.resolve()
  })
  drawer = renderer.root.findByType('Drawer')
  assert.equal(drawer.findAllByType('List').length, 0, 'requesting B must immediately remove A receipt content')
  assert.ok(drawer.findAllByType('Text').some((node) => node.props.children === '回执加载中…'))
  await act(async () => { rejectFirstB(new Error('receipt unavailable')); await firstB })
  drawer = renderer.root.findByType('Drawer')
  assert.equal(drawer.props.visible, true)
  assert.equal(drawer.findAllByType('List').length, 0, 'B failure must not restore A receipt content')
  assert.ok(drawer.findAllByType('Text').some((node) => node.props.children === '回执加载失败'))
  assert.equal(drawer.findAllByType('Text').some((node) => node.props.children === '回执加载中…'), false)
  const retry = drawer.findAllByType('Button').find((node) => node.props.children === '重试')
  assert.ok(retry)
  await act(async () => retry.props.onClick())
  assert.equal(renderer.root.findByType('Drawer').findAllByType('List')[0].props.dataSource[0].realName, '读者 B')
  await act(async () => renderer.unmount())
})

test('message detail ignores an older rejection and never reopens after close', async () => {
  const messages = [
    { id: 1, senderId: 1, senderName: '我', senderType: 'INTERNAL', content: 'A', readCount: 1, totalCount: 1, readByMe: true, createdAt: '' },
    { id: 2, senderId: 1, senderName: '我', senderType: 'INTERNAL', content: 'B', readCount: 1, totalCount: 1, readByMe: true, createdAt: '' },
  ]
  const pending = []
  const Page = loadTs('src/components/MessagePanel.tsx', {
    '@arco-design/web-react': arco,
    '@arco-design/web-react/icon': new Proxy({}, { get: (_, name) => component(name) }),
    '../api/client': { get: async (url) => {
      if (url === '/projects/1/messages') return { data: { list: messages, total: messages.length } }
      return new Promise((resolve, reject) => pending.push({ url, resolve, reject }))
    } },
    '../store/auth': authModule({ id: 1, userType: 'INTERNAL' }, []),
    '../api/types': { fmtTime: String },
  }).default
  let renderer
  await act(async () => { renderer = create(React.createElement(Page, { projectId: 1, projectStatus: 'IN_PROGRESS' })) })
  const detailButtons = () => renderer.root.findAllByType('Button').filter((node) => node.props.children === '回执详情')
  let oldA
  let currentB
  await act(async () => {
    oldA = detailButtons()[0].props.onClick()
    currentB = detailButtons()[1].props.onClick()
    await Promise.resolve()
  })
  await act(async () => { pending[1].resolve({ data: { readers: [{ userId: 22, realName: '读者 B', userType: 'INTERNAL' }], unread: [] } }); await currentB })
  await act(async () => { pending[0].reject(new Error('late A failure')); await oldA })
  let drawer = renderer.root.findByType('Drawer')
  assert.equal(drawer.findAllByType('List')[0].props.dataSource[0].realName, '读者 B')

  let closingRequest
  await act(async () => { closingRequest = detailButtons()[0].props.onClick(); await Promise.resolve() })
  drawer = renderer.root.findByType('Drawer')
  await act(async () => drawer.props.onCancel())
  await act(async () => { pending[2].reject(new Error('failure after close')); await closingRequest })
  assert.equal(renderer.root.findByType('Drawer').props.visible, false)
  await act(async () => renderer.unmount())
})

test('message pagination stops at the response total for exact 20 and 40 item totals', async () => {
  const makeMessages = (start, count) => Array.from({ length: count }, (_, offset) => ({
    id: start + offset, senderId: 2, senderName: '成员', senderType: 'INTERNAL', content: '留言', readCount: 0, totalCount: 1, readByMe: true, createdAt: '',
  }))
  for (const total of [20, 40]) {
    let calls = 0
    const Page = loadTs('src/components/MessagePanel.tsx', {
      '@arco-design/web-react': arco,
      '@arco-design/web-react/icon': new Proxy({}, { get: (_, name) => component(name) }),
      '../api/client': { get: async () => {
        calls++
        return { data: { list: makeMessages((calls - 1) * 20 + 1, 20), total } }
      } },
      '../store/auth': authModule({ id: 1, userType: 'INTERNAL' }, []),
      '../api/types': { fmtTime: String },
    }).default
    let renderer
    await act(async () => { renderer = create(React.createElement(Page, { projectId: 1, projectStatus: 'IN_PROGRESS' })) })
    const loadMore = () => renderer.root.findAllByType('Button').find((node) => String(node.props.children).startsWith('加载更多'))
    if (total === 20) {
      assert.equal(loadMore(), undefined, 'a full first page equal to total must not advertise another page')
    } else {
      assert.ok(loadMore(), 'a 40-item total needs one additional page after the first 20')
      await act(async () => loadMore().props.onClick())
      assert.equal(loadMore(), undefined, 'two full pages equal to total must not advertise a third page')
    }
    await act(async () => renderer.unmount())
  }
})

test('message cursor pagination keeps loading after concurrent deletion shrinks total', async () => {
  const makeMessages = (start, count) => Array.from({ length: count }, (_, offset) => ({
    id: start + offset, senderId: 2, senderName: '成员', senderType: 'INTERNAL', content: '历史留言', readCount: 0, totalCount: 1, readByMe: true, createdAt: '',
  }))
  const pages = [
    { list: makeMessages(1, 20), total: 41 },
    { list: makeMessages(21, 20), total: 31 },
    { list: makeMessages(41, 1), total: 31 },
  ]
  let call = 0
  const Page = loadTs('src/components/MessagePanel.tsx', {
    '@arco-design/web-react': arco,
    '@arco-design/web-react/icon': new Proxy({}, { get: (_, name) => component(name) }),
    '../api/client': { get: async () => ({ data: pages[call++] }) },
    '../store/auth': authModule({ id: 1, userType: 'INTERNAL' }, []),
    '../api/types': { fmtTime: String },
  }).default
  let renderer
  await act(async () => { renderer = create(React.createElement(Page, { projectId: 1, projectStatus: 'IN_PROGRESS' })) })
  const loadMore = () => renderer.root.findAllByType('Button').find((node) => String(node.props.children).startsWith('加载更多'))
  await act(async () => loadMore().props.onClick())
  assert.ok(loadMore(), 'a full cursor page must remain loadable when the server total changed during pagination')
  await act(async () => loadMore().props.onClick())
  assert.equal(renderer.root.findAll((node) => node.props.className === 'msg-item').length, 41)
  assert.equal(loadMore(), undefined, 'the final short cursor page must terminate pagination')
  await act(async () => renderer.unmount())
})

test('audit deletion from the only row on the last page corrects the controlled page', async () => {
  const row = { id: 21, action: 'LOGIN', createdAt: '' }
  let deleted = false
  const pages = []
  const Page = loadTs('src/pages/system/AuditLog.tsx', {
    '@arco-design/web-react': arco,
    '@arco-design/web-react/icon': new Proxy({}, { get: (_, name) => component(name) }),
    '../../api/client': {
      get: async (_url, { params }) => {
        pages.push(params.page)
        return { data: { list: params.page === 2 && !deleted ? [row] : [], total: deleted ? 20 : 21, page: params.page, pageSize: 20 } }
      },
      delete: async () => { deleted = true; return { data: { deleted: 1 } } },
    },
    '../../store/auth': authModule({ id: 1, userType: 'INTERNAL' }, ['log:view', 'log:delete']),
    '../../api/types': { fmtTime: String },
  }).default
  let renderer
  await act(async () => { renderer = create(React.createElement(Page)) })
  await act(async () => renderer.root.findByType('Table').props.pagination.onChange(2, 20))
  const actions = renderer.root.findByType('Table').props.columns.at(-1).render(null, row)
  const confirm = findElement(actions, (node) => node.props.title === '确认删除这条日志？')
  await act(async () => confirm.props.onOk())
  assert.equal(renderer.root.findByType('Table').props.pagination.current, 1)
  assert.deepEqual(pages.slice(-2), [2, 1], 'the invalid page response must trigger a fetch for the remaining last page')
  await act(async () => renderer.unmount())
})

test('batch download uses a synchronous in-flight latch against repeated clicks', async () => {
  let posts = 0
  let release
  const response = new Promise((resolve) => { release = resolve })
  const Page = loadTs('src/components/FileTable.tsx', {
    '@arco-design/web-react': arco,
    '@arco-design/web-react/icon': new Proxy({}, { get: (_, name) => component(name) }),
    '../api/client': {
      get: async () => ({ data: { list: [{ id: 7, originalName: 'a.pdf', ext: 'pdf', sizeBytes: 1, direction: 'C2S', createdAt: '', canDelete: false }], total: 1, page: 1, pageSize: 10 } }),
      post: async () => { posts++; return response },
    },
    '../store/auth': authModule({ id: 1, userType: 'INTERNAL' }, ['file:download']),
    '../api/types': { fmtSize: String, fmtTime: String },
    './ChunkUploader': component('Uploader'), './PdfPreview': component('PDF'),
  }, {
    URL: { createObjectURL: () => 'blob:test', revokeObjectURL() {} },
    document: { createElement: () => ({ click() {} }) },
  }).default
  let renderer
  await act(async () => { renderer = create(React.createElement(Page, { projectId: 1, projectStatus: 'IN_PROGRESS' })) })
  await act(async () => renderer.root.findByType('Table').props.rowSelection.onChange([7]))
  const button = renderer.root.findAllByType('Button').find((node) => String(node.props.children).startsWith('打包下载'))
  let first
  let second
  await act(async () => {
    first = button.props.onClick()
    second = button.props.onClick()
    await Promise.resolve()
  })
  assert.equal(posts, 1)
  await act(async () => { release({ data: new Blob(['zip']) }); await first; await second })
  await act(async () => renderer.unmount())
})

test('project status update uses a per-project synchronous in-flight latch', async () => {
  const project = { id: 9, name: '项目', supplierName: '供应商', status: 'DRAFT', updatedAt: '' }
  let puts = 0
  let release
  const response = new Promise((resolve) => { release = resolve })
  const Page = loadTs('src/pages/project/ProjectList.tsx', {
    '@arco-design/web-react': arco,
    '@arco-design/web-react/icon': new Proxy({}, { get: (_, name) => component(name) }),
    'react-router-dom': { Link: component('Link'), useNavigate: () => () => {} },
    '../../api/client': {
      get: async (url) => url === '/supplier-options' ? { data: [] } : { data: { list: [project], total: 1, page: 1, pageSize: 10 } },
      put: async () => { puts++; return response },
    },
    '../../store/auth': authModule({ id: 1, userType: 'INTERNAL' }, ['project:status']),
    '../../api/types': { PROJECT_STATUS: { DRAFT: { text: '草稿' } }, fmtTime: String },
  }).default
  let renderer
  await act(async () => { renderer = create(React.createElement(Page)) })
  const actions = renderer.root.findByType('Table').props.columns.at(-1).render(null, project)
  const statusSelect = findElement(actions, (node) => node.props.placeholder === '状态')
  let first
  let second
  await act(async () => {
    first = statusSelect.props.onChange('IN_PROGRESS')
    second = statusSelect.props.onChange('IN_PROGRESS')
    await Promise.resolve()
  })
  assert.equal(puts, 1)
  await act(async () => { release({ data: {} }); await first; await second })
  await act(async () => renderer.unmount())
})
