/// <reference types="vitest/config" />
import react from '@vitejs/plugin-react'
import { configDefaults } from 'vitest/config'
import { defineConfig } from 'vite'

const backend = 'http://localhost:5080'

// https://vite.dev/config/
export default defineConfig({
  plugins: [react()],
  server: {
    proxy: {
      // The live-view WebSocket. It must come before '/api' (first match wins) and keep the browser's Host header
      // (changeOrigin: false): the backend only accepts an upgrade whose Origin matches its Host, and the browser sends
      // Origin http://localhost:5173. The backend sees Host localhost:5173 and Origin http://localhost:5173: same origin.
      '^/api/tasks/[^/]+/tunnel': { target: backend, changeOrigin: false, ws: true },
      '/api': { target: backend, changeOrigin: true },
      '/hubs': { target: backend, changeOrigin: true, ws: true },
    },
  },
  test: {
    environment: 'jsdom',
    setupFiles: ['./src/test/setup.ts'],
    css: false,
    exclude: [...configDefaults.exclude, 'e2e/**'], // Playwright specs run via ./build.sh smoke
  },
})
