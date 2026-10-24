import { defineConfig } from 'vite'
import vue from '@vitejs/plugin-vue'
import { fileURLToPath, URL } from 'node:url'

export default defineConfig({
  plugins: [vue()],
  resolve: {
    alias: {
      '@': fileURLToPath(new URL('./src', import.meta.url)),
    },
  },
  server: {
    proxy: {
      // API + auth redirects go to the ASP.NET backend during development
      '/api': 'http://localhost:5000',
      '/auth': 'http://localhost:5000',
      '/signin': 'http://localhost:5000',
    },
  },
  build: {
    outDir: 'dist',
  },
})
