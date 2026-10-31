import react from '@vitejs/plugin-react'
import { defineConfig } from 'vite'

export default defineConfig({
  plugins: [react()],
  server: {
    proxy: {
      '/api': { target: process.env.LODGE_API_URL ?? 'http://localhost:5080', changeOrigin: true },
    },
  },
})
