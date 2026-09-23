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
    // Files run in parallel with per-file isolation. Four workers keep jsdom UI tests stable on a busy
    // machine; the longer limits absorb CPU contention rather than masking real hangs.
    maxWorkers: 4,
    testTimeout: 20_000,
  },
})
