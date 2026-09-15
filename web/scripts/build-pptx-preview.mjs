import { build } from 'vite'
import { readFile, writeFile, mkdir } from 'node:fs/promises'
import { fileURLToPath } from 'node:url'
import path from 'node:path'
import { patchPptxRenderer } from '../vendor/vue-office-pptx/patch-renderer.mjs'

const root = fileURLToPath(new URL('../', import.meta.url))
const output = path.resolve(root, '.pptx-preview-build')
if (path.dirname(output) !== path.resolve(root)) throw new Error('Invalid viewer build directory')
await build({
  configFile: false, root, publicDir: false, logLevel: 'warn',
  plugins: [{
    name: 'pptx-drawingml-text-layout',
    transform(code, id) {
      if (id.replaceAll('\\', '/').endsWith('/pptx-preview/dist/pptx-preview.es.js')) {
        return { code: patchPptxRenderer(code), map: null }
      }
    },
  }],
  build: {
    outDir: output, emptyOutDir: true, minify: true,
    lib: { entry: path.join(root, 'vendor/vue-office-pptx/viewer.js'), name: 'YfPptxPreview', formats: ['iife'], fileName: () => 'viewer.js', cssFileName: 'viewer' },
  },
})
const code = (await readFile(path.join(output, 'viewer.js'), 'utf8')).replace(/<\/script/gi, '<\\/script')
const css = await readFile(path.join(output, 'viewer.css'), 'utf8')
const html = `<!doctype html><html lang="zh-CN"><head><meta charset="utf-8"><meta http-equiv="Content-Security-Policy" content="default-src 'none'; script-src 'nonce-YF_VIEWER_NONCE'; style-src 'unsafe-inline'; img-src data: blob:; font-src data:; connect-src 'none'; media-src 'none'; object-src 'none'; frame-src 'none'; worker-src 'none'; form-action 'none'; base-uri 'none'"><style>${css}</style></head><body><div id="viewer"></div><script nonce="YF_VIEWER_NONCE">${code}</script></body></html>`
await mkdir(path.join(root, 'generated'), { recursive: true })
await writeFile(path.join(root, 'generated/pptx-viewer.html'), html)
console.log(`PPTX viewer built from supplied wrapper and pinned local engine (${Math.round(html.length / 1024)} KiB)`)
