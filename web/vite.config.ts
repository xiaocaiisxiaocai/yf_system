import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'
import { pdfAssets } from './pdf-assets.ts'

export default defineConfig({
  plugins: [react(), pdfAssets()],
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
    chunkSizeWarningLimit: 2000,
    rolldownOptions: {
      output: {
        codeSplitting: {
          groups: [
            { name: 'excel-parser', test: /node_modules[\\/](?:xlsx|cfb|codepage)[\\/]/ },
            { name: 'arco', test: /node_modules[\\/]@arco-design[\\/]/ },
            { name: 'vendor', test: /node_modules[\\/](?:react|react-dom|react-router-dom|axios|zustand)[\\/]/ },
          ],
        },
      },
    },
  },
})
