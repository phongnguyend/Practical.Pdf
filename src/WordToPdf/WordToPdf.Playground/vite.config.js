import { defineConfig, loadEnv } from 'vite';

export default defineConfig(({ mode }) => {
  const env = loadEnv(mode, process.cwd(), '');
  const proxy = {
    '/api': {
      target: env.WORD_TO_PDF_PROXY_URL || 'http://localhost:62823',
      changeOrigin: true,
      rewrite: (path) => path.replace(/^\/api/, ''),
    },
  };
  return {
    esbuild: { jsx: 'automatic' },
    server: { port: 5176, strictPort: true, proxy },
    preview: { port: 4176, strictPort: true, proxy },
  };
});
