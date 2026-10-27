import { test, expect } from '@playwright/test';

function pdfFile(name, texts, tagged = false) {
  const objects = [
    '<< /Type /Catalog /Pages 2 0 R >>',
    '<< /Type /Pages /Count ' + texts.length + ' /Kids [' + texts.map((_, i) => (5 + i * 2) + ' 0 R').join(' ') + '] >>',
    '<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>',
  ];
  for (const text of texts) {
    let content = 'BT /F1 12 Tf 20 250 Td (' + text + ') Tj ET';
    if (tagged) {
      content = '/P << /MCID 0 >> BDC ' + content + ' EMC';
    }
    const stream = objects.length + 1;
    objects.push('<< /Length ' + content.length + ' >>\nstream\n' + content + '\nendstream');
    objects.push('<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 300] /Resources << /Font << /F1 3 0 R >> >> /Contents ' + stream + ' 0 R >>');
  }
  if (tagged) {
    const root = objects.length + 1;
    const elements = texts.map((_, i) => root + i + 1);
    objects[0] = '<< /Type /Catalog /Pages 2 0 R /MarkInfo << /Marked true >> /StructTreeRoot ' + root + ' 0 R >>';
    objects.push('<< /Type /StructTreeRoot /K [' + elements.map(id => id + ' 0 R').join(' ') + '] /ParentTree << /Nums [' + elements.map((id, i) => i + ' [' + id + ' 0 R]').join(' ') + '] >> >>');
    for (let i = 0; i < texts.length; i++) {
      objects[4 + i * 2] = objects[4 + i * 2].slice(0, -2) + ' /StructParents ' + i + ' >>';
      objects.push('<< /Type /StructElem /S /P /P ' + root + ' 0 R /Pg ' + (5 + i * 2) + ' 0 R /K 0 >>');
    }
  }
  let pdf = '%PDF-1.7\n';
  const offsets = [0];
  objects.forEach((value, index) => {
    offsets.push(Buffer.byteLength(pdf));
    pdf += (index + 1) + ' 0 obj\n' + value + '\nendobj\n';
  });
  const xref = Buffer.byteLength(pdf);
  pdf += 'xref\n0 ' + (objects.length + 1) + '\n0000000000 65535 f \n';
  pdf += offsets.slice(1).map(offset => String(offset).padStart(10, '0') + ' 00000 n \n').join('');
  pdf += 'trailer\n<< /Size ' + (objects.length + 1) + ' /Root 1 0 R >>\nstartxref\n' + xref + '\n%%EOF\n';
  return { name, mimeType: 'application/pdf', buffer: Buffer.from(pdf) };
}

test('browser-style document tabs preserve edits and view state', async ({ page }) => {
  await page.goto('/');
  await page.getByLabel('Open PDF', { exact: true }).setInputFiles([pdfFile('first.pdf', ['First']), pdfFile('second.pdf', ['Second'])]);
  await expect(page.getByRole('tab')).toHaveCount(2);
  await expect(page.locator('.object')).toContainText('First');
  await page.getByLabel('Zoom', { exact: true }).selectOption('1.25');
  await page.getByRole('button', { name: 'Text', exact: true }).click();
  await page.getByLabel('Text to insert').fill('Saved in tab');
  await page.getByRole('button', { name: 'Insert object', exact: true }).click();
  await expect(page.locator('.object').filter({ hasText: 'Saved in tab' })).toHaveCount(1);
  await page.getByRole('tab', { name: 'second.pdf', exact: true }).click();
  await expect(page.locator('.object')).toContainText('Second');
  await expect(page.getByLabel('Zoom', { exact: true })).toHaveValue('1');
  await page.getByRole('tab', { name: /first.pdf/ }).click();
  await expect(page.locator('.object').filter({ hasText: 'Saved in tab' })).toHaveCount(1);
  await expect(page.getByLabel('Zoom', { exact: true })).toHaveValue('1.25');
  await page.getByRole('tab', { name: /first.pdf/ }).focus();
  await page.keyboard.press('ArrowRight');
  await expect(page.getByRole('tab', { name: 'second.pdf', exact: true })).toBeFocused();
  await expect(page.locator('.object')).toContainText('Second');
  await page.getByRole('button', { name: 'New tab', exact: true }).click();
  await expect(page.getByRole('heading', { name: 'Look inside your PDF.' })).toBeVisible();
  await page.getByRole('button', { name: 'Close new tab', exact: true }).click();
  await expect(page.getByRole('tab')).toHaveCount(2);
});

test('extract preserves requested order and split opens page tabs without replacing original', async ({ page }) => {
  await page.goto('/');
  await page.getByLabel('Open PDF', { exact: true }).setInputFiles(pdfFile('source.pdf', ['First', 'Second', 'Third']));
  await expect(page.locator('.object')).toContainText('First');
  await page.getByRole('button', { name: 'Extract pages', exact: true }).click();
  await page.getByLabel('Pages to extract').fill('1,1');
  await page.getByRole('button', { name: 'Extract to new tab', exact: true }).click();
  await expect(page.getByRole('alert')).toContainText('only once');
  await page.getByLabel('Pages to extract').fill('4');
  await page.getByRole('button', { name: 'Extract to new tab', exact: true }).click();
  await expect(page.getByRole('alert')).toContainText('between 1 and 3');
  await page.getByLabel('Pages to extract').fill('3,1');
  await page.getByRole('button', { name: 'Extract to new tab', exact: true }).click();
  await expect(page.getByRole('tab')).toHaveCount(2);
  await expect(page.locator('.object')).toContainText('Third');
  await page.getByLabel('Page', { exact: true }).selectOption('2');
  await expect(page.locator('.object')).toContainText('First');
  await page.getByRole('tab', { name: 'source.pdf', exact: true }).click();
  await expect(page.getByLabel('Page', { exact: true }).locator('option')).toHaveCount(3);
  await page.getByRole('button', { name: 'Split PDF', exact: true }).click();
  await page.getByRole('button', { name: 'Split into 3 tabs', exact: true }).click();
  await expect(page.getByRole('tab')).toHaveCount(5);
  await expect(page.locator('.filename')).toHaveText('source-page-1.pdf');
  await expect(page.getByLabel('Page', { exact: true }).locator('option')).toHaveCount(1);
  await page.getByRole('tab', { name: 'source-page-3.pdf', exact: true }).click();
  await expect(page.locator('.object')).toContainText('Third');
  await page.screenshot({ path: 'test-results/document-tabs.png' });
  await page.getByRole('button', { name: 'Close source-page-3.pdf', exact: true }).click();
  await expect(page.getByRole('tab')).toHaveCount(4);
  await page.getByRole('tab', { name: 'source-extracted.pdf', exact: true }).click();
  await expect(page.getByLabel('Page', { exact: true })).toHaveValue('2');
});

test('assembly failure keeps original and does not open partial tabs', async ({ page }) => {
  await page.goto('/');
  await page.getByLabel('Open PDF', { exact: true }).setInputFiles(pdfFile('source.pdf', ['First', 'Second']));
  await expect(page.locator('.object')).toContainText('First');
  await page.route('**/api/documents/split', route => route.fulfill({ status: 422, contentType: 'application/json', body: JSON.stringify({ detail: 'Unsupported document feature.' }) }));
  await page.getByRole('button', { name: 'Split PDF', exact: true }).click();
  await page.getByRole('button', { name: 'Split into 2 tabs', exact: true }).click();
  await expect(page.getByRole('alert')).toContainText('Unsupported document feature');
  await expect(page.getByRole('tab')).toHaveCount(1);
  await page.getByRole('button', { name: 'Cancel', exact: true }).click();
  await expect(page.locator('.object')).toContainText('First');
});

for (const mode of ['split', 'extract']) {
  test('tagged PDF ' + mode + ' preserves tags without a flag', async ({ page }) => {
    await page.goto('/');
    await page.getByLabel('Open PDF', { exact: true }).setInputFiles(pdfFile('tagged.pdf', ['First', 'Second'], true));
    await expect(page.locator('.object')).toContainText('First');
    await page.getByRole('button', { name: mode === 'split' ? 'Split PDF' : 'Extract pages', exact: true }).click();
    if (mode === 'extract') {
      await page.getByLabel('Pages to extract').fill('2');
    }
    const submit = page.getByRole('button', { name: mode === 'split' ? 'Split into 2 tabs' : 'Extract to new tab', exact: true });
    await expect(page.getByRole('checkbox', { name: /Remove accessibility tags/ })).toHaveCount(0);
    const result = page.waitForResponse(response => response.url().includes('/api/documents/' + mode));
    await submit.click();
    const response = await result;
    expect(response.ok()).toBe(true);
    for (const document of (await response.json()).documents) {
      const pdf = Buffer.from(document.base64, 'base64').toString('latin1');
      expect(pdf).toContain('/StructTreeRoot');
      expect(pdf).toContain('/ParentTree');
      expect(pdf).toContain('/StructParents');
    }
    await expect(page.getByRole('tab')).toHaveCount(mode === 'split' ? 3 : 2);
    await expect(page.locator('.object')).toContainText(mode === 'split' ? 'First' : 'Second');
  });
}
