const test = require('node:test')
const assert = require('node:assert/strict')
const fs = require('node:fs')
const path = require('node:path')
const vm = require('node:vm')
const ts = require('typescript')
const React = require('react')
const { create, act } = require('react-test-renderer')

test('login needs only employee number and password, recovers after failure, and rejects external return paths', async () => {
  let captchaRequests = 0, destination, loginPayload, lockCalls = 0, attempt = 0, rejectAttempt
  const posts = []
  const errors = []
  const cmp = name => props => React.createElement(name, props, props.children)
  const Form = Object.assign(cmp('Form'), { useForm: () => [{}], Item: cmp('Form.Item') })
  const state = { user: null, token: null, setLogin(payload) { loginPayload = payload } }
  const useAuth = selector => selector(state)
  useAuth.getState = () => state
  const mocks = {
    '@arco-design/web-react': { Form, Button: cmp('Button'), Input: cmp('Input'), Message: { error(message) { errors.push(message) }, success() {} } },
    '@arco-design/web-react/icon': new Proxy({}, { get: (_, k) => cmp(k) }),
    'react-router-dom': { useNavigate: () => to => { destination = to }, useLocation: () => ({ state: { from: '//external.invalid' } }) },
    axios: {
      get: async () => { captchaRequests++; throw new Error('captcha must not be requested') },
      post: async (url, body, config) => {
        posts.push({ url, body, config })
        attempt++
        if (attempt === 1) return new Promise((_resolve, reject) => { rejectAttempt = reject })
        return { data: { accessToken: 'fixture-token', mustChangePassword: false } }
      },
      isAxiosError: error => error?.isAxiosError === true,
    },
    '../store/auth': { useAuth }, '../api/client': { withAuthLock: fn => { lockCalls++; return fn() } },
    '../components/AuthShell': cmp('AuthShell'), '../components/PasswordInput': cmp('PasswordInput'),
  }
  const filename = path.resolve(__dirname, '../src/pages/Login.tsx')
  const source = ts.transpileModule(fs.readFileSync(filename, 'utf8'), { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022, jsx: ts.JsxEmit.ReactJSX, esModuleInterop: true } }).outputText
  const exports = {}
  vm.runInNewContext(source, { exports, module: { exports }, require: name => name in mocks ? mocks[name] : require(name), console }, { filename })
  let renderer
  await act(async () => { renderer = create(React.createElement(exports.default)) })
  assert.equal(captchaRequests, 0)
  assert.equal(renderer.root.findAllByType('Input').some(input => input.props['aria-label'] === '验证码'), false)
  const passwordItem = renderer.root.findAllByType('Form.Item').find(item => item.props.field === 'password')
  assert.equal(passwordItem.props.rules.length, 1, 'existing login passwords must not use the new-password length policy')

  let failedLogin
  await act(async () => {
    failedLogin = renderer.root.findByType('Form').props.onSubmit({ employeeNo: ' tester ', password: 'old' })
    await Promise.resolve()
  })
  assert.equal(renderer.root.findByType('Button').props.loading, true)
  await act(async () => {
    rejectAttempt({ isAxiosError: true, response: { status: 401, data: { message: '工号或密码错误' } } })
    await failedLogin
  })
  assert.equal(renderer.root.findByType('Button').props.loading, false)
  assert.deepEqual(errors, ['工号或密码错误'])

  await act(async () => renderer.root.findByType('Form').props.onSubmit({ employeeNo: ' tester ', password: 'old' }))
  assert.equal(posts.length, 2)
  assert.deepEqual({ ...posts[1].body }, { employeeNo: 'tester', password: 'old' })
  assert.equal(posts[1].config.withCredentials, true)
  assert.equal('captchaId' in posts[1].body, false)
  assert.equal('captchaCode' in posts[1].body, false)
  assert.equal(lockCalls, 2)
  assert.equal(loginPayload.accessToken, 'fixture-token')
  assert.equal(destination, '/')
  await act(async () => renderer.unmount())
})

test('the shared client no longer hides obsolete 428 responses', async () => {
  let rejectResponse
  const errors = []
  const client = () => Promise.resolve({})
  client.interceptors = {
    request: { use() {} },
    response: { use(_success, failure) { rejectResponse = failure } },
  }
  const state = { generation: 0, token: null, logout() {} }
  const useAuth = { getState: () => state, setState() {} }
  const mocks = {
    axios: { create: () => client },
    '@arco-design/web-react': { Message: { error(message) { errors.push(message) } } },
    '../store/auth': { useAuth },
  }
  const filename = path.resolve(__dirname, '../src/api/client.ts')
  const source = ts.transpileModule(fs.readFileSync(filename, 'utf8'), { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022, esModuleInterop: true } }).outputText
  const exports = {}
  vm.runInNewContext(source, {
    exports,
    module: { exports },
    require: name => name in mocks ? mocks[name] : require(name),
    console,
    location: { pathname: '/login', href: '' },
  }, { filename })

  const error = { config: { authGeneration: 0, url: '/auth/login' }, response: { status: 428, data: { message: '登录请求失败' } }, message: 'failure' }
  await assert.rejects(rejectResponse(error))
  assert.deepEqual(errors, ['登录请求失败'])
})
