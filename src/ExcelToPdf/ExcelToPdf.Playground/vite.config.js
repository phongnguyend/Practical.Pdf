import { defineConfig, loadEnv } from 'vite';

export default defineConfig(({ mode }) => {
  const env = loadEnv(mode, process.cwd(), '');
  const proxy = {
    '/api': {
      target: env.EXCEL_TO_PDF_PROXY_URL || 'http://localhost:62723',
      changeOrigin: true,
      rewrite: (path) => path.replace(/^\/api/, ''),
    },
  };
  return {
    esbuild: { jsx: 'automatic' },
    server: { port: 5175, strictPort: true, proxy },
    preview: { port: 4175, strictPort: true, proxy },
  };
});
