import { test, expect } from '@playwright/test';
import { toolbarAction } from './toolbar.js';

function fixture(rotation) {
  const content = 'BT /F1 12 Tf 1 0 0 1 20 250 Tm (Name) Tj 1 0 0 1 160 250 Tm (City) Tj 1 0 0 1 20 230 Tm (Alice) Tj 1 0 0 1 160 230 Tm (Paris) Tj 1 0 0 1 20 100 Tm (Outside) Tj ET';
  const objects = ['<< /Type /Catalog /Pages 2 0 R >>', '<< /Type /Pages /Count 1 /Kids [3 0 R] >>',
    `<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 300] /Rotate ${rotation} /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>`,
    '<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>', `<< /Length ${content.length} >>\nstream\n${content}\nendstream`];
  let pdf = '%PDF-1.7\n';
  const offsets = [];
  objects.forEach((object, index) => {
    offsets.push(pdf.length);
    pdf += `${index + 1} 0 obj\n${object}\nendobj\n`;
  });
  const xref = pdf.length;
  pdf += `xref\n0 6\n0000000000 65535 f \n${offsets.map(offset => `${String(offset).padStart(10, '0')} 00000 n \n`).join('')}trailer\n<< /Size 6 /Root 1 0 R >>\nstartxref\n${xref}\n%%EOF\n`;
  return Buffer.from(pdf);
}

for (const rotation of [0, 90]) {
  test(`draw and reapply table hints at page rotation ${rotation}`, async ({ page }) => {
    await page.goto('/');
    await page.getByLabel('Open PDF', { exact: true }).setInputFiles({ name: 'table.pdf', mimeType: 'application/pdf', buffer: fixture(rotation) });
    await (await toolbarAction(page, 'View Markdown')).click();
    const dialog = page.getByRole('dialog', { name: 'Markdown', exact: true });
    await expect(dialog.locator('.markdown-preview')).toContainText('Alice');
    await expect(dialog.locator('.markdown-preview table')).toHaveCount(0);
    await dialog.getByRole('button', { name: 'Options', exact: true }).click();
    await dialog.locator('.table-hints > summary').click();
    const image = dialog.getByRole('img', { name: 'Page 1 for table hints' });
    await expect(image).toBeVisible();
    await image.scrollIntoViewIfNeeded();
    const bounds = await image.boundingBox();
    const region = rotation === 0 ? [10 / 300, 30 / 300, 240 / 300, 80 / 300] : [220 / 300, 10 / 300, 270 / 300, 240 / 300];
    await page.mouse.move(bounds.x + bounds.width * region[0], bounds.y + bounds.height * region[1]);
    await page.mouse.down();
    await page.mouse.move(bounds.x + bounds.width * region[2], bounds.y + bounds.height * region[3], { steps: 5 });
    await page.mouse.up();
    await expect(dialog.locator('.table-hint-box')).toHaveCount(1);
    await dialog.getByRole('button', { name: 'Reapply conversion' }).click();
    await expect(dialog.locator('.markdown-preview table')).toHaveCount(1);
    await expect(dialog.locator('.markdown-preview th')).toHaveText(['Name', 'City']);
    await expect(dialog.locator('.markdown-preview td')).toHaveText(['Alice', 'Paris']);
    await expect(dialog.locator('.markdown-preview')).toContainText('Outside');
    await dialog.getByRole('button', { name: 'Source', exact: true }).click();
    expect((await dialog.locator('.markdown-source').innerText()).split('Alice')).toHaveLength(2);
    await dialog.screenshot({ path: `test-results/table-hints-${rotation}.png` });
    await dialog.getByRole('button', { name: 'Remove table hint 1' }).click();
    await dialog.getByRole('button', { name: 'Reapply conversion' }).click();
    await dialog.getByRole('button', { name: 'Preview', exact: true }).click();
    await expect(dialog.locator('.markdown-preview table')).toHaveCount(0);
    await expect(dialog.locator('.markdown-preview')).toContainText('Alice');
  });
}

test('table hints API validates overlap, pages and empty regions', async ({ request }) => {
  const hint = { pageNumber: 1, x: 10, y: 220, width: 230, height: 50 };
  for (const tableHints of [[hint, hint], [{ ...hint, pageNumber: 2 }], [{ ...hint, y: 0, height: 10 }], null]) {
    const response = await request.post('/api/documents/markdown?' + new URLSearchParams({ options: JSON.stringify({ tableHints }) }), { data: fixture(0), headers: { 'Content-Type': 'application/pdf' } });
    expect(response.status(), await response.text()).toBe(422);
  }
});
