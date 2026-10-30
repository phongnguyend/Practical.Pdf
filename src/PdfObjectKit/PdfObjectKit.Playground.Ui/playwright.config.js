import { defineConfig } from '@playwright/test';
const api = process.env.PLAYGROUND_API_URL ?? 'http://localhost:5080';
const ui = `http://localhost:${process.env.PLAYGROUND_UI_PORT ?? 5173}`;

export default defineConfig({
  testDir: './e2e',
  workers: 1,
  forbidOnly: !!process.env.CI,
  reporter: process.env.CI ? [['line'], ['html', { open: 'never' }]] : 'list',
  use: { baseURL: ui, channel: 'chrome', viewport: { width: 1440, height: 1000 }, screenshot: 'only-on-failure', trace: 'retain-on-failure' },
  webServer: [
    { command: `dotnet run --project ../PdfObjectKit.Playground.Api -c ${process.env.PLAYGROUND_CONFIGURATION ?? 'Release'} --no-launch-profile --urls ${api}`, url: `${api}/api/health`, reuseExistingServer: !process.env.CI, timeout: 120000 },
    { command: 'npm run dev', url: ui, reuseExistingServer: !process.env.CI },
  ],
});
