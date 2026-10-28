import { test, expect } from '@playwright/test';

function pdfFile(name, texts, tagged = false, rotation = 0) {
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
    objects.push('<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 300] /Rotate ' + rotation + ' /Resources << /Font << /F1 3 0 R >> >> /Contents ' + stream + ' 0 R >>');
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


for (const rotation of [0, 90]) {
  test(`partial passage annotation uses character bounds at rotation ${rotation}`, async ({ page }) => {
    await page.goto('/');
    await page.getByLabel('Open PDF', { exact: true }).setInputFiles(pdfFile('selection.pdf', ['Alpha Beta Gamma'], false, rotation));
    await expect(page.locator('.object')).toContainText('Alpha Beta Gamma');
    await page.getByLabel('Zoom', { exact: true }).selectOption('1.5');
    await page.getByRole('button', { name: 'Select text', exact: true }).click();
    await expect(page.locator('canvas.overlay')).toHaveCount(0);
    const span = page.locator('.selectable-text-layer span').filter({ hasText: 'Alpha Beta Gamma' });
    await expect(span).toBeVisible();
    // Select exactly Beta, including a partial PDF text run.
    await span.evaluate(element => {
      const text = element.firstChild;
      const range = document.createRange();
      range.setStart(text, 6);
      range.setEnd(text, 10);
      const selection = window.getSelection();
      selection.removeAllRanges();
      selection.addRange(range);
    });
    await expect(page.locator('.selected-text-preview')).toHaveText('Beta');
    const response = page.waitForResponse(r => r.url().endsWith('/api/documents/edit') && r.request().method() === 'POST');
    await page.getByRole('group', { name: 'Annotate selected passage' }).getByRole('button', { name: 'Highlight', exact: true }).click();
    expect((await response).ok()).toBe(true);
    await expect(page.locator('.object').filter({ hasText: 'Highlight:' })).toHaveCount(1);
    await page.locator('.object').filter({ hasText: 'Highlight:' }).click();
    await expect(page.getByRole('button', { name: 'Select objects', exact: true })).toHaveAttribute('aria-pressed', 'true');
    const details = JSON.parse(await page.locator('.technical-details pre').textContent());
    expect(details.rectangle[0]).toBeGreaterThan(45);
    expect(details.rectangle[2] - details.rectangle[0]).toBeGreaterThan(15);
    expect(details.rectangle[2] - details.rectangle[0]).toBeLessThan(30);
    expect(details.quadPoints).toHaveLength(8);
    await page.getByRole('button', { name: 'PdfObjectKit (experimental)', exact: true }).click();
    await page.getByRole('button', { name: 'Select text', exact: true }).click();
    await expect(page.locator('.selectable-text-layer span').filter({ hasText: 'Alpha Beta Gamma' })).toBeVisible();
    await expect(page.getByRole('group', { name: 'Annotate selected passage' })).toHaveCount(0);
  });
}

test('mouse selects text; pan scrolls without moving objects; switching modes clears selection', async ({ page }) => {
  await page.setViewportSize({ width: 1100, height: 600 });
  await page.goto('/');
  await page.getByLabel('Open PDF', { exact: true }).setInputFiles(pdfFile('modes.pdf', ['Alpha Beta Gamma']));
  await expect(page.locator('.object')).toContainText('Alpha Beta Gamma');
  await page.getByLabel('Zoom', { exact: true }).selectOption('2');
  await page.getByRole('button', { name: 'Select text', exact: true }).click();
  const span = page.locator('.selectable-text-layer span').filter({ hasText: 'Alpha Beta Gamma' });
  await expect(span).toBeVisible();
  const box = await span.boundingBox();
  await page.mouse.move(box.x + 2, box.y + box.height / 2);
  await page.mouse.down();
  await page.mouse.move(box.x + box.width / 2, box.y + box.height / 2, { steps: 8 });
  await page.mouse.up();
  await expect(page.locator('.selected-text-preview')).toContainText('Alpha');
  await page.getByRole('button', { name: 'Pan', exact: true }).click();
  await expect(page.locator('.selectable-text-layer')).toHaveCount(0);
  await expect(page.locator('.markup-tools')).toHaveCount(0);
  const workspace = page.getByRole('region', { name: 'PDF canvas' });
  const surface = await workspace.boundingBox();
  const before = await workspace.evaluate(e => e.scrollTop);
  await page.mouse.move(surface.x + surface.width / 2, surface.y + surface.height - 40);
  await page.mouse.down();
  await page.mouse.move(surface.x + surface.width / 2, surface.y + 40, { steps: 8 });
  await page.mouse.up();
  expect(await workspace.evaluate(e => e.scrollTop)).toBeGreaterThan(before);
  await page.getByRole('button', { name: 'Select objects', exact: true }).click();
  await expect(page.locator('canvas.overlay')).toHaveCount(1);
  await expect(page.locator('.object')).toHaveCount(1);
});

test('multiline selection creates separate markup regions and Escape clears it', async ({ page }) => {
  await page.goto('/');
  await page.getByLabel('Open PDF', { exact: true }).setInputFiles(pdfFile('lines.pdf', ['Alpha Beta) Tj 0 -25 Td (Gamma Delta']));
  await expect(page.locator('.object')).toHaveCount(2);
  await page.getByRole('button', { name: 'Select text', exact: true }).click();
  await expect(page.locator('.selectable-text-layer span').filter({ hasText: 'Gamma Delta' })).toBeVisible();
  async function selectPassage() {
    await page.locator('.selectable-text-layer').evaluate(element => {
      const spans = [...element.querySelectorAll('span')];
      const first = spans.find(span => span.textContent === 'Alpha Beta').firstChild;
      const last = spans.find(span => span.textContent === 'Gamma Delta').firstChild;
      const range = document.createRange();
      range.setStart(first, 6);
      range.setEnd(last, 5);
      const selection = window.getSelection();
      selection.removeAllRanges();
      selection.addRange(range);
    });
  }
  await selectPassage();
  await expect(page.locator('.selected-text-preview')).toContainText('Beta');
  await page.screenshot({ path: 'test-results/text-selection-modes.png' });
  await page.keyboard.press('Escape');
  await expect(page.locator('.selected-text-preview')).toHaveCount(0);
  await selectPassage();
  await page.getByRole('group', { name: 'Annotate selected passage' }).getByRole('button', { name: 'Underline', exact: true }).click();
  await page.locator('.object').filter({ hasText: 'Underline:' }).click();
  const details = JSON.parse(await page.locator('.technical-details pre').textContent());
  expect(details.quadPoints).toHaveLength(16);
  expect(Math.abs(details.quadPoints[1] - details.quadPoints[9])).toBeGreaterThan(20);
});
