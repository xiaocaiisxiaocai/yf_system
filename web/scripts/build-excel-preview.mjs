import { build } from 'vite'
import { readFile, writeFile, mkdir } from 'node:fs/promises'
import { fileURLToPath } from 'node:url'
import path from 'node:path'

const root = fileURLToPath(new URL('../', import.meta.url))
const output = path.resolve(root, '.excel-preview-build')
if (path.dirname(output) !== path.resolve(root)) throw new Error('Invalid viewer build directory')
await build({
  configFile: false, root, publicDir: false, logLevel: 'warn',
  build: {
    outDir: output, emptyOutDir: true, minify: true,
    lib: { entry: path.join(root, 'vendor/vue-office-excel/viewer.js'), name: 'YfExcelPreview', formats: ['iife'], fileName: () => 'viewer.js', cssFileName: 'viewer' },
  },
})
const code = (await readFile(path.join(output, 'viewer.js'), 'utf8')).replace(/<\/script/gi, '<\\/script')
const css = await readFile(path.join(output, 'viewer.css'), 'utf8')
const html = `<!doctype html><html lang="zh-CN"><head><meta charset="utf-8"><meta http-equiv="Content-Security-Policy" content="default-src 'none'; script-src 'nonce-YF_VIEWER_NONCE'; style-src 'unsafe-inline'; img-src data: blob:; font-src data:; connect-src 'none'; form-action 'none'; base-uri 'none'"><style>${css}
html,body,#viewer{height:100%;margin:0;overflow:hidden}body{font-family:'Segoe UI','Microsoft YaHei',sans-serif}*{box-sizing:border-box}.vue-office-excel-main{height:100%}.x-spreadsheet-resizer,.x-spreadsheet-editor{display:none!important}
</style></head><body><div id="viewer"></div><script nonce="YF_VIEWER_NONCE">${code}</script></body></html>`
await mkdir(path.join(root, 'generated'), { recursive: true })
await writeFile(path.join(root, 'generated/excel-viewer.html'), html)
console.log(`Excel viewer built from supplied source (${Math.round(html.length / 1024)} KiB)`)
