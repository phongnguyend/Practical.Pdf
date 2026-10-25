import { expect, test } from '@playwright/test';
import { createServer } from 'node:http';

test('validates website URLs and retains file upload mode', async ({ page }) => {
  await page.goto('/');
  await page.getByRole('button', { name: 'Website URL', exact: true }).click();
  await expect(page.getByRole('button', { name: 'Convert to PDF' })).toBeDisabled();
  for (const url of ['example.com', 'file:///etc/passwd', 'ftp://example.com', 'https://user:password@example.com']) {
    await page.getByLabel('Website URL', { exact: true }).fill(url);
    await page.getByRole('button', { name: 'Convert to PDF' }).click();
    await expect(page.getByRole('alert')).toContainText('HTTP or HTTPS');
  }
  await page.getByRole('button', { name: 'Upload file', exact: true }).click();
  await expect(page.getByRole('button', { name: 'Browse files' })).toBeVisible();
  await expect(page.getByRole('alert')).toHaveCount(0);
});

test('converts a URL with all engines and clears stale results', async ({ page }) => {
  const requested = new Set();
  await page.route('**/api/*/convert-url', async (route) => {
    expect(route.request().postDataJSON()).toEqual({ url: 'https://example.com/report?year=2026' });
    expect(route.request().headers()['content-type']).toContain('application/json');
    requested.add(route.request().url().split('/').at(-2));
    await route.fulfill({ contentType: 'application/pdf', body: '%PDF-1.4\n%%EOF' });
  });
  await page.setViewportSize({ width: 1280, height: 720 });
  await page.goto('/');
  await page.getByRole('button', { name: 'Website URL', exact: true }).click();
  await page.getByLabel('Website URL', { exact: true }).fill('https://example.com/report?year=2026');
  for (const checkbox of await page.getByRole('checkbox').all()) {
    await checkbox.check();
  }
  await page.getByRole('button', { name: 'Convert to PDF' }).click();
  await expect(page.getByRole('link', { name: 'Download PDF' })).toHaveCount(3);
  expect([...requested].sort()).toEqual(['chrome', 'playwright', 'puppeteer']);
  await expect(page.getByRole('region', { name: 'Chrome result', exact: true }).getByRole('link', { name: 'Download PDF' })).toHaveAttribute('download', 'example.com.chrome.pdf');
  await page.screenshot({ path: 'test-results/url-results.png', fullPage: true });
  expect(await page.evaluate(() => document.documentElement.scrollHeight)).toBe(720);
  await page.getByLabel('Website URL', { exact: true }).fill('https://example.org');
  await expect(page.getByRole('link', { name: 'Download PDF' })).toHaveCount(0);
});

test('real URL navigation and validation through all three APIs', async ({ request }) => {
  test.skip(process.env.HTML_TO_PDF_URL_LIVE_TEST !== '1', 'Requires all three APIs and proxy running locally.');
  test.setTimeout(120000);
  const assets = new Set();
  const server = createServer((req, res) => {
    if (req.url === '/style.css') {
      assets.add('css');
      res.writeHead(200, { 'Content-Type': 'text/css' });
      res.end('h1 { color: green; }');
    } else if (req.url === '/script.js') {
      assets.add('js');
      res.writeHead(200, { 'Content-Type': 'application/javascript' });
      res.end('document.querySelector("h1").textContent = "URL conversion verified";');
    } else {
      res.writeHead(200, { 'Content-Type': 'text/html' });
      res.end('<!doctype html><link rel="stylesheet" href="/style.css"><h1>Loading</h1><script src="/script.js"></script>');
    }
  });
  await new Promise((resolve) => server.listen(0, '127.0.0.1', resolve));
  try {
    for (const engine of ['chrome', 'playwright', 'puppeteer']) {
      assets.clear();
      const response = await request.post(`/api/${engine}/convert-url`, { data: { url: `http://127.0.0.1:${server.address().port}/page` }, timeout: 100000 });
      expect(response.status(), await response.text()).toBe(200);
      expect(response.headers()['content-type']).toContain('application/pdf');
      expect((await response.body()).subarray(0, 5).toString()).toBe('%PDF-');
      expect([...assets].sort()).toEqual(['css', 'js']);
      for (const url of ['', 'not a URL', 'file:///etc/passwd', 'ftp://example.com', 'https://user:password@example.com']) {
        const invalid = await request.post(`/api/${engine}/convert-url`, { data: { url } });
        expect(invalid.status()).toBe(400);
      }
    }
  } finally {
    server.closeAllConnections();
    await new Promise((resolve) => server.close(resolve));
  }
});
