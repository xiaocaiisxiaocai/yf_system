const test = require('node:test')
const assert = require('node:assert/strict')
const fs = require('node:fs')
const path = require('node:path')
const vm = require('node:vm')
const ts = require('typescript')
const React = require('react')
const { create, act } = require('react-test-renderer')

const component = name => Object.assign(
  props => React.createElement(name, props, props.children),
  {
    Search: props => React.createElement(`${name}.Search`, props, props.children),
    Option: props => React.createElement(`${name}.Option`, props, props.children),
    TextArea: props => React.createElement(`${name}.TextArea`, props, props.children),
  },
)

const arco = new Proxy({
  Form: Object.assign(component('Form'), { useForm: () => [{}], Item: component('Form.Item') }),
  Typography: { Text: component('Text') },
  Message: { success() {}, warning() {}, error() {}, info() {} },
}, { get: (target, name) => target[name] ?? component(name) })

const icons = new Proxy({}, { get: (_, name) => component(name) })

function deferred() {
  let resolve
  let reject
  const promise = new Promise((ok, fail) => { resolve = ok; reject = fail })
  return { promise, resolve, reject }
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

function loadPage(relativePath, http, permissions) {
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
  const authState = {
    user: { id: 999, userType: 'INTERNAL' },
    hasPerm: code => permissions.includes(code),
  }
  const mocks = {
    '@arco-design/web-react': arco,
    '@arco-design/web-react/icon': icons,
    '../../api/client': http,
    '../../api/types': {
      fmtTime: String,
      PROJECT_STATUS: {
        DRAFT: { text: '草稿', color: 'gray' },
        IN_PROGRESS: { text: '进行中', color: 'blue' },
      },
    },
    '../../components/ActionSlots': {
      actionSlots: slots => React.createElement('div', null, slots.filter(Boolean)),
    },
    '../../components/PasswordInput': component('PasswordInput'),
    '../../store/auth': {
      useAuth: selector => selector ? selector(authState) : authState,
    },
    '../../store/collaboration': {
      useCollaboration: selector => selector({ revision: '', status: 'connected' }),
    },
    '../../utils/password': { passwordRule: {} },
    '../../utils/textRules': { textLengthRule: () => ({}) },
    'react-router-dom': {
      Link: component('Link'),
      useNavigate: () => () => {},
    },
  }
  vm.runInNewContext(source, {
    exports,
    module: { exports },
    console,
    setTimeout,
    clearTimeout,
    URL,
    URLSearchParams,
    AbortController,
    window: {
      matchMedia: () => ({ matches: false, addEventListener() {}, removeEventListener() {} }),
    },
    require: name => mocks[name] ?? require(name),
  }, { filename })
  return exports.default
}

const flush = () => new Promise(resolve => setImmediate(resolve))

test('role permission save failure resolves the click handler, keeps selections, and unlocks retry', async () => {
  const role = {
    id: 7, name: '质量角色', description: '', isBuiltIn: false, status: 'ACTIVE',
    permissionIds: [1], assignedUserCount: 0, canManage: true, supplierRestricted: false,
  }
  const perms = [
    { id: 1, name: '项目协作', type: 'MENU', parentId: null, grantable: true, supplierAssignable: true },
    { id: 2, name: '查看项目', type: 'ACTION', parentId: 1, grantable: true, supplierAssignable: true },
  ]
  const firstSave = deferred()
  let saveAttempts = 0
  const http = {
    get: async url => ({ data: url === '/permissions' ? perms : { list: [role], total: 1, page: 1, pageSize: 20 } }),
    put: async url => {
      assert.equal(url, '/admin/roles/7/permissions')
      saveAttempts += 1
      if (saveAttempts === 1) return firstSave.promise
      return { data: {} }
    },
  }
  const RoleList = loadPage('src/pages/rbac/RoleList.tsx', http, [])
  let renderer
  await act(async () => { renderer = create(React.createElement(RoleList)); await flush() })

  const actions = renderer.root.findByType('Table').props.columns.at(-1).render(null, role)
  await act(async () => findElement(actions, node => node.props.children === '分配权限').props.onClick())
  let tree = renderer.root.findByType('Tree')
  await act(async () => tree.props.onCheck([], { checked: true, node: { key: '2' } }))
  assert.deepEqual(new Set(renderer.root.findByType('Tree').props.checkedKeys), new Set(['1', '2']))

  const saveButton = () => findElement(renderer.root.findByType('Drawer').props.footer,
    node => node.props.children === '保存权限')
  let firstPromise
  await act(async () => {
    firstPromise = saveButton().props.onClick()
    await Promise.resolve()
  })
  tree = renderer.root.findByType('Tree')
  assert.ok(tree.props.treeData.every(group => group.disabled
    && group.children.every(child => child.disabled)))
  await act(async () => tree.props.onCheck([], { checked: false, node: { key: '2' } }))
  assert.deepEqual(new Set(renderer.root.findByType('Tree').props.checkedKeys), new Set(['1', '2']))

  await act(async () => {
    firstSave.reject(new Error('save failed'))
    await firstPromise
  })
  assert.equal(renderer.root.findByType('Drawer').props.visible, true)
  assert.ok(renderer.root.findByType('Tree').props.treeData.every(group => !group.disabled
    && group.children.every(child => !child.disabled)))
  assert.deepEqual(new Set(renderer.root.findByType('Tree').props.checkedKeys), new Set(['1', '2']))

  await act(async () => { await saveButton().props.onClick(); await flush() })
  assert.equal(saveAttempts, 2)
  assert.equal(renderer.root.findByType('Drawer').props.visible, false)
  await act(async () => renderer.unmount())
})

test('empty permission catalog disables saving but an intentionally cleared role remains saveable', async () => {
  for (const catalogAvailable of [false, true]) {
    const role = { id: 7, name: '测试角色', isBuiltIn: false, status: 'ACTIVE', permissionIds: [1], assignedUserCount: 0, canManage: true }
    const writes = []
    const permissions = catalogAvailable ? [{ id: 1, name: '项目协作', type: 'MENU', parentId: null, grantable: true }] : []
    const Page = loadPage('src/pages/rbac/RoleList.tsx', {
      get: async (url) => ({ data: url === '/permissions' ? permissions : { list: [role], total: 1, page: 1, pageSize: 20 } }),
      put: async (_url, body) => { writes.push(body); return { data: {} } },
    }, [])
    let renderer
    await act(async () => { renderer = create(React.createElement(Page)); await flush() })
    const actions = renderer.root.findByType('Table').props.columns.at(-1).render(null, role)
    await act(async () => findElement(actions, (node) => node.props.children === '分配权限').props.onClick())
    if (catalogAvailable) await act(async () => renderer.root.findByType('Tree').props.onCheck([], { checked: false, node: { key: '1' } }))
    const save = findElement(renderer.root.findByType('Drawer').props.footer, (node) => node.props.children === '保存权限')
    assert.equal(save.props.disabled, !catalogAvailable)
    await act(async () => { await save.props.onClick(); await flush() })
    assert.equal(writes.length, catalogAvailable ? 1 : 0)
    if (catalogAvailable) assert.deepEqual(Array.from(writes[0].permissionIds), [])
    await act(async () => renderer.unmount())
  }
})

test('project status failure resolves the select handler and re-enables the same row for retry', async () => {
  const project = {
    id: 101, name: '失败重试项目', supplierId: 8, supplierName: '供应商', status: 'DRAFT',
    createdByName: '创建人', updatedAt: '',
  }
  const firstWrite = deferred()
  let projectGets = 0
  let writeAttempts = 0
  const http = {
    get: async url => {
      if (url === '/projects') { projectGets += 1; return { data: { list: [project], total: 1, page: 1, pageSize: 10 } } }
      if (url === '/supplier-options') return { data: [{ id: 8, name: '供应商' }] }
      throw new Error(`unexpected GET ${url}`)
    },
    put: async url => {
      assert.equal(url, '/projects/101/status')
      writeAttempts += 1
      if (writeAttempts === 1) return firstWrite.promise
      return { data: {} }
    },
  }
  const ProjectList = loadPage('src/pages/project/ProjectList.tsx', http,
    ['project:list', 'project:status'])
  let renderer
  await act(async () => { renderer = create(React.createElement(ProjectList)); await flush() })

  const statusControl = () => findElement(
    renderer.root.findByType('Table').props.columns.at(-1).render(null, project),
    node => node.props.placeholder === '状态' && typeof node.props.onChange === 'function',
  )
  let firstPromise
  await act(async () => {
    firstPromise = statusControl().props.onChange('IN_PROGRESS')
    await Promise.resolve()
  })
  assert.equal(statusControl().props.disabled, true)
  await act(async () => {
    firstWrite.reject(new Error('status failed'))
    await firstPromise
  })
  assert.equal(statusControl().props.disabled, false)
  assert.equal(projectGets, 1, 'failed writes must keep the current list snapshot')

  await act(async () => { await statusControl().props.onChange('IN_PROGRESS'); await flush() })
  assert.equal(writeAttempts, 2)
  assert.ok(projectGets > 1, 'successful retry refreshes the project list')
  await act(async () => renderer.unmount())
})

test('supplier account status failure resolves the button handler and preserves the account for retry', async () => {
  const supplier = { id: 8, name: '供应商', status: 'ACTIVE', createdAt: '' }
  const account = { id: 12, employeeNo: 's12', realName: '供应商账号', email: 's12@example.invalid', status: 'ACTIVE', createdAt: '' }
  const firstWrite = deferred()
  let accountGets = 0
  let writeAttempts = 0
  const http = {
    get: async url => {
      if (url === '/admin/suppliers') return { data: { list: [supplier], total: 1, page: 1, pageSize: 10 } }
      if (url === '/admin/suppliers/8/accounts') { accountGets += 1; return { data: [account] } }
      throw new Error(`unexpected GET ${url}`)
    },
    put: async url => {
      assert.equal(url, '/admin/supplier-accounts/12/status')
      writeAttempts += 1
      if (writeAttempts === 1) return firstWrite.promise
      return { data: {} }
    },
  }
  const SupplierList = loadPage('src/pages/supplier/SupplierList.tsx', http,
    ['supplier:account'])
  let renderer
  await act(async () => { renderer = create(React.createElement(SupplierList)); await flush() })

  const supplierActions = renderer.root.findAllByType('Table')[0].props.columns.at(-1).render(null, supplier)
  await act(async () => { findElement(supplierActions, node => node.props.children === '账号管理').props.onClick(); await flush() })
  const accountStatusButton = () => findElement(
    renderer.root.findAllByType('Table')[1].props.columns.at(-1).render(null, account),
    node => node.props.children === '禁用',
  )
  let firstPromise
  await act(async () => {
    firstPromise = accountStatusButton().props.onClick()
    await Promise.resolve()
  })
  assert.equal(accountStatusButton().props.disabled, true)
  await act(async () => {
    firstWrite.reject(new Error('account status failed'))
    await firstPromise
  })
  assert.equal(accountStatusButton().props.disabled, false)
  assert.equal(accountGets, 1, 'failed writes must keep the current account snapshot')
  assert.equal(renderer.root.findAllByType('Table')[1].props.data[0].id, 12)

  await act(async () => { await accountStatusButton().props.onClick(); await flush() })
  assert.equal(writeAttempts, 2)
  assert.equal(accountGets, 2, 'successful retry refreshes the account list')
  await act(async () => renderer.unmount())
})
