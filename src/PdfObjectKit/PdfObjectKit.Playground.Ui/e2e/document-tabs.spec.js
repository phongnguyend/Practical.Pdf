import { test, expect } from '@playwright/test';

function pdfFile(name, texts, tagged = false, height = 300) {
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
    objects.push('<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 ' + height + '] /Resources << /Font << /F1 3 0 R >> >> /Contents ' + stream + ' 0 R >>');
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

test('merge checked tabs uses saved edits and tab order, and opens an independent result', async ({ page }) => {
  await page.goto('/');
  await page.getByLabel('Open PDF', { exact: true }).setInputFiles([pdfFile('first.pdf', ['First']), pdfFile('second.pdf', ['Second'])]);
  await expect(page.locator('.object')).toContainText('First');
  await page.getByRole('region', { name: 'Object toolbox' }).getByRole('button', { name: 'Text', exact: true }).click();
  await page.getByLabel('Text to insert').fill('Saved edit');
  await page.getByRole('button', { name: 'Insert object', exact: true }).click();
  await expect(page.locator('.object').filter({ hasText: 'Saved edit' })).toHaveCount(1);
  // Reverse selection order still merges in visible tab order.
  await page.getByLabel('Select second.pdf for merge').check();
  await expect(page.getByRole('button', { name: 'Merge selected (1)', exact: true })).toBeDisabled();
  await page.getByLabel('Select first.pdf for merge').check();
  await page.screenshot({ path: 'test-results/merge-tabs.png' });
  await page.getByRole('button', { name: 'Merge selected (2)', exact: true }).click();
  await expect(page.getByRole('tab')).toHaveCount(3);
  await expect(page.getByRole('tab', { name: 'merged.pdf', exact: true })).toHaveAttribute('aria-selected', 'true');
  await expect(page.locator('.object').filter({ hasText: 'Saved edit' })).toHaveCount(1);
  await expect(page.getByLabel('Page', { exact: true }).locator('option')).toHaveCount(2);
  await page.getByLabel('Page', { exact: true }).selectOption('2');
  await expect(page.locator('.object')).toContainText('Second');
  await page.getByRole('tab', { name: /^first\.pdf(?: Modified)?$/ }).click();
  await expect(page.getByLabel('Page', { exact: true }).locator('option')).toHaveCount(1);
  await expect(page.locator('.object').filter({ hasText: 'Saved edit' })).toHaveCount(1);
  await expect(page.getByLabel('Select first.pdf for merge')).not.toBeChecked();
});

test('Ctrl selection and merge failure preserve source tabs and selection', async ({ page }) => {
  await page.goto('/');
  await page.getByLabel('Open PDF', { exact: true }).setInputFiles([pdfFile('one.pdf', ['One']), pdfFile('two.pdf', ['Two'])]);
  await expect(page.locator('.object')).toContainText('One');
  await page.getByRole('tab', { name: 'two.pdf', exact: true }).click({ modifiers: ['Control'] });
  await expect(page.getByLabel('Select one.pdf for merge')).toBeChecked();
  await expect(page.getByLabel('Select two.pdf for merge')).toBeChecked();
  await page.route('**/api/documents/merge', route => route.fulfill({ status: 422, contentType: 'application/json', body: JSON.stringify({ detail: 'Unsupported merge feature.' }) }));
  await page.getByRole('button', { name: 'Merge selected (2)', exact: true }).click();
  await expect(page.getByRole('alert')).toContainText('Unsupported merge feature');
  await expect(page.getByRole('tab')).toHaveCount(2);
  await expect(page.locator('.object')).toContainText('One');
  await expect(page.getByLabel('Select two.pdf for merge')).toBeChecked();
  await page.getByRole('button', { name: 'Close two.pdf', exact: true }).click();
  await expect(page.getByRole('button', { name: 'Merge selected (1)', exact: true })).toBeDisabled();
  await page.getByRole('button', { name: 'Clear merge selection', exact: true }).click();
  await expect(page.getByLabel('Select one.pdf for merge')).not.toBeChecked();
});

test('merge endpoint rejects fewer than two files', async ({ request }) => {
  const response = await request.post('/api/documents/merge', { multipart: { files: { name: 'one.pdf', mimeType: 'application/pdf', buffer: pdfFile('one.pdf', ['One']).buffer } } });
  expect(response.status()).toBe(400);
});

test('drag and keyboard tab reordering preserves saved edits, view and merge order', async ({ page }) => {
  await page.goto('/');
  await page.getByLabel('Open PDF', { exact: true }).setInputFiles([pdfFile('a.pdf', ['Alpha']), pdfFile('b.pdf', ['Beta']), pdfFile('c.pdf', ['Gamma'])]);
  await expect(page.locator('.object')).toContainText('Alpha');
  await page.getByLabel('Zoom', { exact: true }).selectOption('1.25');
  await page.getByRole('region', { name: 'Object toolbox' }).getByRole('button', { name: 'Text', exact: true }).click();
  await page.getByLabel('Text to insert').fill('Saved before reorder');
  await page.getByRole('button', { name: 'Insert object', exact: true }).click();
  await expect(page.locator('.object').filter({ hasText: 'Saved before reorder' })).toHaveCount(1);
  for (const name of ['a.pdf', 'b.pdf', 'c.pdf']) {
    await page.getByLabel(`Select ${name} for merge`).check();
  }
  const a = page.getByRole('tab', { name: /^a\.pdf/ });
  const c = page.getByRole('tab', { name: 'c.pdf', exact: true });
  const cBox = await c.boundingBox();
  await a.dragTo(c, { targetPosition: { x: cBox.width - 3, y: cBox.height / 2 } });
  await expect(page.getByRole('tab')).toHaveText(['b.pdf', 'c.pdf', 'a.pdf']);
  await expect(a).toHaveAttribute('aria-selected', 'true');
  await expect(page.getByLabel('Zoom', { exact: true })).toHaveValue('1.25');
  await expect(page.locator('.object').filter({ hasText: 'Saved before reorder' })).toHaveCount(1);
  await a.focus();
  await a.press('Alt+ArrowLeft');
  await expect(page.getByRole('tab')).toHaveText(['b.pdf', 'a.pdf', 'c.pdf']);
  await expect(a).toBeFocused();
  await expect(page.getByRole('button', { name: 'Merge selected (3)', exact: true })).toBeEnabled();
  await a.press('Alt+ArrowLeft');
  await a.press('Alt+ArrowLeft');
  await expect(page.getByRole('tab')).toHaveText(['a.pdf', 'b.pdf', 'c.pdf']);
  await a.press('Alt+ArrowRight');
  await page.getByRole('button', { name: 'Merge selected (3)', exact: true }).click();
  await expect(page.getByRole('tab')).toHaveCount(4);
  await expect(page.locator('.object')).toContainText('Beta');
  await page.getByLabel('Page', { exact: true }).selectOption('2');
  await expect(page.locator('.object').filter({ hasText: 'Saved before reorder' })).toHaveCount(1);
  await page.getByLabel('Page', { exact: true }).selectOption('3');
  await expect(page.locator('.object')).toContainText('Gamma');
});


test('delete current page preserves tab, remaining order and view, and protects last page', async ({ page }) => {
  await page.goto('/');
  await page.getByLabel('Open PDF', { exact: true }).setInputFiles(pdfFile('delete.pdf', ['First', 'Second', 'Third'], true));
  await expect(page.locator('.object')).toContainText('First');
  await page.getByLabel('Zoom', { exact: true }).selectOption('1.25');
  await page.getByLabel('Page', { exact: true }).selectOption('2');
  await expect(page.locator('.object')).toContainText('Second');
  await page.getByRole('button', { name: 'Delete page', exact: true }).click();
  await expect(page.getByRole('dialog')).toContainText('Delete page 2?');
  await expect(page.getByRole('button', { name: 'Cancel', exact: true })).toBeFocused();
  await page.keyboard.press('Escape');
  await expect(page.getByRole('dialog')).toHaveCount(0);
  await expect(page.getByLabel('Page', { exact: true }).locator('option')).toHaveCount(3);
  await page.getByRole('button', { name: 'Delete page', exact: true }).click();
  await page.getByRole('dialog').getByRole('button', { name: 'Delete page', exact: true }).click();
  await expect(page.locator('.object')).toContainText('Third');
  await expect(page.getByRole('tab')).toHaveCount(1);
  await expect(page.getByLabel('Page', { exact: true })).toHaveValue('2');
  await expect(page.getByLabel('Zoom', { exact: true })).toHaveValue('1.25');
  await page.getByRole('button', { name: 'Delete page', exact: true }).click();
  await page.getByRole('dialog').getByRole('button', { name: 'Delete page', exact: true }).click();
  await expect(page.locator('.object')).toContainText('First');
  await expect(page.getByLabel('Page', { exact: true })).toHaveValue('1');
  await expect(page.getByRole('button', { name: 'Delete page', exact: true })).toBeDisabled();
  await page.getByRole('button', { name: 'New tab', exact: true }).click();
  await page.getByRole('tab', { name: /delete.pdf/ }).click();
  await expect(page.locator('.object')).toContainText('First');
  await expect(page.getByLabel('Page', { exact: true }).locator('option')).toHaveCount(1);
});

test('delete page API rejects invalid page and deleting the last page', async ({ request }) => {
  for (const pageNumber of [0, 2, 1]) {
    const file = pdfFile('single.pdf', ['Only']);
    const response = await request.post('/api/documents/edit', { multipart: {
      file: { name: file.name, mimeType: file.mimeType, buffer: file.buffer },
      edit: JSON.stringify({ kind: 'delete-page', pageNumber, value: '' }),
    } });
    expect(response.status()).toBe(422);
  }
});


for (const renderer of ['PDF.js', 'PdfObjectKit (experimental)']) {
  test(`rotate page updates canvas and persists in tab with ${renderer}`, async ({ page }) => {
    await page.goto('/');
    await page.getByLabel('Open PDF', { exact: true }).setInputFiles(pdfFile('rotate.pdf', ['First', 'Second'], false, 400));
    await expect(page.locator('.object')).toContainText('First');
    await page.getByRole('button', { name: renderer, exact: true }).click();
    const canvas = page.locator('.page > div > canvas');
    await expect(page.getByRole('button', { name: 'Rotate right', exact: true })).toBeEnabled();
    const size = await canvas.evaluate(c => [c.width, c.height]);
    await page.getByRole('button', { name: 'Rotate right', exact: true }).click();
    await expect(canvas).toHaveJSProperty('width', size[1]);
    await expect(canvas).toHaveJSProperty('height', size[0]);
    await page.getByLabel('Page', { exact: true }).selectOption('2');
    await expect(canvas).toHaveJSProperty('height', size[1]);
    await page.getByLabel('Page', { exact: true }).selectOption('1');
    await expect(canvas).toHaveJSProperty('width', size[1]);
    await page.getByRole('button', { name: 'New tab', exact: true }).click();
    await page.getByRole('tab', { name: /rotate.pdf/ }).click();
    await expect(canvas).toHaveJSProperty('width', size[1]);
    await page.getByRole('button', { name: 'Rotate left', exact: true }).click();
    await expect(canvas).toHaveJSProperty('width', size[0]);
    await expect(canvas).toHaveJSProperty('height', size[1]);
    await expect(page.locator('.object')).toContainText('First');
  });
}
