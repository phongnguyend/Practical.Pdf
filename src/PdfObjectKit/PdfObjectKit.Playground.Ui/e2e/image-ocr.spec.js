import { readFile } from 'node:fs/promises';
import { test, expect } from '@playwright/test';

function imagePdf(jpeg) {
  const drawing = 'q 400 0 0 120 20 20 cm /Im1 Do Q';
  const objects = ['<< /Type /Catalog /Pages 2 0 R >>', '<< /Type /Pages /Kids [3 0 R] /Count 1 >>',
    '<< /Type /Page /Parent 2 0 R /MediaBox [0 0 440 160] /Resources << /XObject << /Im1 4 0 R >> >> /Contents 5 0 R >>',
    `<< /Type /XObject /Subtype /Image /Width 800 /Height 240 /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode /Length ${jpeg.length} >>\nstream\n${jpeg.toString('latin1')}\nendstream`,
    `<< /Length ${drawing.length} >>\nstream\n${drawing}\nendstream`];
  let pdf = '%PDF-1.7\n';
  const offsets = [];
  for (const [index, object] of objects.entries()) {
    offsets.push(pdf.length);
    pdf += `${index + 1} 0 obj\n${object}\nendobj\n`;
  }
  const xref = pdf.length;
  pdf += `xref\n0 6\n0000000000 65535 f \n${offsets.map(offset => `${String(offset).padStart(10, '0')} 00000 n \n`).join('')}trailer\n<< /Size 6 /Root 1 0 R >>\nstartxref\n${xref}\n%%EOF\n`;
  return Buffer.from(pdf, 'latin1');
}

test('selected image OCR recognizes text locally and renders its canvas', async ({ page }) => {
  test.setTimeout(120000);
  await page.goto('/');
  const data = await page.evaluate(() => {
    const canvas = document.createElement('canvas');
    canvas.width = 800;
    canvas.height = 240;
    const context = canvas.getContext('2d');
    context.fillStyle = 'white';
    context.fillRect(0, 0, 800, 240);
    context.fillStyle = 'black';
    context.font = '64px Arial';
    context.fillText('Hello OCR', 40, 100);
    context.fillText('Selected image text', 40, 190);
    return canvas.toDataURL('image/jpeg').split(',')[1];
  });
  await page.getByLabel('Open PDF', { exact: true }).setInputFiles({ name: 'ocr.pdf', mimeType: 'application/pdf', buffer: imagePdf(Buffer.from(data, 'base64')) });
  await page.locator('.object').filter({ hasText: 'Image 1' }).click();
  await page.getByRole('button', { name: 'Extract text (OCR)', exact: true }).click();
  const dialog = page.getByRole('dialog', { name: 'Image OCR', exact: true });
  const canvas = dialog.getByRole('img', { name: 'Extracted text canvas' });
  await expect(canvas).toBeVisible({ timeout: 90000 });
  expect(await canvas.evaluate(element => {
    const pixels = element.getContext('2d').getImageData(0, 0, element.width, element.height).data;
    return pixels.some((value, index) => index % 4 === 0 && value < 100);
  })).toBe(true);
  await dialog.getByRole('button', { name: 'Text', exact: true }).click();
  await expect(dialog.getByLabel('Extracted text', { exact: true })).toHaveValue(/Hello OCR/);
  await expect(dialog.getByLabel('Extracted text', { exact: true })).toHaveValue(/Selected image text/);
  await dialog.getByRole('button', { name: 'Canvas', exact: true }).click();
  const line = dialog.getByRole('button', { name: 'OCR line 1', exact: true });
  await line.click();
  await expect(line).toHaveAttribute('aria-pressed', 'true');
  await dialog.getByLabel('Selected OCR text', { exact: true }).fill('Edited OCR text');
  const original = await line.boundingBox();
  await page.mouse.move(original.x + original.width / 2, original.y + original.height / 2);
  await page.mouse.down();
  await page.mouse.move(original.x + original.width / 2 + 40, original.y + original.height / 2 + 20, { steps: 5 });
  await page.mouse.up();
  const moved = await line.boundingBox();
  expect(moved.x - original.x).toBeCloseTo(40, 0);
  expect(moved.y - original.y).toBeCloseTo(20, 0);
  await page.mouse.down();
  await page.mouse.move(moved.x + moved.width / 2 + 20, moved.y + moved.height / 2 + 10, { steps: 3 });
  await page.keyboard.press('Escape');
  await page.mouse.up();
  await expect(dialog).toBeVisible();
  const cancelled = await line.boundingBox();
  expect(cancelled.x).toBeCloseTo(moved.x, 0);
  expect(cancelled.y).toBeCloseTo(moved.y, 0);
  await line.press('ArrowRight');
  expect((await line.boundingBox()).x).toBeGreaterThan(moved.x);
  await dialog.getByRole('button', { name: 'Text', exact: true }).click();
  await expect(dialog.getByLabel('Extracted text', { exact: true })).toHaveValue(/Edited OCR text/);
  await dialog.getByRole('button', { name: 'Maximize OCR' }).click();
  await expect(dialog).toHaveClass(/maximized/);
  await dialog.getByRole('button', { name: 'Canvas', exact: true }).click();
  await dialog.screenshot({ path: 'test-results/image-ocr.png' });
  await dialog.getByRole('button', { name: 'Close OCR' }).click();
  await expect(dialog).toHaveCount(0);
  await page.route('**/api/documents/image?*', route => route.fulfill({ status: 400, json: { detail: 'Image unavailable' } }));
  await page.getByRole('button', { name: 'Extract text (OCR)', exact: true }).click();
  await expect(dialog.getByRole('alert')).toContainText('Image unavailable');
  await expect(dialog.getByRole('button', { name: 'Retry OCR' })).toBeEnabled();
  await page.keyboard.press('Escape');
  await expect(dialog).toHaveCount(0);
});

for (const mode of ['searchable', 'replace']) {
  test(`apply corrected OCR as ${mode} text in the PDF`, async ({ page, request }) => {
    test.setTimeout(120000);
    await page.goto('/');
    const jpeg = await page.evaluate(() => {
      const canvas = document.createElement('canvas');
      canvas.width = 800;
      canvas.height = 240;
      const context = canvas.getContext('2d');
      context.fillStyle = 'white';
      context.fillRect(0, 0, 800, 240);
      context.fillStyle = 'black';
      context.font = '64px Arial';
      context.fillText('Hello OCR', 40, 100);
      return canvas.toDataURL('image/jpeg').split(',')[1];
    });
    await page.getByLabel('Open PDF', { exact: true }).setInputFiles({ name: 'apply.pdf', mimeType: 'application/pdf', buffer: imagePdf(Buffer.from(jpeg, 'base64')) });
    await page.locator('.object').filter({ hasText: 'Image 1' }).click();
    const pixelsBefore = await page.locator('.page > div > canvas').evaluate(canvas => canvas.toDataURL());
    await page.getByRole('button', { name: 'Extract text (OCR)', exact: true }).click();
    const dialog = page.getByRole('dialog', { name: 'Image OCR', exact: true });
    const line = dialog.getByRole('button', { name: 'OCR line 1', exact: true });
    await expect(line).toBeVisible({ timeout: 90000 });
    await line.click();
    await dialog.getByLabel('Selected OCR text', { exact: true }).fill('Hello Vi\u1ec7t');
    await line.press('Shift+ArrowRight');
    await dialog.getByLabel('OCR apply mode').selectOption(mode);
    const responsePromise = page.waitForResponse(response => response.url().endsWith('/api/documents/edit') && response.request().method() === 'POST');
    await dialog.getByRole('button', { name: 'Apply to PDF', exact: true }).click();
    const response = await responsePromise;
    expect(response.status()).toBe(200);
    await expect(dialog).toHaveCount(0, { timeout: 30000 });
    const [download] = await Promise.all([page.waitForEvent('download'), page.getByRole('button', { name: 'Download PDF', exact: true }).click()]);
    const bytes = await readFile(await download.path());
    expect(bytes.subarray(0, 5).toString()).toBe('%PDF-');
    if (mode === 'searchable') {
      await expect.poll(() => page.locator('.page > div > canvas').evaluate(canvas => canvas.toDataURL())).toBe(pixelsBefore);
    }
    const extracted = await page.evaluate(async base64 => {
      const { getDocument, GlobalWorkerOptions } = await import('/node_modules/pdfjs-dist/build/pdf.mjs');
      GlobalWorkerOptions.workerSrc = '/node_modules/pdfjs-dist/build/pdf.worker.mjs';
      const task = getDocument({ data: Uint8Array.from(atob(base64), value => value.charCodeAt(0)) });
      try {
        const document = await task.promise;
        const pdfPage = await document.getPage(1);
        const content = await pdfPage.getTextContent();
        return content.items.filter(item => 'str' in item).map(item => ({ text: item.str, transform: item.transform }));
      } catch (error) {
        throw new Error(error.message);
      } finally {
        await task.destroy();
      }
    }, bytes.toString('base64'));
    expect(extracted.map(item => item.text).join('')).toContain('Hello Vi\u1ec7t');
    const text = extracted.find(item => item.text.includes('Hello'));
    // The original image starts at x=20; the OCR line starts at x=40/800,
    // then moves 10/800, so its PDF origin is 20 + 400 * 50/800 = 45.
    expect(text.transform[4]).toBeGreaterThan(43);
    expect(text.transform[4]).toBeLessThan(49);
    const inspection = await request.post('/api/documents/inspect', { data: bytes, headers: { 'Content-Type': 'application/pdf' } });
    expect(inspection.ok()).toBe(true);
    const objects = (await inspection.json()).objects;
    expect(objects.filter(item => item.kind === 'image')).toHaveLength(mode === 'replace' ? 0 : 1);
  });
}
