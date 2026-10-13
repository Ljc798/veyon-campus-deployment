import { defineConfig } from 'vite';
import { readFileSync } from 'node:fs';

const appProject = readFileSync(new URL('../src/VeyonCampus.App/VeyonCampus.App.csproj', import.meta.url), 'utf8');
const appVersion = appProject.match(/<Version>\s*([^<]+?)\s*<\/Version>/)?.[1];

if (!appVersion) {
  throw new Error('Could not read the application version from VeyonCampus.App.csproj.');
}

export default defineConfig(({ mode }) => ({
  base: '/',
  envPrefix: mode === 'static-only' ? [] : 'VITE_',
  publicDir: 'public',
  define: {
    __VEYON_CAMPUS_APP_VERSION__: JSON.stringify(appVersion.trim())
  },
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
