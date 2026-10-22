import { defineConfig } from 'vite';

export default defineConfig({
  server: { port: Number(process.env.PLAYGROUND_UI_PORT ?? 5173), strictPort: true, proxy: { '/api': process.env.PLAYGROUND_API_URL ?? 'http://localhost:5080' } },
});
