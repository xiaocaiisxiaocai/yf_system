const test = require('node:test')
const assert = require('node:assert/strict')
const fs = require('node:fs')
const path = require('node:path')
const vm = require('node:vm')
const ts = require('typescript')
const React = require('react')
const { create, act } = require('react-test-renderer')

const component = name => props => React.createElement(name, props, props.children)

function deferred() {
  let resolve
  let reject
  const promise = new Promise((ok, fail) => { resolve = ok; reject = fail })
  return { promise, resolve, reject }
}

function loadMessagePanel(http, observedIds) {
  const observers = []
  class IntersectionObserverMock {
    constructor(callback) {
      this.callback = callback
      this.nodes = []
      this.disconnected = false
      observers.push(this)
    }
    observe(node) { this.nodes.push(node) }
    disconnect() { this.disconnected = true }
  }

  const arco = new Proxy({
    Input: Object.assign(component('Input'), { TextArea: component('Input.TextArea') }),
    List: Object.assign(component('List'), { Item: Object.assign(component('List.Item'), { Meta: component('List.Item.Meta') }) }),
    Typography: { Text: component('Text') },
  }, { get: (target, name) => target[name] ?? component(name) })
  const mocks = {
    '@arco-design/web-react': arco,
    '@arco-design/web-react/icon': new Proxy({}, { get: (_, name) => component(name) }),
    '../api/client': http,
    '../store/auth': { useAuth: () => ({ hasPerm: () => false, user: { id: 9 } }) },
    '../api/types': { fmtTime: String },
  }
  const filename = path.resolve(__dirname, '../src/components/MessagePanel.tsx')
  const source = ts.transpileModule(fs.readFileSync(filename, 'utf8'), {
    compilerOptions: {
      module: ts.ModuleKind.CommonJS,
      jsx: ts.JsxEmit.ReactJSX,
      target: ts.ScriptTarget.ES2022,
      esModuleInterop: true,
    },
  }).outputText
  const exports = {}
  const context = {
    exports,
    module: { exports },
    console,
    IntersectionObserver: IntersectionObserverMock,
    require: name => mocks[name] ?? require(name),
  }
  vm.runInNewContext(source, context, { filename })

  const createNodeMock = element => element.type === 'div' ? {
    querySelectorAll: () => observedIds.current.map(id => ({
      closest: () => ({ dataset: { messageId: String(id) } }),
    })),
  } : {}
  return { MessagePanel: exports.default, observers, createNodeMock }
}

function row(id, overrides = {}) {
  return {
    id,
    projectId: 1,
    senderId: 2,
    senderName: '留言人',
    senderType: 'INTERNAL',
    content: '待确认内容',
    readCount: 0,
    totalCount: 1,
    readByMe: false,
    createdAt: '',
    ...overrides,
  }
}

function page(list) {
  return { data: { list, total: list.length, page: 1, pageSize: 20 } }
}

function activeObserver(observers) {
  const observer = [...observers].reverse().find(item => !item.disconnected && item.nodes.length > 0)
  assert.ok(observer, 'an unread marker must be observed')
  return observer
}

async function intersect(observer) {
  await act(async () => {
    observer.callback(observer.nodes.map(target => ({ target, isIntersecting: true, intersectionRatio: 0.6 })))
    await new Promise(resolve => setImmediate(resolve))
  })
}

function readLabel(renderer, value) {
  const textContent = input => {
    if (Array.isArray(input)) return input.map(textContent).join('')
    if (React.isValidElement(input)) return textContent(input.props.children)
    return input == null ? '' : String(input)
  }
  return renderer.root.findAllByType('Text').some(node => textContent(node.props.children).includes(`已读 ${value}`))
}

test('mark-read refreshes the participant count from the authoritative receipt', async () => {
  const observedIds = { current: [7] }
  let postCalls = 0
  const http = {
    get: async url => url === '/projects/1/messages'
      ? page([row(7)])
      : { data: { readers: [{ userId: 9 }], unread: [] } },
    post: async () => { postCalls += 1; return { data: {} } },
    delete: async () => ({ data: {} }),
  }
  const { MessagePanel, observers, createNodeMock } = loadMessagePanel(http, observedIds)
  let renderer
  await act(async () => { renderer = create(React.createElement(MessagePanel, { projectId: 1, projectStatus: 'IN_PROGRESS' }), { createNodeMock }) })

  await intersect(activeObserver(observers))

  assert.equal(postCalls, 1)
  assert.equal(readLabel(renderer, '1/1'), true)
  assert.equal(renderer.root.findByProps({ 'data-message-id': 7 }).props['data-unread'], 'false')
  await act(async () => renderer.unmount())
})

test('mark-read does not count a view-all reader who is not a project participant', async () => {
  const observedIds = { current: [7] }
  const http = {
    get: async url => url === '/projects/1/messages'
      ? page([row(7)])
      : { data: { readers: [], unread: [{ userId: 3 }] } },
    post: async () => ({ data: {} }),
    delete: async () => ({ data: {} }),
  }
  const { MessagePanel, observers, createNodeMock } = loadMessagePanel(http, observedIds)
  let renderer
  await act(async () => { renderer = create(React.createElement(MessagePanel, { projectId: 1, projectStatus: 'IN_PROGRESS' }), { createNodeMock }) })

  await intersect(activeObserver(observers))

  assert.equal(readLabel(renderer, '0/1'), true)
  assert.equal(renderer.root.findByProps({ 'data-message-id': 7 }).props['data-unread'], 'false')
  await act(async () => renderer.unmount())
})

test('a failed receipt refresh marks the message read without inventing a count', async () => {
  const observedIds = { current: [7] }
  const http = {
    get: async url => {
      if (url === '/projects/1/messages') return page([row(7, { readCount: 2, totalCount: 3 })])
      throw new Error('receipt temporarily unavailable')
    },
    post: async () => ({ data: {} }),
    delete: async () => ({ data: {} }),
  }
  const { MessagePanel, observers, createNodeMock } = loadMessagePanel(http, observedIds)
  let renderer
  await act(async () => { renderer = create(React.createElement(MessagePanel, { projectId: 1, projectStatus: 'IN_PROGRESS' }), { createNodeMock }) })

  await intersect(activeObserver(observers))

  assert.equal(readLabel(renderer, '2/3'), true)
  assert.equal(renderer.root.findByProps({ 'data-message-id': 7 }).props['data-unread'], 'false')
  await act(async () => renderer.unmount())
})

test('repeated visibility callbacks submit the same message only once', async () => {
  const observedIds = { current: [7] }
  let postCalls = 0
  const http = {
    get: async url => url === '/projects/1/messages'
      ? page([row(7)])
      : { data: { readers: [{ userId: 9 }], unread: [] } },
    post: async () => { postCalls += 1; return { data: {} } },
    delete: async () => ({ data: {} }),
  }
  const { MessagePanel, observers, createNodeMock } = loadMessagePanel(http, observedIds)
  let renderer
  await act(async () => { renderer = create(React.createElement(MessagePanel, { projectId: 1, projectStatus: 'IN_PROGRESS' }), { createNodeMock }) })

  await intersect(activeObserver(observers))
  await intersect(activeObserver(observers))

  assert.equal(postCalls, 1)
  await act(async () => renderer.unmount())
})

test('a late receipt from the previous project cannot overwrite the current project', async () => {
  const observedIds = { current: [7] }
  const oldReceipt = deferred()
  const http = {
    get: async url => {
      if (url === '/projects/1/messages') return page([row(7)])
      if (url === '/projects/2/messages') return page([row(8, { projectId: 2, readCount: 0, totalCount: 2, readByMe: true })])
      if (url === '/messages/7/reads') return oldReceipt.promise
      throw new Error(`unexpected GET ${url}`)
    },
    post: async () => ({ data: {} }),
    delete: async () => ({ data: {} }),
  }
  const { MessagePanel, observers, createNodeMock } = loadMessagePanel(http, observedIds)
  let renderer
  await act(async () => { renderer = create(React.createElement(MessagePanel, { projectId: 1, projectStatus: 'IN_PROGRESS' }), { createNodeMock }) })
  await intersect(activeObserver(observers))

  observedIds.current = []
  await act(async () => {
    renderer.update(React.createElement(MessagePanel, { projectId: 2, projectStatus: 'IN_PROGRESS' }))
    await new Promise(resolve => setImmediate(resolve))
  })
  await act(async () => {
    oldReceipt.resolve({ data: { readers: [{ userId: 9 }], unread: [] } })
    await oldReceipt.promise
    await new Promise(resolve => setImmediate(resolve))
  })

  assert.equal(readLabel(renderer, '0/2'), true)
  assert.equal(renderer.root.findAllByProps({ 'data-message-id': 7 }).length, 0)
  await act(async () => renderer.unmount())
})

test('a late receipt cannot pass an A-B-A project switch', async () => {
  const observedIds = { current: [7] }
  const oldReceipt = deferred()
  let projectARequests = 0
  const http = {
    get: async url => {
      if (url === '/projects/1/messages') {
        projectARequests += 1
        return projectARequests === 1
          ? page([row(7)])
          : page([row(7, { readCount: 0, totalCount: 2, readByMe: true })])
      }
      if (url === '/projects/2/messages') return page([row(8, { projectId: 2, readByMe: true })])
      if (url === '/messages/7/reads') return oldReceipt.promise
      throw new Error(`unexpected GET ${url}`)
    },
    post: async () => ({ data: {} }),
    delete: async () => ({ data: {} }),
  }
  const { MessagePanel, observers, createNodeMock } = loadMessagePanel(http, observedIds)
  let renderer
  await act(async () => { renderer = create(React.createElement(MessagePanel, { projectId: 1, projectStatus: 'IN_PROGRESS' }), { createNodeMock }) })
  await intersect(activeObserver(observers))

  observedIds.current = []
  await act(async () => {
    renderer.update(React.createElement(MessagePanel, { projectId: 2, projectStatus: 'IN_PROGRESS' }))
    await new Promise(resolve => setImmediate(resolve))
    renderer.update(React.createElement(MessagePanel, { projectId: 1, projectStatus: 'IN_PROGRESS' }))
    await new Promise(resolve => setImmediate(resolve))
  })
  await act(async () => {
    oldReceipt.resolve({ data: { readers: [{ userId: 9 }], unread: [] } })
    await oldReceipt.promise
    await new Promise(resolve => setImmediate(resolve))
  })

  assert.equal(projectARequests, 2)
  assert.equal(readLabel(renderer, '0/2'), true)
  await act(async () => renderer.unmount())
})

test('receipt synchronization uses at most four concurrent requests', async () => {
  const ids = [1, 2, 3, 4, 5]
  const observedIds = { current: ids }
  const receipts = new Map(ids.map(id => [id, deferred()]))
  let active = 0
  let maximum = 0
  const http = {
    get: async url => {
      if (url === '/projects/1/messages') return page(ids.map(id => row(id)))
      const id = Number(url.match(/^\/messages\/(\d+)\/reads$/)?.[1])
      active += 1
      maximum = Math.max(maximum, active)
      const result = await receipts.get(id).promise
      active -= 1
      return result
    },
    post: async () => ({ data: {} }),
    delete: async () => ({ data: {} }),
  }
  const { MessagePanel, observers, createNodeMock } = loadMessagePanel(http, observedIds)
  let renderer
  await act(async () => { renderer = create(React.createElement(MessagePanel, { projectId: 1, projectStatus: 'IN_PROGRESS' }), { createNodeMock }) })

  const pending = intersect(activeObserver(observers))
  await new Promise(resolve => setImmediate(resolve))
  assert.equal(maximum, 4)
  receipts.get(1).resolve({ data: { readers: [], unread: [] } })
  receipts.get(2).resolve({ data: { readers: [], unread: [] } })
  receipts.get(3).resolve({ data: { readers: [], unread: [] } })
  receipts.get(4).resolve({ data: { readers: [], unread: [] } })
  await new Promise(resolve => setImmediate(resolve))
  assert.equal(maximum, 4)
  receipts.get(5).resolve({ data: { readers: [], unread: [] } })
  await pending

  await act(async () => renderer.unmount())
})
