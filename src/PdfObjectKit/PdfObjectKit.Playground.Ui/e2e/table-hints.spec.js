import { test, expect } from '@playwright/test';
import { toolbarAction } from './toolbar.js';

function fixture(rotation, text) {
  const content = text ?? 'BT /F1 12 Tf 1 0 0 1 20 250 Tm (Name) Tj 1 0 0 1 160 250 Tm (City) Tj 1 0 0 1 20 230 Tm (Alice) Tj 1 0 0 1 160 230 Tm (Paris) Tj 1 0 0 1 20 100 Tm (Outside) Tj ET';
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
    await expect(dialog.getByRole('button', { name: 'Move table hint 1', exact: true })).toBeVisible();
    await dialog.getByRole('button', { name: 'Reapply conversion' }).click();
    await expect(dialog.locator('.markdown-preview table')).toHaveCount(1);
    await expect(dialog.locator('.markdown-preview th')).toHaveText(['Name', 'City']);
    await expect(dialog.locator('.markdown-preview td')).toHaveText(['Alice', 'Paris']);
    await expect(dialog.locator('.markdown-preview')).toContainText('Outside');
    await dialog.getByRole('button', { name: 'Edit table 1', exact: true }).click();
    const headerToggle = dialog.locator('.table-column-tools').getByRole('checkbox', { name: 'First row is header', exact: true });
    await expect(headerToggle).toBeChecked();
    await headerToggle.uncheck();
    const headerConversion = page.waitForResponse(response => response.url().includes('/markdown') && response.request().method() === 'POST');
    await dialog.getByRole('button', { name: 'Reapply conversion' }).click();
    const headerHint = (await (await headerConversion).json()).options.tableHints[0];
    expect(headerHint.firstRowIsHeader).toBe(false);
    expect(headerHint.columnBoundaries ?? null).toBeNull();
    await expect(dialog.locator('.markdown-preview td')).toHaveText(['Name', 'City', 'Alice', 'Paris']);
    await headerToggle.check();
    async function clickColumn(x) {
      await image.scrollIntoViewIfNeeded();
      const box = await image.boundingBox();
      const point = rotation === 0 ? [x / 300, 55 / 300] : [245 / 300, x / 300];
      await page.mouse.click(box.x + box.width * point[0], box.y + box.height * point[1]);
    }
    await clickColumn(100);
    const separator = dialog.getByRole('button', { name: 'Table 1 column separator 1', exact: true });
    await expect(separator).toBeVisible();
    const line = await separator.boundingBox();
    const sheet = await image.boundingBox();
    await page.mouse.move(line.x + line.width / 2, line.y + line.height / 2);
    await page.mouse.down();
    await page.mouse.move(line.x + line.width / 2 + (rotation === 0 ? sheet.width * 20 / 300 : 0), line.y + line.height / 2 + (rotation === 90 ? sheet.height * 20 / 300 : 0), { steps: 5 });
    await page.mouse.up();
    await expect.poll(async () => Number(await dialog.getByLabel('Separator X', { exact: true }).inputValue())).toBeCloseTo(120, 1);
    await dialog.getByLabel('Separator X', { exact: true }).fill('120');
    await expect(dialog.getByLabel('Separator X', { exact: true })).toHaveValue('120');
    await clickColumn(140);
    await dialog.getByRole('button', { name: 'Reapply conversion' }).click();
    await expect(dialog.locator('.markdown-preview th')).toHaveText(['Name', '', 'City']);
    await expect(dialog.locator('.markdown-preview td')).toHaveText(['Alice', '', 'Paris']);
    await dialog.getByRole('button', { name: 'Done editing table', exact: true }).click();
    const move = dialog.getByRole('button', { name: 'Move table hint 1', exact: true });
    await move.scrollIntoViewIfNeeded();
    const originalBounds = await move.boundingBox();
    await page.mouse.move(originalBounds.x + originalBounds.width / 2, originalBounds.y + originalBounds.height / 2);
    await page.mouse.down();
    await page.mouse.move(originalBounds.x + originalBounds.width / 2 + 20, originalBounds.y + originalBounds.height / 2 + 20, { steps: 4 });
    await page.keyboard.press('Escape');
    await page.mouse.up();
    await expect(dialog).toBeVisible();
    const cancelledBounds = await move.boundingBox();
    expect(cancelledBounds.x).toBeCloseTo(originalBounds.x, 1);
    expect(cancelledBounds.y).toBeCloseTo(originalBounds.y, 1);
    await page.mouse.move(originalBounds.x + originalBounds.width / 2, originalBounds.y + originalBounds.height / 2);
    await page.mouse.down();
    await page.mouse.move(originalBounds.x + originalBounds.width / 2 + 6, originalBounds.y + originalBounds.height / 2 + 6, { steps: 4 });
    await page.mouse.up();
    const movedBounds = await move.boundingBox();
    expect(movedBounds.x - originalBounds.x).toBeCloseTo(6, 1);
    expect(movedBounds.y - originalBounds.y).toBeCloseTo(6, 1);
    const handle = dialog.getByRole('button', { name: 'Resize table hint 1 se', exact: true });
    const corner = await handle.boundingBox();
    await page.mouse.move(corner.x + corner.width / 2, corner.y + corner.height / 2);
    await page.mouse.down();
    await page.mouse.move(corner.x + corner.width / 2 + 12, corner.y + corner.height / 2 + 12, { steps: 4 });
    await page.mouse.up();
    const resizedBounds = await move.boundingBox();
    expect(resizedBounds.width - movedBounds.width).toBeCloseTo(12, 1);
    expect(resizedBounds.height - movedBounds.height).toBeCloseTo(12, 1);
    const converted = page.waitForResponse(response => response.url().includes('/markdown?'));
    await dialog.getByRole('button', { name: 'Reapply conversion' }).click();
    const updated = (await (await converted).json()).options.tableHints[0];
    // Separators retain their relative positions as the rectangle moves and scales.
    expect((updated.columnBoundaries[0] - updated.x) / updated.width).toBeCloseTo((120 - 10) / 230, 3);
    expect((updated.columnBoundaries[1] - updated.x) / updated.width).toBeCloseTo((140 - 10) / 230, 3);
    await expect(dialog.locator('.markdown-preview th')).toHaveText(['Name', '', 'City']);
    await dialog.getByRole('button', { name: 'Edit table 1', exact: true }).click();
    await dialog.getByRole('button', { name: 'Table 1 column separator 2', exact: true }).click();
    await dialog.locator('.table-hint-scroll').screenshot({ path: `test-results/table-columns-${rotation}.png` });
    await dialog.getByRole('button', { name: 'Remove separator', exact: true }).click();
    await dialog.getByRole('button', { name: 'Reapply conversion' }).click();
    await expect(dialog.locator('.markdown-preview th')).toHaveText(['Name', 'City']);
    await dialog.getByRole('button', { name: 'Use automatic columns', exact: true }).click();
    await dialog.getByRole('button', { name: 'Reapply conversion' }).click();
    await expect(dialog.locator('.markdown-preview th')).toHaveText(['Name', 'City']);
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
  for (const tableHints of [[hint, hint], [{ ...hint, pageNumber: 2 }], [{ ...hint, y: 0, height: 10 }], [{ ...hint, columnBoundaries: [140, 100] }], [{ ...hint, columnBoundaries: [10] }], null]) {
    const response = await request.post('/api/documents/markdown?' + new URLSearchParams({ options: JSON.stringify({ tableHints }) }), { data: fixture(0), headers: { 'Content-Type': 'application/pdf' } });
    expect(response.status(), await response.text()).toBe(422);
  }
});


test('manual columns keep closely spaced amount and currency text separate', async ({ request }) => {
  const content = 'BT /F1 12 Tf 1 0 0 1 20 250 Tm (Refund) Tj 1 0 0 1 160 250 Tm (4,493,336) Tj 1 0 0 1 218 250 Tm (VND) Tj 1 0 0 1 20 230 Tm (Payable) Tj 1 0 0 1 206 230 Tm (-) Tj 1 0 0 1 218 230 Tm (VND) Tj ET';
  const options = { tableHints: [{ pageNumber: 1, x: 10, y: 220, width: 250, height: 50, firstRowIsHeader: false, columnBoundaries: [140, 216] }] };
  const response = await request.post('/api/documents/markdown?' + new URLSearchParams({ options: JSON.stringify(options) }), { data: fixture(0, content), headers: { 'Content-Type': 'application/pdf' } });
  expect(response.ok()).toBe(true);
  const result = await response.json();
  expect(result.markdown).toContain('| Refund | 4,493,336 | VND |');
  expect(result.markdown).toContain('| Payable | - | VND |');
  expect(result.warnings.some(warning => warning.includes('crosses a text fragment'))).toBe(false);
});

test('moving a table hint onto another region restores its original bounds', async ({ page }) => {
  await page.goto('/');
  await page.getByLabel('Open PDF', { exact: true }).setInputFiles({ name: 'table.pdf', mimeType: 'application/pdf', buffer: fixture(0) });
  await (await toolbarAction(page, 'View Markdown')).click();
  const dialog = page.getByRole('dialog', { name: 'Markdown', exact: true });
  await expect(dialog.locator('.markdown-preview')).toBeVisible();
  await dialog.getByRole('button', { name: 'Options', exact: true }).click();
  await dialog.locator('.table-hints > summary').click();
  const image = dialog.getByRole('img', { name: 'Page 1 for table hints' });
  await expect(image).toBeVisible();
  await image.scrollIntoViewIfNeeded();
  const sheet = await image.boundingBox();
  for (const top of [30, 180]) {
    await page.mouse.move(sheet.x + sheet.width / 30, sheet.y + sheet.height * top / 300);
    await page.mouse.down();
    await page.mouse.move(sheet.x + sheet.width * .8, sheet.y + sheet.height * (top + 50) / 300, { steps: 4 });
    await page.mouse.up();
  }
  const first = await dialog.getByRole('button', { name: 'Move table hint 1', exact: true }).boundingBox();
  const second = dialog.getByRole('button', { name: 'Move table hint 2', exact: true });
  const before = await second.boundingBox();
  await page.mouse.move(before.x + before.width / 2, before.y + before.height / 2);
  await page.mouse.down();
  await page.mouse.move(first.x + first.width / 2, first.y + first.height / 2, { steps: 4 });
  await page.mouse.up();
  await expect(dialog.getByRole('alert')).toContainText('previous position has been restored');
  const after = await second.boundingBox();
  const currentSheet = await image.boundingBox();
  expect(after.x - currentSheet.x).toBeCloseTo(before.x - sheet.x, 1);
  expect(after.y - currentSheet.y).toBeCloseTo(before.y - sheet.y, 1);
});
