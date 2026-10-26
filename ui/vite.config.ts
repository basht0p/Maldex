/// <reference types="vitest/config" />
import react from '@vitejs/plugin-react'
import { configDefaults } from 'vitest/config'
import { defineConfig, searchForWorkspaceRoot } from 'vite'

const backend = 'http://localhost:5080'

// https://vite.dev/config/
export default defineConfig({
  plugins: [react()],
  server: {
    // The brand tokens, logos and icons live in ../brand (the single source), outside the Vite root. Only that
    // folder is added: the repo root also holds .data/ (samples, artifacts), which the dev server must never serve.
    fs: { allow: [searchForWorkspaceRoot(process.cwd()), '../brand'] },
    proxy: {
      // Every entry keeps the browser's Host header (changeOrigin: false). The backend refuses an /api or /hubs request (and a
      // WebSocket upgrade) whose Origin is not its own {scheme}://{Host}, and the browser sends Origin http://localhost:5173 on
      // every mutation and upgrade; with Host localhost:5173 the two match. The SSO callback needs it too, so redirect_uri
      // matches. vite.config.test.ts runs this table through a real dev server.
      '/api': { target: backend, changeOrigin: false, ws: true }, // ws: the live-view tunnels (/api/tasks/{id}/tunnel, /api/admin/template-jobs/{id}/tunnel)
      '/hubs': { target: backend, changeOrigin: false, ws: true },
      '/signin-oidc': { target: backend, changeOrigin: false },
    },
  },
  test: {
    environment: 'jsdom',
    setupFiles: ['./src/test/setup.ts'],
    css: false,
    exclude: [...configDefaults.exclude, 'e2e/**'], // Playwright specs run via ./build.sh smoke
  },
})
