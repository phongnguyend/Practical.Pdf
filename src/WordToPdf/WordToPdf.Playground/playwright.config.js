import { defineConfig } from '@playwright/test';

export default defineConfig({
  testDir: './e2e',
  use: { baseURL: 'http://localhost:5176', channel: 'chrome', screenshot: 'only-on-failure' },
  webServer: { command: 'npm run dev', url: 'http://localhost:5176', reuseExistingServer: !process.env.CI },
});
