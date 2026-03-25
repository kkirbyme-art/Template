import { defineConfig, loadEnv } from 'vite'
import react from '@vitejs/plugin-react'
import tailwindcss from '@tailwindcss/vite'
import { TanStackRouterVite } from '@tanstack/router-vite-plugin'
import path from "path"

export default ({ mode }: { mode: string }) => {
  process.env = { ...process.env, ...loadEnv(mode, process.cwd()) }

  return defineConfig({
    base: process.env.VITE_URL_BASEPATH,
    plugins: [react(), tailwindcss(), TanStackRouterVite()],
    optimizeDeps: {
      include: ['pdfjs-dist'],
    },
    build: {
      outDir: "D:\\Documents\\Published\\erms\\wwwroot",
      emptyOutDir: true,
    },
    resolve: {
      alias: {
        "@": path.resolve(__dirname, "./src"),
      },
    },
    server: {
      proxy: {
        "/api": {
          target: "https://localhost:7260",
          changeOrigin: true,
          secure: false,
        },
      },
    },
  })
}