import { defineConfig } from 'vite';

export default defineConfig(({ mode }) => ({
  base: '/',
  envPrefix: mode === 'static-only' ? [] : 'VITE_',
  publicDir: 'public',
  server: {
    host: '127.0.0.1',
    port: 5173,
    strictPort: true
  },
  preview: {
    host: '127.0.0.1',
    port: 4173,
    strictPort: true
  },
  build: {
    modulePreload: { polyfill: mode !== 'static-only' },
    outDir: 'dist',
    emptyOutDir: true
  }
}));
