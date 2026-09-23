import { readFileSync, readdirSync } from 'node:fs'
import { join } from 'node:path'
import ts from 'typescript'
import { expect, test } from 'vitest'

const sourceFiles = (directory: string): string[] => readdirSync(directory, { withFileTypes: true })
  .flatMap(entry => entry.isDirectory() ? sourceFiles(join(directory, entry.name))
    : ['.ts', '.tsx', '.js', '.jsx'].some(extension => entry.name.endsWith(extension)) ? [join(directory, entry.name)] : [])

test('the frontend has one Vitest entry point and no legacy renderer dependency', () => {
  const manifest = JSON.parse(readFileSync('package.json', 'utf8'))
  expect(manifest.scripts.test).toBe('vitest run')
  expect(manifest.scripts).not.toHaveProperty('test:legacy')
  expect(manifest.dependencies ?? {}).not.toHaveProperty('react-test-renderer')
  expect(manifest.devDependencies ?? {}).not.toHaveProperty('react-test-renderer')
  expect(readdirSync('test').filter(file => file.endsWith('.test.cjs'))).toEqual([])
})

test('regressions import real modules instead of a source-transpiling or renderer compatibility layer', () => {
  const violations: string[] = []
  const forbiddenModules = new Set(['node:test', 'node:vm', 'vm', 'react-test-renderer'])
  const forbiddenCalls = new Set(['transpileModule', 'runInNewContext', 'runInContext', 'findByType', 'findAllByType'])
  for (const file of sourceFiles('src/test')) {
    const source = ts.createSourceFile(file, readFileSync(file, 'utf8'), ts.ScriptTarget.Latest, true)
    const visit = (node: ts.Node) => {
      if (ts.isImportDeclaration(node) && ts.isStringLiteral(node.moduleSpecifier)
          && forbiddenModules.has(node.moduleSpecifier.text)) violations.push(`${file}: ${node.moduleSpecifier.text}`)
      if (ts.isCallExpression(node)) {
        if (ts.isIdentifier(node.expression) && node.expression.text === 'require'
            && node.arguments[0] && ts.isStringLiteral(node.arguments[0])
            && forbiddenModules.has(node.arguments[0].text)) violations.push(`${file}: ${node.arguments[0].text}`)
        const called = ts.isPropertyAccessExpression(node.expression) ? node.expression.name.text
          : ts.isIdentifier(node.expression) ? node.expression.text : ''
        if (forbiddenCalls.has(called)) violations.push(`${file}: ${called}`)
      }
      ts.forEachChild(node, visit)
    }
    visit(source)
  }
  expect(violations).toEqual([])
})
