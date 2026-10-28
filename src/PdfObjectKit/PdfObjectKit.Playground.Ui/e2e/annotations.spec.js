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

test('annotations save, render in both engines, edit and delete without changing text', async ({ page }) => {
  await page.goto('/');
  await page.getByLabel('Open PDF', { exact: true }).setInputFiles(pdfFile('annotations.pdf', ['Review this text']));
  await expect(page.locator('.object')).toContainText('Review this text');
  const textFilter = page.getByRole('group', { name: 'Object visibility' }).getByRole('button', { name: 'Text visibility', exact: true });
  await textFilter.click();
  await expect(textFilter).toHaveAttribute('aria-pressed', 'false');
  await expect(page.locator('.object')).toHaveCount(0);
  await textFilter.press('Space');
  await expect(textFilter).toHaveAttribute('aria-pressed', 'true');
  await expect(page.locator('.object')).toContainText('Review this text');
  await page.getByRole('button', { name: 'Comment', exact: true }).click();
  await page.getByLabel('Annotation comment').fill('Please review ???');
  await page.getByRole('button', { name: 'Add annotation', exact: true }).click();
  await expect(page.locator('.object').filter({ hasText: 'Please review ???' })).toHaveCount(1);
  await page.locator('.object').filter({ hasText: 'Please review ???' }).click();
  await page.getByLabel('New value').fill('Reviewed');
  await page.getByRole('button', { name: 'Save', exact: true }).click();
  await expect(page.locator('.object').filter({ hasText: 'Comment: Reviewed' })).toHaveCount(1);
  for (const label of ['Highlight', 'Underline', 'Strikethrough']) {
    await page.locator('.object').filter({ hasText: 'Review this text' }).click();
    if (label === 'Highlight') {
      await page.screenshot({ path: 'test-results/inspector-markup.png' });
    }
    await page.getByRole('group', { name: 'Annotate selected text' }).getByRole('button', { name: label, exact: true }).click();
    await expect(page.getByRole('status')).toContainText('indirect objects');
    await expect(page.getByRole('alert')).toHaveCount(0);
  }
  await expect(page.locator('.object')).toHaveCount(5);
  const canvas = page.locator('.page > div > canvas');
  async function checkMarkup() {
    await expect.poll(async () => canvas.evaluate(c => {
      const data = c.getContext('2d').getImageData(0, 0, c.width, c.height).data;
      let yellow = 0;
      let red = 0;
      for (let i = 0; i < data.length; i += 4) {
        if (data[i] > 200 && data[i + 1] > 150 && data[i + 2] < 220) {
          yellow++;
        }
        if (data[i] > 150 && data[i + 1] < 130 && data[i + 2] < 80) {
          red++;
        }
      }
      return yellow > 100 && red > 20;
    })).toBe(true);
  }
  await checkMarkup();
  await page.getByRole('button', { name: 'PdfObjectKit (experimental)', exact: true }).click();
  await expect(canvas).toHaveAttribute('data-renderer', 'native');
  await checkMarkup();
  await page.locator('.object').filter({ hasText: 'Comment: Reviewed' }).click();
  await page.getByRole('button', { name: 'Delete annotation', exact: true }).click();
  await expect(page.locator('.object')).toHaveCount(4);
  await page.locator('.object').filter({ hasText: 'Highlight:' }).click();
  await page.locator('.object').filter({ hasText: 'Underline:' }).click({ modifiers: ['Control'] });
  await page.getByRole('button', { name: 'Delete selected objects', exact: true }).click();
  await expect(page.locator('.object')).toHaveCount(2);
  await expect(page.locator('.object').filter({ hasText: 'Review this text' })).toHaveCount(1);
});

for (const rotation of [0, 90]) {
  test(`markup applies only to selected text on rotated page ${rotation}`, async ({ page }) => {
    await page.goto('/');
    await page.getByLabel('Open PDF', { exact: true }).setInputFiles(pdfFile('region.pdf', ['Region'], false, rotation));
    await expect(page.locator('.object')).toContainText('Region');
    for (const name of ['Highlight', 'Underline', 'Strikethrough']) {
      await expect(page.getByRole('button', { name, exact: true })).toHaveCount(0);
    }
    await page.locator('.object').filter({ hasText: 'Region' }).click();
    await page.getByRole('group', { name: 'Annotate selected text' }).getByRole('button', { name: 'Highlight', exact: true }).click();
    await expect(page.locator('.object').filter({ hasText: 'Highlight:' })).toHaveCount(1);
    await expect(page.getByLabel('Annotation preview')).toHaveCount(0);
    for (const engine of ['PDF.js', 'PdfObjectKit (experimental)']) {
      await page.getByRole('button', { name: engine, exact: true }).click();
      const canvas = page.locator('.page > div > canvas');
      await expect(canvas).toHaveAttribute('data-renderer', engine === 'PDF.js' ? 'pdfjs' : 'native');
      await expect.poll(() => canvas.evaluate((c, rotation) => {
        const scale = c.width / 300;
        const [x, y, w, h] = rotation ? [247, 20, 16, 40] : [20, 37, 40, 16];
        const data = c.getContext('2d').getImageData(x * scale, y * scale, w * scale, h * scale).data;
        let yellow = 0;
        for (let i = 0; i < data.length; i += 4) {
          if (data[i] > 240 && data[i + 1] > 180 && data[i + 2] < 230) {
            yellow++;
          }
        }
        return yellow > 50;
      }, rotation)).toBe(true);
    }
  });
}

test('highlighted text can be selected on canvas and dragged while markup remains editable', async ({ page }) => {
  await page.goto('/');
  await page.getByLabel('Open PDF', { exact: true }).setInputFiles(pdfFile('move-markup.pdf', ['Move me']));
  await page.locator('.object').filter({ hasText: 'Move me' }).click();
  await page.getByRole('group', { name: 'Annotate selected text' }).getByRole('button', { name: 'Highlight', exact: true }).click();
  await expect(page.locator('.object').filter({ hasText: 'Highlight:' })).toHaveCount(1);
  const overlay = page.locator('canvas.overlay');
  await overlay.click({ position: { x: 30, y: 45 } });
  const handle = page.getByLabel('Move selected object', { exact: true });
  await expect(handle).toBeVisible();
  await handle.click({ modifiers: ['Alt'] });
  await expect(page.getByRole('button', { name: 'Delete annotation', exact: true })).toBeVisible();
  await overlay.click({ position: { x: 30, y: 45 }, modifiers: ['Alt'] });
  await expect(page.getByRole('button', { name: 'Delete annotation', exact: true })).toBeVisible();
  await overlay.click({ position: { x: 30, y: 45 } });
  await expect(handle).toBeVisible();
  const before = await handle.boundingBox();
  const saved = page.waitForResponse(response => response.url().endsWith('/api/documents/edit') && response.request().method() === 'POST');
  await page.mouse.move(before.x + before.width / 2, before.y + before.height / 2);
  await page.mouse.down();
  await page.mouse.move(before.x + before.width / 2 + 50, before.y + before.height / 2 + 30, { steps: 5 });
  await page.mouse.up();
  expect((await saved).ok()).toBe(true);
  await expect.poll(async () => (await handle.boundingBox())?.x ?? 0).toBeCloseTo(before.x + 50, 0);
  await page.locator('.object').filter({ hasText: 'Highlight:' }).click();
  await expect(page.getByRole('button', { name: 'Delete annotation', exact: true })).toBeVisible();
});
