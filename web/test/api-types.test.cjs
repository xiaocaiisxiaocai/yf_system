const test = require('node:test')
const assert = require('node:assert/strict')
const fs = require('node:fs')
const path = require('node:path')
const { spawnSync } = require('node:child_process')

const root = path.resolve(__dirname, '..')

test('generated API types match the backend OpenAPI contract', () => {
  const result = spawnSync(process.execPath, ['scripts/generate-api-types.mjs', '--check'], { cwd: root, encoding: 'utf8' })
  assert.equal(result.status, 0, result.stderr || 'run: npm run generate:api-types')
})

test('every typed http call names a route that exists in the contract', () => {
  const generated = fs.readFileSync(path.join(root, 'src/api/generated/api-types.ts'), 'utf8')
  const routes = new Set(Array.from(generated.matchAll(/^  "([A-Z]+ [^"]+)": /gm), match => match[1]))
  const files = []
  const walk = directory => {
    for (const entry of fs.readdirSync(directory, { withFileTypes: true })) {
      const full = path.join(directory, entry.name)
      if (entry.isDirectory()) { if (entry.name !== 'generated') walk(full) }
      else if (/\.(ts|tsx)$/.test(entry.name)) files.push(full)
    }
  }
  walk(path.join(root, 'src'))
  const unknown = []
  let calls = 0
  for (const file of files) {
    for (const [, method, route] of fs.readFileSync(file, 'utf8').matchAll(/http\.(get|post|put|delete)<ApiResponses\['([A-Z]+ [^']+)'\]>/g)) {
      calls++
      if (!routes.has(route) || !route.startsWith(method.toUpperCase() + ' ')) unknown.push(`${path.relative(root, file)}: ${route}`)
    }
  }
  assert.ok(calls >= 100, 'expected the API calls to be typed')
  assert.deepEqual(unknown, [])
})
