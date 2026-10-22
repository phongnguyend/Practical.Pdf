import { test, expect } from '@playwright/test';
import path from 'node:path';
import { readFile } from 'node:fs/promises';

function rotatedFormPdf() {
  const objects = [
    '<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [4 0 R] >> >>',
    '<< /Type /Pages /Kids [3 0 R] /Count 1 >>',
    '<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 300] /CropBox [10 20 210 220] /Rotate 90 /Resources << >> /Annots [4 0 R] >>',
    '<< /Type /Annot /Subtype /Widget /FT /Tx /T (FullName) /V (Jane) /Rect [30 50 90 80] /P 3 0 R >>',
  ];
  let pdf = '%PDF-1.7\n';
  const offsets = [0];
  objects.forEach((value, index) => {
    offsets.push(Buffer.byteLength(pdf));
    pdf += `${index + 1} 0 obj\n${value}\nendobj\n`;
  });
  const xref = Buffer.byteLength(pdf);
  pdf += `xref\n0 ${objects.length + 1}\n0000000000 65535 f \n`;
  pdf += offsets.slice(1).map(offset => `${String(offset).padStart(10, '0')} 00000 n \n`).join('');
  pdf += `trailer\n<< /Size ${objects.length + 1} /Root 1 0 R >>\nstartxref\n${xref}\n%%EOF\n`;
  return Buffer.from(pdf);
}

test('upload, render, inspect, zoom and navigate a real PDF', async ({ page }) => {
  const errors = [];
  page.on('pageerror', error => errors.push(error.message));
  await page.goto('/');
  await page.getByLabel('Open PDF', { exact: true }).setInputFiles(path.resolve('../PdfObjectKit.SnapshotTests/Fixtures/input.pdf'));
  await expect(page.getByRole('status')).toContainText('indirect objects');
  await expect(page.locator('.object')).not.toHaveCount(0);
  await page.locator('.object').first().click();
  await expect(page.locator('.inspector pre')).toContainText('streamObject');
  await page.getByLabel('Zoom', { exact: true }).selectOption('1.5');
  await expect(page.getByRole('status')).toContainText('indirect objects');
  await expect(page.locator('.page > div > canvas')).toHaveJSProperty('width', 918);
  await page.getByLabel('Page', { exact: true }).selectOption('2');
  await expect(page.getByRole('status')).toContainText('indirect objects');
  await expect(page.locator('.inspector pre')).toHaveCount(0);
  await page.getByLabel('text', { exact: true }).uncheck();
  await expect(page.locator('.object .badge').filter({ hasText: 'text' })).toHaveCount(0);
  await page.screenshot({ path: 'test-results/playground.png' });
  await page.getByLabel('Open PDF', { exact: true }).setInputFiles(path.resolve('../PdfObjectKit.SnapshotTests/Fixtures/images.pdf'));
  await expect(page.locator('.filename')).toHaveText('images.pdf');
  await expect(page.getByRole('status')).toContainText('indirect objects');
  expect(errors).toEqual([]);
});

test('image placement overlays support canvas selection', async ({ page }) => {
  await page.goto('/');
  await page.getByLabel('Open PDF', { exact: true }).setInputFiles(path.resolve('../PdfObjectKit.SnapshotTests/Fixtures/images.pdf'));
  await expect(page.getByRole('status')).toContainText('indirect objects');
  await page.locator('.object').filter({ hasText: 'image' }).first().click();
  await expect(page.locator('.inspector pre')).toContainText('pixelWidth');
  // Derive the on-canvas image centroid from backend PDF-space coordinates.
  const details = JSON.parse(await page.locator('.inspector pre').innerText());
  const canvasHeight = await page.locator('.overlay').evaluate(canvas => canvas.getBoundingClientRect().height);
  const center = details.corners.reduce((sum, point) => ({ x: sum.x + point.x / 4, y: sum.y + point.y / 4 }), { x: 0, y: 0 });
  await page.locator('.overlay').click({ position: { x: center.x, y: canvasHeight - center.y } });
  await expect(page.locator('.inspector pre')).toContainText('pixelWidth');
});

test('bad uploads return a useful error; API rejects wrong content types', async ({ page, request }) => {
  await page.goto('/');
  await page.getByLabel('Open PDF', { exact: true }).setInputFiles({ name: 'bad.pdf', mimeType: 'application/pdf', buffer: Buffer.from('not pdf') });
  await expect(page.getByRole('alert')).toContainText('PDF header');
  const response = await request.post('/api/documents/inspect', { data: 'not a PDF', headers: { 'Content-Type': 'text/plain' } });
  expect(response.status()).toBe(415);
});

test('field overlay aligns with a rotated and cropped page', async ({ page }) => {
  await page.goto('/');
  await page.getByLabel('Open PDF', { exact: true }).setInputFiles({ name: 'rotated-form.pdf', mimeType: 'application/pdf', buffer: rotatedFormPdf() });
  await expect(page.getByRole('status')).toContainText('indirect objects');
  await expect(page.locator('.overlay')).toHaveCSS('width', '200px');
  await page.locator('.overlay').click({ position: { x: 45, y: 50 } });
  await expect(page.locator('.inspector pre')).toContainText('FullName');
  await expect(page.locator('.inspector pre')).toContainText('Jane');
  await expect(page.locator('.inspector pre')).toContainText('Text');
  await page.getByLabel('New value', { exact: true }).fill('Alex');
  await page.getByRole('button', { name: 'Save', exact: true }).click();
  await expect(page.locator('.inspector pre')).toHaveCount(0);
  await expect(page.getByRole('status')).toContainText('indirect objects');
  await page.locator('.overlay').click({ position: { x: 45, y: 50 } });
  await expect(page.locator('.inspector pre')).toContainText('Alex');
  await page.getByLabel('New value', { exact: true }).fill('\u4e2d');
  await page.getByRole('button', { name: 'Save', exact: true }).click();
  await expect(page.getByRole('alert')).toContainText('ASCII');
  await expect(page.locator('.inspector pre')).toContainText('Alex');
});

test('text Save rebuilds the PDF, preserves page/zoom and downloads edited bytes', async ({ page, request }) => {
  await page.goto('/');
  await page.getByLabel('Open PDF', { exact: true }).setInputFiles(path.resolve('../PdfObjectKit.SnapshotTests/Fixtures/input.pdf'));
  await expect(page.getByRole('status')).toContainText('indirect objects');
  await page.getByLabel('Page', { exact: true }).selectOption('2');
  await page.getByLabel('Zoom', { exact: true }).selectOption('1.5');
  await expect(page.getByRole('status')).toContainText('indirect objects');
  await page.locator('.object').first().click();
  const original = await page.getByLabel('New value', { exact: true }).inputValue();
  const replacement = original + original;
  await page.getByLabel('New value', { exact: true }).fill(replacement);
  await page.getByRole('button', { name: 'Save', exact: true }).click();
  await expect(page.locator('.inspector pre')).toHaveCount(0);
  await expect(page.getByRole('status')).toContainText('indirect objects');
  await expect(page.getByLabel('Page', { exact: true })).toHaveValue('2');
  await expect(page.getByLabel('Zoom', { exact: true })).toHaveValue('1.5');
  await expect(page.locator('.object').first()).toContainText(replacement);
  const [download] = await Promise.all([page.waitForEvent('download'), page.getByRole('button', { name: 'Download PDF' }).click()]);
  const stream = await download.createReadStream();
  const chunks = [];
  for await (const chunk of stream) {
    chunks.push(chunk);
  }
  const inspected = await request.post('/api/documents/inspect', { data: Buffer.concat(chunks), headers: { 'Content-Type': 'application/pdf' } });
  expect(inspected.ok()).toBeTruthy();
  expect((await inspected.json()).objects.some(object => object.label.includes(replacement))).toBeTruthy();
});

test('add and delete text, image and form field through the playground', async ({ page }) => {
  await page.goto('/');
  await page.getByLabel('Open PDF', { exact: true }).setInputFiles({ name: 'form.pdf', mimeType: 'application/pdf', buffer: rotatedFormPdf() });
  await expect(page.getByRole('status')).toContainText('indirect objects');
  await page.getByRole('button', { name: 'Add object', exact: true }).click();
  await page.getByLabel('Text to insert').fill('Added text');
  await page.getByRole('button', { name: 'Insert object', exact: true }).click();
  await expect(page.locator('.object').filter({ hasText: 'Added text' })).toHaveCount(1);
  await page.locator('.object').filter({ hasText: 'Added text' }).click();
  await page.getByRole('button', { name: 'Delete text run' }).click();
  await expect(page.locator('.object').filter({ hasText: 'Added text' })).toHaveCount(0);

  await page.getByRole('button', { name: 'Add object', exact: true }).click();
  await page.getByLabel('Object type').selectOption('add-field');
  await page.getByLabel('Field name').fill('AddedField');
  await page.getByLabel('Text to insert').fill('Value');
  await page.getByRole('button', { name: 'Insert object', exact: true }).click();
  await page.locator('.object').filter({ hasText: 'AddedField' }).click();
  await expect(page.locator('.inspector pre')).toContainText('Value');
  await page.getByRole('button', { name: 'Delete field', exact: true }).click();
  await expect(page.locator('.object').filter({ hasText: 'AddedField' })).toHaveCount(0);
  await expect(page.locator('.object').filter({ hasText: 'FullName' })).toHaveCount(1);

  await page.getByRole('button', { name: 'Add object', exact: true }).click();
  await page.getByLabel('Object type').selectOption('add-image');
  await page.getByLabel('Image file').setInputFiles({ name: 'pixel.png', mimeType: 'image/png', buffer: Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=', 'base64') });
  await page.getByRole('button', { name: 'Insert object', exact: true }).click();
  await page.locator('.object').filter({ hasText: 'Image 1' }).click();
  await page.getByRole('button', { name: 'Delete image occurrence' }).click();
  await expect(page.locator('.object').filter({ hasText: 'Image 1' })).toHaveCount(0);
  await expect(page.getByRole('alert')).toHaveCount(0);
});

test('deleting a shared image occurrence leaves other placements intact', async ({ request }) => {
  let bytes = await readFile(path.resolve('../PdfObjectKit.SnapshotTests/Fixtures/images.pdf'));
  async function inspect(data) {
    const response = await request.post('/api/documents/inspect', { data, headers: { 'Content-Type': 'application/pdf' } });
    expect(response.ok()).toBeTruthy();
    return (await response.json()).objects.filter(object => object.kind === 'image');
  }
  const before = await inspect(bytes);
  expect(before.length).toBeGreaterThan(1);
  const target = before[1];
  const response = await request.post('/api/documents/edit', { multipart: { file: { name: 'images.pdf', mimeType: 'application/pdf', buffer: bytes }, edit: JSON.stringify({ kind: 'delete-image', value: '', pageNumber: target.pageNumber, imageNumber: target.details.imageNumber }) } });
  expect(response.ok(), await response.text()).toBeTruthy();
  bytes = await response.body();
  const after = await inspect(bytes);
  expect(after.length).toBe(before.length - 1);
  const expected = before.filter(object => object !== target).map(object => [object.pageNumber, object.details.encodedDataSha256, object.corners]);
  expect(after.map(object => [object.pageNumber, object.details.encodedDataSha256, object.corners])).toEqual(expected);
});

test('drop image, draw placement, resize and move before inserting on a rotated page', async ({ page }) => {
  await page.goto('/');
  await page.getByLabel('Open PDF', { exact: true }).setInputFiles({ name: 'rotated.pdf', mimeType: 'application/pdf', buffer: rotatedFormPdf() });
  await expect(page.getByRole('status')).toContainText('indirect objects');
  await page.locator('.workspace').evaluate(element => {
    const data = new DataTransfer();
    const bytes = Uint8Array.from(atob('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII='), c => c.charCodeAt(0));
    data.items.add(new File([bytes], 'dropped.png', { type: 'image/png' }));
    element.dispatchEvent(new DragEvent('drop', { bubbles: true, dataTransfer: data }));
  });
  await expect(page.getByLabel('Object type')).toHaveValue('add-image');
  const surface = await page.getByLabel('Draw object placement', { exact: true }).boundingBox();
  await page.mouse.move(surface.x + 10, surface.y + 20);
  await page.mouse.down();
  await page.mouse.move(surface.x + 100, surface.y + 90, { steps: 6 });
  await page.mouse.up();
  await expect(page.getByLabel('Width', { exact: true })).toHaveValue('70');
  await expect(page.getByLabel('Height', { exact: true })).toHaveValue('90');
  await page.getByRole('button', { name: 'Resize bottom right', exact: true }).hover();
  await page.mouse.down();
  await page.mouse.move(surface.x + 140, surface.y + 120, { steps: 6 });
  await page.mouse.up();
  await expect(page.getByLabel('Width', { exact: true })).toHaveValue('100');
  await expect(page.getByLabel('Height', { exact: true })).toHaveValue('130');
  const handle = await page.getByRole('button', { name: 'Move placement' }).boundingBox();
  await page.mouse.move(handle.x + handle.width / 2, handle.y + handle.height / 2);
  await page.mouse.down();
  await page.mouse.move(handle.x + handle.width / 2 + 10, handle.y + handle.height / 2 + 10, { steps: 4 });
  await page.mouse.up();
  await expect(page.getByLabel('X', { exact: true })).toHaveValue('40');
  await expect(page.getByLabel('Y', { exact: true })).toHaveValue('40');
  await page.getByRole('button', { name: 'Insert object', exact: true }).click();
  await page.locator('.object').filter({ hasText: 'Image 1' }).click();
  const image = JSON.parse(await page.locator('.inspector pre').innerText());
  expect(image.corners).toEqual([{ x: 40, y: 40 }, { x: 140, y: 40 }, { x: 140, y: 170 }, { x: 40, y: 170 }]);
});

test('drag types from left toolbox, move the object body and resize its frame', async ({ page }) => {
  await page.goto('/');
  await page.getByLabel('Open PDF', { exact: true }).setInputFiles({ name: 'rotated.pdf', mimeType: 'application/pdf', buffer: rotatedFormPdf() });
  await expect(page.getByRole('status')).toContainText('indirect objects');
  for (const [label, kind] of [['Image', 'add-image'], ['Form field', 'add-field'], ['Text', 'add-text']]) {
    await page.getByRole('button', { name: label, exact: true }).dragTo(page.locator('.page'), { targetPosition: { x: 100, y: 100 } });
    await expect(page.getByLabel('Object type')).toHaveValue(kind);
    await expect(page.locator('.placement-box')).toBeVisible();
    if (kind !== 'add-text') {
      await page.getByRole('button', { name: 'Cancel', exact: true }).click();
    }
  }
  const x = Number(await page.getByLabel('X', { exact: true }).inputValue());
  const y = Number(await page.getByLabel('Y', { exact: true }).inputValue());
  const box = await page.locator('.placement-box').boundingBox();
  await page.mouse.move(box.x + box.width / 2, box.y + box.height / 2);
  await page.mouse.down();
  await page.mouse.move(box.x + box.width / 2 + 10, box.y + box.height / 2 + 10, { steps: 4 });
  await page.mouse.up();
  await expect(page.getByLabel('X', { exact: true })).toHaveValue(String(x + 10));
  await expect(page.getByLabel('Y', { exact: true })).toHaveValue(String(y + 10));
  const width = Number(await page.getByLabel('Width', { exact: true }).inputValue());
  const height = Number(await page.getByLabel('Height', { exact: true }).inputValue());
  const handle = await page.getByRole('button', { name: 'Resize top left', exact: true }).boundingBox();
  await page.mouse.move(handle.x + handle.width / 2, handle.y + handle.height / 2);
  await page.mouse.down();
  await page.mouse.move(handle.x + handle.width / 2 - 10, handle.y + handle.height / 2 - 10, { steps: 4 });
  await page.mouse.up();
  await expect(page.getByLabel('Width', { exact: true })).toHaveValue(String(width + 10));
  await expect(page.getByLabel('Height', { exact: true })).toHaveValue(String(height + 10));
  await page.getByLabel('Text to insert').fill('Toolbox text');
  await page.getByRole('button', { name: 'Insert object', exact: true }).click();
  await expect(page.locator('.object').filter({ hasText: 'Toolbox text' })).toHaveCount(1);
});
