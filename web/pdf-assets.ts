import { readFileSync, readdirSync } from 'node:fs'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'
import type { Plugin } from 'vite'

// Versioned local CMaps, fonts and image decoders for Vite and production.
export function pdfAssets(): Plugin {
  const root = dirname(fileURLToPath(import.meta.resolve('pdfjs-dist/package.json')))
  const { version } = JSON.parse(readFileSync(join(root, 'package.json'), 'utf8')) as { version: string }
  const assets = new Map<string, string>()
  assets.set(`pdfjs/${version}/LICENSE`, join(root, 'LICENSE'))
  for (const directory of ['cmaps', 'standard_fonts', 'wasm', 'iccs']) {
    for (const file of readdirSync(join(root, directory), { withFileTypes: true })) {
      if (file.isFile()) assets.set(`pdfjs/${version}/${directory}/${file.name}`, join(root, directory, file.name))
    }
  }
  return {
    name: 'local-pdf-assets',
    configureServer(server) {
      server.middlewares.use((request, response, next) => {
        const pathname = (request.url ?? '').split('?')[0]
        const base = server.config.base
        const key = pathname.startsWith(base) ? pathname.slice(base.length) : pathname.slice(1)
        const source = assets.get(key)
        if (!source) return next()
        response.setHeader('Content-Type', key.endsWith('.wasm') ? 'application/wasm'
          : key.endsWith('.js') ? 'text/javascript' : 'application/octet-stream')
        response.end(readFileSync(source))
      })
    },
    generateBundle() {
      for (const [fileName, source] of assets) this.emitFile({ type: 'asset', fileName, source: readFileSync(source) })
    },
  }
}
