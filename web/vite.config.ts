import { defineConfig } from 'vite'
import type { Plugin } from 'vite'
import react from '@vitejs/plugin-react'
import { pdfAssets } from './pdf-assets.ts'
import { contentSecurityPolicyPlugin } from './csp.ts'

const LARGE_CHUNK_BYTES = 600 * 1024
const knownLargeChunks = [
  { name: /(?:^|\/)ExcelPreview-/, maximumBytes: 1900 * 1024 },
]

function largeChunkPolicy(): Plugin {
  return {
    name: 'large-chunk-policy',
    generateBundle(_options, bundle) {
      for (const item of Object.values(bundle)) {
        if (item.type !== 'chunk') continue
        const bytes = Buffer.byteLength(item.code)
        if (bytes <= LARGE_CHUNK_BYTES) continue
        const known = knownLargeChunks.find(entry => entry.name.test(item.fileName))
        if (!known) {
          this.error(`Unexpected JavaScript chunk exceeds 600 KiB: ${item.fileName} (${bytes} bytes)`)
        }
        if (bytes > known.maximumBytes) {
          this.error(`Known large chunk exceeded its reviewed ceiling: ${item.fileName} (${bytes} bytes)`)
        }
      }
    },
  }
}

export default defineConfig({
  plugins: [react(), pdfAssets(), contentSecurityPolicyPlugin(), largeChunkPolicy()],
  // 懒加载路由里的依赖（如 dockview-react）若首次访问才被发现，Vite 会重新预构建并让已加载页面拿到 504 Outdated Optimize Dep；
  // 启动时扫描全部源码，一次性预构建。
  optimizeDeps: {
    entries: ['index.html', 'src/**/*.{ts,tsx}', '!src/test/**', '!src/**/*.test.{ts,tsx}'],
  },
  server: {
    host: '127.0.0.1',
    port: 5173,
    // 验收下载和浏览器日志不是源码；Windows 独占写入可能使监听器 EBUSY 退出。
    watch: { ignored: ['**/output/**', '**/.playwright-cli/**'] },
    proxy: {
      '/api': {
        target: 'http://127.0.0.1:8080',
        changeOrigin: true,
        ws: true,
      },
    },
  },
  build: {
    chunkSizeWarningLimit: 600,
    rolldownOptions: {
      output: {
        codeSplitting: {
          groups: [
            { name: 'excel-parser', test: /node_modules[\\/](?:xlsx|cfb|codepage)[\\/]/ },
            // Arco is deliberately not grouped: each lazy route then loads only the components it uses
            // (the login page ships ~550 KB of JS instead of ~1 MB).
            { name: 'vendor', test: /node_modules[\\/](?:react|react-dom|react-router-dom|axios|zustand)[\\/]/ },
          ],
        },
      },
    },
  },
})
