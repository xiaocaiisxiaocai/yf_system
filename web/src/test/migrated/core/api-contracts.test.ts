import { readFileSync, readdirSync } from 'node:fs'
import { join, relative } from 'node:path'
import ts from 'typescript'
import { expect, test } from 'vitest'
import { render } from '../../../../scripts/generate-api-types.mjs'

test('generated API types match the backend OpenAPI contract', () => {
  expect(readFileSync('src/api/generated/api-types.ts', 'utf8').replaceAll('\r\n', '\n')).toBe(render())
})

test('every typed http call names a route that exists in the contract', () => {
  const schema = JSON.parse(readFileSync('../server_dotnet/tests/Contracts/openapi-v1.json', 'utf8'))
  const normalize = (route: string) => route.split('?')[0].replace(/\{[^}]*\}/g, '{}')
  const routes = new Set(Object.entries(schema.paths).flatMap(([path, value]) =>
    Object.keys(value as object).map(method => normalize(`${method.toUpperCase()} ${path.replace('/api/v1', '')}`))))
  const files = (directory: string): string[] => readdirSync(directory, { withFileTypes: true }).flatMap(entry => {
    if (entry.isDirectory()) return ['test', 'generated'].includes(entry.name) ? [] : files(join(directory, entry.name))
    return ['.ts', '.tsx'].some(suffix => entry.name.endsWith(suffix)) ? [join(directory, entry.name)] : []
  })
  const calls: string[] = []
  const errors: string[] = []
  let checkedDynamicImageSource = false
  for (const file of files('src')) {
    const source = ts.createSourceFile(file, readFileSync(file, 'utf8'), ts.ScriptTarget.Latest, true)
    const visit = (node: ts.Node) => {
      if (ts.isCallExpression(node) && ts.isPropertyAccessExpression(node.expression)
          && ts.isIdentifier(node.expression.expression) && node.expression.expression.text === 'http') {
        const method = node.expression.name.text.toUpperCase()
        const argument = node.arguments[0]
        const location = `${relative('.', file)}:${source.getLineAndCharacterOfPosition(node.getStart()).line + 1}`
        const generic = node.typeArguments?.[0]
        let actualPath: string | null = null
        if (argument && ts.isStringLiteralLike(argument)) actualPath = argument.text
        else if (argument && ts.isTemplateExpression(argument)) {
          actualPath = argument.head.text + argument.templateSpans.map(span => '{}' + span.literal.text).join('')
        }
        // ImageDocument accepts either a file or message-image URL from its two typed callers.
        // This is the sole dynamic URL boundary; its HTTP options and exact endpoints have DOM tests.
        else if (file.replaceAll('\\', '/') === 'src/components/ImagePreview.tsx'
            && method === 'GET' && argument && ts.isIdentifier(argument) && argument.text === 'requestUrl'
            && generic && ts.isTypeReferenceNode(generic)
            && ts.isIdentifier(generic.typeName) && generic.typeName.text === 'Blob') {
          checkedDynamicImageSource = true
          for (const route of ['GET /files/{}/content', 'GET /messages/{}/images/{}']) {
            if (!routes.has(route)) errors.push(`${location}: missing image endpoint ${route}`)
          }
        } else errors.push(`${location}: HTTP URL requires a literal/template or an explicitly tested dynamic boundary`)
        calls.push(`${method} ${actualPath ?? '[dynamic image]'}`)
        if (!generic || generic.kind === ts.SyntaxKind.AnyKeyword || generic.kind === ts.SyntaxKind.UnknownKeyword) {
          errors.push(`${location}: HTTP response must have an explicit contract`)
        }
        const actualRoute = actualPath === null ? null : normalize(`${method} ${actualPath}`)
        if (actualRoute !== null && !routes.has(actualRoute)) errors.push(`${location}: unknown HTTP route ${actualRoute}`)
        if (generic && ts.isIndexedAccessTypeNode(generic) && ts.isTypeReferenceNode(generic.objectType)
            && ts.isIdentifier(generic.objectType.typeName) && generic.objectType.typeName.text === 'ApiResponses'
            && ts.isLiteralTypeNode(generic.indexType) && ts.isStringLiteral(generic.indexType.literal)) {
          const route = normalize(generic.indexType.literal.text)
          if (!routes.has(route) || route !== actualRoute) errors.push(`${location}: response route ${route} does not match ${actualRoute}`)
        }
      }
      ts.forEachChild(node, visit)
    }
    visit(source)
  }
  expect(calls.length).toBeGreaterThanOrEqual(100)
  expect(checkedDynamicImageSource).toBe(true)
  expect(errors).toEqual([])
})
