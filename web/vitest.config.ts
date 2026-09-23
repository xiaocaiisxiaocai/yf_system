import react from '@vitejs/plugin-react'
import { defineConfig } from 'vitest/config'

export default defineConfig({
  plugins: [react()],
  test: {
    environment: 'jsdom',
    setupFiles: ['./src/test/setup.ts'],
    include: ['src/test/**/*.test.{ts,tsx,js,jsx}'],
    clearMocks: true,
    restoreMocks: true,
    mockReset: true,
    maxWorkers: 1,
    minWorkers: 1,
  },
})
