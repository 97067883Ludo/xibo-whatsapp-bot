import { defineConfig } from 'vite'
import vue from '@vitejs/plugin-vue'

export default defineConfig({
  plugins: [vue()],
  server: {
    // During development the API runs on `dotnet run` (port 5080)
    proxy: { '/api': 'http://localhost:5080' }
  }
})
