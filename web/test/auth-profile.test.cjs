const test = require('node:test')
const assert = require('node:assert/strict')
const fs = require('node:fs')
const path = require('node:path')
const vm = require('node:vm')
const ts = require('typescript')

function loadClient(mocks, globals = {}) {
  const filename = path.resolve(__dirname, '..', 'src/api/client.ts')
  const source = ts.transpileModule(fs.readFileSync(filename, 'utf8'), {
    compilerOptions: {
      module: ts.ModuleKind.CommonJS,
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
    ...globals,
    require: (name) => name in mocks ? mocks[name] : require(name),
  }, { filename })
  return exports
}

function profileResponse(id) {
  return {
    data: {
      user: { id, employeeNo: `user-${id}`, realName: `User ${id}`, email: `${id}@example.invalid`, userType: 'INTERNAL' },
      permissions: [`perm-${id}`],
      menus: [`menu-${id}`],
      mustChangePassword: false,
    },
  }
}

function createHarness() {
  const applied = []
  const profileRequests = []
  let current = {
    token: 'token-A',
    user: null,
    permissions: [],
    menus: [],
    mustChangePassword: false,
    booted: true,
    generation: 1,
  }

  const setLogin = (payload, replaceSession = true) => {
    applied.push({ payload, replaceSession })
    current = {
      ...current,
      token: payload.accessToken,
      user: payload.user,
      permissions: payload.permissions,
      menus: payload.menus,
      mustChangePassword: payload.mustChangePassword,
      generation: current.generation + (replaceSession ? 1 : 0),
    }
  }
  const logout = () => {
    current = { ...current, token: null, user: null, generation: current.generation + 1 }
  }
  current = { ...current, setLogin, logout }

  function useAuth(selector) {
    return selector ? selector(current) : current
  }
  useAuth.getState = () => current
  useAuth.setState = (patch) => {
    const next = typeof patch === 'function' ? patch(current) : patch
    current = { ...current, ...next }
  }

  const requestInterceptors = []
  const responseInterceptors = []
  const http = {
    interceptors: {
      request: {
        use(fulfilled, rejected) {
          requestInterceptors.push({ fulfilled, rejected })
        },
      },
      response: {
        use(fulfilled, rejected) {
          responseInterceptors.push({ fulfilled, rejected })
        },
      },
    },
  }
  const axios = {
    create: () => http,
    get(url, config) {
      const request = { url, config }
      request.promise = new Promise((resolve, reject) => {
        request.resolve = resolve
        request.reject = reject
      })
      profileRequests.push(request)
      return request.promise
    },
  }
  const auth = { __esModule: true, useAuth }
  const client = loadClient({
    axios: { __esModule: true, default: axios },
    '@arco-design/web-react': { Message: { error() {} } },
    '../store/auth': auth,
  })

  async function rejectForbidden() {
    const requestInterceptor = requestInterceptors[0]
    const responseInterceptor = responseInterceptors[0]
    const config = requestInterceptor.fulfilled({ url: '/projects', headers: {} })
    return responseInterceptor.rejected({
      config,
      response: { status: 403, data: { message: 'forbidden' } },
      message: 'forbidden',
    })
  }

  return {
    client,
    applied,
    profileRequests,
    rejectForbidden,
    setSession(token, generation) {
      current = { ...current, token, generation }
    },
  }
}

const nextTick = () => new Promise((resolve) => setTimeout(resolve, 0))
const observed = (promise) => promise.then(() => undefined, (error) => error)

test('403 profile refresh starts a new request for a new token and applies only that session', async () => {
  const harness = createHarness()
  const oldError = observed(harness.rejectForbidden())
  assert.equal(harness.profileRequests.length, 1)
  assert.equal(harness.profileRequests[0].config.headers.Authorization, 'Bearer token-A')
  assert.equal(harness.profileRequests[0].config.timeout, 60000)

  harness.setSession('token-B', 2)
  const newError = observed(harness.rejectForbidden())
  assert.equal(harness.profileRequests.length, 2, 'a new session must not reuse the old profile request')
  assert.equal(harness.profileRequests[1].config.headers.Authorization, 'Bearer token-B')
  assert.equal(harness.profileRequests[1].config.timeout, 60000)

  harness.profileRequests[0].resolve(profileResponse('A'))
  assert.equal((await oldError).response.status, 403)
  assert.deepEqual(harness.applied, [], 'the old session response must be discarded')

  harness.profileRequests[1].resolve(profileResponse('B'))
  assert.equal((await newError).response.status, 403)
  assert.equal(harness.applied.length, 1)
  assert.equal(harness.applied[0].payload.accessToken, 'token-B')
  assert.equal(harness.applied[0].payload.user.id, 'B')
})

test('same-session concurrent 403 responses share one profile request', async () => {
  const harness = createHarness()
  const firstError = observed(harness.rejectForbidden())
  const secondError = observed(harness.rejectForbidden())

  assert.equal(harness.profileRequests.length, 1)
  assert.equal(harness.profileRequests[0].config.headers.Authorization, 'Bearer token-A')
  assert.equal(harness.profileRequests[0].config.timeout, 60000)

  harness.profileRequests[0].resolve(profileResponse('A'))
  assert.equal((await firstError).response.status, 403)
  assert.equal((await secondError).response.status, 403)
  assert.equal(harness.applied.length, 1)
  assert.equal(harness.applied[0].payload.accessToken, 'token-A')
})

test('an old profile request cannot clear a newer session request', async () => {
  const harness = createHarness()
  const oldError = observed(harness.rejectForbidden())
  harness.setSession('token-B', 2)
  const currentError = observed(harness.rejectForbidden())
  assert.equal(harness.profileRequests.length, 2)

  harness.profileRequests[0].resolve(profileResponse('A'))
  assert.equal((await oldError).response.status, 403)
  await nextTick()

  const concurrentError = observed(harness.rejectForbidden())
  assert.equal(harness.profileRequests.length, 2, 'old cleanup must preserve the newer in-flight request')

  harness.profileRequests[1].resolve(profileResponse('B'))
  assert.equal((await currentError).response.status, 403)
  assert.equal((await concurrentError).response.status, 403)
  assert.equal(harness.applied.length, 1)
  assert.equal(harness.applied[0].payload.user.id, 'B')
})
