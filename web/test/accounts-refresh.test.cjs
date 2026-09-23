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

function loadSupplierList(http) {
  const mocks = {
    '@arco-design/web-react': arco,
    '@arco-design/web-react/icon': new Proxy({}, { get: (_, name) => component(name) }),
    '../../api/client': http,
    '../../api/types': { fmtTime: String },
    '../../components/ActionSlots': {
      actionSlots: slots => React.createElement('div', null, slots.filter(Boolean)),
    },
    '../../components/PasswordInput': component('PasswordInput'),
    '../../store/auth': {
      useAuth: selector => {
        const state = { hasPerm: code => code === 'supplier:account' }
        return selector ? selector(state) : state
      },
    },
    '../../utils/password': { passwordRule: {} },
    '../../utils/textRules': { textLengthRule: () => ({}) },
  }

  function loadModule(filename) {
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
      require: name => {
        if (name in mocks) return mocks[name]
        const normalizedName = name.replace(/\\/g, '/')
        const mock = Object.entries(mocks).find(([key]) => normalizedName.endsWith(key.replace(/\\/g, '/')))
        if (mock) return mock[1]
        if (name.startsWith('.')) {
          const local = ['.tsx', '.ts'].map((ext) => path.resolve(path.dirname(filename), name + ext)).find(fs.existsSync)
          if (local) return loadModule(local)
        }
        return require(name)
      },
    }, { filename })
    return exports
  }

  return loadModule(path.resolve(__dirname, '../src/pages/supplier/SupplierList.tsx')).default
}

const supplier = { id: 8, name: '并发供应商', status: 'ACTIVE', createdAt: '' }
const accountA = { id: 11, employeeNo: 'a11', realName: '账号A', email: 'a@example.invalid', status: 'ACTIVE', createdAt: '' }
const accountB = { id: 12, employeeNo: 'b12', realName: '账号B', email: 'b@example.invalid', status: 'ACTIVE', createdAt: '' }

for (const connected of [true, false]) {
  test(`closing supplier accounts restores only a connected opener (connected=${connected})`, async () => {
    let focused = 0
    const trigger = { isConnected: connected, focus: () => { focused += 1 } }
    const http = {
      get: async url => {
        if (url === '/admin/suppliers') return { data: { list: [supplier], total: 1, page: 1, pageSize: 10 } }
        if (url === '/admin/suppliers/8/accounts') return { data: [accountA] }
        throw new Error(`unexpected GET ${url}`)
      },
    }
    const SupplierList = loadSupplierList(http)
    let renderer
    try {
      await act(async () => { renderer = create(React.createElement(SupplierList)) })
      const actions = renderer.root.findAllByType('Table')[0].props.columns.at(-1).render(null, supplier)
      const open = findElement(actions, node => node.props.children === '账号管理')
      await act(async () => { open.props.onClick({ currentTarget: trigger }) })
      assert.equal(focused, 0, 'opening the drawer must leave focus to its focus lock')
      const drawer = renderer.root.findByType('Drawer')
      assert.equal(drawer.props.visible, true)
      await act(async () => { drawer.props.onCancel() })
      assert.equal(renderer.root.findByType('Drawer').props.visible, false)
      assert.equal(focused, connected ? 1 : 0, 'closed drawer restores the active opener without focusing a removed element')
    } finally { if (renderer) await act(async () => renderer.unmount()) }
  })
}

for (const lateOutcome of ['success', 'failure']) {
  test(`an older account refresh ${lateOutcome} cannot replace the latest successful snapshot`, async () => {
    const writes = new Map([[11, deferred()], [12, deferred()]])
    const refreshes = []
    let accountGets = 0
    const http = {
      get: async url => {
        if (url === '/admin/suppliers') return { data: { list: [supplier], total: 1, page: 1, pageSize: 10 } }
        if (url === '/admin/suppliers/8/accounts') {
          accountGets += 1
          if (accountGets === 1) return { data: [accountA, accountB] }
          const request = deferred()
          refreshes.push(request)
          return request.promise
        }
        throw new Error(`unexpected GET ${url}`)
      },
      put: async url => {
        const id = Number(url.match(/^\/admin\/supplier-accounts\/(\d+)\/status$/)?.[1])
        return writes.get(id).promise
      },
      post: async () => ({ data: {} }),
      delete: async () => ({ data: {} }),
    }
    const SupplierList = loadSupplierList(http)
    let renderer
    await act(async () => { renderer = create(React.createElement(SupplierList)) })

    const supplierTable = renderer.root.findAllByType('Table')[0]
    const supplierActions = supplierTable.props.columns.at(-1).render(null, supplier)
    const openAccounts = findElement(supplierActions, node => node.props.children === '账号管理')
    await act(async () => {
      openAccounts.props.onClick()
      await new Promise(resolve => setImmediate(resolve))
    })

    const accountTable = () => renderer.root.findAllByType('Table')[1]
    const statusAction = account => findElement(
      accountTable().props.columns.at(-1).render(null, account),
      node => node.props.children === '禁用',
    )
    let firstWrite
    let secondWrite
    await act(async () => {
      firstWrite = statusAction(accountA).props.onClick()
      secondWrite = statusAction(accountB).props.onClick()
      await Promise.resolve()
    })

    await act(async () => {
      writes.get(11).resolve({ data: {} })
      await firstWrite
      await new Promise(resolve => setImmediate(resolve))
    })
    assert.equal(refreshes.length, 1)
    await act(async () => {
      writes.get(12).resolve({ data: {} })
      await secondWrite
      await new Promise(resolve => setImmediate(resolve))
    })
    assert.equal(refreshes.length, 2)

    const latest = [
      { ...accountA, status: 'DISABLED' },
      { ...accountB, status: 'DISABLED' },
    ]
    await act(async () => {
      refreshes[1].resolve({ data: latest })
      await refreshes[1].promise
      await new Promise(resolve => setImmediate(resolve))
    })
    await act(async () => {
      if (lateOutcome === 'success') {
        refreshes[0].resolve({ data: [{ ...accountA, status: 'DISABLED' }, accountB] })
        await refreshes[0].promise
      } else {
        refreshes[0].reject(new Error('older refresh failed'))
        await refreshes[0].promise.catch(() => undefined)
      }
      await new Promise(resolve => setImmediate(resolve))
    })

    assert.deepEqual(accountTable().props.data.map(item => [item.id, item.status]), [[11, 'DISABLED'], [12, 'DISABLED']])
    assert.equal(renderer.root.findAll(node => node.props.children === '加载失败').length, 0)
    await act(async () => renderer.unmount())
  })
}
