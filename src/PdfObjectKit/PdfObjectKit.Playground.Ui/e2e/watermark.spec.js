import { test, expect } from '@playwright/test';
function source() {
  const objects = ['<< /Type /Catalog /Pages 2 0 R >>', '<< /Type /Pages /Count 2 /Kids [4 0 R 5 0 R] >>', '<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>', '<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 400] /Resources << /Font << /F1 3 0 R >> >> /Contents 6 0 R >>', '<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 400] /Rotate 90 /Resources << /Font << /F1 3 0 R >> >> /Contents 6 0 R >>'];
  const content = 'BT /F1 12 Tf 20 350 Td (Original) Tj ET 0 0 10 10 re W n';
  objects.push(`<< /Length ${content.length} >>\nstream\n${content}\nendstream`);
  let pdf = '%PDF-1.7\n';
  const offsets = [0];
  objects.forEach((object, i) => { offsets.push(pdf.length); pdf += `${i + 1} 0 obj\n${object}\nendobj\n`; });
  const xref = pdf.length;
  pdf += `xref\n0 ${objects.length + 1}\n0000000000 65535 f \n` + offsets.slice(1).map(offset => `${String(offset).padStart(10, '0')} 00000 n \n`).join('');
  pdf += `trailer\n<< /Size ${objects.length + 1} /Root 1 0 R >>\nstartxref\n${xref}\n%%EOF\n`;
  return { name: 'watermark.pdf', mimeType: 'application/pdf', buffer: Buffer.from(pdf) };
}

test('watermark preview, add, update and remove survive PDF round trips', async ({ page }) => {
  await page.goto('/');
  await page.getByLabel('Open PDF', { exact: true }).setInputFiles(source());
  await expect(page.locator('.object')).toContainText('Original');
  await page.getByRole('button', { name: 'Watermark', exact: true }).click();
  const dialog = page.getByRole('dialog');
  await dialog.getByLabel('Watermark text').fill('DRAFT');
  await dialog.getByLabel('Font size', { exact: true }).fill('30');
  await dialog.getByLabel('Angle', { exact: true }).fill('45');
  await dialog.getByLabel('Color', { exact: true }).fill('#ff0000');
  await dialog.getByRole('button', { name: 'Preview', exact: true }).click();
  await expect(dialog.getByLabel('Watermark preview')).toBeVisible();
  await expect(page.getByRole('tab')).toHaveCount(1);
  await dialog.getByRole('button', { name: 'Add watermark', exact: true }).click();
  await expect(page.locator('.object').filter({ hasText: 'DRAFT' })).toHaveCount(0);
  await expect(page.getByRole('tab')).toHaveAccessibleName('watermark.pdf Modified');
  await expect(page.getByRole('button', { name: 'Watermark', exact: true })).toBeEnabled();
  const originalText = page.locator('.object').filter({ hasText: 'Original' });
  await originalText.click();
  await expect(originalText).toHaveAttribute('aria-pressed', 'true');
  const canvas = page.locator('.page > div > canvas');
  await expect(page.getByRole('button', { name: 'Watermark', exact: true })).toBeEnabled();
  const redPixels = await canvas.evaluate(c => {
    const data = c.getContext('2d').getImageData(0, 0, c.width, c.height).data;
    let count = 0;
    for (let i = 0; i < data.length; i += 4) {
      if (data[i] > data[i + 1] + 10) {
        count++;
      }
    }
    return count;
  });
  expect(redPixels).toBeGreaterThan(30);
  await page.getByLabel('Page', { exact: true }).selectOption('2');
  await expect(page.getByLabel('Page', { exact: true })).toHaveValue('2');
  await expect(page.getByRole('button', { name: 'Watermark', exact: true })).toBeEnabled();
  expect(await canvas.evaluate(c => {
    const pixels = c.getContext('2d').getImageData(Math.floor(c.width / 4), Math.floor(c.height / 4), Math.floor(c.width / 2), Math.floor(c.height / 2)).data;
    let red = 0;
    for (let i = 0; i < pixels.length; i += 4) {
      if (pixels[i] > pixels[i + 1] + 10) {
        red++;
      }
    }
    return red;
  })).toBeGreaterThan(30);
  await page.getByRole('button', { name: 'Watermark', exact: true }).click();
  await dialog.getByLabel('Watermark to edit').selectOption({ label: 'DRAFT (pages 1, 2)' });
  await dialog.getByLabel('Watermark text').fill('FINAL');
  await dialog.getByLabel('Watermark pages', { exact: true }).selectOption('current');
  await dialog.getByRole('button', { name: 'Update watermark', exact: true }).click();
  await expect(page.locator('.object').filter({ hasText: 'DRAFT' })).toHaveCount(0);
  await page.getByRole('button', { name: 'Watermark', exact: true }).click();
  await dialog.getByLabel('Watermark to edit').selectOption({ label: 'FINAL (pages 2)' });
  await dialog.getByRole('button', { name: 'Remove watermark', exact: true }).click();
  await page.getByRole('button', { name: 'Watermark', exact: true }).click();
  await expect(dialog.getByLabel('Watermark to edit').locator('option')).toHaveCount(1);
  await dialog.getByRole('button', { name: 'Cancel', exact: true }).click();
  await expect(page.locator('.object')).toContainText('Original');
});
