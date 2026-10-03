import path from 'node:path'
import tailwindcss from '@tailwindcss/vite'
import react from '@vitejs/plugin-react'
import { defineConfig } from 'vitest/config'

// Locally, Aspire passes the gateway's address; everything the SPA calls goes through it,
// so the browser sees one origin, like in Azure (design §8.1).
const gateway = process.env.services__gateway__https__0 ?? process.env.services__gateway__http__0 ?? 'https://localhost:7100'
const toGateway = { target: gateway, changeOrigin: true, secure: false }

export default defineConfig({
  plugins: [react(), tailwindcss()],
  resolve: {
    alias: { '@': path.resolve(import.meta.dirname, './src') },
  },
  server: {
    port: process.env.PORT ? Number(process.env.PORT) : undefined,
    proxy: {
      '/api': toGateway,
      '/config.json': toGateway,
      '/version': toGateway,
    },
  },
  build: {
    // The gateway serves the build from its wwwroot.
    outDir: '../Gateway/wwwroot',
    emptyOutDir: true,
  },
  test: {
    environment: 'jsdom',
    setupFiles: ['./src/test-setup.ts'],
  },
})
