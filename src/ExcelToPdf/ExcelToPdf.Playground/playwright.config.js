import { defineConfig } from '@playwright/test';

export default defineConfig({
  testDir: './e2e',
  use: { baseURL: 'http://localhost:5175', channel: 'chrome', screenshot: 'only-on-failure' },
  webServer: { command: 'npm run dev', url: 'http://localhost:5175', reuseExistingServer: !process.env.CI },
});
