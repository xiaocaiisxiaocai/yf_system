import { readFileSync } from 'node:fs'
import { execFileSync } from 'node:child_process'
import { createRequire } from 'node:module'
import { resolve } from 'node:path'
import { expect, test } from 'vitest'

const require = createRequire(resolve('package.json'))
const lock = JSON.parse(readFileSync('package-lock.json', 'utf8'))
const copies = Object.entries(lock.packages as Record<string, { version: string }>)
  .filter(([directory]) => directory.endsWith('node_modules/brace-expansion'))
  .map(([directory, entry]) => ({ directory, version: entry.version }))

type Expand = (pattern: string, options?: { max: number, maxLength: number }) => string[]

function loadExpansion(directory: string, version: string): Expand {
  const installed = require(resolve(directory, 'package.json'))
  expect(installed.version).toBe(version)
  return require(resolve(directory)) as Expand
}

test('the dependency security regression covers the installed brace-expansion copies', () => {
  expect(copies.length).toBeGreaterThan(0)
})

test.each(copies)('$directory preserves ordinary brace and range expansion', ({ directory, version }) => {
  const expand = loadExpansion(directory, version)
  expect(expand('drawing/{left,right}-{1..2}.step')).toEqual([
    'drawing/left-1.step', 'drawing/left-2.step',
    'drawing/right-1.step', 'drawing/right-2.step',
  ])
})

// GHSA-qhr7-859c-m2p7: nesting used to overflow before output limits applied.
test.each(copies)('$directory bounds deeply nested brace groups without overflowing', ({ directory, version }) => {
  const expand = loadExpansion(directory, version)
  const pattern = '{'.repeat(8000) + 'a,b' + '}'.repeat(8000)
  const output = expand(pattern, { max: 16, maxLength: 65536 })
  expect(output.length).toBeGreaterThan(0)
  expect(output.length).toBeLessThanOrEqual(16)
})

// GHSA-6j4f-fj2g-mc7p: sibling groups used to recurse in parseCommaParts().
test.each(copies)('$directory parses many sibling brace groups without overflowing', ({ directory, version }) => {
  const expand = loadExpansion(directory, version)
  const pattern = '{' + Array(12000).fill('{a,b}').join(',') + '}'
  const output = expand(pattern, { max: 16, maxLength: 65536 })
  expect(output.length).toBeGreaterThan(0)
  expect(output.length).toBeLessThanOrEqual(16)
})

// CVE-2026-93749: a tiny indexed map used to drive a synchronous loop for its
// attacker-controlled line offset. Keep a regressed dependency in a bounded child.
test.each([
  { line: 0, expected: 'drawing();' },
  { line: 1_000_000_000_000, expected: 'rejected' },
])('source-map-js handles indexed line offset $line safely', ({ line, expected }) => {
  const code = `
    const { SourceMapConsumer, SourceNode } = require('source-map-js');
    try {
      const consumer = new SourceMapConsumer({ version: 3, sections: [{
        offset: { line: ${line}, column: 0 },
        map: { version: 3, sources: ['drawing.js'], sourcesContent: ['drawing();'], names: [], mappings: 'AAAA' }
      }] });
      process.stdout.write(SourceNode.fromStringWithSourceMap('drawing();', consumer).toString());
    } catch (error) {
      if (!(error instanceof Error)) throw error;
      process.stdout.write('rejected');
    }
  `
  expect(execFileSync(process.execPath, ['--max-old-space-size=64', '-e', code], {
    cwd: resolve('.'), encoding: 'utf8', timeout: 3000, windowsHide: true,
  })).toBe(expected)
})
