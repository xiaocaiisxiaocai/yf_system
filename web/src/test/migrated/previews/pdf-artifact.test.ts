// @vitest-environment node
import { readFile } from 'node:fs/promises'
import { resolve } from 'node:path'
import { pathToFileURL } from 'node:url'
import { fileURLToPath } from 'node:url'
import { describe, expect, it } from 'vitest'
import { GlobalWorkerOptions } from 'pdfjs-dist/legacy/build/pdf.mjs'
import { openPdf } from '../../../components/pdfEngine'

describe('PDF engine and vendored provenance', () => {
  it('production PDF engine reads a real document without native Uint8Array.toHex', async () => {
    const fixturePath = fileURLToPath(new URL('../../../../test/fixtures/pdf-compatibility.pdf', import.meta.url))
    const bytes = new Uint8Array(await readFile(fixturePath))
    const descriptor = Object.getOwnPropertyDescriptor(Uint8Array.prototype, 'toHex')
    Reflect.deleteProperty(Uint8Array.prototype, 'toHex')
    // Vite exposes ?url as a browser-root path. Point the Node worker boundary
    // at the same installed production worker while leaving openPdf untouched.
    GlobalWorkerOptions.workerSrc = pathToFileURL(resolve(
      process.cwd(), 'node_modules/pdfjs-dist/legacy/build/pdf.worker.min.mjs',
    )).href
    const task = openPdf(bytes)

    try {
      expect('toHex' in Uint8Array.prototype).toBe(false)
      const pdf = await task.promise
      expect(pdf.numPages).toBe(1)
      const page = await pdf.getPage(1)
      const content = await page.getTextContent()
      expect(content.items.map(item => 'str' in item ? item.str : '').join(' ')).toContain('Local acceptance PDF')
    } finally {
      await task.destroy()
      if (descriptor) Object.defineProperty(Uint8Array.prototype, 'toHex', descriptor)
    }
  }, 15_000)

  it('vendored vue-office renderer preserves source/license and removes old loaders and download', async () => {
    const root = new URL('../../../../vendor/vue-office-pdf/', import.meta.url)
    const [source, provenance, license] = await Promise.all([
      readFile(new URL('src/main.ts', root), 'utf8'),
      readFile(new URL('SOURCE.md', root), 'utf8'),
      readFile(new URL('core/LICENSE', root), 'utf8'),
    ])

    // These files are the immutable provenance artifact, so plaintext assertions
    // verify the recorded origin/license and the intentionally removed loader path.
    expect(provenance).toContain('core/packages/js-pdf/src/main.js')
    expect(provenance).toContain('Archive SHA-256:')
    expect(license).toContain('Copyright (c) 2023 hit757')
    expect(source).not.toMatch(/unpkg|window\.pdfjsLib|pdfLibJsStr|workerStr|downloadFile/)
    expect(source).toContain('openPdf')
  })
})
