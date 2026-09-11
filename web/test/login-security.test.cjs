const test = require('node:test')
const assert = require('node:assert/strict')
const fs = require('node:fs')
const path = require('node:path')
const vm = require('node:vm')
const ts = require('typescript')
const React = require('react')
const { create, act } = require('react-test-renderer')

test('login loads a raster challenge immediately and rejects external return paths', async () => {
  let requests = 0, posted, destination
  const cmp = name => props => React.createElement(name, props, props.children)
  const form = { setFieldValue() {} }
  const Form = Object.assign(cmp('Form'), { useForm: () => [form], Item: cmp('Form.Item') })
  const state = { user: null, token: null, setLogin() {} }
  const useAuth = selector => selector(state)
  useAuth.getState = () => state
  const uri = 'data:image/jpeg;base64,/9j/fixture'
  const mocks = {
    '@arco-design/web-react': { Form, Button: cmp('Button'), Input: cmp('Input'), Message: { error() {}, info() {}, success() {} } },
    '@arco-design/web-react/icon': new Proxy({}, { get: (_, k) => cmp(k) }),
    'react-router-dom': { useNavigate: () => to => { destination = to }, useLocation: () => ({ state: { from: '//external.invalid' } }) },
    axios: { get: async () => { requests++; return { data: { captchaId: 'one-use', svg: uri } } }, post: async (_, body) => { posted = body; return { data: { mustChangePassword: false } } }, isAxiosError: () => false },
    '../store/auth': { useAuth }, '../api/client': { withAuthLock: fn => fn() },
    '../components/AuthShell': cmp('AuthShell'), '../components/PasswordInput': cmp('PasswordInput'),
  }
  const filename = path.resolve(__dirname, '../src/pages/Login.tsx')
  const source = ts.transpileModule(fs.readFileSync(filename, 'utf8'), { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022, jsx: ts.JsxEmit.ReactJSX, esModuleInterop: true } }).outputText
  const exports = {}
  vm.runInNewContext(source, { exports, module: { exports }, require: name => name in mocks ? mocks[name] : require(name), console }, { filename })
  let renderer
  await act(async () => { renderer = create(React.createElement(exports.default)) })
  assert.equal(requests, 1)
  const input = renderer.root.findAllByType('Input').find(x => x.props['aria-label'] === '验证码')
  const image = React.Children.toArray(input.props.suffix.props.children)[0]
  assert.equal(image.props.src, uri)
  await act(async () => renderer.root.findByType('Form').props.onSubmit({ employeeNo: 'tester', password: 'Fixture123456', captchaCode: 'abcd' }))
  assert.equal(posted.captchaId, 'one-use')
  assert.equal(posted.captchaCode, 'abcd')
  assert.equal(destination, '/')
  await act(async () => renderer.unmount())
})
