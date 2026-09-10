const test = require('node:test')
const assert = require('node:assert/strict')
const fs = require('node:fs')
const path = require('node:path')
const vm = require('node:vm')
const ts = require('typescript')
const React = require('react')
const { act, create } = require('react-test-renderer')

function component(name) {
  const Component = (props) => React.createElement(name, props)
  Component.displayName = name
  return Component
}

const Input = component('Input')
Input.Password = component('Input.Password')
const IconEye = component('IconEye')
const IconEyeInvisible = component('IconEyeInvisible')

function loadTs(relativePath) {
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
    require: (name) => {
      if (name === '@arco-design/web-react') return { Input }
      if (name === '@arco-design/web-react/icon') return { IconEye, IconEyeInvisible }
      return require(name)
    },
  }, { filename })
  return exports
}

const PasswordInput = loadTs('src/components/PasswordInput.tsx').default

function passwordField(renderer) {
  return renderer.root.findByType(Input.Password)
}

function visibilityToggle(renderer) {
  return passwordField(renderer).props.suffix
}

function click(toggle) {
  let propagationStopped = false
  toggle.props.onClick({
    preventDefault() {},
    stopPropagation() { propagationStopped = true },
  })
  return propagationStopped
}

test('toggle click stops the Arco wrapper from moving keyboard focus back to the input', async () => {
  let renderer
  await act(async () => { renderer = create(React.createElement(PasswordInput)) })
  let propagationStopped
  await act(async () => { propagationStopped = click(visibilityToggle(renderer)) })
  assert.equal(propagationStopped, true, 'the wrapper focuses its input when a suffix click bubbles')
})

test('password toggle is an accessible non-submit button and preserves input props', async () => {
  const onChange = () => {}
  const prefix = React.createElement('span', null, 'prefix')
  let renderer
  await act(async () => {
    renderer = create(React.createElement(PasswordInput, {
      value: 'secret',
      onChange,
      prefix,
      readOnly: true,
      defaultVisibility: false,
      'aria-label': '密码',
    }))
  })

  const input = passwordField(renderer)
  assert.equal(input.props.value, 'secret')
  assert.equal(input.props.onChange, onChange)
  assert.equal(input.props.prefix, prefix)
  assert.equal(input.props.readOnly, true)
  assert.equal(input.props.visibilityToggle, false)
  assert.equal(input.props.visibility, false)

  const toggle = visibilityToggle(renderer)
  assert.equal(toggle.type, 'button')
  assert.equal(toggle.props.type, 'button')
  assert.equal(toggle.props['aria-label'], '显示密码')
  assert.equal(toggle.props.title, '显示密码')
  assert.equal(toggle.props.children.type, IconEyeInvisible)
})

test('clicking the toggle changes visibility and announces the next action', async () => {
  const changes = []
  let renderer
  await act(async () => {
    renderer = create(React.createElement(PasswordInput, {
      onVisibilityChange: (visible) => changes.push(visible),
    }))
  })

  await act(async () => click(visibilityToggle(renderer)))
  assert.equal(passwordField(renderer).props.visibility, true)
  assert.equal(visibilityToggle(renderer).props['aria-label'], '隐藏密码')
  assert.equal(visibilityToggle(renderer).props.children.type, IconEye)
  assert.deepEqual(changes, [true])

  await act(async () => click(visibilityToggle(renderer)))
  assert.equal(passwordField(renderer).props.visibility, false)
  assert.equal(visibilityToggle(renderer).props['aria-label'], '显示密码')
  assert.deepEqual(changes, [true, false])
})

test('default visibility starts in the requested state', async () => {
  let renderer
  await act(async () => {
    renderer = create(React.createElement(PasswordInput, { defaultVisibility: true }))
  })

  assert.equal(passwordField(renderer).props.visibility, true)
  assert.equal(visibilityToggle(renderer).props['aria-label'], '隐藏密码')
})

test('visibilityToggle=false keeps a custom suffix without adding a toggle', async () => {
  const suffix = React.createElement('span', null, 'suffix')
  let renderer
  await act(async () => {
    renderer = create(React.createElement(PasswordInput, { visibilityToggle: false, suffix }))
  })

  assert.equal(passwordField(renderer).props.visibilityToggle, false)
  assert.equal(passwordField(renderer).props.suffix, suffix)
})

test('controlled visibility and disabled state keep their existing contracts', async () => {
  const changes = []
  const props = {
    visibility: false,
    onVisibilityChange: (visible) => changes.push(visible),
  }
  let renderer
  await act(async () => { renderer = create(React.createElement(PasswordInput, props)) })

  await act(async () => click(visibilityToggle(renderer)))
  assert.equal(passwordField(renderer).props.visibility, false, 'controlled visibility must remain owned by the caller')
  assert.deepEqual(changes, [true])

  await act(async () => {
    renderer.update(React.createElement(PasswordInput, { ...props, visibility: true }))
  })
  assert.equal(passwordField(renderer).props.visibility, true)
  assert.equal(visibilityToggle(renderer).props['aria-label'], '隐藏密码')

  await act(async () => {
    renderer.update(React.createElement(PasswordInput, { disabled: true, onVisibilityChange: (visible) => changes.push(visible) }))
  })
  await act(async () => click(visibilityToggle(renderer)))
  assert.equal(passwordField(renderer).props.disabled, true)
  assert.equal(passwordField(renderer).props.visibility, false)
  assert.equal(visibilityToggle(renderer).props.disabled, true)
  assert.deepEqual(changes, [true])
})

test('toggle does not submit its containing form', async () => {
  let submits = 0
  let renderer
  await act(async () => {
    renderer = create(React.createElement('form', { onSubmit: () => { submits += 1 } }, React.createElement(PasswordInput)))
  })

  const toggle = visibilityToggle(renderer)
  assert.equal(toggle.props.type, 'button')
  await act(async () => click(toggle))
  assert.equal(submits, 0)
})
