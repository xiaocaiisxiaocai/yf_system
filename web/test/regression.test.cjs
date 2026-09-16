const test = require('node:test')
const assert = require('node:assert/strict')
const fs = require('node:fs')
const path = require('node:path')
const vm = require('node:vm')
const ts = require('typescript')
const React = require('react')
const { create, act } = require('react-test-renderer')

const component = (name) => Object.assign((props) => React.createElement(name, props), {
  Group: (props) => React.createElement(`${name}.Group`, props),
  Search: (props) => React.createElement(`${name}.Search`, props),
  Option: (props) => React.createElement(`${name}.Option`, props),
  TextArea: (props) => React.createElement(`${name}.TextArea`, props),
  Password: (props) => React.createElement(`${name}.Password`, props),
  RangePicker: (props) => React.createElement(`${name}.RangePicker`, props),
  TabPane: (props) => React.createElement(`${name}.TabPane`, props),
  Item: (props) => React.createElement(`${name}.Item`, props),
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

function authModule(user, permissions = [], menus = []) {
  const state = { user, menus, hasPerm: (code) => permissions.includes(code) }
  return { useAuth: (selector) => selector ? selector(state) : state }
}

test('account menu opens personal profile maintenance inside the authenticated layout', () => {
  const root = path.resolve(__dirname, '..')
  const layout = fs.readFileSync(path.join(root, 'src/layouts/AdminLayout.tsx'), 'utf8')
  const app = fs.readFileSync(path.join(root, 'src/App.tsx'), 'utf8')

  assert.match(layout, /<Menu\.Item key="profile">[\s\S]*?个人资料\s*<\/Menu\.Item>/)
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
  assert.ok(renderer.root.findAll((node) => node.props.children === '账号信息由企业管理员维护').length > 0,
    'supplier profile must not imply that external users can administer supplier accounts')

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

test('project interaction wording and navigation match the current supplier access model', () => {
  const root = path.resolve(__dirname, '..')
  const layout = fs.readFileSync(path.join(root, 'src/layouts/AdminLayout.tsx'), 'utf8')
  const list = fs.readFileSync(path.join(root, 'src/pages/project/ProjectList.tsx'), 'utf8')
  const group = fs.readFileSync(path.join(root, 'src/pages/project/ProjectGroupDetail.tsx'), 'utf8')
  const detail = fs.readFileSync(path.join(root, 'src/pages/project/ProjectDetail.tsx'), 'utf8')
  const files = fs.readFileSync(path.join(root, 'src/components/FileTable.tsx'), 'utf8')
  const notifications = fs.readFileSync(path.join(root, 'src/components/CollaborationNotifications.tsx'), 'utf8')
  const types = fs.readFileSync(path.join(root, 'src/api/types.ts'), 'utf8')

  assert.match(layout, /loc\.pathname\.startsWith\('\/project-groups\/'\)/)
  assert.match(list, /该供应商的全部启用账号均可访问此主项目及其子项目/)
  assert.match(group, /该供应商的全部启用账号均可访问此主项目及其子项目/)
  assert.doesNotMatch(`${detail}\n${files}`, /引用履历|暂无引用记录|>引用</)
  assert.match(files, />\s*复制件\s*</)
  assert.match(files, /文件当前不统计已读状态/)
  assert.match(notifications, /item\.type === 'MESSAGE' && item\.action === 'CREATE'\) return '发送留言'/)
  assert.match(notifications, /不等同于留言已读/)
  assert.equal(fs.existsSync(path.join(root, 'src/components/MemberPanel.tsx')), false)
  assert.doesNotMatch(types, /interface (?:Supplier)?Member/)
})

test('profile locks both forms during save, rejects overlapping requests and unlocks after failure', async () => {
  const calls = []
  let resolveRequest, rejectRequest
  const user = { id: 8, employeeNo: 'review8', realName: '测试', email: 'before@example.invalid', userType: 'INTERNAL' }
  const profileForm = { setFieldsValue() {} }
  let formIndex = 0
  const profileArco = new Proxy({
    ...arco,
    Form: Object.assign(component('Form'), {
      useForm: () => [formIndex++ % 2 === 0 ? profileForm : {}],
      Item: component('Form.Item'),
    }),
  }, { get: (obj, key) => obj[key] ?? component(key) })
  const Page = loadTs('src/pages/Profile.tsx', {
    '@arco-design/web-react': profileArco,
    '@arco-design/web-react/icon': new Proxy({}, { get: (_, name) => component(name) }),
    'react-router-dom': { useNavigate: () => () => {} },
    '../api/client': {
      __esModule: true,
      default: { put: (url, body) => {
        calls.push({ url, body })
        return new Promise((resolve, reject) => { resolveRequest = resolve; rejectRequest = reject })
      } },
      withAuthLock: async (action) => action(),
    },
    '../store/auth': { useAuth: () => ({ user, setUser() {}, logout() {} }) },
  }).default
  let renderer, saving
  await act(async () => { renderer = create(React.createElement(Page)) })
  const passwordValues = { oldPassword: 'old123', newPassword: 'new123', confirm: 'new123' }
  await act(async () => {
    const [saveProfile, changePassword] = renderer.root.findAllByType('Form').map((form) => form.props.onSubmit)
    saving = saveProfile({ email: 'after@example.invalid' })
    await saveProfile({ email: 'duplicate@example.invalid' })
    await changePassword(passwordValues)
  })
  assert.equal(calls.length, 1, 'repeat or cross-form submission must not race the in-flight save')
  assert.equal(renderer.root.findByProps({ placeholder: '请输入联系邮箱' }).props.disabled, true)
  assert.equal(renderer.root.findByProps({ placeholder: '请输入当前密码' }).props.disabled, true)
  assert.ok(renderer.root.findAllByType('Button').every((button) => button.props.disabled))
  await act(async () => { rejectRequest(new Error('mock save failure')); await saving })
  assert.equal(renderer.root.findByProps({ placeholder: '请输入联系邮箱' }).props.disabled, false)
  await act(async () => {
    saving = renderer.root.findAllByType('Form')[0].props.onSubmit({ email: 'retry@example.invalid' })
  })
  assert.equal(calls.length, 2)
  await act(async () => { resolveRequest({ data: { user: { ...user, email: 'retry@example.invalid' } } }); await saving })
  assert.equal(renderer.root.findByProps({ placeholder: '请输入联系邮箱' }).props.disabled, false)
  await act(async () => {
    const [saveProfile, changePassword] = renderer.root.findAllByType('Form').map((form) => form.props.onSubmit)
    saving = changePassword(passwordValues)
    await saveProfile({ email: 'concurrent@example.invalid' })
  })
  assert.equal(calls.length, 3)
  assert.equal(calls[2].url, '/auth/password')
  await act(async () => { rejectRequest(new Error('mock password failure')); await saving })
  assert.equal(renderer.root.findByProps({ placeholder: '请输入当前密码' }).props.disabled, false)
  await act(async () => renderer.unmount())
})

for (const [page, formIndex] of [['ChangePassword', 0], ['Profile', 1]]) {
  test(`${page} rejects the current password without submitting or ending the session`, async () => {
    const errors = []
    const calls = []
    const pageArco = new Proxy({
      ...arco,
      Message: { ...arco.Message, error: (message) => errors.push(message) },
    }, { get: (obj, key) => obj[key] ?? component(key) })
    const Page = loadTs(`src/pages/${page}.tsx`, {
      '@arco-design/web-react': pageArco,
      '@arco-design/web-react/icon': new Proxy({}, { get: (_, name) => component(name) }),
      'react-router-dom': { useNavigate: () => () => calls.push('navigate') },
      '../components/AuthShell': { default: component('AuthShell'), __esModule: true },
      '../api/client': {
        __esModule: true,
        default: { put: async () => calls.push('put') },
        withAuthLock: async (action) => action(),
      },
      '../store/auth': { useAuth: () => ({
        mustChangePassword: true,
        user: { id: 1, employeeNo: 'test', realName: '测试', userType: 'INTERNAL' },
        logout: () => calls.push('logout'),
      }) },
    }).default
    let renderer
    await act(async () => { renderer = create(React.createElement(Page)) })
    await act(async () => renderer.root.findAllByType('Form')[formIndex].props.onSubmit({
      oldPassword: 'Unchanged#2026', newPassword: 'Unchanged#2026', confirm: 'Unchanged#2026',
    }))
    assert.deepEqual(errors, ['新密码不能与当前密码相同'])
    assert.deepEqual(calls, [])
    assert.ok(renderer.root.findAllByType('Form')[formIndex])
    await act(async () => renderer.unmount())
  })
}

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

test('table action slots preserve action order and mark unavailable actions', () => {
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
  let fields = {}
  const form = { resetFields() { fields = {} }, setFieldsValue(values) { fields = values } }
  const orgArco = new Proxy({
    ...arco,
    Form: Object.assign(component('Form'), { useForm: () => [form], Item: component('Form.Item') }),
  }, { get: (obj, key) => obj[key] ?? component(key) })
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
    '@arco-design/web-react': orgArco,
    '@arco-design/web-react/icon': new Proxy({}, { get: (_, name) => component(name) }),
    '../../api/client': { get: async () => ({ data: departments }) },
    '../../store/auth': authModule({ id: 1, userType: 'INTERNAL', isSystemAdmin: true }, ['dept:manage', 'dept:delete']),
  }).default
  let renderer
  await act(async () => { renderer = create(React.createElement(Page)) })
  assert.ok(renderer.root.findAll((node) => node.props.children === '组织架构').length > 0)
  assert.ok(renderer.root.findAll((node) => node.props.children === '新增事业部').length > 0)
  const click = async (label) => act(async () => renderer.root.findAllByType('Button').find((node) => node.props.children === label).props.onClick())
  await click('新增事业部')
  assert.equal(fields.sortNo, 2)
  const tree = renderer.root.findByType('Tree')
  assert.equal(tree.props.blockNode, true)
  assert.equal(tree.props.showLine, true)
  await act(async () => renderer.root.findByType('Tree').props.onSelect(['1']))
  assert.ok(renderer.root.findAll((node) => node.props.children === '新增部门').length > 0)
  await click('新增部门')
  assert.equal(fields.sortNo, 4, 'Disabled siblings still participate in ordering')
  await act(async () => renderer.root.findByType('Tree').props.onSelect(['2']))
  assert.ok(renderer.root.findAll((node) => node.props.children === '研发部').length > 0)
  assert.equal(renderer.root.findByProps({ 'aria-label': '层级路径' }).type, 'nav')
  assert.ok(renderer.root.findAll((node) => node.props.children === '编辑部门').length > 0)
  assert.ok(renderer.root.findAll((node) => node.props.children === '新增课别').length > 0)
  await click('新增课别')
  assert.equal(fields.sortNo, 2, 'Only children of the selected department determine the next sort number')
  await click('编辑部门')
  assert.equal(fields.sortNo, 3, 'Editing keeps the existing sort number')
  departments[0].children[0].children = []
  await click('新增课别')
  assert.equal(fields.sortNo, 1, 'The first sibling starts at 1')
  departments[0].children[0].children = [{ id: 3, name: '开发课', kind: 'SECTION', parentId: 2, sortNo: 1, status: 'ACTIVE' }]
  assert.equal(renderer.root.findAll((node) => node.props.children === '新增子部门').length, 0)
  await act(async () => renderer.root.findByType('Tree').props.onSelect(['3']))
  assert.ok(renderer.root.findAll((node) => node.props.children === '编辑课别').length > 0)
  assert.equal(renderer.root.findAll((node) => node.props.children === '新增课别').length, 0)
  assert.ok(renderer.root.findAll((node) => node.props.children === '事业一部 > 研发部 > 开发课').length > 0)
  assert.ok(findElement(renderer.root.findByType('Dropdown').props.droplist, node => node.props.children === '删除课别'))
  const search = () => renderer.root.findByProps({ placeholder: '搜索组织名称' })
  await act(async () => search().props.onChange('开发课'))
  let filtered = renderer.root.findByType('Tree').props.treeData
  assert.equal(filtered[0].key, '1', 'Search retains division ancestors')
  assert.equal(filtered[0].children[0].key, '2', 'Search retains department ancestors')
  assert.equal(filtered[0].children[0].children[0].key, '3')
  await click('收起')
  assert.deepEqual(Array.from(renderer.root.findByType('Tree').props.expandedKeys), [])
  await click('展开')
  assert.deepEqual(Array.from(renderer.root.findByType('Tree').props.expandedKeys), ['1', '2', '3'])
  await act(async () => search().props.onChange('不存在的组织'))
  assert.equal(renderer.root.findAllByType('Tree').length, 0)
  assert.ok(renderer.root.findAll((node) => node.props.children === '未找到匹配的组织').length > 0)
  await act(async () => search().props.onChange(''))
  const breadcrumb = renderer.root.findByProps({ 'aria-label': '层级路径' })
  await act(async () => breadcrumb.findAllByType('button').find((node) => node.props.children === '事业一部').props.onClick())
  const childRow = renderer.root.findAllByType('button').find((node) => node.props.className === 'org-view-child')
  await act(async () => childRow.props.onClick())
  assert.deepEqual(Array.from(renderer.root.findByType('Tree').props.selectedKeys), ['2'])
  assert.ok(renderer.root.findAll((node) => node.props.children === '直属课别').length > 0)
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
      row: { id: 1, name: 'p', status: 'DRAFT', subprojectCount: 0 },
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
  const busy = renderer.root.findAllByType('Table')[0].props.columns.at(-1).render(null, { id: 2, name: 'busy', status: 'IN_PROGRESS', subprojectCount: 1 })
  assert.equal(findActionButton(busy, '删除'), undefined, 'main projects with children must be cleared before deletion')
  const completed = renderer.root.findAllByType('Table')[0].props.columns.at(-1).render(null, { id: 4, name: 'completed', status: 'COMPLETED', subprojectCount: 0 })
  assert.ok(findActionButton(completed, '删除'), 'empty completed main projects may be deleted')
  const terminated = renderer.root.findAllByType('Table')[0].props.columns.at(-1).render(null, { id: 3, name: 'terminated', status: 'TERMINATED', subprojectCount: 0 })
  assert.ok(findActionButton(terminated, '删除'), 'empty terminated main projects may be deleted')
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
    assert.equal(!!findElement(renderer.root.findByType('Dropdown').props.droplist, node => node.props.children === '删除课别'), expected)
    await act(async () => renderer.unmount())
  }
})

test('organization more actions require confirmation and retain the selected target', async () => {
  let confirmation
  const writes = []
  const nodes = [
    { id: 1, name: '事业一部', kind: 'DIVISION', sortNo: 1, status: 'ACTIVE' },
    { id: 2, name: '事业二部', kind: 'DIVISION', sortNo: 2, status: 'ACTIVE' },
  ]
  const ui = new Proxy({
    ...arco,
    Modal: Object.assign(component('Modal'), { confirm: (options) => { confirmation = options } }),
  }, { get: (obj, key) => obj[key] ?? component(key) })
  const Page = loadTs('src/pages/org/DeptManage.tsx', {
    '@arco-design/web-react': ui,
    '@arco-design/web-react/icon': new Proxy({}, { get: (_, name) => component(name) }),
    '../../api/client': {
      get: async () => ({ data: nodes }),
      delete: async (url) => { writes.push(url) },
      put: async (url, body) => { writes.push({ url, body }) },
    },
    '../../store/auth': authModule({ id: 9, userType: 'INTERNAL' }, ['dept:manage', 'dept:delete']),
  }).default
  let renderer
  await act(async () => { renderer = create(React.createElement(Page)) })
  await act(async () => renderer.root.findByType('Tree').props.onSelect(['1']))
  await act(async () => renderer.root.findByType('Dropdown').props.droplist.props.onClickMenuItem('delete'))
  assert.equal(confirmation.title, '删除事业部')
  assert.equal(writes.length, 0, 'Selecting the menu must not delete without confirmation')
  await act(async () => renderer.root.findByType('Tree').props.onSelect(['2']))
  await act(async () => confirmation.onOk())
  assert.deepEqual(writes, ['/admin/departments/1'], 'Confirmation retains the original node')
  await act(async () => renderer.root.findByType('Dropdown').props.droplist.props.onClickMenuItem('status'))
  assert.equal(writes.length, 1, 'Status change also waits for confirmation')
  await act(async () => confirmation.onOk())
  assert.equal(writes[1].url, '/admin/departments/2/status')
  assert.equal(writes[1].body.status, 'DISABLED')
  await act(async () => renderer.unmount())
})

function messageFixture(id, content = `留言 ${id}`) {
  return {
    id, projectId: 1, senderId: 2, senderName: '成员', senderType: 'INTERNAL', content,
    readCount: 0, totalCount: 1, readByMe: true, createdAt: '',
  }
}

function visibleMessageIds(renderer) {
  return renderer.root.findAll((node) => node.props['data-message-id'] !== undefined)
    .map((node) => node.props['data-message-id'])
}

function createMessageSyncClock() {
  const timers = new Map()
  let nextId = 0
  return {
    timers,
    globals: {
      setTimeout(callback, delay) {
        const id = ++nextId
        timers.set(id, { callback, delay })
        return id
      },
      clearTimeout(id) { timers.delete(id) },
    },
  }
}

test('message revision automatically shows the new message while preserving the draft', async () => {
  let rows = [messageFixture(3), messageFixture(2), messageFixture(1)]
  const requests = []
  let sent
  const Page = loadTs('src/components/MessagePanel.tsx', {
    '@arco-design/web-react': arco,
    '@arco-design/web-react/icon': new Proxy({}, { get: (_, name) => component(name) }),
    '../store/auth': authModule({ id: 1, userType: 'INTERNAL' }, ['message:create']),
    '../api/types': { fmtTime: String },
    '../api/client': {
      get: async (url, config) => {
        requests.push({ url, config })
        return { data: { list: rows, total: rows.length } }
      },
      post: async (_url, body) => { sent = body.content; return { data: messageFixture(5, body.content) } },
    },
  }).default
  const props = { projectId: 1, projectStatus: 'IN_PROGRESS', revision: 'initial' }
  let renderer
  await act(async () => { renderer = create(React.createElement(Page, props)) })
  await act(async () => renderer.root.findByType('Input.TextArea').props.onChange('正在写的回复'))

  rows = [messageFixture(4, '刚刚发出的新留言'), ...rows]
  await act(async () => renderer.update(React.createElement(Page, { ...props, revision: 'new-message' })))

  assert.equal(requests.length, 2, 'a revision change fetches the current message window automatically')
  assert.equal(requests[1].config.params.pageSize, 20)
  assert.equal(requests[1].config.quietNetworkError, true)
  assert.deepEqual(visibleMessageIds(renderer), [4, 3, 2, 1])
  assert.equal(renderer.root.findByType('Input.TextArea').props.value, '正在写的回复')
  assert.equal(renderer.root.findAllByType('Button').some((node) => node.props.children === '查看最新留言'), false)

  await act(async () => renderer.root.findAllByType('Button').find((node) => node.props.children === '发送').props.onClick())
  assert.equal(sent, '正在写的回复')
  assert.equal(renderer.root.findByType('Input.TextArea').props.value, '')
  await act(async () => renderer.unmount())
})

test('confirmed message response appears immediately without refetching or dropping loaded history', async () => {
  const rows = [messageFixture(3), messageFixture(2), messageFixture(1)]
  const gets = []
  const created = { ...messageFixture(4, '服务端已确认'), senderId: 1, senderName: '我' }
  const Page = loadTs('src/components/MessagePanel.tsx', {
    '@arco-design/web-react': arco,
    '@arco-design/web-react/icon': new Proxy({}, { get: (_, name) => component(name) }),
    '../store/auth': authModule({ id: 1, userType: 'INTERNAL' }, ['message:create']),
    '../api/types': { fmtTime: String },
    '../api/client': {
      get: async (url, config) => { gets.push({ url, config }); return { data: { list: rows, total: rows.length } } },
      post: async () => ({ data: created }),
    },
  }).default
  let renderer
  await act(async () => { renderer = create(React.createElement(Page, { projectId: 1, projectStatus: 'IN_PROGRESS', revision: 'r1' })) })
  await act(async () => renderer.root.findByType('Input.TextArea').props.onChange('服务端已确认'))
  await act(async () => renderer.root.findAllByType('Button').find((node) => node.props.children === '发送').props.onClick())

  assert.equal(gets.length, 1, 'a successful POST must not start a second list request')
  assert.deepEqual(visibleMessageIds(renderer), [4, 3, 2, 1], 'the confirmed row is prepended without losing loaded history')
  assert.equal(React.Children.toArray(renderer.root.findByType('h2').props.children).join(''), '协作留言（4）')
  await act(async () => renderer.unmount())
})

test('a push arriving before the POST response is deduplicated by the confirmed message id', async () => {
  let rows = [messageFixture(2), messageFixture(1)]
  let releasePost
  const postResponse = new Promise((resolve) => { releasePost = resolve })
  const gets = []
  const created = { ...messageFixture(3, '推送先到'), senderId: 1, senderName: '我' }
  const Page = loadTs('src/components/MessagePanel.tsx', {
    '@arco-design/web-react': arco,
    '@arco-design/web-react/icon': new Proxy({}, { get: (_, name) => component(name) }),
    '../store/auth': authModule({ id: 1, userType: 'INTERNAL' }, ['message:create']),
    '../api/types': { fmtTime: String },
    '../api/client': {
      get: async (url, config) => { gets.push({ url, config }); return { data: { list: rows, total: rows.length } } },
      post: async () => postResponse,
    },
  }).default
  const render = (revision) => React.createElement(Page, { projectId: 1, projectStatus: 'IN_PROGRESS', revision })
  let renderer
  await act(async () => { renderer = create(render('r1')) })
  await act(async () => renderer.root.findByType('Input.TextArea').props.onChange('推送先到'))
  let sending
  await act(async () => {
    sending = renderer.root.findAllByType('Button').find((node) => node.props.children === '发送').props.onClick()
    await Promise.resolve()
  })
  rows = [created, ...rows]
  await act(async () => renderer.update(render('r2')))
  assert.deepEqual(visibleMessageIds(renderer), [3, 2, 1])

  await act(async () => {
    releasePost({ data: created })
    await sending
  })
  assert.deepEqual(visibleMessageIds(renderer), [3, 2, 1], 'the same committed id must remain unique')
  assert.equal(gets.length, 2, 'the POST response must not refetch after the push already synchronized the revision')
  assert.equal(React.Children.toArray(renderer.root.findByType('h2').props.children).join(''), '协作留言（3）')
  await act(async () => renderer.unmount())
})

test('late message synchronization cannot replace a message confirmed by POST', async () => {
  const rows = [messageFixture(2), messageFixture(1)]
  const pendingSync = []
  const created = { ...messageFixture(3, '已确认的新留言'), senderId: 1, senderName: '我' }
  const Page = loadTs('src/components/MessagePanel.tsx', {
    '@arco-design/web-react': arco,
    '@arco-design/web-react/icon': new Proxy({}, { get: (_, name) => component(name) }),
    '../store/auth': authModule({ id: 1, userType: 'INTERNAL' }, ['message:create']),
    '../api/types': { fmtTime: String },
    '../api/client': {
      get: async (_url, config) => {
        if (!config.quietNetworkError) return { data: { list: rows, total: rows.length } }
        return new Promise((resolve) => pendingSync.push({ config, resolve }))
      },
      post: async () => ({ data: created }),
    },
  }).default
  const render = (revision) => React.createElement(Page, { projectId: 1, projectStatus: 'IN_PROGRESS', revision })
  let renderer
  await act(async () => { renderer = create(render('r1')) })
  await act(async () => renderer.update(render('r2')))
  assert.equal(pendingSync.length, 1)
  await act(async () => renderer.root.findByType('Input.TextArea').props.onChange('已确认的新留言'))
  await act(async () => renderer.root.findAllByType('Button').find((node) => node.props.children === '发送').props.onClick())
  assert.deepEqual(visibleMessageIds(renderer), [3, 2, 1])
  assert.equal(pendingSync[0].config.signal.aborted, true, 'the older synchronization is cancelled after the confirmed mutation')

  await act(async () => pendingSync[0].resolve({ data: { list: rows, total: rows.length } }))
  assert.deepEqual(visibleMessageIds(renderer), [3, 2, 1], 'a stale list response must not remove the confirmed row')
  await act(async () => renderer.unmount())
})

test('late load-more response cannot overwrite a confirmed message', async () => {
  const rows = Array.from({ length: 25 }, (_, index) => messageFixture(25 - index))
  let releaseAppend
  const appendResponse = new Promise((resolve) => { releaseAppend = resolve })
  const created = { ...messageFixture(26, '分页期间发送'), senderId: 1, senderName: '我' }
  const Page = loadTs('src/components/MessagePanel.tsx', {
    '@arco-design/web-react': arco,
    '@arco-design/web-react/icon': new Proxy({}, { get: (_, name) => component(name) }),
    '../store/auth': authModule({ id: 1, userType: 'INTERNAL' }, ['message:create']),
    '../api/types': { fmtTime: String },
    '../api/client': {
      get: async (_url, { params }) => params.beforeId
        ? appendResponse
        : { data: { list: rows.slice(0, 20), total: rows.length } },
      post: async () => ({ data: created }),
    },
  }).default
  let renderer
  await act(async () => { renderer = create(React.createElement(Page, { projectId: 1, projectStatus: 'IN_PROGRESS', revision: 'r1' })) })
  const more = renderer.root.findAllByType('Button').find((node) => String(node.props.children).startsWith('加载更多'))
  let append
  await act(async () => { append = more.props.onClick(); await Promise.resolve() })
  await act(async () => renderer.root.findByType('Input.TextArea').props.onChange('分页期间发送'))
  await act(async () => renderer.root.findAllByType('Button').find((node) => node.props.children === '发送').props.onClick())

  await act(async () => {
    releaseAppend({ data: { list: rows.slice(20), total: rows.length } })
    await append
  })
  assert.deepEqual(visibleMessageIds(renderer), [26, ...rows.slice(0, 20).map((item) => item.id)])
  assert.equal(React.Children.toArray(renderer.root.findByType('h2').props.children).join(''), '协作留言（26）')
  await act(async () => renderer.unmount())
})

test('sending from a notification keeps the full list and inserts the confirmed message', async () => {
  const gets = []
  let sent = 0
  const Page = loadTs('src/components/MessagePanel.tsx', {
    '@arco-design/web-react': arco,
    '@arco-design/web-react/icon': new Proxy({}, { get: (_, name) => component(name) }),
    '../store/auth': authModule({ id: 1, userType: 'INTERNAL' }, ['message:create']),
    '../api/types': { fmtTime: String },
    '../api/client': {
      get: async (_url, config) => { gets.push(config); return { data: { list: [messageFixture(9)], total: 1 } } },
      post: async (_url, body) => { sent++; return { data: messageFixture(10, body.content) } },
    },
  }).default
  let renderer
  await act(async () => {
    renderer = create(React.createElement(Page, {
      projectId: 1, projectStatus: 'IN_PROGRESS', targetId: 9, revision: 'r1',
    }))
  })
  await act(async () => renderer.root.findByType('Input.TextArea').props.onChange('离开定位后查看'))
  await act(async () => renderer.root.findAllByType('Button').find((node) => node.props.children === '发送').props.onClick())

  assert.equal(sent, 1)
  assert.equal(gets.length, 1, 'sending does not reload the list or clear the target')
  assert.equal(gets[0].params.targetId, undefined)
  assert.deepEqual(visibleMessageIds(renderer), [10, 9])
  await act(async () => renderer.unmount())
})

test('sending preserves later edits and rejects rapid duplicate clicks before rerender', async () => {
  let releasePost
  const postResponse = new Promise((resolve) => { releasePost = resolve })
  let posts = 0
  const created = { ...messageFixture(2, '第一版草稿'), senderId: 1, senderName: '我' }
  const Page = loadTs('src/components/MessagePanel.tsx', {
    '@arco-design/web-react': arco,
    '@arco-design/web-react/icon': new Proxy({}, { get: (_, name) => component(name) }),
    '../store/auth': authModule({ id: 1, userType: 'INTERNAL' }, ['message:create']),
    '../api/types': { fmtTime: String },
    '../api/client': {
      get: async () => ({ data: { list: [messageFixture(1)], total: 1 } }),
      post: async () => { posts++; return postResponse },
    },
  }).default
  let renderer
  await act(async () => { renderer = create(React.createElement(Page, { projectId: 1, projectStatus: 'IN_PROGRESS', revision: 'r1' })) })
  await act(async () => renderer.root.findByType('Input.TextArea').props.onChange('第一版草稿'))
  const click = renderer.root.findAllByType('Button').find((node) => node.props.children === '发送').props.onClick
  let first
  await act(async () => {
    first = click()
    click()
    await Promise.resolve()
  })
  assert.equal(posts, 1, 'the in-flight ref closes the same-render double-click window')
  await act(async () => renderer.root.findByType('Input.TextArea').props.onChange('发送期间继续编辑'))
  await act(async () => {
    releasePost({ data: created })
    await first
  })

  assert.equal(renderer.root.findByType('Input.TextArea').props.value, '发送期间继续编辑')
  assert.deepEqual(visibleMessageIds(renderer), [2, 1])
  await act(async () => renderer.unmount())
})

test('notification message views show the full list and continue receiving new messages', async () => {
  let target = messageFixture(9, '定位留言旧内容')
  const requests = []
  const Page = loadTs('src/components/MessagePanel.tsx', {
    '@arco-design/web-react': arco,
    '@arco-design/web-react/icon': new Proxy({}, { get: (_, name) => component(name) }),
    '../store/auth': authModule({ id: 1, userType: 'INTERNAL' }, ['message:create']),
    '../api/types': { fmtTime: String },
    '../api/client': { get: async (_url, config) => {
      requests.push(config)
      return { data: { list: [messageFixture(10), target, messageFixture(8)], total: 3 } }
    } },
  }).default
  const render = (revision) => React.createElement(Page, {
    projectId: 1, projectStatus: 'IN_PROGRESS', targetId: 9, revision,
  })
  let renderer
  await act(async () => { renderer = create(render('r1')) })
  target = messageFixture(9, '定位留言最新内容')
  await act(async () => renderer.update(render('r2')))

  assert.equal(requests.length, 2, 'the complete loaded window is synchronized once')
  assert.deepEqual(visibleMessageIds(renderer), [10, 9, 8])
  assert.ok(requests.every((config) => config.params.targetId === undefined))
  assert.equal(requests[1].params.beforeId, undefined)
  assert.equal(renderer.root.findAll((node) => node.props.children === '定位留言最新内容').length, 1)
  assert.equal(renderer.root.findByType('Input.TextArea').props.disabled, undefined, 'target mode remains writable')
  await act(async () => renderer.unmount())
})

for (const missing of [false, true]) test(`notification target loads contiguous history without filtering, missing=${missing}`, async () => {
  const rows = Array.from({ length: 65 }, (_, i) => messageFixture(65 - i)).filter(row => !missing || row.id !== 22)
  const requests = []
  const scrolled = []
  const Page = loadTs('src/components/MessagePanel.tsx', {
    '@arco-design/web-react': arco,
    '@arco-design/web-react/icon': new Proxy({}, { get: (_, name) => component(name) }),
    '../store/auth': authModule({ id: 1, userType: 'INTERNAL' }, []),
    '../api/types': { fmtTime: String },
    '../api/client': { get: async (_url, { params }) => {
      requests.push(params)
      return { data: { list: rows.filter(row => params.beforeId === undefined || row.id < params.beforeId).slice(0, 20), total: rows.length } }
    } },
  }).default
  let renderer
  await act(async () => { renderer = create(React.createElement(Page, {
    projectId: 1, projectStatus: 'IN_PROGRESS', targetId: 22, revision: 'r1',
  }), { createNodeMock: () => ({ querySelector: () => missing ? null : { scrollIntoView: () => scrolled.push(22) } }) }) })
  assert.deepEqual(requests.map(r => r.beforeId), [undefined, 46, 26])
  assert.ok(requests.every(r => r.targetId === undefined), 'target never becomes an API filter')
  assert.deepEqual(visibleMessageIds(renderer), rows.slice(0, 60).map(row => row.id))
  assert.deepEqual(scrolled, missing ? [] : [22], 'only the actual target is scrolled into view once')
  const more = renderer.root.findAllByType('Button').find(node => String(node.props.children).startsWith('加载更多'))
  await act(async () => more.props.onClick())
  assert.deepEqual(visibleMessageIds(renderer), rows.map(row => row.id), 'older history stays available after locating')
  assert.deepEqual(scrolled, missing ? [] : [22], 'loading history never pulls the reader back to the target')
  await act(async () => renderer.unmount())
})

test('message auto-sync covers every page through the loaded history and keeps the next cursor contiguous', async () => {
  let rows = Array.from({ length: 60 }, (_, index) => messageFixture(60 - index))
  let phase = 'initial'
  const requests = []
  const Page = loadTs('src/components/MessagePanel.tsx', {
    '@arco-design/web-react': arco,
    '@arco-design/web-react/icon': new Proxy({}, { get: (_, name) => component(name) }),
    '../store/auth': authModule({ id: 1, userType: 'INTERNAL' }, []),
    '../api/types': { fmtTime: String },
    '../api/client': { get: async (url, { params }) => {
      requests.push({ phase, url, params: { ...params } })
      const list = rows.filter((message) => params.beforeId === undefined || message.id < params.beforeId).slice(0, 20)
      return { data: { list, total: rows.length } }
    } },
  }).default
  let renderer
  await act(async () => { renderer = create(React.createElement(Page, { projectId: 1, projectStatus: 'IN_PROGRESS', revision: 'r1' })) })
  const loadMore = () => renderer.root.findAllByType('Button')
    .find((node) => String(node.props.children).startsWith('加载更多'))
  await act(async () => loadMore().props.onClick())
  assert.deepEqual(visibleMessageIds(renderer), Array.from({ length: 40 }, (_, index) => 60 - index))

  phase = 'sync'
  rows = Array.from({ length: 85 }, (_, index) => messageFixture(85 - index))
  await act(async () => renderer.update(React.createElement(Page, { projectId: 1, projectStatus: 'IN_PROGRESS', revision: 'r2' })))

  const syncBeforeIds = requests.filter((request) => request.phase === 'sync').map((request) => request.params.beforeId)
  assert.deepEqual(syncBeforeIds, [undefined, 66, 46, 26], 'sync walks descending cursors until the old id 21 is covered')
  assert.deepEqual(visibleMessageIds(renderer), Array.from({ length: 80 }, (_, index) => 85 - index), 'new and old loaded messages stay gap-free')
  assert.equal(React.Children.toArray(loadMore().props.children).join(''), '加载更多（80/85）')

  await act(async () => loadMore().props.onClick())
  assert.equal(requests.at(-1).params.beforeId, 6, 'load-more continues after the oldest message returned by auto-sync')
  assert.deepEqual(visibleMessageIds(renderer), Array.from({ length: 85 }, (_, index) => 85 - index))
  assert.equal(loadMore(), undefined)
  await act(async () => renderer.unmount())
})

test('loading older messages cannot acknowledge a newer revision before auto-sync runs', async () => {
  const initialRows = Array.from({ length: 40 }, (_, index) => messageFixture(40 - index))
  let currentRows = initialRows
  let resolveAppend
  const requests = []
  const appendResponse = new Promise((resolve) => { resolveAppend = resolve })
  const Page = loadTs('src/components/MessagePanel.tsx', {
    '@arco-design/web-react': arco,
    '@arco-design/web-react/icon': new Proxy({}, { get: (_, name) => component(name) }),
    '../store/auth': authModule({ id: 1, userType: 'INTERNAL' }, []),
    '../api/types': { fmtTime: String },
    '../api/client': { get: async (_url, config) => {
      requests.push(config)
      if (!config.quietNetworkError && config.params.beforeId === 21) return appendResponse
      const list = currentRows.filter((message) => config.params.beforeId === undefined || message.id < config.params.beforeId).slice(0, 20)
      return { data: { list, total: currentRows.length } }
    } },
  }).default
  const render = (revision) => React.createElement(Page, { projectId: 1, projectStatus: 'IN_PROGRESS', revision })
  let renderer
  await act(async () => { renderer = create(render('r1')) })
  const loadMore = renderer.root.findAllByType('Button')
    .find((node) => String(node.props.children).startsWith('加载更多'))
  let append
  await act(async () => {
    append = loadMore.props.onClick()
    await Promise.resolve()
  })
  assert.equal(requests.length, 2)

  currentRows = [messageFixture(41, '追加历史期间到达'), ...initialRows]
  await act(async () => renderer.update(render('r2')))
  assert.equal(requests.length, 2, 'the revision waits while an append request owns the list')

  await act(async () => {
    resolveAppend({ data: { list: initialRows.slice(20), total: initialRows.length } })
    await append
    await Promise.resolve()
  })
  assert.ok(requests.some((config) => config.quietNetworkError), 'finishing append still triggers auto-sync for the unacknowledged revision')
  assert.deepEqual(visibleMessageIds(renderer), Array.from({ length: 41 }, (_, index) => 41 - index))
  await act(async () => renderer.unmount())
})

test('failed message auto-sync preserves history and draft, then retries after five seconds', async () => {
  const clock = createMessageSyncClock()
  const oldRows = [messageFixture(2), messageFixture(1)]
  const newRows = [messageFixture(3, '重试后出现'), ...oldRows]
  let syncAttempts = 0
  const Page = loadTs('src/components/MessagePanel.tsx', {
    '@arco-design/web-react': arco,
    '@arco-design/web-react/icon': new Proxy({}, { get: (_, name) => component(name) }),
    '../store/auth': authModule({ id: 1, userType: 'INTERNAL' }, ['message:create']),
    '../api/types': { fmtTime: String },
    '../api/client': { get: async (_url, config) => {
      if (!config.quietNetworkError) return { data: { list: oldRows, total: oldRows.length } }
      syncAttempts++
      if (syncAttempts === 1) throw new Error('temporary message sync failure')
      return { data: { list: newRows, total: newRows.length } }
    } },
  }, clock.globals).default
  let renderer
  await act(async () => { renderer = create(React.createElement(Page, { projectId: 1, projectStatus: 'IN_PROGRESS', revision: 'r1' })) })
  await act(async () => renderer.root.findByType('Input.TextArea').props.onChange('失败时也保留'))
  await act(async () => renderer.update(React.createElement(Page, { projectId: 1, projectStatus: 'IN_PROGRESS', revision: 'r2' })))

  assert.deepEqual(visibleMessageIds(renderer), [2, 1])
  assert.equal(renderer.root.findByType('Input.TextArea').props.value, '失败时也保留')
  assert.ok(renderer.root.findAllByType('Text').some((node) => node.props.children === '留言同步失败，正在重试'))
  const retryTimer = [...clock.timers.entries()].find(([, timer]) => timer.delay === 5000)
  assert.ok(retryTimer, 'failed sync schedules a five-second retry')
  clock.timers.delete(retryTimer[0])
  await act(async () => {
    const retry = retryTimer[1].callback()
    await retry
  })

  assert.equal(syncAttempts, 2)
  assert.deepEqual(visibleMessageIds(renderer), [3, 2, 1])
  assert.equal(renderer.root.findByType('Input.TextArea').props.value, '失败时也保留')
  assert.equal(renderer.root.findAllByType('Text').some((node) => node.props.children === '留言同步失败，正在重试'), false)
  await act(async () => renderer.unmount())
})

test('message auto-sync ignores late responses from older revisions and projects', async () => {
  const pending = []
  const Page = loadTs('src/components/MessagePanel.tsx', {
    '@arco-design/web-react': arco,
    '@arco-design/web-react/icon': new Proxy({}, { get: (_, name) => component(name) }),
    '../store/auth': authModule({ id: 1, userType: 'INTERNAL' }, []),
    '../api/types': { fmtTime: String },
    '../api/client': { get: async (url, config) => {
      const projectId = Number(url.match(/projects\/(\d+)/)[1])
      if (!config.quietNetworkError) return { data: { list: [messageFixture(projectId * 10)], total: 1 } }
      return new Promise((resolve) => pending.push({ projectId, config, resolve }))
    } },
  }).default
  const render = (projectId, revision) => React.createElement(Page, { projectId, projectStatus: 'IN_PROGRESS', revision })
  let renderer
  await act(async () => { renderer = create(render(1, 'r1')) })

  await act(async () => {
    renderer.update(render(1, 'r2'))
    await Promise.resolve()
  })
  assert.equal(pending.length, 1)
  await act(async () => {
    renderer.update(render(1, 'r3'))
    await Promise.resolve()
  })
  assert.equal(pending[0].config.signal.aborted, true)
  assert.equal(pending.length, 2)
  await act(async () => pending[1].resolve({ data: { list: [messageFixture(12, '当前 revision')], total: 1 } }))
  await act(async () => pending[0].resolve({ data: { list: [messageFixture(11, '旧 revision')], total: 1 } }))
  assert.deepEqual(visibleMessageIds(renderer), [12], 'late data from an older revision cannot replace the current window')

  await act(async () => {
    renderer.update(render(1, 'r4'))
    await Promise.resolve()
  })
  const oldProjectRequest = pending.at(-1)
  await act(async () => renderer.update(render(2, 'project-2')))
  assert.equal(oldProjectRequest.config.signal.aborted, true)
  await act(async () => oldProjectRequest.resolve({ data: { list: [messageFixture(13, '旧项目')], total: 1 } }))
  assert.deepEqual(visibleMessageIds(renderer), [20], 'late data from the previous project cannot replace the new project load')
  await act(async () => renderer.unmount())
})

test('inactive message panels do not auto-sync until activated', async () => {
  let rows = [messageFixture(1)]
  const requests = []
  const Page = loadTs('src/components/MessagePanel.tsx', {
    '@arco-design/web-react': arco,
    '@arco-design/web-react/icon': new Proxy({}, { get: (_, name) => component(name) }),
    '../store/auth': authModule({ id: 1, userType: 'INTERNAL' }, []),
    '../api/types': { fmtTime: String },
    '../api/client': { get: async (_url, config) => {
      requests.push(config)
      return { data: { list: rows, total: rows.length } }
    } },
  }).default
  let renderer
  await act(async () => { renderer = create(React.createElement(Page, { projectId: 1, projectStatus: 'IN_PROGRESS', revision: 'r1', active: false })) })
  rows = [messageFixture(2), ...rows]
  await act(async () => renderer.update(React.createElement(Page, { projectId: 1, projectStatus: 'IN_PROGRESS', revision: 'r2', active: false })))
  assert.equal(requests.length, 1, 'inactive panels perform only their initial load')
  assert.deepEqual(visibleMessageIds(renderer), [1])

  await act(async () => renderer.update(React.createElement(Page, { projectId: 1, projectStatus: 'IN_PROGRESS', revision: 'r2', active: true })))
  assert.equal(requests.length, 2)
  assert.equal(requests[1].quietNetworkError, true)
  assert.deepEqual(visibleMessageIds(renderer), [2, 1])
  await act(async () => renderer.unmount())
})

function loadTs(relativePath, mocks, globals = {}) {
  const filename = path.resolve(__dirname, '..', relativePath)
  const source = ts.transpileModule(fs.readFileSync(filename, 'utf8'), {
    compilerOptions: { module: ts.ModuleKind.CommonJS, jsx: ts.JsxEmit.ReactJSX, target: ts.ScriptTarget.ES2022, esModuleInterop: true },
  }).outputText
  const exports = {}
  vm.runInNewContext(source, {
    exports, module: { exports }, console, setTimeout, clearTimeout, URL, URLSearchParams, AbortController, FormData, ...globals,
    require: (name) => {
      if (typeof name === 'string' && name in mocks) return name === 'react-router-dom' ? { Link: component('Link'), ...mocks[name] } : mocks[name]
      if (name.endsWith('/store/collaboration')) return { useCollaboration: (selector) => selector({ revision: '', unreadCount: 0, refresh: async () => {} }) }
      if (name.endsWith('/CollaborationNotifications')) return component('CollaborationNotifications')
      if (name === './MessageImages') return { MessageImageComposer: component('MessageImageComposer'), MessageImages: component('MessageImages'), pasteMessageImages() {} }
      if (name.endsWith('.css')) return {}
      if (name.endsWith('.json')) return JSON.parse(fs.readFileSync(path.resolve(path.dirname(filename), name), 'utf8'))
      if (typeof name === 'string' && name.replace(/\\/g, '/').endsWith('/ActionSlots')) return actionSlotsModule
      if (typeof name === 'string' && name.replace(/\\/g, '/').endsWith('/PasswordInput')) return component('PasswordInput')
      if (typeof name === 'string' && name.startsWith('.') && name.replace(/\\/g, '/').endsWith('/utils/password')) {
        return loadTs('src/utils/password.ts', {})
      }
      if (name.endsWith('/textRules')) return loadTs('src/utils/textRules.ts', {})
      if (name === './auditLogDetails') return loadTs('src/pages/system/auditLogDetails.ts', {})
      return require(name)
    },
  }, { filename })
  return exports
}

function createReceiptBrowser() {
  const makeEventTarget = () => {
    const listeners = new Map()
    return {
      addEventListener(type, listener) {
        const current = listeners.get(type) ?? new Set()
        current.add(listener)
        listeners.set(type, current)
      },
      removeEventListener(type, listener) {
        listeners.get(type)?.delete(listener)
      },
      dispatch(type) {
        for (const listener of [...(listeners.get(type) ?? [])]) listener({ type })
      },
    }
  }
  const document = makeEventTarget()
  document.visibilityState = 'visible'
  const window = makeEventTarget()
  window.innerHeight = 1000
  const navigator = { onLine: true }
  window.navigator = navigator
  const timers = new Map()
  let nextTimerId = 0
  const setTimeout = (callback, delay) => {
    const id = ++nextTimerId
    timers.set(id, { callback, delay })
    return id
  }
  const clearTimeout = (id) => timers.delete(id)
  const state = { nodes: [] }
  const listNode = {
    querySelectorAll: () => state.nodes,
  }
  return {
    document,
    window,
    navigator,
    state,
    listNode,
    timers,
    globals: { document, window, navigator, setTimeout, clearTimeout },
  }
}

function receiptNode(id, rect) {
  return {
    dataset: { messageId: String(id) },
    getBoundingClientRect: () => rect,
  }
}

async function flushReceiptMicrotasks() {
  await Promise.resolve()
  await Promise.resolve()
}

async function runReceiptTimer(browser, delay) {
  const entry = [...browser.timers.entries()].find(([, timer]) => timer.delay === delay)
  assert.ok(entry, `expected a receipt timer with ${delay}ms delay`)
  const [id, timer] = entry
  browser.timers.delete(id)
  await act(async () => {
    await timer.callback()
    await flushReceiptMicrotasks()
  })
}

function startReceiptTimer(browser, delay) {
  const entry = [...browser.timers.entries()].find(([, timer]) => timer.delay === delay)
  assert.ok(entry, `expected a receipt timer with ${delay}ms delay`)
  const [id, timer] = entry
  browser.timers.delete(id)
  timer.callback()
}

function receiptCountButton(renderer) {
  return renderer.root.findAllByType('Button')
    .find((node) => String(node.props['aria-label'] ?? '').startsWith('查看留言回执'))
}

for (const [page, api, props] of [
  ['pages/project/ProjectList.tsx', '/project-groups', {}],
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
  assert.equal(renderer.root.findByType('Drawer').findAllByType('Table').length,0, 'an unknown account list must not masquerade as an empty table')
  assert.ok(renderer.root.findAll((node)=>node.props.children==='账号列表暂不可用').length > 0)
  assert.equal(renderer.root.findAllByType('Button').find(n=>n.props.children==='重试').props.loading,false)
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
    assert.equal(renderer.root.findByType('Drawer').props.focusLock, false, 'Only the child dialog may own focus')
    await act(async () => renderer.root.findByType('Drawer').props.onCancel())
    assert.equal(renderer.root.findByType('Drawer').props.visible, true, 'The parent cannot close underneath the dialog')
    assert.equal(resetCalls, 1, 'opening a supplier password reset must start with an empty form')
    await act(async () => renderer.root.findAllByType('Modal').find((node) => node.props.visible).props.onCancel())
    assert.equal(renderer.root.findByType('Drawer').props.focusLock, true, 'Closing the child restores the drawer focus boundary')
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
      get: async (url) => url.endsWith('/accounts') || url.endsWith('/supplier-role-options') ? { data: [] } : { data: { list: [supplier], total: 1, page: 1, pageSize: 10 } },
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
      file: 'src/pages/project/ProjectList.tsx', api: '/project-groups', props: {},
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
    const failureText = '加载失败'
    assert.ok(renderer.root.findAll((node) => node.props && node.props.children === failureText).length > 0, item.file)
    const retry = renderer.root.findAllByType('Button').find((node) => node.props.children === '重试')
    assert.ok(retry, `${item.file} must offer retry`)
    fail = false
    await act(async () => retry.props.onClick())
    assert.equal(renderer.root.findAll((node) => node.props && node.props.children === failureText).length, 0, item.file)
    await act(async () => renderer.unmount())
  }
})

test('list actions cannot let an old refresh overwrite a newer filter result', async () => {
  const iconMock = new Proxy({}, { get: (_, name) => component(name) })
  const auth = authModule({ id: 1, userType: 'INTERNAL', isSystemAdmin: true }, [
    'project:status', 'project:update', 'project:delete', 'supplier:account', 'supplier:manage', 'user:manage', 'role:manage',
  ])
  const cases = [
    {
      file: 'src/pages/project/ProjectList.tsx', api: '/project-groups',
      row: { id: 1, name: '旧主项目', status: 'DRAFT', supplierId: 1, subprojectCount: 0 },
      action: (actions) => findElement(actions, (node) => typeof node.props.onOk === 'function' && String(node.props.title).includes('删除')),
      invoke: (node) => node.props.onOk(),
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
      delete: async () => ({}),
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

test('main projects with pending child acceptance expose a disabled edit action and reason', async () => {
  const group = {
    id: 3, name: '待验收主项目', supplierId: 8, status: 'IN_PROGRESS',
    subprojectCount: 2, completedCount: 0, pendingCount: 1, terminatedCount: 0,
    workOrderNos: [], unreadMessages: 0,
  }
  const Page = loadTs('src/pages/project/ProjectList.tsx', {
    '@arco-design/web-react': arco,
    '@arco-design/web-react/icon': new Proxy({}, { get: (_, name) => component(name) }),
    'react-router-dom': { useNavigate: () => () => {} },
    '../../api/client': {
      get: async (url) => url === '/project-groups'
        ? { data: { list: [group], total: 1, page: 1, pageSize: 10 } }
        : { data: [] },
    },
    '../../store/auth': authModule({ id: 1, userType: 'INTERNAL' }, ['project:update']),
    '../../api/types': { PROJECT_STATUS: { IN_PROGRESS: { text: '进行中' } } },
    '../../components/ActionSlots': actionSlotsModule,
  }).default
  let renderer
  await act(async () => { renderer = create(React.createElement(Page)) })
  const actions = renderer.root.findByType('Table').props.columns.at(-1).render(null, group)
  const edit = findActionButton(actions, '编辑')
  const tooltip = findElement(actions, (node) => String(node.props.content ?? '').includes('待公司验收'))
  assert.ok(edit)
  assert.equal(edit.props.disabled, true)
  assert.match(String(tooltip.props.content), /1 个子项目待公司验收/)
  await act(async () => renderer.unmount())
})

test('dashboard and project detail panels expose retry instead of a false empty state', async () => {
  const iconMock = new Proxy({}, { get: (_, name) => component(name) })
  const state = { user: { id: 1, realName: '管理员', userType: 'INTERNAL' }, menus: ['dashboard'], hasPerm: () => false }
  const auth = { useAuth: (selector) => selector ? selector(state) : state }
  const cases = [
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
      if (url === '/dashboard/messages') return { data: { list: [], total: 0, page: 1, pageSize: 10 } }
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
    '../store/auth': authModule({ id: 1, realName: '管理员', userType: 'INTERNAL' }, ['project:confirm'], ['dashboard']),
    '../api/types': { fmtTime: String },
    'react-router-dom': { Link: component('Link'), useNavigate: () => () => {} },
  }).default
  let renderer
  await act(async () => { renderer = create(React.createElement(Page)) })

  assert.deepEqual(JSON.parse(JSON.stringify(pendingRequests[0].params)), { page: 1, pageSize: 10 })
  await act(async () => pendingRequests[0].reject(new Error('pending projects unavailable')))
  assert.ok(renderer.root.findAll((node) => node.props.children === '内部待验收子项目加载失败').length > 0)
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

  const refresh = renderer.root.findAllByType('Card').find((node) => node.props.className?.split(' ').includes('dashboard-pending')).props.extra
  assert.ok(refresh, 'a workflow status change must have a reachable list refresh')
  await act(async () => refresh.props.onClick())
  await act(async () => pendingRequests[4].resolve({ data: { list: [], total: 0, page: 1, pageSize: 10 } }))
  assert.ok(renderer.root.findAllByType('Empty').some((node) => node.props.description === '暂无内部待验收子项目'))
  await act(async () => renderer.unmount())
})

test('supplier dashboard uses company-acceptance wording while internal wording stays role-specific', async () => {
  const http = {
    get: async (url) => {
      if (url === '/dashboard/summary') return { data: { projectCount: 1, activeProjectCount: 1, pendingConfirmations: 0, unreadMessages: 0, recentMessages: [] } }
      return { data: { list: [], total: 0, page: 1, pageSize: 10 } }
    },
  }
  const Page = loadTs('src/pages/Dashboard.tsx', {
    '@arco-design/web-react': new Proxy({
      Grid: Object.assign(component('Grid'), { Row: component('Grid.Row'), Col: component('Grid.Col') }),
      List: Object.assign(component('List'), { Item: Object.assign(component('List.Item'), { Meta: component('List.Item.Meta') }) }),
      Typography: arco.Typography,
      Message: arco.Message,
    }, { get: (obj, key) => obj[key] ?? component(key) }),
    '@arco-design/web-react/icon': new Proxy({}, { get: (_, name) => component(name) }),
    '../api/client': http,
    '../store/auth': authModule({ id: 2, realName: '供应商用户', userType: 'SUPPLIER' }, [], ['dashboard']),
    '../api/types': { fmtTime: String },
    'react-router-dom': { Link: component('Link'), useNavigate: () => () => {} },
  }).default
  let renderer
  await act(async () => { renderer = create(React.createElement(Page)) })
  const text = renderer.root.findAll((node) => typeof node.props.children === 'string')
    .map((node) => node.props.children)
  const pendingCard = renderer.root.findAllByType('Card')
    .find((node) => String(node.props.className ?? '').includes('dashboard-pending'))
  const pendingHeading = findElement(pendingCard.props.title, (node) => node.type === 'h2')
  assert.ok(text.includes('跟进待公司验收与未读留言'))
  assert.equal(pendingHeading.props.children, '待公司验收子项目')
  assert.ok(renderer.root.findAllByType('Statistic').some((node) => node.props.title === '待公司验收子项目'))
  assert.equal(text.includes('公司内部待验收子项目'), false)
  assert.equal(text.includes('优先处理公司内部验收与未读留言'), false)
  await act(async () => renderer.unmount())
})

test('dashboard without its menu permission stays local and does not call dashboard APIs', async () => {
  const calls = []
  const Page = loadTs('src/pages/Dashboard.tsx', {
    '@arco-design/web-react': new Proxy({
      Grid: Object.assign(component('Grid'), { Row: component('Grid.Row'), Col: component('Grid.Col') }),
      List: Object.assign(component('List'), { Item: Object.assign(component('List.Item'), { Meta: component('List.Item.Meta') }) }),
      Typography: arco.Typography,
      Message: arco.Message,
    }, { get: (obj, key) => obj[key] ?? component(key) }),
    '@arco-design/web-react/icon': new Proxy({}, { get: (_, name) => component(name) }),
    '../api/client': { get: async (url) => { calls.push(url); throw new Error('must not request') } },
    '../store/auth': authModule({ id: 1, realName: '受限用户', userType: 'INTERNAL' }, [], []),
    '../api/types': { fmtTime: String },
    'react-router-dom': { Link: component('Link'), useNavigate: () => () => {} },
  }).default
  let renderer
  await act(async () => { renderer = create(React.createElement(Page)) })
  assert.deepEqual(calls, [])
  assert.equal(renderer.root.findByType('Result').props.title, '工作台不可用')
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
        if (url === '/admin/system/configs') return { data: [{ key: 'storage.warn_percent', value: '85' }] }
        if (url === '/admin/system/storage') return { data: { totalBytes: 100, availableBytes: 40, usedPercent: 60, warnPercent: 80, warning: false, root: '/' } }
        return { data: {
          configured: false,
          notificationsEnabled: true,
          queue: { pending: 2, sending: 1, sent: 8, failed: 3, cancelled: 4 },
          latestSentAt: '2026-09-09T01:00:00Z',
          latestFailedAt: '2026-09-09T02:00:00Z',
          missingEmailCount: 1,
          missingEmailAccounts: [{ userId: 7, employeeNo: 'E7', realName: '未填邮箱', userType: 'INTERNAL', status: 'ACTIVE' }],
          recent: [
            { id: 11, action: 'EMAIL_FAILED', targetType: 'email_outbox', targetId: '11', detail: { error: 'SMTP 连接失败' }, createdAt: '2026-09-09T02:00:00Z' },
            { id: 12, action: 'EMAIL_CANCELLED_STALE', targetType: 'email_outbox', targetId: '12', detail: { reason: '项目状态已变化' }, createdAt: '2026-09-09T03:00:00Z' },
            { id: 13, action: 'EMAIL_FUTURE_ACTION', targetType: 'email_outbox', targetId: '13', detail: {}, createdAt: '2026-09-09T04:00:00Z' },
          ],
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
  assert.ok(!calls.includes('/admin/system/storage'))
  assert.equal(renderer.root.findAll(n => n.props.title === '存储状态').length, 0)
  assert.ok(renderer.root.findAll((node) => node.props.title === '邮件发送').length > 0)
  const tables = renderer.root.findAllByType('Table')
  assert.ok(tables.every(table => !table.props.data?.some(row => row.key === 'storage.warn_percent')))
  const mailTable = tables.find((table) => table.props.data?.some((row) => row.action === 'EMAIL_FAILED'))
  assert.ok(mailTable)
  const cancelled = mailTable.props.data.find((row) => row.action === 'EMAIL_CANCELLED_STALE')
  const unknown = mailTable.props.data.find((row) => row.action === 'EMAIL_FUTURE_ACTION')
  assert.match(String(mailTable.props.columns[1].render(cancelled.action).props.children), /已取消/)
  assert.equal(mailTable.props.columns[2].render(null, cancelled), '项目状态已变化')
  assert.match(String(mailTable.props.columns[1].render(unknown.action).props.children), /未知动作/)
  assert.equal(mailTable.props.columns[2].render(null, unknown), '邮件处理结果未识别')
  assert.ok(renderer.root.findAll((node) => node.type === 'strong' && node.props.children === 4).length > 0)
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

test('receipt sync updates visible counts without replacing loaded messages or the draft', async () => {
  const browser = createReceiptBrowser()
  const messages = [
    { id: 1, senderId: 2, senderName: '成员一', senderType: 'INTERNAL', content: '第一条内容', readCount: 0, totalCount: 1, readByMe: true, createdAt: '' },
    { id: 2, senderId: 2, senderName: '成员二', senderType: 'INTERNAL', content: '第二条内容', readCount: 0, totalCount: 1, readByMe: true, createdAt: '' },
  ]
  const requests = []
  const Page = loadTs('src/components/MessagePanel.tsx', {
    '@arco-design/web-react': arco,
    '@arco-design/web-react/icon': new Proxy({}, { get: (_, n) => component(n) }),
    '../store/auth': authModule({ id: 1, userType: 'INTERNAL' }, ['message:create']),
    '../api/types': { fmtTime: String },
    '../api/client': {
      get: async (url, config) => {
        requests.push({ url, config })
        if (url === '/projects/1/messages') return { data: { list: messages, total: messages.length } }
        if (url === '/projects/1/message-receipts') return { data: [{ id: 1, readCount: 1, totalCount: 1 }] }
        throw new Error(`unexpected request: ${url}`)
      },
    },
  }, browser.globals).default
  let renderer
  await act(async () => {
    renderer = create(React.createElement(Page, { projectId: 1, projectStatus: 'IN_PROGRESS' }), {
      createNodeMock: () => browser.listNode,
    })
    await flushReceiptMicrotasks()
  })
  browser.state.nodes = [
    receiptNode(1, { width: 120, height: 32, top: 12, bottom: 44 }),
    receiptNode(2, { width: 120, height: 32, top: 1100, bottom: 1132 }),
  ]
  await act(async () => renderer.root.findByType('Input.TextArea').props.onChange('正在编辑的草稿'))
  await runReceiptTimer(browser, 5000)

  const receiptRequests = requests.filter(({ url }) => url.endsWith('/message-receipts'))
  assert.equal(receiptRequests.length, 1)
  assert.equal(receiptRequests[0].config.params.ids, '1', 'only the visible message id is polled')
  assert.equal(receiptCountButton(renderer).props['aria-label'], '查看留言回执：1/1')
  assert.equal(renderer.root.findAllByProps({ className: 'msg-item' }).length, 2)
  assert.equal(renderer.root.findByType('Input.TextArea').props.value, '正在编辑的草稿')
  assert.equal(renderer.root.findAll((node) => node.props.children === '第一条内容').length, 1)
  assert.equal(renderer.root.findAll((node) => node.props.children === '第二条内容').length, 1)
  await act(async () => renderer.unmount())
})

test('receipt sync pauses while hidden, resumes on focus, and cleans up when inactive', async () => {
  const browser = createReceiptBrowser()
  const messages = [{ id: 1, senderId: 2, senderName: '成员', senderType: 'INTERNAL', content: '可见留言', readCount: 0, totalCount: 1, readByMe: true, createdAt: '' }]
  let receiptCount = 0
  let receiptCalls = 0
  const Page = loadTs('src/components/MessagePanel.tsx', {
    '@arco-design/web-react': arco,
    '@arco-design/web-react/icon': new Proxy({}, { get: (_, n) => component(n) }),
    '../store/auth': authModule({ id: 1, userType: 'INTERNAL' }, []),
    '../api/types': { fmtTime: String },
    '../api/client': {
      get: async (url) => {
        if (url === '/projects/1/messages') return { data: { list: messages, total: 1 } }
        if (url === '/projects/1/message-receipts') {
          receiptCalls++
          receiptCount++
          return { data: [{ id: 1, readCount: receiptCount, totalCount: Math.max(1, receiptCount) }] }
        }
        throw new Error(`unexpected request: ${url}`)
      },
    },
  }, browser.globals).default
  let renderer
  await act(async () => {
    renderer = create(React.createElement(Page, { projectId: 1, projectStatus: 'IN_PROGRESS', active: true }), {
      createNodeMock: () => browser.listNode,
    })
    await flushReceiptMicrotasks()
  })
  browser.state.nodes = [receiptNode(1, { width: 120, height: 32, top: 12, bottom: 44 })]
  await runReceiptTimer(browser, 5000)
  assert.equal(receiptCountButton(renderer).props['aria-label'], '查看留言回执：1/1')

  browser.document.visibilityState = 'hidden'
  await act(async () => {
    browser.document.dispatch('visibilitychange')
    await flushReceiptMicrotasks()
  })
  const callsWhileHidden = receiptCalls
  assert.equal(browser.timers.size, 0, 'hidden state clears the scheduled poll')

  browser.document.visibilityState = 'visible'
  await act(async () => {
    browser.window.dispatch('focus')
    await flushReceiptMicrotasks()
  })
  assert.equal(receiptCalls, callsWhileHidden + 1, 'focus resumes polling immediately')
  assert.equal(receiptCountButton(renderer).props['aria-label'], '查看留言回执：2/2')

  await act(async () => renderer.update(React.createElement(Page, { projectId: 1, projectStatus: 'IN_PROGRESS', active: false })))
  const callsAfterInactive = receiptCalls
  assert.equal(browser.timers.size, 0, 'inactive panels must not retain a poll timer')
  await act(async () => {
    browser.window.dispatch('focus')
    browser.window.dispatch('online')
    browser.document.dispatch('visibilitychange')
    await flushReceiptMicrotasks()
  })
  assert.equal(receiptCalls, callsAfterInactive, 'inactive panels remove all wake listeners')
  await act(async () => renderer.unmount())
})

test('receipt sync ignores delayed responses after project switch and aborts on unmount', async () => {
  const browser = createReceiptBrowser()
  const pending = []
  const messageFor = (projectId) => ({
    id: 1, senderId: 2, senderName: '成员', senderType: 'INTERNAL', content: `项目${projectId}留言`,
    readCount: 0, totalCount: 1, readByMe: true, createdAt: '',
  })
  const Page = loadTs('src/components/MessagePanel.tsx', {
    '@arco-design/web-react': arco,
    '@arco-design/web-react/icon': new Proxy({}, { get: (_, n) => component(n) }),
    '../store/auth': authModule({ id: 1, userType: 'INTERNAL' }, []),
    '../api/types': { fmtTime: String },
    '../api/client': {
      get: async (url, config) => {
        const projectId = Number(url.match(/projects\/(\d+)/)?.[1])
        if (url.endsWith('/messages')) return { data: { list: [messageFor(projectId)], total: 1 } }
        if (url.endsWith('/message-receipts')) {
          if (projectId === 2) return { data: [{ id: 1, readCount: 0, totalCount: 1 }] }
          let resolve
          let reject
          const promise = new Promise((ok, fail) => { resolve = ok; reject = fail })
          pending.push({ projectId, config, resolve, reject })
          return promise
        }
        throw new Error(`unexpected request: ${url}`)
      },
    },
  }, browser.globals).default
  const renderOptions = { createNodeMock: () => browser.listNode }
  let renderer
  await act(async () => {
    renderer = create(React.createElement(Page, { projectId: 1, projectStatus: 'IN_PROGRESS', active: true }), renderOptions)
    await flushReceiptMicrotasks()
  })
  browser.state.nodes = [receiptNode(1, { width: 120, height: 32, top: 12, bottom: 44 })]
  await act(async () => {
    startReceiptTimer(browser, 5000)
    await flushReceiptMicrotasks()
  })
  assert.equal(pending.length, 1)

  browser.state.nodes = [receiptNode(1, { width: 120, height: 32, top: 12, bottom: 44 })]
  await act(async () => {
    renderer.update(React.createElement(Page, { projectId: 2, projectStatus: 'IN_PROGRESS', active: true }))
    await flushReceiptMicrotasks()
  })
  assert.equal(pending[0].config.signal.aborted, true, 'switching projects aborts the old receipt request')
  await act(async () => {
    pending[0].resolve({ data: [{ id: 1, readCount: 1, totalCount: 1 }] })
    await flushReceiptMicrotasks()
  })
  assert.equal(receiptCountButton(renderer).props['aria-label'], '查看留言回执：0/1')
  assert.equal(renderer.root.findAll((node) => node.props.children === '项目2留言').length, 1)
  await act(async () => renderer.unmount())

  browser.state.nodes = []
  let unmountedRenderer
  await act(async () => {
    unmountedRenderer = create(React.createElement(Page, { projectId: 3, projectStatus: 'IN_PROGRESS', active: true }), renderOptions)
    await flushReceiptMicrotasks()
  })
  browser.state.nodes = [receiptNode(1, { width: 120, height: 32, top: 12, bottom: 44 })]
  await act(async () => {
    startReceiptTimer(browser, 5000)
    await flushReceiptMicrotasks()
  })
  const lateUnmountRequest = pending.find((request) => request.projectId === 3)
  assert.ok(lateUnmountRequest)
  await act(async () => unmountedRenderer.unmount())
  assert.equal(lateUnmountRequest.config.signal.aborted, true, 'unmount aborts the in-flight receipt request')
  await act(async () => {
    lateUnmountRequest.resolve({ data: [{ id: 1, readCount: 1, totalCount: 1 }] })
    await flushReceiptMicrotasks()
  })
  assert.equal(unmountedRenderer.toJSON(), null)
})

test('receipt sync keeps the last count after failure and recovers on the backoff poll', async () => {
  const browser = createReceiptBrowser()
  const messages = [{ id: 1, senderId: 2, senderName: '成员', senderType: 'INTERNAL', content: '保留内容', readCount: 0, totalCount: 1, readByMe: true, createdAt: '' }]
  let fail = true
  let receiptCalls = 0
  const Page = loadTs('src/components/MessagePanel.tsx', {
    '@arco-design/web-react': arco,
    '@arco-design/web-react/icon': new Proxy({}, { get: (_, n) => component(n) }),
    '../store/auth': authModule({ id: 1, userType: 'INTERNAL' }, []),
    '../api/types': { fmtTime: String },
    '../api/client': {
      get: async (url) => {
        if (url === '/projects/1/messages') return { data: { list: messages, total: 1 } }
        if (url === '/projects/1/message-receipts') {
          receiptCalls++
          if (fail) throw new Error('receipts unavailable')
          return { data: [{ id: 1, readCount: 1, totalCount: 1 }] }
        }
        throw new Error(`unexpected request: ${url}`)
      },
    },
  }, browser.globals).default
  let renderer
  await act(async () => {
    renderer = create(React.createElement(Page, { projectId: 1, projectStatus: 'IN_PROGRESS', active: true }), {
      createNodeMock: () => browser.listNode,
    })
    await flushReceiptMicrotasks()
  })
  browser.state.nodes = [receiptNode(1, { width: 120, height: 32, top: 12, bottom: 44 })]
  await runReceiptTimer(browser, 5000)
  assert.equal(receiptCalls, 1)
  assert.equal(receiptCountButton(renderer).props['aria-label'], '查看留言回执：0/1', 'a failed poll preserves the last count')
  assert.ok([...browser.timers.values()].some((timer) => timer.delay === 10000), 'failure backs off the next poll')

  fail = false
  await runReceiptTimer(browser, 10000)
  assert.equal(receiptCalls, 2)
  assert.equal(receiptCountButton(renderer).props['aria-label'], '查看留言回执：1/1')
  assert.ok([...browser.timers.values()].some((timer) => timer.delay === 5000), 'a successful retry restores the normal interval')
  await act(async () => renderer.unmount())
})

test('receipt revision refreshes visible counts immediately without a realtime poll timer', async () => {
  const browser = createReceiptBrowser()
  const messages = [{ id: 1, senderId: 2, senderName: '成员', senderType: 'INTERNAL', content: '实时回执留言', readCount: 0, totalCount: 1, readByMe: true, createdAt: '' }]
  const visibleNodes = [
    receiptNode(1, { width: 120, height: 32, top: 12, bottom: 44 }),
    receiptNode(2, { width: 120, height: 32, top: 1100, bottom: 1132 }),
  ]
  const requests = []
  let receiptCalls = 0
  const Page = loadTs('src/components/MessagePanel.tsx', {
    '@arco-design/web-react': arco,
    '@arco-design/web-react/icon': new Proxy({}, { get: (_, n) => component(n) }),
    '../store/auth': authModule({ id: 1, userType: 'INTERNAL' }, ['message:create']),
    '../api/types': { fmtTime: String },
    '../api/client': {
      get: async (url, config) => {
        requests.push({ url, config })
        if (url === '/projects/1/messages') {
          browser.state.nodes = visibleNodes
          return { data: { list: messages, total: 1 } }
        }
        if (url === '/projects/1/message-receipts') {
          receiptCalls++
          return { data: [{ id: 1, readCount: 1, totalCount: 1 }] }
        }
        throw new Error(`unexpected request: ${url}`)
      },
    },
  }, browser.globals).default
  let renderer
  await act(async () => {
    renderer = create(React.createElement(Page, {
      projectId: 1, projectStatus: 'IN_PROGRESS', active: true,
      receiptRevision: 'receipt-1', realtimeConnected: true,
    }), { createNodeMock: () => browser.listNode })
    await flushReceiptMicrotasks()
  })
  assert.equal(browser.timers.size, 0, 'a connected realtime panel must not install a fixed poll timer')
  const input = renderer.root.findByType('Input.TextArea')
  await act(async () => input.props.onChange('保留中的实时草稿'))
  const beforeRevision = receiptCalls
  await act(async () => {
    renderer.update(React.createElement(Page, {
      projectId: 1, projectStatus: 'IN_PROGRESS', active: true,
      receiptRevision: 'receipt-2', realtimeConnected: true,
    }))
    await flushReceiptMicrotasks()
  })
  assert.equal(receiptCalls, beforeRevision + 1, 'a receipt revision must trigger an immediate refresh')
  assert.equal(requests.at(-1).config.params.ids, '1', 'the immediate refresh still limits itself to visible ids')
  assert.equal(receiptCountButton(renderer).props['aria-label'], '查看留言回执：1/1')
  assert.equal(renderer.root.findByType('Input.TextArea').props.value, '保留中的实时草稿')
  assert.equal(browser.timers.size, 0)
  await act(async () => renderer.unmount())
})

test('disconnecting realtime receipt updates restores the five second poll interval', async () => {
  const browser = createReceiptBrowser()
  const messages = [{ id: 1, senderId: 2, senderName: '成员', senderType: 'INTERNAL', content: '断线回执留言', readCount: 0, totalCount: 1, readByMe: true, createdAt: '' }]
  let receiptCalls = 0
  const Page = loadTs('src/components/MessagePanel.tsx', {
    '@arco-design/web-react': arco,
    '@arco-design/web-react/icon': new Proxy({}, { get: (_, n) => component(n) }),
    '../store/auth': authModule({ id: 1, userType: 'INTERNAL' }, []),
    '../api/types': { fmtTime: String },
    '../api/client': {
      get: async (url) => {
        if (url === '/projects/1/messages') {
          browser.state.nodes = [receiptNode(1, { width: 120, height: 32, top: 12, bottom: 44 })]
          return { data: { list: messages, total: 1 } }
        }
        if (url === '/projects/1/message-receipts') {
          receiptCalls++
          return { data: [{ id: 1, readCount: 0, totalCount: 1 }] }
        }
        throw new Error(`unexpected request: ${url}`)
      },
    },
  }, browser.globals).default
  let renderer
  await act(async () => {
    renderer = create(React.createElement(Page, {
      projectId: 1, projectStatus: 'IN_PROGRESS', active: true,
      receiptRevision: 'receipt-1', realtimeConnected: true,
    }), { createNodeMock: () => browser.listNode })
    await flushReceiptMicrotasks()
  })
  assert.equal(browser.timers.size, 0)
  const beforeDisconnect = receiptCalls
  await act(async () => {
    renderer.update(React.createElement(Page, {
      projectId: 1, projectStatus: 'IN_PROGRESS', active: true,
      receiptRevision: 'receipt-1', realtimeConnected: false,
    }))
    await flushReceiptMicrotasks()
  })
  assert.equal(receiptCalls, beforeDisconnect + 1, 'disconnecting should perform an immediate catch-up refresh')
  assert.ok([...browser.timers.values()].some((timer) => timer.delay === 5000))
  await runReceiptTimer(browser, 5000)
  assert.equal(receiptCalls, beforeDisconnect + 2)

  await act(async () => {
    renderer.update(React.createElement(Page, {
      projectId: 1, projectStatus: 'IN_PROGRESS', active: true,
      receiptRevision: 'receipt-1', realtimeConnected: true,
    }))
    await flushReceiptMicrotasks()
  })
  assert.equal(browser.timers.size, 0, 'reconnecting clears the fallback poll timer')
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
  assert.ok(table.props.columns.some((column) => column.dataIndex === 'roleName'))
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

// PDF cancellation, late responses and document cleanup are covered with the
// canvas renderer in pdf-preview.test.cjs (the iframe no longer creates blobs).

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
  await act(async () => findElement(nameCell, node => node.props['aria-label'] === '预览文件').props.onClick())
  const preview = renderer.root.findByType('Modal')
  assert.equal(preview.props.footer, null, 'preview has no download entry even with download permission')
  assert.equal(preview.props.className, 'file-preview-modal')
  await act(async () => renderer.unmount())
})

test('document preview branches and message image preview all mount a watermark layer', () => {
  const fileTable = fs.readFileSync(path.resolve(__dirname, '../src/components/FileTable.tsx'), 'utf8')
  for (const componentName of ['ExcelPreview', 'PdfPreview', 'ImagePreview', 'PptxPreview']) {
    assert.match(fileTable, new RegExp(`<WatermarkedPreview[\\s\\S]*?<${componentName}\\b`), `${componentName} watermark`)
  }
  const messageImages = fs.readFileSync(path.resolve(__dirname, '../src/components/MessageImages.tsx'), 'utf8')
  assert.match(messageImages, /className="file-preview-surface"[\s\S]*?PreviewWatermark/)
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
       '../../components/MessagePanel':component('Messages'),
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
     '../../components/MessagePanel':component('Messages'),
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
    '../../components/ProjectActivityPanel': component('Activities'),
    '../../components/ProjectWorkflowPanel': component('Workflow'),
  }).default
  let renderer
  await act(async () => { renderer = create(React.createElement(Page)) })
  const tabs = renderer.root.findByType('Tabs')
  assert.equal(tabs.props.activeTab, 'files')
  assert.ok(renderer.root.findByType('Files'))
  const ellipsis = renderer.root.findByType('Ellipsis')
  assert.equal(ellipsis.props.expandable.single, true, 'single-line descriptions must expose their expansion control')
  const expand = ellipsis.props.expandRender(false)
  assert.equal(expand.props['aria-expanded'], false)
  assert.equal(expand.props.children, '展开')
  await act(async () => renderer.unmount())
})

test('project detail isolates live message revision from summary churn while preserving fallback and reconnect invalidation', async () => {
  const project = {
    id: 1,
    name: '实时留言项目',
    status: 'IN_PROGRESS',
    supplierName: '供应商',
    updatedAt: '2026-09-14T00:00:00Z',
  }
  let activityRevision = 'activity-1'
  let summaryCalls = 0
  let collaboration = {
    revision: 'global-1',
    status: 'ready',
    realtimeStatus: 'connected',
    messageRevisions: { 1: 4 },
    receiptRevisions: {},
    reconnectRevision: 0,
  }
  const useCollaboration = (selector) => selector(collaboration)
  const Page = loadTs('src/pages/project/ProjectDetail.tsx', {
    '@arco-design/web-react': arco,
    'react-router-dom': {
      useParams: () => ({ id: '1' }),
      useSearchParams: () => [new URLSearchParams('tab=messages'), () => {}],
      useNavigate: () => () => {},
    },
    '../../api/client': {
      get: async (url) => {
        if (!url.endsWith('/summary')) return { data: project }
        summaryCalls++
        return { data: { unreadMessages: 0, activityRevision } }
      },
    },
    '../../api/types': { PROJECT_STATUS: { IN_PROGRESS: { text: '进行中', color: 'blue' } }, fmtTime: String },
    '../../components/FileTable': component('Files'),
    '../../components/MessagePanel': component('Messages'),
    '../../components/ProjectActivityPanel': component('Activities'),
    '../../components/ProjectWorkflowPanel': component('Workflow'),
    '../../store/collaboration': { useCollaboration },
  }).default
  let renderer
  const render = () => React.createElement(Page)
  const messages = () => renderer.root.findByType('Messages')
  await act(async () => { renderer = create(render()) })
  assert.equal(messages().props.revision, 'live:4:0')

  activityRevision = 'activity-2'
  collaboration = { ...collaboration, revision: 'global-2' }
  await act(async () => renderer.update(render()))
  assert.equal(summaryCalls, 2, 'the global reconciliation still refreshes the project summary')
  assert.equal(messages().props.revision, 'live:4:0', 'summary activity changes must not invalidate live messages a second time')

  activityRevision = 'activity-3'
  collaboration = { ...collaboration, revision: 'global-3', realtimeStatus: 'disconnected' }
  await act(async () => renderer.update(render()))
  assert.equal(messages().props.revision, 'poll:activity-3:4:0', 'disconnected mode retains summary-based polling invalidation')

  collaboration = { ...collaboration, realtimeStatus: 'connected', reconnectRevision: 1 }
  await act(async () => renderer.update(render()))
  assert.equal(messages().props.revision, 'live:4:1', 'a completed reconnect forces one catch-up invalidation')
  await act(async () => renderer.unmount())
})

test('project workflow keeps acceptance and management internal while supplier submitters may withdraw', async () => {
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
      project: { id: 1, status: 'PENDING_CONFIRMATION', confirmSide: 'COMPANY', latestSubmitterId: 1, latestSubmissionId: 31 },
      onChanged: () => changed.push(true),
    }))
  })
  assert.ok(renderer.root.findAllByType('Button').some((node) => node.props.children === '验收通过'))
  assert.ok(renderer.root.findAllByType('Button').some((node) => node.props.children === '验收驳回'))
  assert.equal(renderer.root.findAllByType('Button').some((node) => node.props.children === '撤回'), false)
  await act(async () => renderer.unmount())

  const SupplierPage = loadTs('src/components/ProjectWorkflowPanel.tsx', {
    '@arco-design/web-react': arco,
    '@arco-design/web-react/icon': new Proxy({}, { get: (_, name) => component(name) }),
    '../api/client': http,
    '../api/types': { PROJECT_STATUS: { PENDING_CONFIRMATION: { text: '待内部验收', color: 'orange' } } },
    '../store/auth': authModule({ id: 2, userType: 'SUPPLIER' }, ['project:confirm', 'project:withdraw']),
  }).default
  await act(async () => {
    renderer = create(React.createElement(SupplierPage, {
      project: { id: 1, status: 'PENDING_CONFIRMATION', confirmSide: 'COMPANY', latestSubmitterId: 2, latestSubmissionId: 32 },
      onChanged: () => changed.push(true),
    }))
  })
  assert.equal(renderer.root.findAllByType('Button').some((node) => node.props.children === '验收通过'), false)
  assert.equal(renderer.root.findAllByType('Button').some((node) => node.props.children === '验收驳回'), false)
  assert.ok(renderer.root.findAllByType('Button').some((node) => node.props.children === '撤回'))
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
      project: { id: 1, status: 'PENDING_CONFIRMATION', confirmSide: 'COMPANY', latestSubmitterId: 2, latestSubmissionId: 33 },
      onChanged: () => changed.push(true),
    }))
  })
  assert.equal(renderer.root.findAllByType('Button').some((node) => node.props.children === '撤回'), false)
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

test('only suppliers can submit a project for company acceptance', async () => {
  const calls = []
  const http = { post: async (url, body) => { calls.push({ url, body }); return { data: {} } } }
  const render = (user) => loadTs('src/components/ProjectWorkflowPanel.tsx', {
    '@arco-design/web-react': arco,
    '@arco-design/web-react/icon': new Proxy({}, { get: (_, name) => component(name) }),
    '../api/client': http,
    '../api/types': { PROJECT_STATUS: { IN_PROGRESS: { text: '进行中' } } },
    '../store/auth': authModule(user, ['project:submit']),
  }).default

  for (const user of [{ id: 1, userType: 'INTERNAL' }, { id: 2, userType: 'SUPPLIER' }]) {
    const Page = render(user)
    let renderer
    await act(async () => {
      renderer = create(React.createElement(Page, { project: { id: 1, status: 'IN_PROGRESS' }, onChanged() {} }))
    })
    const submit = renderer.root.findAllByType('Popconfirm').find((node) => node.props.title === '提交公司验收？')
    if (user.userType === 'INTERNAL') {
      assert.equal(submit, undefined)
      assert.equal(renderer.root.findAllByType('Button').some((node) => node.props.children === '提交公司验收'), false)
    } else {
      assert.ok(submit)
      assert.ok(renderer.root.findAllByType('Button').some((node) => node.props.children === '提交公司验收'))
      await act(async () => submit.props.onOk())
      assert.equal(calls.at(-1).url, '/projects/1/submit')
      assert.equal(calls.at(-1).body.confirmSide, 'COMPANY')
    }
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
      project: { id: 17, status: 'PENDING_CONFIRMATION', confirmSide: 'COMPANY', latestSubmissionId: 41 },
      onChanged() { assert.fail('a cancelled or failed rejection must not report success') },
    }))
  })
  const open = () => renderer.root.findAllByType('Button').find((node) => node.props.children === '验收驳回').props.onClick()
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
  assert.equal(calls[0].body.expectedSubmissionId, 41)
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
      '../store/auth': authModule(
        { id: action === 'submit' || action === 'withdraw' ? 2 : 1, userType: action === 'submit' || action === 'withdraw' ? 'SUPPLIER' : 'INTERNAL' },
        ['project:status', 'project:submit', 'project:confirm', 'project:withdraw'],
      ),
    }).default
    let renderer
    let changed = 0
    await act(async () => {
      renderer = create(React.createElement(Page, {
        project: { id: 17, status: action === 'start' ? 'DRAFT' : action === 'submit' ? 'IN_PROGRESS' : 'PENDING_CONFIRMATION', confirmSide: 'COMPANY', latestSubmitterId: 2, latestSubmissionId: 51 },
        onChanged() { changed += 1 },
      }))
    })
    const modal = () => renderer.root.findByType('Modal')
    let invoke
    if (action === 'start') invoke = renderer.root.findAllByType('Button').find((node) => node.props.children === '开始').props.onClick
    else if (action === 'reject') {
      await act(async () => renderer.root.findAllByType('Button').find((node) => node.props.children === '验收驳回').props.onClick())
      invoke = modal().props.onOk
    } else {
      const prefix = { submit: '提交公司验收', confirm: '确认通过公司验收', withdraw: '撤回验收申请' }[action]
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

test('project workflow actions keep the submission version shown when confirmation opened', async () => {
  let reason = '需要修改'
  const calls = []
  const form = {
    resetFields() { reason = '' },
    validate: async () => ({ reason }),
  }
  const localArco = new Proxy({
    ...arco,
    Form: Object.assign(component('Form'), { useForm: () => [form], Item: component('Form.Item') }),
  }, { get: (obj, key) => obj[key] ?? component(key) })
  const Page = loadTs('src/components/ProjectWorkflowPanel.tsx', {
    '@arco-design/web-react': localArco,
    '@arco-design/web-react/icon': new Proxy({}, { get: (_, name) => component(name) }),
    '../api/client': { post: async (url, body) => { calls.push({ url, body }); return { data: {} } } },
    '../api/types': { PROJECT_STATUS: { PENDING_CONFIRMATION: { text: '待确认' } } },
    '../store/auth': authModule(
      { id: 1, userType: 'INTERNAL' },
      ['project:confirm', 'project:withdraw', 'project:view_all'],
    ),
  }).default
  const project = (latestSubmissionId) => ({
    id: 17,
    status: 'PENDING_CONFIRMATION',
    confirmSide: 'COMPANY',
    latestSubmitterId: 2,
    latestSubmissionId,
  })
  let renderer
  await act(async () => {
    renderer = create(React.createElement(Page, { project: project(61), onChanged() {} }))
  })

  let confirm = renderer.root.findAllByType('Popconfirm')
    .find((node) => String(node.props.title).startsWith('确认通过公司验收'))
  await act(async () => confirm.props.onVisibleChange(true))
  await act(async () => renderer.update(React.createElement(Page, { project: project(62), onChanged() {} })))
  confirm = renderer.root.findAllByType('Popconfirm')
    .find((node) => String(node.props.title).startsWith('确认通过公司验收'))
  await act(async () => confirm.props.onOk())
  assert.deepEqual(JSON.parse(JSON.stringify(calls.at(-1))), {
    url: '/projects/17/confirm',
    body: { expectedSubmissionId: 61 },
  })

  await act(async () => renderer.root.findAllByType('Button')
    .find((node) => node.props.children === '验收驳回').props.onClick())
  await act(async () => renderer.update(React.createElement(Page, { project: project(63), onChanged() {} })))
  await act(async () => renderer.root.findByType('Modal').props.onOk())
  assert.deepEqual(JSON.parse(JSON.stringify(calls.at(-1))), {
    url: '/projects/17/reject',
    body: { reason: '需要修改', expectedSubmissionId: 62 },
  })

  const SupplierPage = loadTs('src/components/ProjectWorkflowPanel.tsx', {
    '@arco-design/web-react': localArco,
    '@arco-design/web-react/icon': new Proxy({}, { get: (_, name) => component(name) }),
    '../api/client': { post: async (url, body) => { calls.push({ url, body }); return { data: {} } } },
    '../api/types': { PROJECT_STATUS: { PENDING_CONFIRMATION: { text: '待确认' } } },
    '../store/auth': authModule({ id: 2, userType: 'SUPPLIER' }, ['project:withdraw']),
  }).default
  await act(async () => renderer.update(React.createElement(SupplierPage, { project: project(63), onChanged() {} })))
  let withdraw = renderer.root.findAllByType('Popconfirm')
    .find((node) => String(node.props.title).startsWith('撤回验收申请'))
  await act(async () => withdraw.props.onVisibleChange(true))
  await act(async () => renderer.update(React.createElement(SupplierPage, { project: project(64), onChanged() {} })))
  withdraw = renderer.root.findAllByType('Popconfirm')
    .find((node) => String(node.props.title).startsWith('撤回验收申请'))
  await act(async () => withdraw.props.onOk())
  assert.deepEqual(JSON.parse(JSON.stringify(calls.at(-1))), {
    url: '/projects/17/withdraw',
    body: { expectedSubmissionId: 63 },
  })
  await act(async () => renderer.unmount())
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

test('completed legacy supplier acceptance remains readable without appearing as pending internal acceptance', async () => {
  const Page = loadTs('src/components/ProjectActivityPanel.tsx', {
    '@arco-design/web-react': activityArco(),
    '../api/client': { get: async () => ({ data: {
      list: [activityRow(6, 'PROJECT', { action: 'CONFIRM' })], nextCursor: null,
      summary: { status: 'COMPLETED', pendingConfirmation: false, confirmSide: 'SUPPLIER', lastActivityAt: null },
    } }) },
    '../api/types': { PROJECT_STATUS: { COMPLETED: { text: '已完成' } }, fmtTime: String },
  }).default
  let renderer
  await act(async () => { renderer = create(React.createElement(Page, { projectId: 1 })) })
  assert.equal(renderer.root.findAll((node) => node.props['data-activity-id'] === 6).length, 1)
  assert.ok(renderer.root.findAll((node) => node.props.children === '无待验收申请').length > 0)
  assert.equal(renderer.root.findAll((node) => node.props.children === '项目动态加载失败').length, 0)
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
          summary: { status: 'IN_PROGRESS', pendingConfirmation: true, confirmSide: 'COMPANY', lastActivityAt: '2026-09-09T00:00:01Z' },
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
    '../../components/ProjectActivityPanel': component('Activities'),
    '../../components/ProjectWorkflowPanel': component('Workflow'),
  }).default
  let renderer
  await act(async () => { renderer = create(React.createElement(Page)) })
  assert.equal(renderer.root.findByType('Tabs').props.activeTab, 'activity')
  const activities = renderer.root.findByType('Activities')
  assert.equal(activities.props.active, true)
  assert.equal(renderer.root.findByType('Messages').props.active, false)
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
  await act(async () => renderer.root.findAllByType('Tabs')[0].props.onChange('messages'))
  await act(async () => renderer.update(React.createElement(Page)))
  assert.equal(renderer.root.findByType('Messages').props.active, true)
  assert.equal(renderer.root.findByType('Activities').props.active, false)
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

test('permission groups cascade on clicks while preserving existing menu-only grants', async () => {
  const role={id:7,name:'测试',permissionIds:[5],assignedUserCount:0,status:'ACTIVE'}
  const perms=[{id:1,name:'工作台',type:'MENU',parentId:null},{id:5,name:'部门',type:'MENU',parentId:null},{id:24,name:'管理部门',type:'ACTION',parentId:5},{id:25,name:'删除部门',type:'ACTION',parentId:5}]
  let saved
  const Page=loadTs('src/pages/rbac/RoleList.tsx',{
    '@arco-design/web-react':arco,'@arco-design/web-react/icon':new Proxy({},{get:(_,n)=>component(n)}),
    '../../api/client':{get:async url=>({data:url==='/permissions'?perms:{list:[role],total:1}}),put:async(url,body)=>{saved=body.permissionIds}},
    '../../store/auth':authModule({id:1,userType:'INTERNAL',isSystemAdmin:false},['role:manage']),
  }).default
  let renderer
  await act(async()=>{renderer=create(React.createElement(Page))})
  const actions=renderer.root.findByType('Table').props.columns.at(-1).render(null,role)
  await act(async()=>findActionButton(actions, '分配权限').props.onClick())
  const tree=()=>renderer.root.findByType('Tree')
  const check=async(key,checked)=>act(async()=>tree().props.onCheck([],{checked,node:{key}}))
  assert.ok(tree().props.halfCheckedKeys.includes('5'),'menu-only access is visibly partial')
  assert.ok(!tree().props.checkedKeys.includes('24'),'opening existing grants must not expand permissions')
  await check('1',true)
  assert.ok(tree().props.halfCheckedKeys.includes('5'),'editing an unrelated menu preserves menu-only access')
  await check('5',true)
  assert.deepEqual([...tree().props.checkedKeys].sort(),['1','24','25','5'])
  assert.equal(tree().props.halfCheckedKeys.length,0)
  await check('24',false)
  assert.ok(tree().props.halfCheckedKeys.includes('5'))
  assert.ok(tree().props.checkedKeys.includes('25'))
  await check('5',true)
  assert.ok(tree().props.checkedKeys.includes('24'),'clicking a partial parent selects all children')
  await check('5',false)
  assert.deepEqual([...tree().props.checkedKeys],['1'],'unchecking parent clears only its own branch')
  assert.equal(tree().props.halfCheckedKeys.length,0)
  await check('24',true)
  assert.ok(tree().props.halfCheckedKeys.includes('5'))
  await act(async()=>findActionButton(renderer.root.findByType('Drawer').props.footer,'保存权限').props.onClick())
  assert.deepEqual([...saved].sort((a,b)=>a-b),[1,5,24],'saving includes the menu for a partially selected branch')
  await act(async()=>renderer.unmount())
})

test('deleting the only row on a controlled last page reloads the last page allowed by the response total', async () => {
  const iconMock = new Proxy({}, { get: (_, name) => component(name) })
  const cases = [
    {
      label: 'projects', page: 'src/pages/project/ProjectList.tsx', pageSize: 10,
      row: { id: 91, name: '末页主项目', status: 'DRAFT', subprojectCount: 0 },
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
  const receiptTrigger = pageRenderer.root.findByType('Popover').props.children
  assert.equal(receiptTrigger.props['aria-label'], '查看留言回执：0/1')
  assert.equal(pageRenderer.root.findByProps({ 'aria-label': '查看留言回执：0/1' }).findAllByType('Button').length > 0, true)
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
    const form = { resetFields() {}, setFieldValue() {}, validate: async () => ({
      name: '项目', supplierId: 8, workOrderNos: ['WO-1'], machineModel: 'M1', robotVendorId: 11,
      robotModelId: 12, responsibleUserId: 13, priorityId: 14, expectedCompletionDate: '2026-09-30', subprojectNames: ['子项目-A'],
    }) }
    const testArco = new Proxy({
      Form: Object.assign(component('Form'), { useForm: () => [form], Item: component('Form.Item') }),
      Typography: arco.Typography, Message: arco.Message,
    }, { get: (obj, key) => obj[key] ?? component(key) })
    const http = {
      get: async (url) => {
        if (url === '/project-groups') return { data: { list: [], total: 0, page: 1, pageSize: 10 } }
        if (url === '/project-owner-options') return { data: [{ id: 13, employeeNo: 'owner', realName: '负责人', sectionName: '研发课' }] }
        if (url === '/project-dictionaries') return { data: [{ id: 11, name: '字典项', enabled: true }] }
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
    const createButton = renderer.root.findAllByType('Button').find((node) => node.props.children === '新建主项目')
    assert.equal(createButton.props.disabled, true, 'project creation must wait for supplier options')
    await act(async () => { rejectFirst(new Error('supplier options unavailable')); await Promise.resolve() })
    assert.ok(renderer.root.findAllByType('Button').some((node) => String(node.props.children).includes('重试')))
    const retry = renderer.root.findAllByType('Button').find((node) => String(node.props.children).includes('重试'))
    await act(async () => retry.props.onClick())
    assert.equal(optionAttempt, 2)
    assert.equal(renderer.root.findAllByType('Button').find((node) => node.props.children === '新建主项目').props.disabled, false)
    await act(async () => renderer.root.findAllByType('Button').find((node) => node.props.children === '新建主项目').props.onClick())
    await act(async () => { await Promise.resolve() })
    await act(async () => renderer.root.findByProps({ placeholder: '选择负责人' }).props.onChange(13))
    await act(async () => renderer.root.findByType('Modal').props.onOk())
    assert.equal(posted, 1)
    await act(async () => renderer.unmount())
  }

  {
    let posted = 0
    let optionRound = 0
    const first = []
    const form = { resetFields() {}, setFieldsValue() {}, validate: async () => ({ employeeNo: 'new_user', password: '123456', realName: '新用户', email: 'new@example.invalid', departmentId: 4, roleId: 3 }) }
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
  assert.match(JSON.stringify(action), /验收驳回/)
  const summary = table.props.columns.find((column) => column.title === '内容摘要').render(null, row)
  assert.match(JSON.stringify(summary), /待验收/)
  assert.match(JSON.stringify(summary), /进行中/)
  assert.match(JSON.stringify(summary), /驳回原因：需要补充资料/)

  const actionSelect = renderer.root.findAllByType('Select').find((select) => select.props.placeholder === '具体操作')
  const values = React.Children.toArray(actionSelect.props.children).map((option) => option.props.value)
  for (const value of ['PROJECT_START', 'PROJECT_SUBMIT', 'PROJECT_CONFIRM', 'PROJECT_REJECT', 'PROJECT_WITHDRAW', 'PROJECT_TERMINATE', 'PROJECT_RESTART']) {
    assert.ok(values.includes(value), `项目日志筛选缺少 ${value}`)
  }
  const copyRow = {
    id: 202,
    action: 'PROJECT_COPY',
    targetType: 'project',
    targetId: '9',
    targetName: '装配线升级 - 副本',
    detail: {
      source: { id: 4, name: '装配线升级', status: 'IN_PROGRESS' },
      target: { id: 9, name: '装配线升级 - 副本', status: 'DRAFT' },
      fileCount: 3,
      totalBytes: 2048,
    },
    createdAt: '2026-09-15T00:00:00Z',
  }
  const copyAction = table.props.columns.find((column) => column.dataIndex === 'action').render(copyRow.action, copyRow)
  assert.match(JSON.stringify(copyAction), /复制项目/)
  const copySummary = table.props.columns.find((column) => column.title === '内容摘要').render(null, copyRow)
  assert.match(JSON.stringify(copySummary), /装配线升级 → 装配线升级 - 副本/)
  assert.match(JSON.stringify(copySummary), /3 个文件，共 2\.0 KB/)
  assert.ok(values.includes('PROJECT_COPY'))
  assert.ok(!values.includes('PROJECT_STATUS'))
  await act(async () => renderer.unmount())
})

test('profile update audit exposes the auth filter label and changed field summary', async () => {
  const row = {
    id: 202,
    userId: 1,
    employeeNo: 'admin',
    action: 'PROFILE_UPDATE',
    targetType: 'user',
    targetId: '1',
    detail: { changedFields: ['email'] },
    createdAt: '2026-09-11T00:00:00Z',
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
  assert.match(JSON.stringify(action), /更新个人资料/)
  const summary = table.props.columns.find((column) => column.title === '内容摘要').render(null, row)
  assert.match(JSON.stringify(summary), /修改了邮箱/)
  const actionSelect = renderer.root.findAllByType('Select').find((select) => select.props.placeholder === '具体操作')
  const values = React.Children.toArray(actionSelect.props.children).map((option) => option.props.value)
  assert.ok(values.includes('PROFILE_UPDATE'))
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
  await act(async () => renderer.root.findByProps({ placeholder: '搜索姓名、对象或详情' }).props.onChange('next'))
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

test('a failed upload aborts sibling chunks promptly and resumes the same session', async () => {
  let initCalls=0, merges=0, settled=false, siblingAborted=false
  const puts=[]
  let markFailure
  const failureObserved=new Promise(resolve=>{markFailure=resolve})
  const originalFailure=new Error('original chunk failure')
  const Uploader=loadTs('src/components/ChunkUploader.tsx',{
    '@arco-design/web-react':arco,'@arco-design/web-react/icon':new Proxy({},{get:(_,n)=>component(n)}),
    '../api/types':{fmtSize:String},'../api/file-hash':{fileMd5:async()=> 'test-hash'},
    '../api/client':{
      post:async url=>{
        if(url.endsWith('/merge')){merges++;return {data:{id:99}}}
        initCalls++
        return {data:{sessionId:'test-session',chunkSize:1,totalChunks:3,uploadedChunks:initCalls===1?[]:[0],resumed:initCalls>1}}
      },
      put:async(url,_blob,config)=>{
        const chunk=Number(url.split('/').at(-1))
        puts.push({attempt:initCalls,chunk})
        if(initCalls>1)return {data:{}}
        if(chunk===0)return {data:{}}
        if(chunk===1){markFailure();throw originalFailure}
        return new Promise((resolve,reject)=>{
          const abort=()=>{siblingAborted=true;const error=new Error('sibling aborted');error.name='AbortError';reject(error)}
          if(config.signal.aborted)abort()
          else config.signal.addEventListener('abort',abort,{once:true})
        })
      },
      delete:async()=>{throw new Error('failed upload must retain its resumable session')},
    },
  }).default
  let renderer,start
  await act(async()=>{renderer=create(React.createElement(Uploader,{projectId:1,visible:true,onClose(){},onDone(){}}))})
  await act(async()=>renderer.root.findByType('input').props.onChange({target:{files:[{name:'sample.pdf',size:3,slice:()=>new Blob(['x'])}]}}))
  await act(async()=>{
    start=renderer.root.findByType('Modal').props.footer.props.children[1].props.onClick().then(()=>{settled=true})
    await failureObserved
    await new Promise(resolve=>setImmediate(resolve))
  })
  assert.equal(siblingAborted,true,'the first failed chunk must abort a sibling that has no other completion path')
  assert.equal(settled,true,'the failed attempt must finish without waiting for the chunk timeout')
  await act(async()=>start)
  assert.deepEqual(puts.filter(item=>item.attempt===1).map(item=>item.chunk).sort(),[0,1,2])
  assert.equal(merges,0)
  const retry=renderer.root.findByType('Modal').props.footer.props.children[1]
  assert.equal(retry.props.disabled,false)
  await act(async()=>retry.props.onClick())
  assert.equal(initCalls,2)
  assert.deepEqual(puts.filter(item=>item.attempt===2).map(item=>item.chunk).sort(),[1,2],'retry keeps the completed chunk and uploads only missing chunks')
  assert.equal(merges,1)
  await act(async()=>renderer.unmount())
})

test('source Excel conversion retains empty sheets and populated sheet navigation data', async () => {
  const XLSX = require('xlsx')
  const book = XLSX.utils.book_new()
  XLSX.utils.book_append_sheet(book, {}, '空白')
  XLSX.utils.book_append_sheet(book, XLSX.utils.aoa_to_sheet([['可见内容']]), '内容')
  const color = loadTs('vendor/vue-office-excel/core/packages/vue-excel/src/color.js', {})
  const media = loadTs('vendor/vue-office-excel/core/packages/vue-excel/src/media.js', {}, { window: { devicePixelRatio: 1 } })
  const engine = loadTs('vendor/vue-office-excel/core/packages/vue-excel/src/excel.js', {
    '../../../utils/url': {}, './color': color, './media': media,
  })
  const workbook = await engine.readExcelData(XLSX.write(book, { type: 'buffer', bookType: 'xlsx' }), false)
  const { workbookData } = engine.transferExcelToSpreadSheet(workbook, { minRowLength: 30, minColLength: 10 })
  assert.deepEqual(Array.from(workbookData, sheet => sheet.name), ['空白', '内容'])
  assert.equal(workbookData[0].rows.len, 30, 'an empty first sheet has valid geometry and retains the tab bar')
  assert.equal(workbookData[1].rows[0].cells[0].text, '可见内容')
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

test('subproject status update uses a per-project synchronous in-flight latch', async () => {
  const project = { id: 9, projectGroupId: 3, projectGroupName: '主项目', name: '子项目', supplierName: '供应商', status: 'DRAFT', updatedAt: '' }
  const group = { id: 3, name: '主项目', status: 'IN_PROGRESS', subprojectCount: 1, completedCount: 0, pendingCount: 0, terminatedCount: 0 }
  let puts = 0
  let release
  const response = new Promise((resolve) => { release = resolve })
  const Page = loadTs('src/pages/project/ProjectGroupDetail.tsx', {
    '@arco-design/web-react': arco,
    '@arco-design/web-react/icon': new Proxy({}, { get: (_, name) => component(name) }),
    'react-router-dom': { useNavigate: () => () => {}, useParams: () => ({ id: '3' }) },
    '../../api/client': {
      get: async () => ({ data: { group, projects: [project] } }),
      put: async () => { puts++; return response },
    },
    '../../store/auth': authModule({ id: 1, userType: 'INTERNAL' }, ['project:status']),
    '../../store/collaboration': { useCollaboration: (selector) => selector({ revision: 0, status: 'connected' }) },
    '../../api/types': { PROJECT_STATUS: { DRAFT: { text: '草稿' } }, fmtTime: String },
    '../../components/ActionSlots': actionSlotsModule,
  }).default
  let renderer
  await act(async () => { renderer = create(React.createElement(Page)) })
  const actions = renderer.root.findByType('Table').props.columns.at(-1).render(null, project)
  const statusButton = findActionButton(actions, '开始')
  let first
  let second
  await act(async () => {
    first = statusButton.props.onClick()
    second = statusButton.props.onClick()
    await Promise.resolve()
  })
  assert.equal(puts, 1)
  await act(async () => { release({ data: {} }); await first; await second })
  await act(async () => renderer.unmount())
})


test('auth persistence removes personal details and migrates legacy records', () => {
  let options
  const auth=loadTs('src/store/auth.ts',{'zustand/middleware':{persist:(fn, config)=>{options=config;return fn}}}).useAuth
  auth.getState().setLogin({accessToken:'fixture-token',user:{id:7,employeeNo:'fixture',realName:'个人姓名',email:'fixture@example.invalid',userType:'INTERNAL'},permissions:['project:list'],menus:['dashboard'],mustChangePassword:false})
  const saved=options.partialize(auth.getState())
  assert.deepEqual(Object.keys(saved.user),['id'])
  assert.equal('token' in saved,false)
  const migrated=options.migrate({user:{id:7,employeeNo:'fixture',realName:'个人姓名',email:'fixture@example.invalid'},token:'legacy-token'},0)
  assert.deepEqual(Object.keys(migrated.user),['id'])
  assert.equal('token' in migrated,false)
})

test('message composition counts Unicode code points and sends the complete 4000 character boundary', async () => {
  const posts = []
  const Page = loadTs('src/components/MessagePanel.tsx', {
    '@arco-design/web-react': arco,
    '@arco-design/web-react/icon': new Proxy({}, { get: (_, name) => component(name) }),
    '../api/client': {
      get: async () => ({ data: { list: [], total: 0 } }),
      post: async (url, body) => { posts.push({ url, body }); return { data: messageFixture(1, body.content) } },
    },
    '../store/auth': authModule({ id: 1, userType: 'INTERNAL' }, ['message:create']),
    '../api/types': { fmtTime: String },
  }).default
  let renderer
  await act(async () => { renderer = create(React.createElement(Page, { projectId: 1, projectStatus: 'IN_PROGRESS' })) })
  const input = () => renderer.root.findByType('Input.TextArea')
  const text = '中'.repeat(3998) + '🙂🙂'
  await act(async () => input().props.onChange(text + '超'))
  assert.equal(input().props.value, text, 'the limit must preserve whole supplementary Unicode characters')
  const send = renderer.root.findAllByType('Button').find((node) => node.props.children === '发送')
  await act(async () => send.props.onClick())
  assert.equal(posts.length, 1)
  assert.equal(posts[0].body.content, text)
  assert.equal(input().props.value, '')
  await act(async () => renderer.unmount())
})

test('message send failure is handled, preserves the draft, and allows a successful retry', async () => {
  let fail = true
  const posts = []
  const Page = loadTs('src/components/MessagePanel.tsx', {
    '@arco-design/web-react': arco,
    '@arco-design/web-react/icon': new Proxy({}, { get: (_, name) => component(name) }),
    '../api/client': {
      get: async () => ({ data: { list: [], total: 0 } }),
      post: async (_url, body) => { if (fail) throw new Error('service unavailable'); posts.push(body); return { data: messageFixture(1, body.content) } },
    },
    '../store/auth': authModule({ id: 1, userType: 'INTERNAL' }, ['message:create']),
    '../api/types': { fmtTime: String },
  }).default
  let renderer
  await act(async () => { renderer = create(React.createElement(Page, { projectId: 1, projectStatus: 'IN_PROGRESS' })) })
  const input = () => renderer.root.findByType('Input.TextArea')
  const send = () => renderer.root.findAllByType('Button').find((node) => node.props.children === '发送')
  await act(async () => input().props.onChange('  保留重试的留言  '))
  await act(async () => { await assert.doesNotReject(send().props.onClick()) })
  assert.equal(input().props.value, '  保留重试的留言  ')
  assert.equal(send().props.loading, false)
  assert.equal(posts.length, 0)
  fail = false
  await act(async () => send().props.onClick())
  assert.equal(posts.length, 1)
  assert.equal(posts[0].content, '保留重试的留言')
  assert.equal(input().props.value, '')
  await act(async () => renderer.unmount())
})


test('message images submit atomically, retain failed drafts and suppress duplicate sends', async () => {
  let rejectPost, resolvePost
  const posts = []
  const Page = loadTs('src/components/MessagePanel.tsx', {
    '@arco-design/web-react': arco,
    '@arco-design/web-react/icon': new Proxy({}, { get: (_, name) => component(name) }),
    '../api/client': {
      get: async () => ({ data: { list: [], total: 0 } }),
      post: (url, body) => { posts.push({ url, body }); return new Promise((resolve, reject) => { resolvePost = resolve; rejectPost = reject }) },
    },
    '../store/auth': authModule({ id: 1, userType: 'INTERNAL' }, ['message:create']),
    '../api/types': { fmtTime: String },
  }).default
  let renderer, pending
  await act(async () => { renderer = create(React.createElement(Page, { projectId: 1, projectStatus: 'IN_PROGRESS' })) })
  const composer = () => renderer.root.findByType('MessageImageComposer')
  const send = () => renderer.root.findAllByType('Button').find(node => node.props.children === '发送')
  const image = new File(['image fixture'], 'screenshot.png', { type: 'image/png' })
  await act(async () => composer().props.onChange([image]))
  assert.equal(send().props.disabled, false, 'image-only messages can be sent')
  await act(async () => { pending = send().props.onClick(); await send().props.onClick() })
  assert.equal(posts.length, 1)
  assert.ok(posts[0].body instanceof FormData)
  assert.equal(posts[0].body.get('content'), '')
  assert.equal(posts[0].body.get('images').name, 'screenshot.png')
  assert.equal(composer().props.disabled, true)
  await act(async () => { rejectPost(new Error('upload failed')); await pending })
  assert.equal(composer().props.files.length, 1, 'failure keeps screenshot for retry')
  await act(async () => { pending = send().props.onClick() })
  await act(async () => { resolvePost({ data: { ...messageFixture(10, ''), images: [{ id: 2, name: 'screenshot.png', sizeBytes: 13, mimeType: 'image/png' }] } }); await pending })
  assert.equal(composer().props.files.length, 0)
  assert.equal(renderer.root.findByType('MessageImages').props.images[0].id, 2)
  await act(async () => composer().props.onChange([image]))
  await act(async () => renderer.update(React.createElement(Page, { projectId: 2, projectStatus: 'IN_PROGRESS' })))
  assert.equal(composer().props.files.length, 0, 'screenshots must not leak to another project')
  await act(async () => renderer.unmount())
})

test('SMTP editor preserves authorization codes, retries failed saves, and keeps unrelated edits', async () => {
  let stored = { host: 'smtp.example.invalid', port: 465, username: 'sender@example.invalid', from: 'notice@example.invalid', security: 'Auto', hasPassword: true, configured: true, passwordNeedsUpdate: false }
  let configValue = 'pdf'
  let fail = true
  const writes = []
  const Page = loadTs('src/pages/system/SysConfig.tsx', {
    '@arco-design/web-react': arco,
    '../../api/client': {
      get: async url => ({ data: url.endsWith('/mail-settings') ? stored : url.endsWith('/configs') ? [{ key: 'notify.enabled', value: 'true' }, { key: 'upload.allowed_exts', value: configValue }] : url.endsWith('/storage') ? { totalBytes: 100, availableBytes: 100, usedPercent: 0, warnPercent: 85, root: '/' } : { configured: true } }),
      put: async (url, body) => {
        writes.push({ url, body })
        if (fail) throw new Error('simulated save failure')
        if (url.endsWith('/configs')) { configValue = body.items.find(item => item.key === 'upload.allowed_exts')?.value || configValue; return { data: {} } }
        const { password: _password, ...fields } = body
        stored = { ...stored, ...fields, hasPassword: true }
        return { data: stored }
      },
    },
    '../../api/types': { fmtTime: String, fmtSize: String },
    'react-router-dom': { useNavigate: () => () => {} },
  }).default
  let renderer
  await act(async () => { renderer = create(React.createElement(Page)) })
  const port = () => renderer.root.findAllByType('InputNumber').find(n => n.props['aria-label'] === 'SMTP 端口')
  const security = () => renderer.root.findAllByType('Select').find(n => n.props['aria-label'] === 'SMTP 连接加密')
  for (let ancestor = security().parent; ancestor; ancestor = ancestor.parent) {
    assert.notEqual(ancestor.type, 'label', 'a wrapping label forwards another click to the Select input and closes its popup')
  }
  await act(async () => security().props.onChange('StartTls'))
  assert.equal(security().props.value, 'StartTls')
  const password = () => renderer.root.findByType('PasswordInput')
  const save = () => renderer.root.findAllByType('Button').find(n => n.props.children === '保存邮箱设置')
  const notification = () => renderer.root.findAllByType('Switch').find(n => n.props['aria-label'] === '内部员工邮件通知')
  assert.equal(password().props.value, '')
  await act(async () => notification().props.onChange(false))
  await act(async () => port().props.onChange(587))
  await act(async () => save().props.onClick())
  assert.equal(save().props.loading, false)
  assert.equal(port().props.value, 587)
  fail = false
  await act(async () => save().props.onClick())
  assert.equal(writes.at(-1).url, '/admin/system/mail-settings')
  assert.equal(writes.at(-1).body.password, null)
  assert.equal(writes.at(-1).body.security, 'StartTls')
  assert.equal(notification().props.checked, false)
  await act(async () => password().props.onChange('new-fixture-code'))
  await act(async () => save().props.onClick())
  assert.equal(writes.at(-1).body.password, 'new-fixture-code')
  assert.equal(password().props.value, '')
  assert.equal(save().props.disabled, true)
  const beforeIdentityChange = writes.length
  await act(async () => renderer.root.findAllByType('Input').find(n => n.props['aria-label'] === 'SMTP 服务器').props.onChange('other.example.invalid'))
  await act(async () => save().props.onClick())
  assert.equal(writes.length, beforeIdentityChange, 'existing code cannot be reused for another server')
  await act(async () => password().props.onChange('replacement-fixture-code'))
  await act(async () => save().props.onClick())
  assert.equal(writes.at(-1).body.host, 'other.example.invalid')
  await act(async () => password().props.onChange('unsaved-mail-code'))
  const allowed = () => {
    const table = renderer.root.findAllByType('Table').find(n => n.props.data?.some(row => row.key === 'upload.allowed_exts'))
    const row = table.props.data.find(item => item.key === 'upload.allowed_exts')
    return findElement(table.props.columns[1].render(row.value, row), n => n.props['aria-label'] === '允许上传类型')
  }
  await act(async () => allowed().props.onChange('pdf,cfgtest'))
  const configCard = renderer.root.findAllByType('Card').find(n => n.props.title === '系统参数')
  await act(async () => findElement(configCard.props.extra, n => n.props.children === '保存').props.onClick())
  assert.equal(password().props.value, 'unsaved-mail-code', 'saving general parameters keeps the SMTP draft')
  assert.equal(configValue, 'pdf,cfgtest')
  await act(async () => renderer.unmount())
})

test('unreadable SMTP authorization code cannot be retained by an empty save', async () => {
  const writes = []
  const notices = []
  const smtpArco = new Proxy({ ...arco, Message: { ...arco.Message, warning: (message) => notices.push(message), success: (message) => notices.push(message) } }, { get: (obj, key) => obj[key] ?? component(key) })
  const settings = { host: 'smtp.example.invalid', port: 465, username: 'sender@example.invalid', from: 'sender@example.invalid', security: 'Auto', hasPassword: true, configured: false, passwordNeedsUpdate: true }
  const Page = loadTs('src/pages/system/SysConfig.tsx', {
    '@arco-design/web-react': smtpArco,
    '../../api/client': {
      get: async (url) => {
        if (writes.length && url.endsWith('/mail-status')) throw new Error('status refresh failed')
        return { data: url.endsWith('/mail-settings') ? settings : url.endsWith('/configs') ? [] : {} }
      },
      put: async (url, body) => {
        writes.push({ url, body })
        return { data: { ...settings, ...body, passwordNeedsUpdate: false, configured: true } }
      },
    },
    '../../api/types': { fmtTime: String },
    'react-router-dom': { useNavigate: () => () => {} },
  }).default
  let renderer
  await act(async () => { renderer = create(React.createElement(Page)) })
  const password = () => renderer.root.findByType('PasswordInput')
  const save = () => renderer.root.findAllByType('Button').find((node) => node.props.children === '保存邮箱设置')
  assert.equal(password().props.placeholder, '请输入邮箱密码或授权码')
  await act(async () => renderer.root.findAllByType('InputNumber').find((node) => node.props['aria-label'] === 'SMTP 端口').props.onChange(587))
  await act(async () => save().props.onClick())
  assert.equal(writes.length, 0, 'changing another field must not submit the unreadable old authorization code')
  await act(async () => password().props.onChange('replacement-fixture-code'))
  await act(async () => save().props.onClick())
  assert.equal(writes.length, 1)
  assert.equal(notices.at(-1), '邮箱设置已保存，但邮件状态刷新失败，请稍后刷新页面')
  assert.equal(notices.includes('邮箱设置已保存，无需重启'), false)
  assert.equal(writes[0].body.password, 'replacement-fixture-code')
  assert.equal(password().props.value, '')
  assert.equal(password().props.placeholder, '已设置，留空保留原授权码')
  await act(async () => renderer.unmount())
})

test('system config freezes edits during save and keeps the committed value when refresh fails', async () => {
  const deferred = () => {
    let resolve
    let reject
    const promise = new Promise((ok, fail) => { resolve = ok; reject = fail })
    return { promise, resolve, reject }
  }
  const write = deferred()
  const refresh = deferred()
  const refreshStarted = deferred()
  const messages = []
  let statusGets = 0
  const configArco = new Proxy({
    ...arco,
    Message: {
      error(message) { messages.push(['error', message]) },
      warning(message) { messages.push(['warning', message]) },
      success(message) { messages.push(['success', message]) },
      info(message) { messages.push(['info', message]) },
    },
  }, { get: (obj, key) => obj[key] ?? component(key) })
  const Page = loadTs('src/pages/system/SysConfig.tsx', {
    '@arco-design/web-react': configArco,
    '../../api/client': {
      get: async (url) => {
        if (url.endsWith('/configs')) return { data: [] }
        if (url.endsWith('/mail-settings')) {
          return { data: { host: '', port: 465, username: '', from: '', security: 'Auto', hasPassword: false, configured: false, passwordNeedsUpdate: false } }
        }
        statusGets += 1
        if (statusGets === 1) return { data: { configured: false, notificationsEnabled: true, notificationPolicy: { globalEnabled: true, internalEnabled: true, supplierEnabled: true, events: { messageCreated: true, fileUploaded: true, projectSubmitted: true, projectConfirmed: true, projectRejected: true, projectWithdrawn: true } }, queue: {}, missingEmailAccounts: [], recent: [] } }
        refreshStarted.resolve()
        return refresh.promise
      },
      put: async (url, body) => {
        assert.equal(url, '/admin/system/configs')
        assert.equal(body.items.find(item => item.key === 'notify.internal.enabled')?.value, 'false')
        return write.promise
      },
    },
    '../../api/types': { fmtTime: String },
    'react-router-dom': { useNavigate: () => () => {} },
  }).default
  let renderer
  await act(async () => { renderer = create(React.createElement(Page)) })
  const notification = () => renderer.root.findAllByType('Switch').find(node => node.props['aria-label'] === '内部员工邮件通知')
  const saveButton = () => {
    return renderer.root.findAllByType('Button').find(node => node.props.children === '保存邮件提醒')
  }

  await act(async () => notification().props.onChange(false))
  let savePromise
  act(() => {
    savePromise = saveButton().props.onClick()
  })
  assert.equal(notification().props.disabled, true)
  assert.equal(saveButton().props.disabled, true)
  await act(async () => notification().props.onChange(true))
  assert.equal(notification().props.checked, false, 'a late edit cannot be accepted while the saved snapshot is in flight')

  await act(async () => {
    write.resolve({ data: {} })
    await refreshStarted.promise
  })
  assert.equal(statusGets, 2, 'a post-save status refresh is attempted')
  await act(async () => {
    refresh.reject(new Error('simulated refresh failure after commit'))
    await savePromise
  })

  assert.equal(notification().props.checked, false, 'the confirmed server value remains visible after refresh failure')
  assert.equal(saveButton().props.disabled, true, 'the committed value is no longer dirty')
  assert.equal(messages.filter(([type]) => type === 'success').length, 0, 'a refresh failure produces one clear committed-but-stale notice')
  assert.ok(messages.some(([type, message]) => type === 'warning' && message.includes('邮件提醒设置已保存，但状态刷新失败')))
  await act(async () => renderer.unmount())
})

test('project detail ignores an older unread summary after the read refresh completes', async () => {
  const deferred = () => {
    let resolve
    const promise = new Promise((ok) => { resolve = ok })
    return { promise, resolve }
  }
  const summaries = []
  const project = { id: 1, name: '竞态项目', status: 'IN_PROGRESS', supplierName: '供应商', updatedAt: '2026-09-13T00:00:00Z' }
  const Page = loadTs('src/pages/project/ProjectDetail.tsx', {
    '@arco-design/web-react': arco,
    'react-router-dom': {
      useParams: () => ({ id: '1' }),
      useSearchParams: () => [new URLSearchParams('tab=messages'), () => {}],
      useNavigate: () => () => {},
    },
    '../../api/client': {
      get: async (url) => {
        if (!url.endsWith('/summary')) return { data: project }
        const request = deferred()
        summaries.push(request)
        return request.promise
      },
    },
    '../../api/types': { PROJECT_STATUS: { IN_PROGRESS: { text: '进行中', color: 'blue' } }, fmtTime: String },
    '../../components/FileTable': component('Files'),
    '../../components/MessagePanel': component('Messages'),
    '../../components/ProjectActivityPanel': component('Activities'),
    '../../components/ProjectWorkflowPanel': component('Workflow'),
  }).default
  let renderer
  await act(async () => { renderer = create(React.createElement(Page)) })
  assert.equal(summaries.length, 1)

  act(() => { void renderer.root.findByType('Messages').props.onRead() })
  assert.equal(summaries.length, 2)
  await act(async () => summaries[1].resolve({ data: { unreadMessages: 0 } }))
  await act(async () => summaries[0].resolve({ data: { unreadMessages: 1 } }))

  assert.equal(renderer.root.findAllByType('Badge').length, 0, 'the older unread count must not restore a stale badge')
  await act(async () => renderer.unmount())
})

test('login rejects duplicate submissions even before the browser lock runs and allows retry after failure', async () => {
  let releaseLock
  const lock = new Promise(resolve => { releaseLock = resolve })
  const requests = []
  const logins = []
  const navigations = []
  const authState = { token: null, user: null, setLogin: value => logins.push(value) }
  const useAuth = Object.assign(selector => selector(authState), { getState: () => authState })
  const axios = {
    post: (url, body) => new Promise((resolve, reject) => requests.push({ url, body, resolve, reject })),
    isAxiosError: () => false,
  }
  const Page = loadTs('src/pages/Login.tsx', {
    '@arco-design/web-react': arco,
    '@arco-design/web-react/icon': new Proxy({}, { get: (_, name) => component(name) }),
    'react-router-dom': { useNavigate: () => path => navigations.push(path), useLocation: () => ({ state: { from: '/projects' } }) },
    axios: { __esModule: true, default: axios },
    '../store/auth': { useAuth },
    '../api/client': { withAuthLock: action => lock.then(action) },
    '../components/AuthShell': { __esModule: true, default: component('AuthShell') },
  }).default
  let renderer
  await act(async () => { renderer = create(React.createElement(Page)) })
  let first
  const payload = { employeeNo: ' admin ', password: 'synthetic-login-password' }
  const submit = renderer.root.findByType('Form').props.onSubmit
  act(() => {
    first = submit(payload)
    void submit(payload)
  })
  assert.equal(requests.length, 0, 'network is still waiting for the shared browser lock')
  assert.equal(renderer.root.findByType('Input').props.disabled, true)
  assert.equal(renderer.root.findByType('PasswordInput').props.disabled, true)
  await act(async () => { releaseLock(); await lock })
  assert.equal(requests.length, 1, 'duplicate calls must not enqueue a second login')
  assert.equal(requests[0].body.employeeNo, 'admin')
  await act(async () => { requests[0].reject(new Error('simulated network failure')); await first })
  assert.equal(renderer.root.findByType('Input').props.disabled, false)
  assert.equal(logins.length, 0)
  let retry
  await act(async () => {
    retry = renderer.root.findByType('Form').props.onSubmit(payload)
    await lock
  })
  assert.equal(requests.length, 2)
  await act(async () => {
    requests[1].resolve({ data: { accessToken: 'fixture', mustChangePassword: false } })
    await retry
  })
  assert.equal(logins.length, 1)
  assert.deepEqual(navigations, ['/projects'])
  await act(async () => renderer.unmount())
})

test('built-in roles never expose hard delete while custom roles still can', async () => {
  const builtIn = { id: 1, name: '供应商人员', isBuiltIn: true, status: 'ACTIVE', permissionIds: [], assignedUserCount: 0 }
  const custom = { id: 2, name: '自定义角色', isBuiltIn: false, status: 'ACTIVE', permissionIds: [], assignedUserCount: 0 }
  const Page = loadTs('src/pages/rbac/RoleList.tsx', {
    '@arco-design/web-react': arco,
    '@arco-design/web-react/icon': new Proxy({}, { get: (_, name) => component(name) }),
    '../../api/client': {
      get: async (url) => ({ data: url === '/permissions' ? [] : { list: [builtIn, custom], total: 2, page: 1, pageSize: 20 } }),
    },
    '../../store/auth': authModule({ id: 1, userType: 'INTERNAL' }, ['role:manage', 'role:delete']),
    '../../api/types': {},
  }).default
  let renderer
  await act(async () => { renderer = create(React.createElement(Page)) })
  const table = renderer.root.findByType('Table')
  const actions = table.props.columns.at(-1)

  assert.equal(findActionButton(actions.render(null, builtIn), '删除'), undefined)
  assert.ok(findActionButton(actions.render(null, custom), '删除'))
  await act(async () => renderer.unmount())
})

test('subproject copy requires a renamed project, blocks duplicate submits and keeps the dialog after failure', async () => {
  const project = { id: 4, projectGroupId: 3, projectGroupName: '主项目', name: '装配线升级', status: 'IN_PROGRESS', workOrderNos: ['WO-1'] }
  const group = { id: 3, name: '主项目', status: 'IN_PROGRESS', subprojectCount: 1, completedCount: 0, pendingCount: 0, terminatedCount: 0 }
  let mainFields = {}
  let copyFields = {}
  const mainForm = {
    resetFields() { mainFields = {} },
    setFieldsValue(values) { mainFields = { ...mainFields, ...values } },
    validate: async () => mainFields,
  }
  const copyForm = {
    resetFields() { copyFields = {} },
    setFieldsValue(values) { copyFields = { ...copyFields, ...values } },
    setFieldValue(field, value) { copyFields[field] = value },
    validate: async () => copyFields,
  }
  let formCall = 0
  const messages = []
  const copyArco = new Proxy({
    ...arco,
    Form: Object.assign(component('Form'), {
      useForm: () => [formCall++ % 2 === 0 ? mainForm : copyForm],
      Item: component('Form.Item'),
    }),
    Message: { ...arco.Message, success: (message) => messages.push(message), error: (message) => messages.push(message) },
  }, { get: (obj, key) => obj[key] ?? component(key) })
  let postCalls = 0
  let rejectFirst
  const navigations = []
  const http = {
    get: async (url) => url === '/project-groups/3' ? { data: { group, projects: [project] } } : { data: [] },
    post: async (url, body) => {
      postCalls++
      assert.equal(url, '/projects/4/copy')
      assert.equal(body.name, '装配线升级 - 副本')
      if (postCalls === 1) return new Promise((_resolve, reject) => { rejectFirst = reject })
      return { data: { project: { id: 9, name: body.name }, copy: { fileCount: 3 } } }
    },
  }
  const Page = loadTs('src/pages/project/ProjectGroupDetail.tsx', {
    '@arco-design/web-react': copyArco,
    '@arco-design/web-react/icon': new Proxy({}, { get: (_, name) => component(name) }),
    'react-router-dom': { useNavigate: () => (path) => navigations.push(path), useParams: () => ({ id: '3' }) },
    '../../api/client': http,
    '../../store/auth': authModule({ id: 1, userType: 'INTERNAL' }, ['project:create']),
    '../../store/collaboration': { useCollaboration: (selector) => selector({ revision: 0, status: 'connected' }) },
    '../../api/types': { PROJECT_STATUS: { IN_PROGRESS: { text: '进行中' } }, fmtTime: String },
    '../../components/ActionSlots': actionSlotsModule,
  }).default
  let renderer
  await act(async () => { renderer = create(React.createElement(Page)) })
  const actionColumn = renderer.root.findByType('Table').props.columns.at(-1)
  await act(async () => findActionButton(actionColumn.render(null, project), '复制').props.onClick())
  assert.equal(copyFields.name, '装配线升级 - 副本')
  let dialog = renderer.root.findAllByType('Modal').find((modal) => modal.props.title === '复制子项目')
  assert.equal(dialog.props.visible, true)
  assert.ok(renderer.root.findAll((node) => node.props.children === '生成独立复制件，复制公共资料和当前项目文件；不复制留言、验收状态、已读回执和通知。').length)

  let first
  await act(async () => {
    first = dialog.props.onOk()
    void dialog.props.onOk()
    await Promise.resolve()
  })
  assert.equal(postCalls, 1, 'an in-flight copy must reject repeated submissions')
  await act(async () => { rejectFirst(new Error('simulated copy failure')); await first })
  dialog = renderer.root.findAllByType('Modal').find((modal) => modal.props.title === '复制子项目')
  assert.equal(dialog.props.visible, true, 'a failed request keeps the entered name and source project')
  assert.equal(copyFields.name, '装配线升级 - 副本')

  await act(async () => dialog.props.onOk())
  assert.equal(postCalls, 2)
  assert.deepEqual(navigations, [])
  assert.ok(messages.some((message) => String(message).includes('3 个文件')))
  await act(async () => renderer.unmount())
})

test('project copy history hides restricted relations and pages file mappings on demand', async () => {
  const project = {
    id: 8,
    name: '新项目',
    status: 'DRAFT',
    workOrderNos: ['WO-8'],
    updatedAt: '2026-09-15T00:00:00Z',
    hasCopyHistory: true,
    copySource: { projectId: 2, name: '原项目' },
  }
  const history = {
    source: {
      copyId: 16,
      projectId: 2,
      name: '原项目',
      fileCount: 21,
      totalBytes: 2048,
      copiedByName: '项目管理员',
      createdAt: '2026-09-15T01:00:00Z',
    },
    copies: [],
    hasRestrictedRelations: true,
  }
  const calls = []
  const http = {
    get: async (url, config = {}) => {
      calls.push({ url, config })
      if (url === '/projects/8') return { data: project }
      if (url === '/projects/8/summary') return { data: { unreadMessages: 0 } }
      if (url === '/projects/8/copy-history') return { data: history }
      if (url === '/project-copies/16/files') {
        return { data: { list: [{ sourceFileId: 1, sourceFileName: '规格.xlsx', sourceDeleted: false, targetFileId: 7, targetFileName: '规格.xlsx', targetDeleted: false }], total: 21, page: config.params.page, pageSize: 20 } }
      }
      throw new Error(`unexpected ${url}`)
    },
  }
  const Page = loadTs('src/pages/project/ProjectDetail.tsx', {
    '@arco-design/web-react': arco,
    'react-router-dom': {
      useParams: () => ({ id: '8' }),
      useSearchParams: () => [new URLSearchParams(), () => {}],
      useNavigate: () => () => {},
    },
    '../../api/client': http,
    '../../api/types': {
      PROJECT_STATUS: { DRAFT: { text: '草稿', color: 'gray' } },
      fmtTime: String,
      fmtSize: (value) => `${value} B`,
    },
    '../../components/FileTable': component('Files'),
    '../../components/MessagePanel': component('Messages'),
    '../../components/ProjectActivityPanel': component('Activities'),
    '../../components/ProjectWorkflowPanel': component('Workflow'),
  }).default
  let renderer
  await act(async () => { renderer = create(React.createElement(Page)); await Promise.resolve() })
  assert.equal(calls.some((call) => call.url === '/projects/8/copy-history'), false, 'history loads only when requested')
  const files = renderer.root.findByType('Files')
  assert.equal(typeof files.props.onOpenCopyHistory, 'function')
  await act(async () => { files.props.onOpenCopyHistory(); await Promise.resolve() })
  assert.ok(calls.some((call) => call.url === '/projects/8/copy-history'))
  const drawer = renderer.root.findByType('Drawer')
  assert.equal(drawer.props.visible, true)
  assert.ok(renderer.root.findAll((node) => node.props.children === '部分复制关联项目当前无权查看，已隐藏其名称和文件信息。').length)
  const mappingToggle = renderer.root.findAllByType('Button').find((button) => button.props.children === '查看文件映射')
  await act(async () => { mappingToggle.props.onClick(); await Promise.resolve() })
  const mappingCall = calls.find((call) => call.url === '/project-copies/16/files')
  assert.deepEqual({ ...mappingCall.config.params }, { page: 1, pageSize: 20 })
  const mappingTable = renderer.root.findAllByType('Table').find((table) => table.props.data?.[0]?.targetFileId === 7)
  assert.ok(mappingTable)
  await act(async () => mappingTable.props.pagination.onChange(2))
  assert.ok(calls.some((call) => call.url === '/project-copies/16/files' && call.config.params.page === 2))
  await act(async () => renderer.unmount())
})
