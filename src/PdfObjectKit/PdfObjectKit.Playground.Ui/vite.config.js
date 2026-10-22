import { defineConfig, loadEnv } from 'vite';

export default defineConfig(({ mode }) => {
  const env = loadEnv(mode, process.cwd(), 'PLAYGROUND_');
  return {
    server: {
      port: Number(process.env.PLAYGROUND_UI_PORT ?? env.PLAYGROUND_UI_PORT ?? 5173),
      strictPort: true,
      proxy: { '/api': process.env.PLAYGROUND_API_URL ?? env.PLAYGROUND_API_URL ?? 'http://localhost:5080' },
    },
  };
});
