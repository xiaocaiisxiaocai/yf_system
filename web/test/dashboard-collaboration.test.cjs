const test = require('node:test')
const assert = require('node:assert/strict')
const fs = require('node:fs')
const path = require('node:path')
const vm = require('node:vm')
const ts = require('typescript')
const React = require('react')
const { create, act } = require('react-test-renderer')
const { create: createStore } = require('zustand')

const component = name => props => React.createElement(name, props, props.children)
const Button = Object.assign(component('Button'), { Group: component('Button.Group') })
const arco = new Proxy({
  Button,
  Grid: Object.assign(component('Grid'), { Row: component('Grid.Row'), Col: component('Grid.Col') }),
  List: Object.assign(component('List'), { Item: Object.assign(component('List.Item'), { Meta: component('List.Item.Meta') }) }),
  Typography: { Text: component('Text') },
}, { get: (target, name) => target[name] ?? component(name) })

function deferred() {
  let resolve
  let reject
  const promise = new Promise((ok, fail) => { resolve = ok; reject = fail })
  return { promise, resolve, reject }
}

function loadDashboard(http, collaboration, navigations = [], auth = createStore(() => ({
  user: { id: 1, realName: '协作用户' }, menus: ['dashboard'], permissions: ['project:list'],
}))) {
  const filename = path.resolve(__dirname, '../src/pages/Dashboard.tsx')
  const source = ts.transpileModule(fs.readFileSync(filename, 'utf8'), {
    compilerOptions: {
      module: ts.ModuleKind.CommonJS,
      jsx: ts.JsxEmit.ReactJSX,
      target: ts.ScriptTarget.ES2022,
      esModuleInterop: true,
    },
  }).outputText
  const exports = {}
  const mocks = {
    '@arco-design/web-react': arco,
    '@arco-design/web-react/icon': new Proxy({}, { get: (_, name) => component(name) }),
    'react-router-dom': { Link: component('Link'), useNavigate: () => target => navigations.push(target) },
    '../api/client': http,
    '../api/types': { fmtTime: String },
    '../store/auth': {
      useAuth: auth,
    },
    '../store/collaboration': { useCollaboration: collaboration },
  }
  vm.runInNewContext(source, {
    exports,
    module: { exports },
    console,
    require: name => name.endsWith('.css') ? {} : (mocks[name] ?? require(name)),
  }, { filename })
  return exports.default
}

const summary = {
  projectCount: 3,
  activeProjectCount: 2,
  pendingConfirmations: 1,
  unreadMessages: 12,
  recentMessages: [],
}

const emptyPage = { list: [], total: 0, page: 1, pageSize: 10 }

test('dashboard menu alone does not request project data, including after project access is revoked', async () => {
  const calls = []
  const auth = createStore(() => ({ user: { id: 1, realName: '供应商账号管理员' }, menus: ['dashboard', 'supplier:list'], permissions: ['supplier:account'] }))
  const collaboration = createStore(() => ({ revision: 'initial', status: 'ready' }))
  const Dashboard = loadDashboard({ get: async url => {
    calls.push(url)
    return { data: url === '/dashboard/summary' ? summary : emptyPage }
  } }, collaboration, [], auth)
  let renderer
  try {
    await act(async () => { renderer = create(React.createElement(Dashboard)) })
    assert.deepEqual(calls, [])
    assert.match(renderer.root.findByType('Result').props.subTitle, /未分配项目查看权限/)
    await act(async () => collaboration.setState({ revision: 'changed-without-access' }))
    assert.deepEqual(calls, [])
    await act(async () => auth.setState({ permissions: ['supplier:account', 'project:list'] }))
    assert.deepEqual(calls.sort(), ['/dashboard/messages', '/dashboard/pending-projects', '/dashboard/summary'])
    const count = calls.length
    await act(async () => auth.setState({ permissions: ['supplier:account'] }))
    await act(async () => collaboration.setState({ revision: 'changed-after-revocation' }))
    assert.equal(calls.length, count)
    assert.equal(renderer.root.findAllByProps({ className: 'dashboard-collaboration' }).length, 0)
    assert.match(renderer.root.findByType('Result').props.subTitle, /未分配项目查看权限/)
  } finally { if (renderer) await act(async () => renderer.unmount()) }
})

test('network recovery reloads dashboard resources even when server revision is unchanged', async () => {
  const collaboration = createStore(() => ({ revision: 'same-server-revision', status: 'ready' }))
  const calls = []
  const Dashboard = loadDashboard({ get: async (url) => {
    calls.push(url)
    return { data: url === '/dashboard/summary' ? summary : emptyPage }
  } }, collaboration)
  let renderer
  await act(async () => { renderer = create(React.createElement(Dashboard)) })
  const initialCount = calls.length
  await act(async () => collaboration.setState({ status: 'error' }))
  assert.equal(calls.length, initialCount, 'do not fan out requests while connection is down')
  await act(async () => collaboration.setState({ status: 'ready' }))
  assert.deepEqual(calls.slice(initialCount).sort(), ['/dashboard/messages', '/dashboard/pending-projects', '/dashboard/summary'])
  await act(async () => renderer.unmount())
})

test('dashboard defaults to the complete paginated unread feed and deep-links a selected message', async () => {
  const calls = []
  const navigations = []
  const collaboration = createStore(() => ({ revision: 'initial' }))
  const unread = {
    id: 37,
    projectId: 8,
    projectName: '设备验收',
    content: '请核对全部验收记录',
    senderName: '供应商成员',
    createdAt: '2026-09-13T01:00:00Z',
    unread: true,
  }
  const http = {
    get: async (url, config = {}) => {
      calls.push({ url, config })
      if (url === '/dashboard/summary') return { data: summary }
      if (url === '/dashboard/pending-projects') return { data: emptyPage }
      if (url === '/dashboard/messages') {
        return { data: config.params.unreadOnly ? { list: [unread], total: 12, page: config.params.page, pageSize: 10 } : emptyPage }
      }
      throw new Error(`unexpected request ${url}`)
    },
  }
  const Dashboard = loadDashboard(http, collaboration, navigations)
  let renderer
  await act(async () => { renderer = create(React.createElement(Dashboard)) })

  const initialMessages = calls.find(call => call.url === '/dashboard/messages')
  assert.deepEqual({ ...initialMessages.config.params }, { page: 1, pageSize: 10, unreadOnly: true })
  assert.equal(initialMessages.config.quietNetworkError, true, 'inline retry states replace duplicate network toasts')
  const messageList = renderer.root.findByProps({ className: 'dashboard-message-list' })
  assert.equal(messageList.props.dataSource[0].id, 37)
  const messageRow = messageList.props.render(unread)
  assert.equal(messageRow.props['aria-label'], '查看设备验收的留言')
  await act(async () => messageRow.props.onClick())
  assert.deepEqual(navigations, ['/projects/8?tab=messages&target=37'])

  const messageCard = renderer.root.findByProps({ id: 'dashboard-messages' })
  const allButton = React.Children.toArray(messageCard.props.extra.props.children).find(node => node.props.children === '全部')
  await act(async () => allButton.props.onClick())
  const allMessages = calls.filter(call => call.url === '/dashboard/messages').at(-1)
  assert.deepEqual({ ...allMessages.config.params }, { page: 1, pageSize: 10, unreadOnly: false })
  await act(async () => renderer.unmount())
})

test('collaboration revisions refresh quietly and older replies cannot replace the latest dashboard snapshot', async () => {
  const collaboration = createStore(() => ({ revision: 'initial' }))
  const refreshes = []
  const http = {
    get: async (url, config = {}) => {
      if (collaboration.getState().revision === 'initial') {
        if (url === '/dashboard/summary') return { data: summary }
        return { data: emptyPage }
      }
      const request = deferred()
      refreshes.push({ url, config, ...request })
      return request.promise
    },
  }
  const Dashboard = loadDashboard(http, collaboration)
  let renderer
  await act(async () => { renderer = create(React.createElement(Dashboard)) })

  await act(async () => collaboration.setState({ revision: 'revision-1' }))
  assert.equal(refreshes.length, 3)
  assert.equal(renderer.root.findAllByType('Spin').length, 0, 'a revision refresh keeps the current workbench visible')
  await act(async () => collaboration.setState({ revision: 'revision-2' }))
  assert.equal(refreshes.length, 6)
  for (const request of refreshes) assert.equal(request.config.quietNetworkError, true)

  const latest = refreshes.slice(3)
  await act(async () => {
    latest.find(request => request.url === '/dashboard/summary').resolve({ data: { ...summary, unreadMessages: 1 } })
    latest.find(request => request.url === '/dashboard/pending-projects').resolve({ data: emptyPage })
    latest.find(request => request.url === '/dashboard/messages').resolve({ data: {
      list: [{ id: 202, projectId: 2, content: '新留言', createdAt: '', unread: true }],
      total: 1,
      page: 1,
      pageSize: 10,
    } })
    await Promise.resolve()
  })

  const older = refreshes.slice(0, 3)
  await act(async () => {
    older.find(request => request.url === '/dashboard/summary').resolve({ data: { ...summary, unreadMessages: 9 } })
    older.find(request => request.url === '/dashboard/pending-projects').resolve({ data: emptyPage })
    older.find(request => request.url === '/dashboard/messages').resolve({ data: {
      list: [{ id: 101, projectId: 1, content: '旧留言', createdAt: '', unread: true }],
      total: 1,
      page: 1,
      pageSize: 10,
    } })
    await Promise.resolve()
  })

  const messageList = renderer.root.findByProps({ className: 'dashboard-message-list' })
  assert.equal(messageList.props.dataSource[0].id, 202)
  await act(async () => renderer.unmount())
})

test('a failed quiet refresh keeps the last data visible and marks it stale', async () => {
  const collaboration = createStore(() => ({ revision: 'initial' }))
  const http = {
    get: async (url) => {
      if (collaboration.getState().revision !== 'initial') throw new Error('temporary refresh failure')
      if (url === '/dashboard/summary') return { data: summary }
      if (url === '/dashboard/pending-projects') return { data: emptyPage }
      return { data: { list: [{ id: 7, projectId: 3, content: '保留内容', createdAt: '', unread: true }], total: 1, page: 1, pageSize: 10 } }
    },
  }
  const Dashboard = loadDashboard(http, collaboration)
  let renderer
  await act(async () => { renderer = create(React.createElement(Dashboard)) })
  await act(async () => collaboration.setState({ revision: 'revision-1' }))

  const messageList = renderer.root.findByProps({ className: 'dashboard-message-list' })
  assert.equal(messageList.props.dataSource[0].id, 7)
  assert.equal(renderer.root.findAll(node => node.props.role === 'status').length, 3)
  assert.equal(renderer.root.findAllByType('Spin').length, 0)
  await act(async () => renderer.unmount())
})
