import { toolbarAction } from './toolbar.js';
import { test, expect } from '@playwright/test';

function fixture() {
  const streams = ['BT /F1 16 Tf 40 700 Td (Sta) Tj (ff Code) Tj ET', 'BT /F1 16 Tf 40 700 Td (staff Code staffing) Tj ET'];
  const objects = [
    '<< /Type /Catalog /Pages 2 0 R >>',
    '<< /Type /Pages /Kids [3 0 R 6 0 R] /Count 2 >>',
    '<< /Type /Page /Parent 2 0 R /MediaBox [0 0 600 1000] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>',
    '<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>',
    `<< /Length ${streams[0].length} >>\nstream\n${streams[0]}\nendstream`,
    '<< /Type /Page /Parent 2 0 R /MediaBox [0 0 600 1000] /Resources << /Font << /F1 4 0 R >> >> /Contents 7 0 R >>',
    `<< /Length ${streams[1].length} >>\nstream\n${streams[1]}\nendstream`,
  ];
  let pdf = '%PDF-1.7\n';
  const offsets = [0];
  objects.forEach((object, index) => {
    offsets.push(pdf.length);
    pdf += `${index + 1} 0 obj\n${object}\nendobj\n`;
  });
  const start = pdf.length;
  pdf += `xref\n0 ${objects.length + 1}\n0000000000 65535 f \n`;
  pdf += offsets.slice(1).map(offset => `${String(offset).padStart(10, '0')} 00000 n \n`).join('');
  pdf += `trailer\n<< /Size ${objects.length + 1} /Root 1 0 R >>\nstartxref\n${start}\n%%EOF\n`;
  return Buffer.from(pdf);
}

test('find across runs/pages, options, highlights, workspace placement and edit invalidation', async ({ page }, testInfo) => {
  await page.goto('/');
  await page.getByLabel('Open PDF', { exact: true }).setInputFiles({ name: 'search.pdf', mimeType: 'application/pdf', buffer: fixture() });
  await expect(page.getByRole('button', { name: 'Find', exact: true })).toBeEnabled();
  await page.keyboard.press('Control+f');
  const workspace = page.getByRole('region', { name: 'PDF canvas', exact: true });
  const search = workspace.getByRole('region', { name: 'Find in PDF' });
  await expect(search).toBeVisible();
  await expect(search.getByRole('textbox')).toBeFocused();
  await expect(search.getByRole('textbox')).toHaveCSS('outline-style', 'none');
  await expect(search.locator('.pdf-search-input')).toHaveCSS('border-top-width', '1px');
  const surface = await workspace.boundingBox();
  const box = await search.boundingBox();
  expect(box.x + box.width).toBeLessThanOrEqual(surface.x + surface.width);
  expect(surface.x + surface.width - box.x - box.width).toBeLessThan(50);
  expect(box.y - surface.y).toBeLessThan(40);
  await search.getByRole('textbox', { name: 'Find text' }).fill('staff Code');
  await expect(search.getByRole('status')).toHaveText('1 of 2');
  const results = search.getByRole('list', { name: 'Search results' });
  await expect(results.getByRole('button')).toHaveCount(2);
  await expect(results.locator('mark').first()).toHaveText('Staff Code');
  await expect(results.getByRole('button').first()).toHaveAttribute('aria-current', 'true');
  await results.getByRole('button').nth(1).click();
  await expect(page.getByRole('combobox', { name: 'Page', exact: true })).toHaveValue('2');
  await results.getByRole('button').nth(1).press('ArrowUp');
  await expect(results.getByRole('button').first()).toBeFocused();
  await expect(page.getByRole('combobox', { name: 'Page', exact: true })).toHaveValue('1');
  await search.screenshot({ path: testInfo.outputPath('search-results.png') });
  await expect(workspace.locator('.active-search-match').first()).toBeVisible();
  await search.getByRole('button', { name: 'Next match', exact: true }).click();
  await expect(page.getByRole('combobox', { name: 'Page', exact: true })).toHaveValue('2');
  await expect(search.getByRole('status')).toHaveText('2 of 2');
  await search.getByRole('textbox').press('Shift+Enter');
  await expect(page.getByRole('combobox', { name: 'Page', exact: true })).toHaveValue('1');
  await search.getByLabel('Match case').check();
  await expect(search.getByRole('status')).toHaveText('1 of 1');
  await expect(page.getByRole('combobox', { name: 'Page', exact: true })).toHaveValue('2');
  await search.getByLabel('Match case').uncheck();
  await search.getByRole('textbox').fill('staff');
  await expect(search.getByRole('status')).toHaveText('1 of 3');
  await search.getByLabel('Whole words').check();
  await expect(search.getByRole('status')).toHaveText('1 of 2');
  await (await toolbarAction(page, 'Duplicate page')).click();
  await expect(search.getByRole('status')).toHaveText('1 of 3');
  await page.getByRole('button', { name: 'PdfObjectKit (experimental)', exact: true }).click();
  await expect(workspace.locator('.active-search-match').first()).toBeVisible();
  await workspace.evaluate(element => { element.scrollTop = 250; });
  const scrolled = await search.boundingBox();
  expect(scrolled.y).toBeGreaterThanOrEqual(surface.y);
  expect(scrolled.y - surface.y).toBeLessThan(40);
  await search.getByRole('textbox').fill('not present');
  await expect(search.getByRole('status')).toHaveText('No matches');
  await expect(workspace.locator('.search-highlights polygon')).toHaveCount(0);
  await search.getByRole('textbox').press('Escape');
  await expect(search).toHaveCount(0);
  await expect(workspace.locator('.search-highlights')).toHaveCount(0);
});

test('search endpoint returns geometry and rejects oversized queries', async ({ request }) => {
  const body = fixture();
  const response = await request.post('/api/documents/search?query=Staff%20Code&matchCase=true', { headers: { 'Content-Type': 'application/pdf' }, data: body });
  expect(response.ok()).toBe(true);
  const result = await response.json();
  expect(result.matches).toHaveLength(1);
  expect(result.matches[0].pageNumber).toBe(1);
  expect(result.matches[0].regions[0].corners).toHaveLength(4);
  const invalid = await request.post('/api/documents/search?query=' + 'a'.repeat(257), { headers: { 'Content-Type': 'application/pdf' }, data: body });
  expect(invalid.status()).toBe(422);
});
