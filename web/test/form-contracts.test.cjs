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
})

const arco = new Proxy({
  Form: Object.assign(component('Form'), { useForm: () => [{}], Item: component('Form.Item') }),
  Typography: { Text: component('Text'), Title: component('Title') },
  Message: { error() {}, warning() {}, success() {}, info() {} },
}, { get: (obj, key) => obj[key] ?? component(key) })

const actionSlots = {
  actionSlots: (slots) => React.createElement('div', {}, slots.filter(Boolean)),
}

function loadTs(relativePath, mocks = {}) {
  const filename = path.resolve(__dirname, '..', relativePath)
  const source = ts.transpileModule(fs.readFileSync(filename, 'utf8'), {
    compilerOptions: {
      module: ts.ModuleKind.CommonJS,
      jsx: ts.JsxEmit.ReactJSX,
      target: ts.ScriptTarget.ES2022,
      esModuleInterop: true,
    },
  }).outputText
  const exports = {}
  vm.runInNewContext(source, {
    exports,
    module: { exports },
    console,
    setTimeout,
    clearTimeout,
    URL,
    URLSearchParams,
    AbortController,
    require: (name) => {
      if (name in mocks) return mocks[name]
      if (name.replace(/\\/g, '/').endsWith('/ActionSlots')) return actionSlots
      if (name.replace(/\\/g, '/').endsWith('/PasswordInput')) return component('PasswordInput')
      return require(name)
    },
  }, { filename })
  return exports
}

function authModule(permissions = []) {
  const state = {
    user: { id: 1, userType: 'INTERNAL' },
    mustChangePassword: false,
    hasPerm: (code) => permissions.includes(code),
    logout() {},
    setUser() {},
  }
  return { useAuth: (selector) => selector ? selector(state) : state }
}

function iconModule() {
  return new Proxy({}, { get: (_, name) => component(name) })
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

function validatorError(rule, value) {
  let result = Symbol('not-called')
  rule.validator(value, (error) => { result = error })
  assert.notEqual(typeof result, 'symbol', 'validator must call its callback synchronously')
  return result
}

test('password contract counts Unicode scalars and is shared by every password-writing form', async () => {
  const password = loadTs('src/utils/password.ts')
  assert.equal(password.PASSWORD_MIN_CHARS, 6)
  assert.equal(password.PASSWORD_MAX_CHARS, 20)
  assert.equal(password.PASSWORD_MAX_BYTES, 128)
  assert.equal(validatorError(password.passwordRule, '😀'.repeat(11)), undefined)
  assert.equal(validatorError(password.passwordRule, '😀'.repeat(20)), undefined)
  assert.match(String(validatorError(password.passwordRule, '😀'.repeat(21))), /6-20/)
  assert.match(String(validatorError(password.passwordRule, '短密码')), /6-20/)

  const pageCases = [
    {
      page: 'src/pages/ChangePassword.tsx',
      passwordImport: '../utils/password',
      fields: ['newPassword'],
      mocks: {
        '../api/client': { __esModule: true, default: {}, withAuthLock: async (fn) => fn() },
        '../store/auth': authModule(),
        '../components/AuthShell': component('AuthShell'),
        'react-router-dom': { useNavigate: () => () => {} },
      },
    },
    {
      page: 'src/pages/Profile.tsx',
      passwordImport: '../utils/password',
      fields: ['newPassword'],
      mocks: {
        '../api/client': { __esModule: true, default: {}, withAuthLock: async (fn) => fn() },
        '../store/auth': authModule(),
        'react-router-dom': { useNavigate: () => () => {} },
      },
    },
    {
      page: 'src/pages/org/UserList.tsx',
      passwordImport: '../../utils/password',
      fields: ['password', 'newPassword'],
      mocks: {
        '../../api/client': { get: async (url) => ({ data: url === '/admin/users' ? { list: [], total: 0, page: 1, pageSize: 10 } : [] }) },
        '../../api/types': { fmtTime: String },
        '../../store/auth': authModule(['user:delete']),
      },
    },
    {
      page: 'src/pages/supplier/SupplierList.tsx',
      passwordImport: '../../utils/password',
      fields: ['password', 'newPassword'],
      mocks: {
        '../../api/client': { get: async () => ({ data: { list: [], total: 0, page: 1, pageSize: 10 } }) },
        '../../api/types': { fmtTime: String },
        '../../store/auth': authModule(['supplier:account']),
      },
    },
  ]

  for (const item of pageCases) {
    const Page = loadTs(item.page, {
      '@arco-design/web-react': arco,
      '@arco-design/web-react/icon': iconModule(),
      [item.passwordImport]: password,
      ...item.mocks,
    }).default
    let renderer
    await act(async () => { renderer = create(React.createElement(Page)) })
    for (const field of item.fields) {
      const formItem = renderer.root.findAllByType('Form.Item').find((node) => node.props.field === field)
      assert.ok(formItem, `${item.page} must render ${field}`)
      assert.ok(formItem.props.rules.includes(password.passwordRule), `${item.page}:${field} must use the shared password contract`)
    }
    await act(async () => renderer.unmount())
  }
})

test('form limits match the backend character contracts', async () => {
  const cases = [
    {
      page: 'src/pages/org/DeptManage.tsx', field: 'name', maxLength: 64,
      mocks: { '../../api/client': { get: async () => ({ data: [] }) }, '../../store/auth': authModule(['dept:manage']) },
    },
    {
      page: 'src/pages/rbac/RoleList.tsx', field: 'description', maxLength: 255,
      mocks: { '../../api/client': { get: async (url) => ({ data: url === '/permissions' ? [] : { list: [], total: 0 } }) }, '../../api/types': {}, '../../store/auth': authModule() },
    },
    {
      page: 'src/pages/supplier/SupplierList.tsx', field: 'remark', maxLength: 500,
      mocks: {
        '../../api/client': { get: async () => ({ data: { list: [], total: 0 } }) },
        '../../api/types': { fmtTime: String },
        '../../store/auth': authModule(),
        '../../utils/password': { passwordRule: {} },
      },
    },
  ]

  for (const item of cases) {
    const Page = loadTs(item.page, {
      '@arco-design/web-react': arco,
      '@arco-design/web-react/icon': iconModule(),
      ...item.mocks,
    }).default
    let renderer
    await act(async () => { renderer = create(React.createElement(Page)) })
    const formItem = renderer.root.findAllByType('Form.Item').find((node) => node.props.field === item.field)
    const input = formItem.find((node) => node.type === 'Input' || node.type === 'Input.TextArea')
    assert.equal(input.props.maxLength, item.maxLength, `${item.page}:${item.field}`)
    await act(async () => renderer.unmount())
  }
})

test('permission tree keeps menu-only grants, adds a parent for actions and removes actions with an unchecked parent', async () => {
  const role = { id: 7, name: '测试', permissionIds: [5], assignedUserCount: 0, status: 'ACTIVE' }
  const perms = [
    { id: 5, name: '部门', type: 'MENU', parentId: null },
    { id: 24, name: '管理部门', type: 'ACTION', parentId: 5 },
  ]
  const Page = loadTs('src/pages/rbac/RoleList.tsx', {
    '@arco-design/web-react': arco,
    '@arco-design/web-react/icon': iconModule(),
    '../../api/client': { get: async (url) => ({ data: url === '/permissions' ? perms : { list: [role], total: 1 } }) },
    '../../api/types': {},
    '../../store/auth': authModule(),
  }).default
  let renderer
  await act(async () => { renderer = create(React.createElement(Page)) })
  const actions = renderer.root.findByType('Table').props.columns.at(-1).render(null, role)
  const assign = React.Children.toArray(actions.props.children).find((node) => node.props.children === '分配权限')
  await act(async () => assign.props.onClick())

  let tree = renderer.root.findByType('Tree')
  assert.deepEqual(Array.from(tree.props.checkedKeys), ['5'])
  await act(async () => tree.props.onCheck(['24'], { checked: true, node: { key: '24' } }))
  tree = renderer.root.findByType('Tree')
  assert.deepEqual(new Set(tree.props.checkedKeys), new Set(['5', '24']))
  await act(async () => tree.props.onCheck(['24'], { checked: false, node: { key: '5' } }))
  assert.deepEqual(Array.from(renderer.root.findByType('Tree').props.checkedKeys), [])
  await act(async () => renderer.unmount())
})

test('supplier and supplier-account status writes ignore synchronous duplicate clicks', async () => {
  const supplier = { id: 8, name: '供应商', status: 'ACTIVE', createdAt: '' }
  const account = { id: 12, employeeNo: 's12', realName: '账号', email: 's12@example.invalid', status: 'ACTIVE', createdAt: '' }
  let releaseSupplier
  let releaseAccount
  const supplierPending = new Promise((resolve) => { releaseSupplier = resolve })
  const accountPending = new Promise((resolve) => { releaseAccount = resolve })
  const puts = []
  const http = {
    get: async (url) => {
      if (url === '/admin/suppliers') return { data: { list: [supplier], total: 1, page: 1, pageSize: 10 } }
      if (url === '/admin/suppliers/8/accounts') return { data: [account] }
      return { data: [] }
    },
    put: async (url) => {
      puts.push(url)
      return url === '/admin/suppliers/8/status' ? supplierPending : accountPending
    },
  }
  const Page = loadTs('src/pages/supplier/SupplierList.tsx', {
    '@arco-design/web-react': arco,
    '@arco-design/web-react/icon': iconModule(),
    '../../api/client': http,
    '../../api/types': { fmtTime: String },
    '../../store/auth': authModule(['supplier:account']),
    '../../utils/password': { passwordRule: {} },
  }).default
  let renderer
  await act(async () => { renderer = create(React.createElement(Page)) })

  const supplierActions = renderer.root.findAllByType('Table')[0].props.columns.at(-1).render(null, supplier)
  const statusConfirm = findElement(supplierActions, (node) => typeof node.props.onOk === 'function')
  let firstSupplier
  let secondSupplier
  await act(async () => {
    firstSupplier = statusConfirm.props.onOk()
    secondSupplier = statusConfirm.props.onOk()
    await Promise.resolve()
  })
  assert.equal(puts.filter((url) => url === '/admin/suppliers/8/status').length, 1)
  releaseSupplier({})
  await act(async () => { await Promise.all([firstSupplier, secondSupplier]) })

  const accountAction = findElement(supplierActions, (node) => node.props.children === '账号管理')
  await act(async () => accountAction.props.onClick())
  const accountActions = renderer.root.findAllByType('Table')[1].props.columns.at(-1).render(null, account)
  const accountStatus = findElement(accountActions, (node) => node.props.children === '禁用')
  let firstAccount
  let secondAccount
  await act(async () => {
    firstAccount = accountStatus.props.onClick()
    secondAccount = accountStatus.props.onClick()
    await Promise.resolve()
  })
  assert.equal(puts.filter((url) => url === '/admin/supplier-accounts/12/status').length, 1)
  releaseAccount({})
  await act(async () => { await Promise.all([firstAccount, secondAccount]) })
  await act(async () => renderer.unmount())
})

test('disabled users can keep their unavailable role while editing profile, but cannot choose another unavailable role', async () => {
  const retiredRoleId = 41
  const user = {
    id: 9,
    employeeNo: 'disabled-user',
    realName: '停用用户',
    email: 'disabled@example.invalid',
    departmentId: null,
    roleId: retiredRoleId,
    roleName: '历史角色',
    status: 'DISABLED',
  }
  let submittedRoleId = retiredRoleId
  const form = {
    resetFields() {},
    setFieldsValue() {},
    validate: async () => ({
      realName: '停用用户已更新',
      email: user.email,
      departmentId: undefined,
      roleId: submittedRoleId,
    }),
  }
  let formIndex = 0
  const userArco = new Proxy({
    ...arco,
    Form: Object.assign(component('Form'), {
      useForm: () => [formIndex++ % 2 === 0 ? form : {}],
      Item: component('Form.Item'),
    }),
  }, { get: (obj, key) => obj[key] ?? component(key) })
  const puts = []
  const http = {
    get: async (url) => {
      if (url === '/admin/users') return { data: { list: [user], total: 1, page: 1, pageSize: 10 } }
      if (url === '/admin/user-role-options') return { data: [{ id: 7, name: '内部成员' }] }
      return { data: [] }
    },
    put: async (url, body) => { puts.push({ url, body }); return { data: {} } },
  }
  const Page = loadTs('src/pages/org/UserList.tsx', {
    '@arco-design/web-react': userArco,
    '@arco-design/web-react/icon': iconModule(),
    '../../api/client': http,
    '../../api/types': { fmtTime: String },
    '../../store/auth': authModule(),
    '../../utils/password': { passwordRule: {} },
  }).default
  let renderer
  await act(async () => { renderer = create(React.createElement(Page)) })
  const actions = renderer.root.findByType('Table').props.columns.at(-1).render(null, user)
  const edit = findElement(actions, (node) => node.props.children === '编辑')
  await act(async () => edit.props.onClick())

  const roleItem = renderer.root.findAllByType('Form.Item').find((node) => node.props.field === 'roleId')
  const retiredOption = roleItem.findAllByType('Select.Option').find((node) => node.props.value === retiredRoleId)
  assert.ok(retiredOption, 'the current unavailable role must remain visible')
  assert.match(JSON.stringify(retiredOption.props.children), /已禁用/)

  const editModal = renderer.root.findAllByType('Modal').find((node) => node.props.title === '编辑用户')
  submittedRoleId = 99
  await act(async () => editModal.props.onOk())
  assert.equal(puts.length, 0, 'only the user current unavailable role may bypass active role options')

  submittedRoleId = retiredRoleId
  await act(async () => editModal.props.onOk())
  assert.equal(puts.length, 1)
  assert.equal(puts[0].url, '/admin/users/9')
  assert.equal(puts[0].body.roleId, retiredRoleId)
  await act(async () => renderer.unmount())
})
