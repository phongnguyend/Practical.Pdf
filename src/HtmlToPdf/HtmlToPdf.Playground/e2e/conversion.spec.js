import { expect, test } from '@playwright/test';

const html = { name: 'invoice.html', mimeType: 'text/html', buffer: Buffer.from('<!doctype html><h1>Invoice</h1>') };

test('validates type, empty files, size, and multiple drops', async ({ page }) => {
  await page.goto('/');
  await expect(page.getByRole('button', { name: 'Convert to PDF' })).toBeDisabled();
  for (const [file, message] of [
    [{ ...html, name: 'file.txt' }, 'extension'],
    [{ ...html, buffer: Buffer.alloc(0) }, 'empty'],
    [{ ...html, buffer: Buffer.alloc(10 * 1024 * 1024 + 1) }, '10 MB'],
  ]) {
    await page.getByLabel('HTML file').setInputFiles(file);
    await expect(page.getByRole('alert')).toContainText(message);
    await expect(page.getByRole('button', { name: 'Convert to PDF' })).toBeDisabled();
  }
  const files = await page.evaluateHandle(() => {
    const transfer = new DataTransfer();
    transfer.items.add(new File(['one'], 'one.html'));
    transfer.items.add(new File(['two'], 'two.html'));
    return transfer;
  });
  await page.locator('.dropzone').dispatchEvent('drop', { dataTransfer: files });
  await expect(page.getByRole('alert')).toContainText('one HTML file');
});

for (const engine of ['chrome', 'playwright', 'puppeteer']) {
  test(`uploads through ${engine} and downloads the PDF`, async ({ page }) => {
    await page.route(`**/api/${engine}/convert`, async (route) => {
      expect(route.request().method()).toBe('POST');
      expect(route.request().headers()['content-type']).toContain('multipart/form-data');
      expect(route.request().postDataBuffer().toString()).toContain('name="file"; filename="invoice.html"');
      await route.fulfill({ contentType: 'application/pdf', body: '%PDF-1.4\n%%EOF' });
    });
    await page.goto('/');
    await page.getByLabel('HTML file').setInputFiles(html);
    await page.locator('input[value="chrome"]').uncheck();
    await page.locator(`input[value="${engine}"]`).check();
    await page.getByRole('button', { name: 'Convert to PDF' }).click();
    await expect(page.getByRole('status')).toContainText('Your PDF is ready');
    const download = page.waitForEvent('download');
    await page.getByRole('link', { name: 'Download PDF' }).click();
    expect((await download).suggestedFilename()).toBe('invoice.pdf');
    await page.getByLabel('HTML file').setInputFiles({ ...html, name: 'replacement.htm' });
    await expect(page.getByRole('link', { name: 'Download PDF' })).toHaveCount(0);
  });
}

test('accepts a dropped file and recovers from API errors', async ({ page }) => {
  await page.route('**/api/chrome/convert', (route) => route.fulfill({ status: 500, contentType: 'application/problem+json', body: JSON.stringify({ detail: 'Chrome is unavailable.' }) }));
  await page.goto('/');
  const files = await page.evaluateHandle(() => {
    const transfer = new DataTransfer();
    transfer.items.add(new File(['<h1>Invoice</h1>'], 'invoice.HTML', { type: 'text/html' }));
    return transfer;
  });
  await page.locator('.dropzone').dispatchEvent('drop', { dataTransfer: files });
  await page.getByRole('button', { name: 'Convert to PDF' }).click();
  await expect(page.getByRole('alert')).toContainText('Chrome is unavailable.');
  await expect(page.getByRole('button', { name: 'Convert to PDF' })).toBeEnabled();
  await page.route('**/api/chrome/convert', (route) => route.fulfill({ contentType: 'text/html', body: '<html>Wrong service</html>' }));
  await page.getByRole('button', { name: 'Convert to PDF' }).click();
  await expect(page.getByRole('alert')).toContainText('did not return a PDF');
});

test('multiple engines keep successful PDFs when another engine fails', async ({ page }) => {
  let finishPlaywright;
  const pending = new Promise((resolve) => {
    finishPlaywright = resolve;
  });
  await page.route('**/api/chrome/convert', (route) => route.fulfill({ contentType: 'application/pdf', body: '%PDF-1.4\nChrome\n%%EOF' }));
  await page.route('**/api/playwright/convert', async (route) => {
    await pending;
    await route.fulfill({ contentType: 'application/pdf', body: '%PDF-1.4\nPlaywright\n%%EOF' });
  });
  await page.route('**/api/puppeteer/convert', (route) => route.fulfill({ status: 503, contentType: 'application/problem+json', body: JSON.stringify({ detail: 'Engine unavailable' }) }));
  await page.goto('/');
  await page.getByLabel('HTML file').setInputFiles(html);
  await page.locator('input[value="chrome"]').uncheck();
  await expect(page.getByRole('button', { name: 'Convert to PDF' })).toBeDisabled();
  for (const engine of ['chrome', 'playwright', 'puppeteer']) {
    await page.locator(`input[value="${engine}"]`).check();
  }
  await page.getByRole('button', { name: 'Convert to PDF' }).click();
  const chrome = page.getByRole('region', { name: 'Chrome result', exact: true });
  const playwright = page.getByRole('region', { name: 'Playwright result', exact: true });
  await expect(chrome.getByRole('link', { name: 'Download PDF' })).toBeVisible();
  await expect(playwright).toContainText('Converting');
  await expect(page.getByRole('alert')).toContainText('PuppeteerSharp: Engine unavailable');
  const chromeUrl = await chrome.getByRole('link', { name: 'Download PDF' }).getAttribute('href');
  finishPlaywright();
  await expect(playwright.getByRole('link', { name: 'Download PDF' })).toHaveAttribute('download', 'invoice.playwright.pdf');
  await expect(chrome.getByRole('link', { name: 'Download PDF' })).toHaveAttribute('download', 'invoice.chrome.pdf');
  await expect(chrome.getByRole('link', { name: 'View PDF' })).toHaveAttribute('target', '_blank');
  expect(await page.evaluate(async (url) => (await (await fetch(url)).text()).startsWith('%PDF-'), chromeUrl)).toBeTruthy();
  await expect(page.getByRole('button', { name: 'Convert to PDF' })).toBeEnabled();
  await page.getByLabel('HTML file').setInputFiles({ ...html, name: 'new.html' });
  await expect(page.getByRole('link', { name: 'Download PDF' })).toHaveCount(0);
});

test('desktop and mobile layouts', async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 1000 });
  await page.goto('/');
  await page.screenshot({ path: 'test-results/desktop.png', fullPage: true });
  await page.setViewportSize({ width: 390, height: 844 });
  await expect(page.getByRole('button', { name: 'Browse files' })).toBeVisible();
  expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBe(390);
  await page.screenshot({ path: 'test-results/mobile.png', fullPage: true });
});

test('real Chrome conversion through HtmlToPdf.Proxy', async ({ page }) => {
  test.skip(process.env.HTML_TO_PDF_LIVE_TEST !== '1', 'Requires proxy and Chrome API.');
  await page.goto('/');
  await page.getByLabel('HTML file').setInputFiles(html);
  const responsePromise = page.waitForResponse('**/api/chrome/convert');
  await page.getByRole('button', { name: 'Convert to PDF' }).click();
  const response = await responsePromise;
  expect(response.ok()).toBeTruthy();
  await expect(page.getByRole('link', { name: 'Download PDF' })).toBeVisible();
  const signature = await page.getByRole('link', { name: 'Download PDF' }).evaluate(async (link) => {
    const pdf = await fetch(link.href);
    const blob = await pdf.blob();
    return blob.slice(0, 5).text();
  });
  expect(signature).toBe('%PDF-');
});
