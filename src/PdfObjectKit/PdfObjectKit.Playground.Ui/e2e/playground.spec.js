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
  await page.getByText('Technical details', { exact: true }).click();
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
  await page.getByRole('region', { name: 'Object toolbox' }).getByRole('button', { name: 'Text', exact: true }).click();
  await page.getByLabel('Text to insert').fill('Added text');
  await page.getByRole('button', { name: 'Insert object', exact: true }).click();
  await expect(page.locator('.object').filter({ hasText: 'Added text' })).toHaveCount(1);
  await page.locator('.object').filter({ hasText: 'Added text' }).click();
  await page.getByRole('button', { name: 'Delete text run' }).click();
  await expect(page.locator('.object').filter({ hasText: 'Added text' })).toHaveCount(0);

  await page.getByRole('region', { name: 'Object toolbox' }).getByRole('button', { name: 'Form field', exact: true }).click();
  await page.getByLabel('Field name').fill('AddedField');
  await page.getByLabel('Text to insert').fill('Value');
  await expect(page.locator('.text-placement-preview text')).toHaveText('Value');
  await expect(page.locator('.text-placement-preview text')).toHaveAttribute('font-size', '12');
  await expect(page.locator('.text-placement-preview clipPath rect')).toHaveCount(1);
  await page.getByLabel('Text to insert').fill('Updated value');
  await expect(page.locator('.text-placement-preview text')).toHaveText('Updated value');
  await page.getByLabel('Text to insert').fill('Value');
  await page.screenshot({ path: 'test-results/inspector-add.png' });
  await page.getByRole('button', { name: 'Insert object', exact: true }).click();
  await page.locator('.object').filter({ hasText: 'AddedField' }).click();
  await expect(page.locator('.inspector pre')).toContainText('Value');
  await page.getByRole('button', { name: 'Delete field', exact: true }).click();
  await expect(page.locator('.object').filter({ hasText: 'AddedField' })).toHaveCount(0);
  await expect(page.locator('.object').filter({ hasText: 'FullName' })).toHaveCount(1);

  await page.getByRole('region', { name: 'Object toolbox' }).getByRole('button', { name: 'Image', exact: true }).click();
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
  await expect(page.getByRole('button', { name: 'Draw image', exact: true })).toBeVisible();
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
  await page.getByText('Technical details', { exact: true }).click();
  const image = JSON.parse(await page.locator('.inspector pre').innerText());
  expect(image.corners).toEqual([{ x: 40, y: 40 }, { x: 140, y: 40 }, { x: 140, y: 170 }, { x: 40, y: 170 }]);
});

test('drag types from left toolbox, move the object body and resize its frame', async ({ page }) => {
  await page.goto('/');
  await page.getByLabel('Open PDF', { exact: true }).setInputFiles({ name: 'rotated.pdf', mimeType: 'application/pdf', buffer: rotatedFormPdf() });
  await expect(page.getByRole('status')).toContainText('indirect objects');
  for (const [label, kind] of [['Image', 'add-image'], ['Form field', 'add-field'], ['Text', 'add-text']]) {
    await page.getByRole('button', { name: label, exact: true }).dragTo(page.locator('.page'), { targetPosition: { x: 100, y: 100 } });
    await expect(page.locator('.inspector legend')).toContainText(`Add ${label.toLowerCase()}`);
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
  await expect(page.getByLabel('Width', { exact: true })).toHaveCount(0);
  await expect(page.getByLabel('Height', { exact: true })).toHaveCount(0);
  await expect(page.getByRole('button', { name: /^Resize / })).toHaveCount(0);
  await expect(page.getByLabel('Font size')).toHaveValue('12');
  await page.getByLabel('Font size').fill('24');
  await page.getByLabel('Text to insert').fill('Toolbox text');
  const preview = page.locator('.text-placement-preview text');
  await expect(preview).toHaveText('Toolbox text');
  await expect(preview).toHaveAttribute('font-size', '24');
  await page.getByLabel('Text to insert').fill('Updated preview');
  await expect(preview).toHaveText('Updated preview');
  await page.getByLabel('Text to insert').fill('Toolbox text');
  await expect(page.getByLabel('Font size')).toHaveValue('24');
  await page.getByRole('button', { name: 'Insert object', exact: true }).click();
  await expect(page.locator('.object').filter({ hasText: 'Toolbox text' })).toHaveCount(1);
});

test('image insertion converts mislabeled WebP bytes and reports invalid images locally', async ({ page }) => {
  await page.goto('/');
  await page.getByLabel('Open PDF', { exact: true }).setInputFiles({ name: 'form.pdf', mimeType: 'application/pdf', buffer: rotatedFormPdf() });
  await expect(page.getByRole('status')).toContainText('indirect objects');
  const webp = await page.evaluate(() => {
    const canvas = document.createElement('canvas');
    canvas.width = canvas.height = 2;
    canvas.getContext('2d').fillRect(0, 0, 2, 2);
    return canvas.toDataURL('image/webp').split(',')[1];
  });
  expect(Buffer.from(webp, 'base64').subarray(0, 4).toString()).toBe('RIFF');
  await page.getByRole('button', { name: 'Image', exact: true }).click();
  await page.getByLabel('Image file', { exact: true }).setInputFiles({ name: 'actually-webp.png', mimeType: 'image/png', buffer: Buffer.from(webp, 'base64') });
  await page.getByRole('button', { name: 'Insert object', exact: true }).click();
  await page.locator('.object').filter({ hasText: 'Image 1' }).click();
  await expect(page.locator('.inspector pre')).toContainText('"pixelWidth": 2');
  await expect(page.getByRole('alert')).toHaveCount(0);

  await page.getByRole('button', { name: 'Image', exact: true }).click();
  await page.getByLabel('Image file', { exact: true }).setInputFiles({ name: 'broken.png', mimeType: 'image/png', buffer: Buffer.from('not image bytes') });
  await page.getByRole('button', { name: 'Insert object', exact: true }).click();
  await expect(page.getByRole('alert')).toContainText('Cannot read "broken.png"');
  await expect(page.locator('.object').filter({ hasText: 'Image 1' })).toHaveCount(1);
});

test('rename field refreshes overlays, preserves value and remains editable', async ({ page }) => {
  await page.goto('/');
  await page.getByLabel('Open PDF', { exact: true }).setInputFiles({ name: 'form.pdf', mimeType: 'application/pdf', buffer: rotatedFormPdf() });
  await expect(page.getByRole('status')).toContainText('indirect objects');
  await page.locator('.object').filter({ hasText: 'FullName' }).click();
  await page.getByLabel('Rename field', { exact: true }).fill('LegalName');
  await page.screenshot({ path: 'test-results/inspector-field.png' });
  await page.getByRole('button', { name: 'Rename', exact: true }).click();
  await expect(page.locator('.object').filter({ hasText: 'FullName' })).toHaveCount(0);
  await page.locator('.object').filter({ hasText: 'LegalName' }).click();
  await expect(page.getByLabel('New value', { exact: true })).toHaveValue('Jane');
  await page.getByLabel('Rename field', { exact: true }).fill('Invalid.Name');
  await page.getByRole('button', { name: 'Rename', exact: true }).click();
  await expect(page.getByRole('alert')).toContainText('without dots');
  await page.getByLabel('New value', { exact: true }).fill('Alex');
  await page.getByRole('button', { name: 'Save', exact: true }).click();
  await expect(page.locator('.inspector pre')).toHaveCount(0);
  await page.locator('.object').filter({ hasText: 'LegalName' }).click();
  await expect(page.getByLabel('New value', { exact: true })).toHaveValue('Alex');
});


test('inspector replaces selections and creation drafts instead of accumulating editors', async ({ page }) => {
  const keyWarnings = [];
  page.on('console', message => {
    if (message.text().includes('same key')) {
      keyWarnings.push(message.text());
    }
  });
  await page.goto('/');
  await page.getByLabel('Open PDF', { exact: true }).setInputFiles(path.resolve('../PdfObjectKit.SnapshotTests/Fixtures/input.pdf'));
  await expect(page.getByRole('status')).toContainText('indirect objects');
  const objects = page.locator('.object');
  for (const index of [0, 1, 2, 0, 2, 1]) {
    await objects.nth(index).click();
    await expect(page.locator('.inspector .object-editor')).toHaveCount(1);
    await expect(page.locator('.inspector .selection-summary')).toHaveCount(1);
    await expect(page.locator('.inspector pre')).toHaveCount(1);
  }
  const toolbox = page.getByRole('region', { name: 'Object toolbox' });
  await toolbox.getByRole('button', { name: 'Text', exact: true }).click();
  await page.getByLabel('Text to insert').fill('Uncommitted draft');
  await toolbox.getByRole('button', { name: 'Form field', exact: true }).click();
  await expect(page.getByLabel('Text to insert')).toHaveValue('');
  await expect(page.getByRole('button', { name: 'Insert object', exact: true })).toHaveCount(1);
  await expect(page.locator('.inspector .object-editor')).toHaveCount(0);
  await objects.first().click();
  await expect(page.getByRole('button', { name: 'Insert object', exact: true })).toHaveCount(0);
  await expect(page.locator('.inspector .object-editor')).toHaveCount(1);
  await expect(page.getByRole('button', { name: 'Move placement' })).toHaveCount(0);
  await page.getByRole('button', { name: 'Clear selection' }).click();
  await expect(page.locator('.inspector .object-editor')).toHaveCount(0);
  await expect(page.locator('.inspector pre')).toHaveCount(0);
  expect(keyWarnings).toEqual([]);
});


test('Escape cancels creation from a focused inspector input', async ({ page }) => {
  await page.goto('/');
  await page.getByLabel('Open PDF', { exact: true }).setInputFiles({ name: 'form.pdf', mimeType: 'application/pdf', buffer: rotatedFormPdf() });
  await expect(page.getByRole('status')).toContainText('indirect objects');
  const count = await page.locator('.object').count();
  for (const type of ['Text', 'Image', 'Form field']) {
    await page.getByRole('region', { name: 'Object toolbox' }).getByRole('button', { name: type, exact: true }).click();
    await expect(page.locator('.placement-box')).toBeVisible();
    await page.getByLabel('X', { exact: true }).focus();
    await page.keyboard.press('Escape');
    await expect(page.locator('.placement-box')).toHaveCount(0);
    await expect(page.locator('.text-placement-preview')).toHaveCount(0);
    await expect(page.getByRole('button', { name: 'Insert object', exact: true })).toHaveCount(0);
    await expect(page.locator('.object')).toHaveCount(count);
  }
  await page.getByRole('region', { name: 'Object toolbox' }).getByRole('button', { name: 'Text', exact: true }).click();
  await expect(page.getByLabel('Text to insert')).toHaveValue('');
});


test('draw an image, undo, clear and insert the captured PNG', async ({ page }) => {
  await page.goto('/');
  await page.getByLabel('Open PDF', { exact: true }).setInputFiles({ name: 'form.pdf', mimeType: 'application/pdf', buffer: rotatedFormPdf() });
  await expect(page.getByRole('status')).toContainText('indirect objects');
  await page.getByRole('region', { name: 'Object toolbox' }).getByRole('button', { name: 'Image', exact: true }).click();
  await page.getByRole('button', { name: 'Draw image', exact: true }).click();
  await expect(page.getByRole('button', { name: 'Use drawing', exact: true })).toBeDisabled();
  const canvas = page.getByLabel('Drawing canvas');
  const dimensions = await canvas.evaluate(element => ({ width: element.width, height: element.height, displayWidth: element.getBoundingClientRect().width, displayHeight: element.getBoundingClientRect().height }));
  const objectWidth = Number(await page.getByLabel('Width', { exact: true }).inputValue());
  const objectHeight = Number(await page.getByLabel('Height', { exact: true }).inputValue());
  expect(dimensions.width / dimensions.height).toBeCloseTo(objectWidth / objectHeight, 2);
  expect(dimensions.displayWidth / dimensions.displayHeight).toBeCloseTo(objectWidth / objectHeight, 2);
  const box = await canvas.boundingBox();
  async function draw() {
    await page.mouse.move(box.x + 30, box.y + 30);
    await page.mouse.down();
    await page.mouse.move(box.x + 180, box.y + 90, { steps: 10 });
    await page.mouse.up();
  }
  await draw();
  await page.getByRole('button', { name: 'Undo', exact: true }).click();
  await expect(page.getByRole('button', { name: 'Use drawing', exact: true })).toBeDisabled();
  await draw();
  await page.getByRole('button', { name: 'Clear', exact: true }).click();
  await expect(page.getByRole('button', { name: 'Use drawing', exact: true })).toBeDisabled();
  await draw();
  await page.screenshot({ path: 'test-results/drawing-dialog.png' });
  await page.getByRole('button', { name: 'Use drawing', exact: true }).click();
  await expect(page.getByRole('dialog')).toHaveCount(0);
  await expect(page.getByAltText('Selected image preview')).toBeVisible();
  const placementX = await page.getByLabel('X', { exact: true }).inputValue();
  const placementY = await page.getByLabel('Y', { exact: true }).inputValue();
  await page.getByRole('button', { name: 'Use image size', exact: true }).click();
  await expect(page.getByLabel('Width', { exact: true })).toHaveValue(String(dimensions.width));
  await expect(page.getByLabel('Height', { exact: true })).toHaveValue(String(dimensions.height));
  await expect(page.getByLabel('X', { exact: true })).toHaveValue(placementX);
  await expect(page.getByLabel('Y', { exact: true })).toHaveValue(placementY);
  await page.getByRole('button', { name: 'Draw image', exact: true }).click();
  await page.keyboard.press('Escape');
  await expect(page.getByRole('dialog')).toHaveCount(0);
  await expect(page.getByRole('button', { name: 'Insert object', exact: true })).toBeEnabled();
  await page.getByRole('button', { name: 'Insert object', exact: true }).click();
  await expect(page.locator('.object').filter({ hasText: 'Image 1' })).toHaveCount(1);
});


test('field alignment previews, saves and can be changed without changing the value', async ({ page }) => {
  await page.goto('/');
  await page.getByLabel('Open PDF', { exact: true }).setInputFiles({ name: 'form.pdf', mimeType: 'application/pdf', buffer: rotatedFormPdf() });
  await expect(page.getByRole('status')).toContainText('indirect objects');
  await page.getByRole('region', { name: 'Object toolbox' }).getByRole('button', { name: 'Form field', exact: true }).click();
  await page.getByLabel('Text to insert').fill('Aligned value');
  await page.getByRole('button', { name: 'Align center', exact: true }).click();
  await expect(page.locator('.text-placement-preview text')).toHaveAttribute('text-anchor', 'middle');
  await page.getByRole('button', { name: 'Insert object', exact: true }).click();
  await page.locator('.object').filter({ hasText: 'NewField' }).click();
  await expect(page.getByRole('button', { name: 'Align center', exact: true })).toHaveAttribute('aria-pressed', 'true');
  await page.getByRole('button', { name: 'Align right', exact: true }).click();
  await page.getByRole('button', { name: 'Save', exact: true }).click();
  await expect(page.locator('.inspector .object-editor')).toHaveCount(0);
  await page.locator('.object').filter({ hasText: 'NewField' }).click();
  await expect(page.getByRole('button', { name: 'Align right', exact: true })).toHaveAttribute('aria-pressed', 'true');
  await expect(page.getByLabel('New value', { exact: true })).toHaveValue('Aligned value');
});


test('view current PDF as Markdown, inspect source and download', async ({ page, request }) => {
  await page.goto('/');
  await page.getByLabel('Open PDF', { exact: true }).setInputFiles(path.resolve('../PdfObjectKit.SnapshotTests/Fixtures/input.pdf'));
  await expect(page.getByRole('status')).toContainText('indirect objects');
  await page.getByRole('button', { name: 'View Markdown', exact: true }).click();
  await expect(page.locator('.markdown-preview')).toBeVisible();
  await expect(page.locator('.markdown-preview table').first()).toBeVisible();
  await page.screenshot({ path: 'test-results/markdown-view.png' });
  await page.getByRole('button', { name: 'Source', exact: true }).click();
  const source = await page.locator('.markdown-source').innerText();
  await page.context().grantPermissions(['clipboard-read', 'clipboard-write']);
  await page.getByRole('button', { name: 'Copy Markdown', exact: true }).click();
  await expect(page.getByText('Copied Markdown', { exact: true })).toBeVisible();
  expect((await page.evaluate(() => navigator.clipboard.readText())).replace(/\r\n/g, '\n').trim()).toBe(source.trim());
  await page.evaluate(() => Object.defineProperty(navigator.clipboard, 'writeText', { configurable: true, value: async () => { throw new Error('Denied'); } }));
  await page.getByRole('button', { name: 'Copy Markdown', exact: true }).click();
  await expect(page.getByText('Could not copy. Select the Source text and copy manually.', { exact: true })).toBeVisible();
  expect(source.length).toBeGreaterThan(100);
  const [download] = await Promise.all([page.waitForEvent('download'), page.getByRole('button', { name: 'Download Markdown', exact: true }).click()]);
  expect(download.suggestedFilename()).toBe('input.md');
  const chunks = [];
  for await (const chunk of await download.createReadStream()) {
    chunks.push(chunk);
  }
  expect(Buffer.concat(chunks).toString('utf8').trim()).toBe(source.trim());
  await page.keyboard.press('Escape');
  await expect(page.getByRole('dialog')).toHaveCount(0);
  await expect(page.locator('.overlay')).toBeVisible();
  const invalid = await request.post('/api/documents/markdown', { data: 'bad', headers: { 'Content-Type': 'application/pdf' } });
  expect(invalid.status()).toBe(400);
});

test('Markdown viewer retries errors and sanitizes rendered HTML', async ({ page }) => {
  await page.goto('/');
  await page.getByLabel('Open PDF', { exact: true }).setInputFiles({ name: 'form.pdf', mimeType: 'application/pdf', buffer: rotatedFormPdf() });
  await expect(page.getByRole('status')).toContainText('indirect objects');
  let calls = 0;
  await page.route('**/api/documents/markdown', route => {
    calls++;
    return route.fulfill({ status: calls === 1 ? 422 : 200, contentType: 'application/json', body: JSON.stringify(calls === 1 ? { detail: 'Conversion unavailable' } : { markdown: '# Safe heading\n\n<script>window.badMarkdown=true</script><img src="/unexpected-image" onerror="window.badMarkdown=true"><a href="javascript:alert(1)">Bad link</a>', warnings: ['Example conversion note'] }) });
  });
  await page.getByRole('button', { name: 'View Markdown', exact: true }).click();
  await expect(page.getByRole('alert')).toContainText('Conversion unavailable');
  await page.getByRole('button', { name: 'Retry', exact: true }).click();
  await expect(page.getByRole('heading', { name: 'Safe heading' })).toBeVisible();
  await expect(page.locator('.markdown-preview script, .markdown-preview img, .markdown-preview [href^="javascript:"]')).toHaveCount(0);
  expect(await page.evaluate(() => window.badMarkdown)).toBeUndefined();
  await page.getByText('Conversion notes (1)', { exact: true }).click();
  await expect(page.getByText('Example conversion note', { exact: true })).toBeVisible();
});
