import { readFileSync, readdirSync } from 'node:fs'
import { join, relative } from 'node:path'
import ts from 'typescript'
import { expect, test } from 'vitest'
import { render } from '../../../../scripts/generate-api-types.mjs'

test('generated API types match the backend OpenAPI contract', () => {
  expect(readFileSync('src/api/generated/api-types.ts', 'utf8').replaceAll('\r\n', '\n')).toBe(render())
})

const HTTP_METHODS = new Set(['GET', 'POST', 'PUT', 'DELETE', 'PATCH'])

function extractLiteralPath(argument: ts.Expression | undefined): string | null {
  if (!argument) return null
  if (ts.isStringLiteralLike(argument)) return argument.text
  if (ts.isTemplateExpression(argument)) {
    return argument.head.text + argument.templateSpans.map(span => '{}' + span.literal.text).join('')
  }
  return null
}

function extractApiResponsesRoute(generic: ts.TypeNode | undefined): string | null {
  if (generic && ts.isIndexedAccessTypeNode(generic) && ts.isTypeReferenceNode(generic.objectType)
      && ts.isIdentifier(generic.objectType.typeName) && generic.objectType.typeName.text === 'ApiResponses'
      && ts.isLiteralTypeNode(generic.indexType) && ts.isStringLiteral(generic.indexType.literal)) {
    return generic.indexType.literal.text
  }
  return null
}

function isInsideOemApiWrapperHelper(node: ts.Node): boolean {
  let current: ts.Node | undefined = node.parent
  while (current) {
    if (ts.isMethodDeclaration(current) && ts.isIdentifier(current.name)
        && HTTP_METHODS.has(current.name.text.toUpperCase())
        && current.modifiers?.some(modifier => modifier.kind === ts.SyntaxKind.PrivateKeyword)) {
      return true
    }
    current = current.parent
  }
  return false
}

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
  let checkedOemApiCalls = 0
  let checkedPortalHttpCalls = 0
  let checkedLoginCall = false
  for (const file of files('src')) {
    const normalizedFile = file.replaceAll('\\', '/')
    const source = ts.createSourceFile(file, readFileSync(file, 'utf8'), ts.ScriptTarget.Latest, true)
    const visit = (node: ts.Node) => {
      if (ts.isCallExpression(node) && ts.isPropertyAccessExpression(node.expression)) {
        const method = node.expression.name.text.toUpperCase()
        const receiver = node.expression.expression
        const location = `${relative('.', file)}:${source.getLineAndCharacterOfPosition(node.getStart()).line + 1}`
        const argument = node.arguments[0]
        const generic = node.typeArguments?.[0]

        const isHttp = ts.isIdentifier(receiver) && receiver.text === 'http'
        const isPortalHttp = ts.isIdentifier(receiver) && receiver.text === 'portalHttp' && HTTP_METHODS.has(method)
        const isAxios = ts.isIdentifier(receiver) && receiver.text === 'axios' && HTTP_METHODS.has(method)
        const isOemHelper = normalizedFile === 'src/oem/api/OemApi.ts'
          && receiver.kind === ts.SyntaxKind.ThisKeyword && HTTP_METHODS.has(method)
        const isOemThisHttp = normalizedFile === 'src/oem/api/OemApi.ts'
          && ts.isPropertyAccessExpression(receiver) && receiver.expression.kind === ts.SyntaxKind.ThisKeyword
          && receiver.name.text === 'http' && HTTP_METHODS.has(method) && !isInsideOemApiWrapperHelper(node)

        if (isHttp || isPortalHttp || isOemThisHttp) {
          const actualPath = extractLiteralPath(argument)
          // ImageDocument accepts either a file or message-image URL from its two typed callers.
          // This is the sole dynamic URL boundary; its HTTP options and exact endpoints have DOM tests.
          if (actualPath === null && isHttp && normalizedFile === 'src/components/ImagePreview.tsx'
              && method === 'GET' && argument && ts.isIdentifier(argument) && argument.text === 'requestUrl'
              && generic && ts.isTypeReferenceNode(generic)
              && ts.isIdentifier(generic.typeName) && generic.typeName.text === 'Blob') {
            checkedDynamicImageSource = true
            for (const route of ['GET /files/{}/content', 'GET /messages/{}/images/{}']) {
              if (!routes.has(route)) errors.push(`${location}: missing image endpoint ${route}`)
            }
          } else if (actualPath === null) {
            errors.push(`${location}: HTTP URL requires a literal/template or an explicitly tested dynamic boundary`)
          }
          calls.push(`${method} ${actualPath ?? '[dynamic image]'}`)
          if (isPortalHttp) checkedPortalHttpCalls += 1
          if (isOemThisHttp) checkedOemApiCalls += 1
          if (!generic || generic.kind === ts.SyntaxKind.AnyKeyword || generic.kind === ts.SyntaxKind.UnknownKeyword) {
            errors.push(`${location}: HTTP response must have an explicit contract`)
          }
          const actualRoute = actualPath === null ? null : normalize(`${method} ${actualPath}`)
          if (actualRoute !== null && !routes.has(actualRoute)) errors.push(`${location}: unknown HTTP route ${actualRoute}`)
          const responseRoute = extractApiResponsesRoute(generic)
          if (responseRoute !== null) {
            const route = normalize(responseRoute)
            if (!routes.has(route) || route !== actualRoute) errors.push(`${location}: response route ${route} does not match ${actualRoute}`)
          }
        } else if (isOemHelper) {
          checkedOemApiCalls += 1
          const actualPath = extractLiteralPath(argument)
          if (actualPath === null) {
            errors.push(`${location}: OemApi URL requires a literal/template`)
          }
          calls.push(`${method} ${actualPath ?? '[unknown]'}`)
          const actualRoute = actualPath === null ? null : normalize(`${method} ${actualPath}`)
          if (actualRoute !== null && !routes.has(actualRoute)) errors.push(`${location}: unknown OemApi route ${actualRoute}`)
          if (!generic || !ts.isLiteralTypeNode(generic) || !ts.isStringLiteral(generic.literal)) {
            errors.push(`${location}: OemApi helper call must specify a string-literal route key`)
          } else {
            const route = normalize(generic.literal.text)
            if (!routes.has(route) || route !== actualRoute) errors.push(`${location}: OemApi route key ${route} does not match ${actualRoute}`)
          }
        } else if (isAxios) {
          const rawPath = extractLiteralPath(argument)
          if (rawPath === null) {
            errors.push(`${location}: axios URL requires a literal/template`)
          } else if (!rawPath.startsWith('/api/v1/')) {
            errors.push(`${location}: axios URL must target /api/v1/: ${rawPath}`)
          } else {
            const actualRoute = normalize(`${method} ${rawPath.replace(/^\/api\/v1/, '')}`)
            calls.push(actualRoute)
            if (normalizedFile === 'src/pages/Login.tsx' && actualRoute === 'POST /auth/login') checkedLoginCall = true
            if (!routes.has(actualRoute)) errors.push(`${location}: unknown axios route ${actualRoute}`)
            const responseRoute = extractApiResponsesRoute(generic)
            if (responseRoute !== null) {
              const route = normalize(responseRoute)
              if (!routes.has(route) || route !== actualRoute) errors.push(`${location}: response route ${route} does not match ${actualRoute}`)
            }
          }
        }
      }
      ts.forEachChild(node, visit)
    }
    visit(source)
  }
  expect(calls.length).toBeGreaterThanOrEqual(150)
  expect(checkedDynamicImageSource).toBe(true)
  expect(checkedOemApiCalls).toBeGreaterThanOrEqual(40)
  expect(checkedPortalHttpCalls).toBeGreaterThanOrEqual(3)
  expect(checkedLoginCall).toBe(true)
  expect(errors).toEqual([])
})
