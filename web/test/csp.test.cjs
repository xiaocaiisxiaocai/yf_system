const test = require('node:test')
const assert = require('node:assert/strict')
const crypto = require('node:crypto')
const fs = require('node:fs')
const path = require('node:path')
const vm = require('node:vm')
const ts = require('typescript')

function loadCsp() {
  const filename = path.resolve(__dirname, '../csp.ts')
  const source = ts.transpileModule(fs.readFileSync(filename, 'utf8'), {
    compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 },
  }).outputText
  const module = { exports: {} }
  vm.runInNewContext(source, { exports: module.exports, module, require, process }, { filename })
  return module.exports
}

const csp = loadCsp()
const hashes = html => [...csp.inlineScriptHashes(html)] // copy out of the vm realm
const sha = text => `'sha256-${crypto.createHash('sha256').update(text, 'utf8').digest('base64')}'`
const viewer = body => `<!doctype html><html><head><meta http-equiv="Content-Security-Policy" content="script-src 'nonce-YF_VIEWER_NONCE'"><style>p{}</style></head><body><script nonce="YF_VIEWER_NONCE">${body}</script></body></html>`

test('viewer scripts are hashed exactly as the browser parses them', () => {
  assert.deepEqual(hashes(viewer('run()')), [sha('run()')])
  // HTML parsing normalizes CRLF and lone CR to LF before the script text is hashed.
  assert.deepEqual(hashes(viewer('a()\r\nb()\rc()')), [sha('a()\nb()\nc()')])
  assert.deepEqual(hashes('<script src="/x.js"></script>'), [])
})

test('the policy allows only the hashed viewer scripts, never inline script or eval in general', () => {
  const policy = csp.contentSecurityPolicy([viewer('one()'), viewer('two()')])
  const scriptSrc = policy.split('; ').find(directive => directive.startsWith('script-src '))
  assert.equal(scriptSrc, `script-src 'self' 'wasm-unsafe-eval' ${sha('one()')} ${sha('two()')}`)
  assert.doesNotMatch(scriptSrc, /'unsafe-inline'|'unsafe-eval'/)
  assert.match(policy, /object-src 'none'/)
  assert.match(policy, /base-uri 'self'/)
})

test('a viewer with an unexpected script count fails the build instead of shipping a broken policy', () => {
  assert.throws(() => csp.contentSecurityPolicy(['<html></html>']), /exactly one inline script/)
  assert.throws(() => csp.contentSecurityPolicy([viewer('a()') + '<script>b()</script>']), /exactly one inline script/)
})

test('generated viewers, when built, each carry one hashable inline script', (t) => {
  const files = csp.VIEWER_FILES.map(file => path.resolve(__dirname, '..', file))
  if (!files.every(file => fs.existsSync(file))) return t.skip('preview viewers have not been generated yet')
  const policy = csp.contentSecurityPolicy(files.map(file => fs.readFileSync(file, 'utf8')))
  assert.equal((policy.match(/'sha256-/g) || []).length, 2)
})
