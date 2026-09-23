import { createHash } from 'node:crypto'
import { readFileSync } from 'node:fs'
import { resolve } from 'node:path'
import { expect, test } from 'vitest'
import { contentSecurityPolicy, inlineScriptHashes, VIEWER_FILES } from '../../../../csp'

const sha = (value: string) => `'sha256-${createHash('sha256').update(value).digest('base64')}'`
const viewer = (body: string) => `<html><head><meta http-equiv="Content-Security-Policy" content="script-src 'nonce-YF_VIEWER_NONCE'"></head><body><script nonce="YF_VIEWER_NONCE">${body}</script></body></html>`
const directives = (policy: string) => new Map(policy.split('; ').map(value => {
  const [name, ...sources] = value.split(' ')
  return [name, sources]
}))

test('viewer scripts are hashed exactly as the browser parses them', () => {
  expect(inlineScriptHashes(viewer('run()'))).toEqual([sha('run()')])
  const html = viewer('a()\r\nb()\rc()')
  const parsed = new DOMParser().parseFromString(html, 'text/html')
  expect(inlineScriptHashes(html)).toEqual([sha(parsed.querySelector('script')!.textContent!)])
  expect(inlineScriptHashes('<script src="/x.js"></script>')).toEqual([])
})

test('the policy allows only the hashed viewer scripts, never inline script or eval in general', () => {
  const policy = directives(contentSecurityPolicy([viewer('one()'), viewer('two()')]))
  expect(policy.get('script-src')).toEqual(["'self'", "'wasm-unsafe-eval'", sha('one()'), sha('two()')])
  expect(policy.get('script-src')).not.toContain("'unsafe-inline'")
  expect(policy.get('script-src')).not.toContain("'unsafe-eval'")
  expect(policy.get('object-src')).toEqual(["'none'"])
  expect(policy.get('base-uri')).toEqual(["'self'"])
})

test('a viewer with an unexpected script count fails the build instead of shipping a broken policy', () => {
  expect(() => contentSecurityPolicy(['<html></html>'])).toThrow('exactly one inline script')
  expect(() => contentSecurityPolicy([viewer('a()') + '<script>b()</script>'])).toThrow('exactly one inline script')
})

test('generated viewers, when built, each carry one hashable inline script', () => {
  const documents = VIEWER_FILES.map(file => readFileSync(resolve(file), 'utf8'))
  const policy = directives(contentSecurityPolicy(documents))
  expect(policy.get('script-src')!.filter(source => source.startsWith("'sha256-"))).toHaveLength(2)
  for (const html of documents) {
    const parsed = new DOMParser().parseFromString(html, 'text/html')
    const scripts = [...parsed.querySelectorAll('script')]
    expect(scripts).toHaveLength(1)
    expect(scripts[0].hasAttribute('src')).toBe(false)
    expect(policy.get('script-src')).toContain(sha(scripts[0].textContent!))
  }
})
