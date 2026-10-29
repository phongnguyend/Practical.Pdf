import { test, expect } from '@playwright/test';
import { readFile } from 'node:fs/promises';
import { getDocument } from 'pdfjs-dist/legacy/build/pdf.mjs';

function fixture() {
  const content = 'BT /F1 16 Tf 40 700 Td (Compression keeps text) Tj ET\n' + ' '.repeat(15000);
  const objects = [
    '<< /Type /Catalog /Pages 2 0 R >>',
    '<< /Type /Pages /Kids [3 0 R] /Count 1 >>',
    '<< /Type /Page /Parent 2 0 R /MediaBox [0 0 600 800] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>',
    '<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>',
    `<< /Length ${content.length} >>\nstream\n${content}\nendstream`,
    `<< /Length 10000 >>\nstream\n${'x'.repeat(10000)}\nendstream`,
  ];
  let pdf = '%PDF-1.7\n';
  const offsets = [0];
  objects.forEach((object, index) => {
    offsets.push(pdf.length);
    pdf += `${index + 1} 0 obj\n${object}\nendobj\n`;
  });
  const xref = pdf.length;
  pdf += `xref\n0 ${objects.length + 1}\n0000000000 65535 f \n`;
  pdf += offsets.slice(1).map(offset => `${String(offset).padStart(10, '0')} 00000 n \n`).join('');
  pdf += `trailer\n<< /Size ${objects.length + 1} /Root 1 0 R >>\nstartxref\n${xref}\n%%EOF\n`;
  return Buffer.from(pdf);
}

test('compress compares sizes, downloads a readable PDF and opens a separate tab', async ({ page }) => {
  const source = fixture();
  await page.goto('/');
  await page.getByLabel('Open PDF', { exact: true }).setInputFiles({ name: 'large.pdf', mimeType: 'application/pdf', buffer: source });
  await page.getByRole('button', { name: 'Compress PDF', exact: true }).click();
  const dialog = page.getByRole('dialog');
  await dialog.getByRole('button', { name: 'Compress', exact: true }).click();
  await expect(dialog.getByRole('region', { name: 'Compression result' })).toContainText('% smaller');
  const downloadEvent = page.waitForEvent('download');
  await dialog.getByRole('button', { name: 'Download', exact: true }).click();
  const downloaded = await downloadEvent;
  const bytes = await readFile(await downloaded.path());
  expect(bytes.length).toBeLessThan(source.length / 2);
  const loading = getDocument({ data: new Uint8Array(bytes) });
  const pdf = await loading.promise;
  expect(pdf.numPages).toBe(1);
  expect((await (await pdf.getPage(1)).getTextContent()).items.map(item => item.str).join('')).toBe('Compression keeps text');
  await loading.destroy();
  await dialog.getByRole('button', { name: 'Open in new tab' }).click();
  await expect(page.getByRole('tab', { name: 'large.pdf', exact: true })).toBeVisible();
  await expect(page.getByRole('tab', { name: 'large-compressed.pdf', exact: true })).toHaveAttribute('aria-selected', 'true');
  await expect(page.locator('.object').filter({ hasText: 'Compression keeps text' })).toBeVisible();
  await page.getByRole('button', { name: 'Compress PDF', exact: true }).click();
  await dialog.getByRole('button', { name: 'Compress', exact: true }).click();
  await expect(dialog).toContainText('Already compact');
});

test('compression rejects invalid input without creating a result', async ({ request }) => {
  const response = await request.post('/api/documents/compress', { multipart: { file: { name: 'bad.pdf', mimeType: 'application/pdf', buffer: Buffer.from('not a PDF') } } });
  expect(response.status()).toBe(422);
});
