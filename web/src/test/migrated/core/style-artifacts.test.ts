import { readFileSync } from 'node:fs'
import ts from 'typescript'
import { expect, test } from 'vitest'
import { importedComponents, render } from '../../../../scripts/generate-arco-styles.mjs'
import configured from '../../../../vite.config'

function imports(source: string) {
  const ast = ts.createSourceFile('styles.ts', source, ts.ScriptTarget.Latest, true)
  return ast.statements.filter(ts.isImportDeclaration).map(node =>
    ts.isStringLiteral(node.moduleSpecifier) ? node.moduleSpecifier.text : '')
}

test('the generated Arco stylesheet list matches the components imported in src', () => {
  expect(importedComponents().length).toBeGreaterThan(0)
  expect(readFileSync('src/styles/arco-components.ts', 'utf8').replaceAll('\r\n', '\n')).toBe(render())
})

test('stylesheets keep the full arco.css order: base, shared, then components alphabetically', () => {
  const sheets = imports(render()).map(value => value.replace('@arco-design/web-react/es/', ''))
  expect(sheets[0]).toBe('style/index.css')
  const components = sheets.filter(sheet => !sheet.startsWith('_class/') && sheet !== 'style/index.css')
  expect(components).toEqual([...components].sort((a, b) => a.toLowerCase().localeCompare(b.toLowerCase())))
  expect(sheets).toEqual(expect.arrayContaining(['Grid/style/index.css', 'Trigger/style/index.css', 'InputTag/style/index.css']))
})

test('the full Arco stylesheet is no longer bundled', () => {
  const entryImports = imports(readFileSync('src/main.tsx', 'utf8'))
  expect(entryImports).toContain('./styles/arco-components')
  expect(entryImports).not.toContain('@arco-design/web-react/dist/css/arco.css')
  expect(imports(render())).not.toContain('@arco-design/web-react/dist/css/arco.css')
})

test('Arco JavaScript is split per route instead of forced into one up-front chunk', () => {
  const output = configured.build!.rolldownOptions!.output
  const outputs = Array.isArray(output) ? output : [output]
  for (const entry of outputs) {
    const split = entry?.codeSplitting
    const groups: Array<{ name?: unknown; test?: unknown }> = typeof split === 'object' && split !== null ? split.groups || [] : []
    expect(groups.filter(group => group.name === 'arco')).toEqual([])
    expect(groups.some(group => group.test instanceof RegExp && group.test.test('/node_modules/@arco-design/web-react/es/Button/index.js'))).toBe(false)
  }
})
