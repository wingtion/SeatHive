import tailwindcss from '@tailwindcss/vite'
import react from '@vitejs/plugin-react'
import { defineConfig } from 'vitest/config'

// The API on this machine ("docker compose up" publishes it on 8080). The dev server forwards to it, so the
// browser talks to one origin; a deployed build calls the API at VITE_API_BASE_URL instead.
const api = process.env.SEATHIVE_API ?? 'http://localhost:8080'

export default defineConfig({
  plugins: [react(), tailwindcss()],
  server: {
    port: 5173,
    strictPort: true,
    proxy: {
      '/api': { target: api, changeOrigin: true },
      '/hubs': { target: api, changeOrigin: true, ws: true },
    },
  },
  test: {
    environment: 'node',
  },
})
