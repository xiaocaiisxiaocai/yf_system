const test = require('node:test')
const assert = require('node:assert/strict')
const fs = require('node:fs')
const path = require('node:path')
const { spawnSync } = require('node:child_process')

const root = path.resolve(__dirname, '..')

test('the generated Arco stylesheet list matches the components imported in src', () => {
  const result = spawnSync(process.execPath, ['scripts/generate-arco-styles.mjs', '--check'], { cwd: root, encoding: 'utf8' })
  assert.equal(result.status, 0, result.stderr || 'run: node scripts/generate-arco-styles.mjs')
})

test('stylesheets keep the full arco.css order: base, shared, then components alphabetically', () => {
  const sheets = Array.from(fs.readFileSync(path.join(root, 'src/styles/arco-components.ts'), 'utf8')
    .matchAll(/^import '@arco-design\/web-react\/es\/(.+)'$/gm), match => match[1])
  assert.equal(sheets[0], 'style/index.css')
  const components = sheets.filter(sheet => !sheet.startsWith('_class/') && sheet !== 'style/index.css')
  assert.deepEqual(components, [...components].sort((a, b) => a.toLowerCase().localeCompare(b.toLowerCase())))
  // Components rendered internally by imported ones must be present too.
  for (const dependency of ['Grid/style/index.css', 'Trigger/style/index.css', 'InputTag/style/index.css']) {
    assert.ok(sheets.includes(dependency), dependency)
  }
})

test('the full Arco stylesheet is no longer bundled', () => {
  const main = fs.readFileSync(path.join(root, 'src/main.tsx'), 'utf8')
  assert.doesNotMatch(main, /web-react\/dist\/css\/arco\.css/)
  assert.match(main, /import '\.\/styles\/arco-components'/)
})

test('Arco JavaScript is split per route instead of forced into one up-front chunk', () => {
  const config = fs.readFileSync(path.join(root, 'vite.config.ts'), 'utf8')
  assert.doesNotMatch(config, /name:\s*'arco'/, 'grouping all of Arco doubles the login page payload')
})
