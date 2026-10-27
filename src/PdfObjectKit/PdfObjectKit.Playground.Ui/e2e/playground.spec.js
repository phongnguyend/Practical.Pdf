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
  await page.getByRole('button', { name: 'Clear selection' }).click();
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


test('replace a selected image without changing other occurrences', async ({ page }) => {
  await page.goto('/');
  await page.getByLabel('Open PDF', { exact: true }).setInputFiles(path.resolve('../PdfObjectKit.SnapshotTests/Fixtures/images.pdf'));
  await expect(page.getByRole('status')).toContainText('indirect objects');
  const images = page.locator('.object').filter({ hasText: 'image' });
  const count = await images.count();
  await images.first().click();
  const before = JSON.parse(await page.locator('.inspector pre').textContent());
  await page.getByLabel('Image file').setInputFiles({ name: 'pixel.png', mimeType: 'image/png', buffer: Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=', 'base64') });
  await expect(page.getByLabel('Image sizing')).toHaveValue('Fit');
  await page.getByLabel('Image sizing').selectOption('Stretch');
  await page.getByRole('button', { name: 'Replace image', exact: true }).click();
  await expect(page.locator('.inspector .object-editor')).toHaveCount(0);
  await expect(images).toHaveCount(count);
  await images.first().click();
  const after = JSON.parse(await page.locator('.inspector pre').textContent());
  expect(after.corners).toEqual(before.corners);
  expect(after.pixelWidth).toBe(1);
  expect(after.pixelHeight).toBe(1);
  expect(after.encodedDataSha256).not.toBe(before.encodedDataSha256);
  await expect(page.getByRole('alert')).toHaveCount(0);
});


test('generate a QR image for insertion and replacement', async ({ page }) => {
  await page.goto('/');
  await page.getByLabel('Open PDF', { exact: true }).setInputFiles({ name: 'form.pdf', mimeType: 'application/pdf', buffer: rotatedFormPdf() });
  await expect(page.getByRole('status')).toContainText('indirect objects');
  await page.getByRole('region', { name: 'Object toolbox' }).getByRole('button', { name: 'Image', exact: true }).click();
  await page.getByRole('button', { name: 'Generate QR code', exact: true }).click();
  await expect(page.getByRole('button', { name: 'Use QR code', exact: true })).toBeDisabled();
  await page.getByLabel('QR content').fill('https://github.com/phongnguyend');
  await expect(page.getByAltText('QR code preview')).toBeVisible();
  await page.screenshot({ path: 'test-results/qr-dialog.png' });
  await page.getByRole('button', { name: 'Use QR code', exact: true }).click();
  await expect(page.getByRole('dialog')).toHaveCount(0);
  await expect(page.getByAltText('Selected image preview')).toBeVisible();
  expect(await page.getByLabel('Width', { exact: true }).inputValue()).toBe(await page.getByLabel('Height', { exact: true }).inputValue());
  await page.getByRole('button', { name: 'Insert object', exact: true }).click();
  await page.locator('.object').filter({ hasText: 'Image 1' }).click();
  const original = JSON.parse(await page.locator('.inspector pre').textContent());
  expect(original.pixelWidth).toBe(original.pixelHeight);
  await page.getByRole('button', { name: 'Generate QR code', exact: true }).click();
  await page.getByLabel('QR content').fill('x'.repeat(4000));
  await expect(page.getByRole('alert')).toContainText('too long');
  await expect(page.getByRole('button', { name: 'Use QR code', exact: true })).toBeDisabled();
  await page.getByLabel('QR content').fill('Replacement QR text');
  await expect(page.getByAltText('QR code preview')).toBeVisible();
  await page.getByRole('button', { name: 'Use QR code', exact: true }).click();
  await page.getByRole('button', { name: 'Replace image', exact: true }).click();
  await expect(page.locator('.inspector .object-editor')).toHaveCount(0);
  await page.locator('.object').filter({ hasText: 'Image 1' }).click();
  const replaced = JSON.parse(await page.locator('.inspector pre').textContent());
  expect(replaced.corners).toEqual(original.corners);
  expect(replaced.encodedDataSha256).not.toBe(original.encodedDataSha256);
  await page.getByRole('button', { name: 'Generate QR code', exact: true }).click();
  await page.keyboard.press('Escape');
  await expect(page.getByRole('dialog')).toHaveCount(0);
  await expect(page.locator('.inspector .object-editor')).toHaveCount(1);
});


test('drag existing field and image saves on drop and Escape cancels', async ({ page }) => {
  await page.goto('/');
  await page.getByLabel('Open PDF', { exact: true }).setInputFiles({ name: 'form.pdf', mimeType: 'application/pdf', buffer: rotatedFormPdf() });
  await expect(page.getByRole('status')).toContainText('indirect objects');
  await page.locator('.object').filter({ hasText: 'FullName' }).click();
  const before = JSON.parse(await page.locator('.inspector pre').textContent());
  async function drag(dx, dy, cancel = false) {
    const box = await page.getByLabel('Move selected object').boundingBox();
    await page.mouse.move(box.x + box.width / 2, box.y + box.height / 2);
    await page.mouse.down();
    await page.mouse.move(box.x + box.width / 2 + dx, box.y + box.height / 2 + dy, { steps: 5 });
    if (cancel) {
      await page.keyboard.press('Escape');
    }
    await page.mouse.up();
  }
  const saved = page.waitForResponse(response => response.url().endsWith('/api/documents/inspect') && response.request().method() === 'POST');
  await drag(20, 10);
  await saved;
  await expect.poll(async () => JSON.parse(await page.locator('.inspector pre').textContent()).widget.rectangle).toEqual(before.widget.rectangle.map((n, i) => n + (i % 2 === 0 ? 10 : 20)));
  await expect(page.getByRole('status')).toContainText('indirect objects');
  let edits = 0;
  page.on('request', request => {
    if (request.url().endsWith('/api/documents/edit')) {
      edits++;
    }
  });
  await drag(15, 15, true);
  expect(edits).toBe(0);
  await page.getByLabel('Open PDF', { exact: true }).setInputFiles(path.resolve('../PdfObjectKit.SnapshotTests/Fixtures/images.pdf'));
  await expect(page.getByRole('status')).toContainText('indirect objects');
  await page.locator('.object').filter({ hasText: 'image' }).first().click();
  const original = JSON.parse(await page.locator('.inspector pre').textContent());
  await drag(12, 8);
  await expect.poll(async () => JSON.parse(await page.locator('.inspector pre').textContent()).corners[0].x).toBeCloseTo(original.corners[0].x + 12);
  const updated = JSON.parse(await page.locator('.inspector pre').textContent());
  expect(updated.corners[0].y).toBeCloseTo(original.corners[0].y - 8);
  expect(updated.encodedDataSha256).toBe(original.encodedDataSha256);
  await expect(page.getByRole('alert')).toHaveCount(0);
});


test('moving a clipped Form image moves its border and preserves other copies', async ({ page }) => {
  const content = 'q 20 20 60 60 re W n 60 0 0 60 20 20 cm /Fm Do Q q 30 0 0 30 10 100 cm /Fm Do Q';
  const form = '/Im Do';
  const objects = [
    '<< /Type /Catalog /Pages 2 0 R >>',
    '<< /Type /Pages /Kids [3 0 R] /Count 1 >>',
    '<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << /XObject << /Fm 5 0 R >> >> /Contents 4 0 R >>',
    `<< /Length ${content.length} >>\nstream\n${content}\nendstream`,
    `<< /Type /XObject /Subtype /Form /BBox [0 0 1 1] /Resources << /XObject << /Im 6 0 R >> >> /Length ${form.length} >>\nstream\n${form}\nendstream`,
    '<< /Type /XObject /Subtype /Image /Width 1 /Height 1 /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /ASCIIHexDecode /Length 7 >>\nstream\nFF0000>\nendstream',
  ];
  let pdf = '%PDF-1.7\n';
  const offsets = [0];
  objects.forEach((value, i) => {
    offsets.push(Buffer.byteLength(pdf));
    pdf += `${i + 1} 0 obj\n${value}\nendobj\n`;
  });
  const xref = Buffer.byteLength(pdf);
  pdf += `xref\n0 7\n0000000000 65535 f \n${offsets.slice(1).map(n => `${String(n).padStart(10, '0')} 00000 n \n`).join('')}trailer\n<< /Size 7 /Root 1 0 R >>\nstartxref\n${xref}\n%%EOF\n`;
  await page.goto('/');
  await page.getByLabel('Open PDF', { exact: true }).setInputFiles({ name: 'clipped.pdf', mimeType: 'application/pdf', buffer: Buffer.from(pdf) });
  await expect(page.getByRole('status')).toContainText('indirect objects');
  async function pixel(x, y) {
    return page.locator('.page > div > canvas').evaluate((canvas, point) => {
      const ratio = canvas.width / 200;
      return [...canvas.getContext('2d').getImageData(point.x * ratio, point.y * ratio, 1, 1).data];
    }, { x, y });
  }
  expect(await pixel(50, 150)).toEqual([255, 0, 0, 255]);
  await page.locator('.object').filter({ hasText: 'image' }).first().click();
  const bounds = await page.getByLabel('Move selected object').boundingBox();
  await page.mouse.move(bounds.x + bounds.width / 2, bounds.y + bounds.height / 2);
  await page.mouse.down();
  await page.mouse.move(bounds.x + bounds.width / 2 + 80, bounds.y + bounds.height / 2, { steps: 5 });
  await page.mouse.up();
  await expect.poll(() => pixel(130, 150)).toEqual([255, 0, 0, 255]);
  expect(await pixel(50, 150)).toEqual([255, 255, 255, 255]);
  expect(await pixel(25, 85)).toEqual([255, 0, 0, 255]);
  await expect(page.getByRole('alert')).toHaveCount(0);
});


for (const clip of ['none', 'path', 'form']) {
  for (const show of ['(Hello ) Tj (world) Tj', '[(Hello ) -50 (world)] TJ', '(Hello) Tj ( ) Tj (world) Tj', '[(Hello) 0 ( ) -50 (world)] TJ']) {
    test(`dragging a text segment preserves rendered run spacing (${clip}): ${show}`, async ({ page }) => {
      const text = `BT /F1 12 Tf 20 120 Td ${show} ET`;
      const content = (clip === 'path' ? `q 15 115 80 20 re W n ${text} Q` : clip === 'form' ? '/TextForm Do' : text) + ' BT /F1 12 Tf 20 40 Td (Other text) Tj ET';
      const objects = [
        '<< /Type /Catalog /Pages 2 0 R >>',
        '<< /Type /Pages /Kids [3 0 R] /Count 1 >>',
        '<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Rotate 90 /Resources << /Font << /F1 5 0 R >> /XObject << /TextForm 6 0 R >> >> /Contents 4 0 R >>',
        `<< /Length ${content.length} >>\nstream\n${content}\nendstream`,
        '<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>',
        `<< /Type /XObject /Subtype /Form /BBox [15 115 95 135] /Resources << /Font << /F1 5 0 R >> >> /Length ${text.length} >>\nstream\n${text}\nendstream`,
      ];
      let pdf = '%PDF-1.7\n';
      const offsets = [0];
      objects.forEach((value, i) => {
        offsets.push(Buffer.byteLength(pdf));
        pdf += `${i + 1} 0 obj\n${value}\nendobj\n`;
      });
      const xref = Buffer.byteLength(pdf);
      pdf += `xref\n0 7\n0000000000 65535 f \n${offsets.slice(1).map(n => `${String(n).padStart(10, '0')} 00000 n \n`).join('')}trailer\n<< /Size 7 /Root 1 0 R >>\nstartxref\n${xref}\n%%EOF\n`;
      await page.goto('/');
      await page.getByLabel('Open PDF', { exact: true }).setInputFiles({ name: 'text.pdf', mimeType: 'application/pdf', buffer: Buffer.from(pdf) });
      await expect(page.getByRole('status')).toContainText('indirect objects');
      await page.locator('.object').filter({ hasText: 'Hello world' }).click();
      const before = JSON.parse(await page.locator('.inspector pre').textContent());
      expect(before.runs).toHaveLength(2);
      const box = await page.getByLabel('Move selected object').boundingBox();
      async function ink(x, y, width, height) {
        return page.locator('.page > div > canvas').evaluate((canvas, r) => {
          const scale = canvas.width / 200;
          const pixels = canvas.getContext('2d').getImageData(r.x * scale, r.y * scale, r.width * scale, r.height * scale).data;
          let count = 0;
          for (let i = 0; i < pixels.length; i += 4) {
            if (pixels[i] < 150) {
              count++;
            }
          }
          return count;
        }, { x, y, width, height });
      }
      async function pixels(x, y) {
        return page.locator('.page > div > canvas').evaluate((canvas, r) => {
          const scale = canvas.width / 200;
          return [...canvas.getContext('2d').getImageData(r.x * scale, r.y * scale, 20 * scale, 75 * scale).data];
        }, { x, y });
      }
      const originalPixels = await pixels(105, 15);
      const originalInk = await ink(105, 15, 20, 75);
      expect(originalInk).toBeGreaterThan(30);
      await page.mouse.move(box.x + box.width / 2, box.y + box.height / 2);
      await page.mouse.down();
      await page.mouse.move(box.x + box.width / 2 + 35, box.y + box.height / 2 + 15, { steps: 5 });
      await page.mouse.up();
      await expect.poll(async () => JSON.parse(await page.locator('.inspector pre').textContent()).left).toBeCloseTo(before.left + 15);
      const after = JSON.parse(await page.locator('.inspector pre').textContent());
      expect(after.baseline).toBeCloseTo(before.baseline + 35);
      expect(after.runs.map(run => [run.text, run.fontName, run.fontSize])).toEqual(before.runs.map(run => [run.text, run.fontName, run.fontSize]));
      expect(await ink(105, 15, 20, 75)).toBe(0);
      expect(await pixels(140, 30)).toEqual(originalPixels);
      for (const direction of [-1, 1, -1]) {
        const bounds = await page.getByLabel('Move selected object').boundingBox();
        await page.mouse.move(bounds.x + bounds.width / 2, bounds.y + bounds.height / 2);
        await page.mouse.down();
        await page.mouse.move(bounds.x + bounds.width / 2 + direction * 35, bounds.y + bounds.height / 2 + direction * 15, { steps: 5 });
        await page.mouse.up();
        const atOriginal = direction === -1;
        await expect.poll(async () => JSON.parse(await page.locator('.inspector pre').textContent()).left).toBeCloseTo(before.left + (atOriginal ? 0 : 15));
        expect(await pixels(atOriginal ? 105 : 140, atOriginal ? 15 : 30)).toEqual(originalPixels);
      }
      await page.getByRole('button', { name: 'Delete whole line', exact: true }).click();
      await expect(page.locator('.object').filter({ hasText: 'Hello world' })).toHaveCount(0);
      await expect(page.locator('.inspector pre')).toHaveCount(0);
      expect(await ink(105, 15, 20, 75)).toBe(0);
      await expect(page.locator('.object').filter({ hasText: 'Other text' })).toHaveCount(1);
      await expect(page.getByRole('alert')).toHaveCount(0);
    });
  }
}


test('move text from a local reproduction PDF beyond its original clipping edge', async ({ page }) => {
  test.skip(!process.env.PLAYGROUND_REPRO_PDF || !process.env.PLAYGROUND_REPRO_TEXT, 'Optional local PDF and text; never commit personal fixtures.');
  await page.goto('/');
  await page.getByLabel('Open PDF', { exact: true }).setInputFiles(process.env.PLAYGROUND_REPRO_PDF);
  await expect(page.getByRole('status')).toContainText('indirect objects');
  await expect(page.locator('.page > div > canvas')).toBeVisible();
  await page.locator('.object').filter({ hasText: process.env.PLAYGROUND_REPRO_TEXT }).first().click();
  const box = await page.getByLabel('Move selected object').boundingBox();
  const canvas = await page.locator('.page > div > canvas').boundingBox();
  const details = JSON.parse(await page.locator('.inspector pre').textContent());
  const x = Math.floor(details.left);
  const y = Math.floor(canvas.height - details.baseline - details.height);
  const width = Math.ceil(details.right - x);
  const height = Math.ceil(details.height * 1.2 + 1);
  async function pixels(x, y) {
    return page.locator('.page > div > canvas').evaluate((canvas, r) => {
      const scale = canvas.width / canvas.getBoundingClientRect().width;
      return [...canvas.getContext('2d').getImageData(r.x * scale, r.y * scale, r.width * scale, r.height * scale).data];
    }, { x, y, width, height });
  }
  const before = await pixels(x, y);
  for (const [left, baseline] of [[details.left, details.baseline + 20], [details.left, details.baseline + 40], [details.left, details.baseline]]) {
    const current = JSON.parse(await page.locator('.inspector pre').textContent());
    const bounds = await page.getByLabel('Move selected object').boundingBox();
    await page.mouse.move(bounds.x + bounds.width / 2, bounds.y + bounds.height / 2);
    await page.mouse.down();
    await page.mouse.move(bounds.x + bounds.width / 2 + left - current.left, bounds.y + bounds.height / 2 - baseline + current.baseline, { steps: 8 });
    await page.mouse.up();
    await expect.poll(async () => JSON.parse(await page.locator('.inspector pre').textContent()).baseline).toBeCloseTo(baseline);
    const movedPixels = await pixels(x, y - baseline + details.baseline);
    expect(movedPixels.filter((v, i) => i % 4 === 0 && v < 150).length).toBeGreaterThan(20);
  }
  await page.screenshot({ path: 'test-results/local-text-move.png' });
  const after = await pixels(x, y);
  // The destination can contain other artwork (this example crosses a table border).
  // Check original dark glyph pixels remain visible without requiring blank background.
  let ink = 0;
  let retained = 0;
  for (let i = 0; i < before.length; i += 4) {
    if (before[i] < 100 && before[i + 1] < 100 && before[i + 2] < 100) {
      ink++;
      const pixel = i / 4;
      const column = pixel % width;
      const row = Math.floor(pixel / width);
      let visible = false;
      // Allow one pixel of font rasterization variation after translation.
      for (let yy = Math.max(0, row - 1); yy <= Math.min(height - 1, row + 1); yy++) {
        for (let xx = Math.max(0, column - 1); xx <= Math.min(width - 1, column + 1); xx++) {
          const neighbor = (yy * width + xx) * 4;
          visible ||= after[neighbor] < 150 && after[neighbor + 1] < 150 && after[neighbor + 2] < 150;
        }
      }
      if (visible) {
        retained++;
      }
    }
  }
  expect(ink).toBeGreaterThan(20);
  expect(retained / ink).toBeGreaterThan(.98);
});


test('Delete removes a selected form field but preserves keyboard editing in its input', async ({ page }) => {
  await page.goto('/');
  await page.getByLabel('Open PDF', { exact: true }).setInputFiles({ name: 'form.pdf', mimeType: 'application/pdf', buffer: rotatedFormPdf() });
  await expect(page.getByRole('status')).toContainText('indirect objects');
  await page.locator('.object').filter({ hasText: 'FullName' }).click();
  await page.getByLabel('New value', { exact: true }).focus();
  await page.keyboard.press('Delete');
  await expect(page.locator('.object').filter({ hasText: 'FullName' })).toHaveCount(1);
  await page.locator('.object').filter({ hasText: 'FullName' }).click();
  await page.keyboard.press('Delete');
  await expect(page.locator('.object').filter({ hasText: 'FullName' })).toHaveCount(0);
  await expect(page.getByRole('alert')).toHaveCount(0);
});


for (const type of ['CheckBox', 'Choice', 'RadioButton']) {
  test(`create, edit and delete ${type} field`, async ({ page }) => {
    await page.goto('/');
    await page.getByLabel('Open PDF', { exact: true }).setInputFiles({ name: 'form.pdf', mimeType: 'application/pdf', buffer: rotatedFormPdf() });
    await expect(page.getByRole('status')).toContainText('indirect objects');
    await page.getByRole('button', { name: 'Form field', exact: true }).click();
    await page.getByLabel('Field type', { exact: true }).selectOption(type);
    await page.getByLabel('Field name', { exact: true }).fill('AddedField');
    await page.getByLabel('Initial field value', { exact: true }).selectOption(type === 'CheckBox' ? 'Yes' : type === 'Choice' ? 'Option 1' : 'Choice1');
    await page.getByRole('button', { name: 'Insert object', exact: true }).click();
    const widgets = page.locator('.object').filter({ hasText: 'AddedField' });
    await expect(widgets).toHaveCount(type === 'RadioButton' ? 2 : 1);
    await expect(page.getByRole('status')).toContainText('indirect objects');
    const canvas = page.locator('.page > div > canvas');
    const before = await canvas.evaluate(c => c.toDataURL());
    await widgets.first().click();
    await expect(page.locator('.inspector pre')).toContainText(type);
    if (type === 'Choice') {
      await page.getByLabel('New value', { exact: true }).fill('Option 2');
    } else {
      await page.getByLabel('New value', { exact: true }).selectOption(type === 'CheckBox' ? 'Off' : 'Choice2');
    }
    await page.getByRole('button', { name: 'Save', exact: true }).click();
    await expect(page.locator('.inspector pre')).toHaveCount(0);
    await expect(page.getByRole('status')).toContainText('indirect objects');
    expect(await canvas.evaluate(c => c.toDataURL())).not.toBe(before);
    await widgets.first().click();
    await expect(page.locator('.inspector pre')).toContainText(type === 'CheckBox' ? 'Off' : type === 'Choice' ? 'Option 2' : 'Choice2');
    await page.getByRole('button', { name: 'Delete field', exact: true }).click();
    await expect(widgets).toHaveCount(0);
    await expect(page.getByRole('alert')).toHaveCount(0);
  });
}

for (const type of ['MultilineText', 'ListBox']) {
  test(`create and edit ${type} field`, async ({ page }) => {
    await page.goto('/');
    await page.getByLabel('Open PDF', { exact: true }).setInputFiles({ name: 'form.pdf', mimeType: 'application/pdf', buffer: rotatedFormPdf() });
    await expect(page.getByRole('status')).toContainText('indirect objects');
    await page.getByRole('button', { name: 'Form field', exact: true }).click();
    await page.getByLabel('Field type', { exact: true }).selectOption(type);
    await page.getByLabel('Field name', { exact: true }).fill('NewMode');
    if (type === 'MultilineText') {
      await page.getByLabel('Text to insert', { exact: true }).fill('First line\nSecond line');
      await expect(page.getByLabel('Text placement preview').locator('text')).toHaveCount(2);
    } else {
      await page.getByLabel('Initial field value', { exact: true }).selectOption('Option 1');
      await expect(page.getByLabel('Text placement preview').locator('text')).toHaveCount(2);
    }
    await page.getByRole('button', { name: 'Insert object', exact: true }).click();
    const widget = page.locator('.object').filter({ hasText: 'NewMode' });
    await expect(widget).toHaveCount(1);
    await expect(page.getByRole('status')).toContainText('indirect objects');
    const canvas = page.locator('.page > div > canvas');
    const before = await canvas.evaluate(c => c.toDataURL());
    await widget.click();
    if (type === 'MultilineText') {
      await expect(page.getByLabel('New value', { exact: true })).toHaveValue('First line\nSecond line');
      await page.getByLabel('New value', { exact: true }).fill('Changed\nAnother line');
    } else {
      await page.getByLabel('New value', { exact: true }).selectOption('Option 2');
    }
    await page.getByRole('button', { name: 'Save', exact: true }).click();
    await expect(page.locator('.inspector pre')).toHaveCount(0);
    await expect(page.getByRole('status')).toContainText('indirect objects');
    expect(await canvas.evaluate(c => c.toDataURL())).not.toBe(before);
    await widget.click();
    await expect(page.getByLabel('New value', { exact: true })).toHaveValue(type === 'MultilineText' ? 'Changed\nAnother line' : 'Option 2');
    await expect(page.getByRole('alert')).toHaveCount(0);
  });
}

test('create, edit, reset and clear a multi-select list box', async ({ page }) => {
  await page.goto('/');
  await page.getByLabel('Open PDF', { exact: true }).setInputFiles({ name: 'form.pdf', mimeType: 'application/pdf', buffer: rotatedFormPdf() });
  await expect(page.getByRole('status')).toContainText('indirect objects');
  await page.getByRole('button', { name: 'Form field', exact: true }).click();
  await page.getByLabel('Field type', { exact: true }).selectOption('MultiSelectListBox');
  await page.getByLabel('Field name', { exact: true }).fill('Languages');
  await page.getByLabel('Field options', { exact: true }).fill('English\nFrench\nGerman');
  await page.getByRole('checkbox', { name: 'English', exact: true }).check();
  await page.getByRole('checkbox', { name: 'German', exact: true }).check();
  await expect(page.getByLabel('Text placement preview').locator('rect[fill="#3366cc"]')).toHaveCount(2);
  await page.getByRole('button', { name: 'Insert object', exact: true }).click();
  const widget = page.locator('.object').filter({ hasText: 'Languages' });
  await expect(widget).toHaveCount(1);
  await widget.click();
  await expect(page.getByRole('checkbox', { name: 'English', exact: true })).toBeChecked();
  await expect(page.getByRole('checkbox', { name: 'German', exact: true })).toBeChecked();
  await page.getByRole('checkbox', { name: 'English', exact: true }).uncheck();
  await page.getByRole('button', { name: 'Reset', exact: true }).click();
  await expect(page.getByRole('checkbox', { name: 'English', exact: true })).toBeChecked();
  await page.getByRole('checkbox', { name: 'English', exact: true }).uncheck();
  await page.getByRole('checkbox', { name: 'German', exact: true }).uncheck();
  await page.getByRole('checkbox', { name: 'French', exact: true }).check();
  await page.getByRole('button', { name: 'Save', exact: true }).click();
  await expect(page.locator('.inspector pre')).toHaveCount(0);
  await widget.click();
  await expect(page.getByRole('checkbox', { name: 'French', exact: true })).toBeChecked();
  await expect(page.getByRole('checkbox', { name: 'English', exact: true })).not.toBeChecked();
  await page.getByRole('checkbox', { name: 'French', exact: true }).uncheck();
  await page.getByRole('button', { name: 'Save', exact: true }).click();
  await expect(page.locator('.inspector pre')).toHaveCount(0);
  await widget.click();
  await expect(page.locator('.object-editor input[type="checkbox"]:checked')).toHaveCount(0);
  await expect(page.getByRole('alert')).toHaveCount(0);
});

for (const type of ['PushButton', 'Signature']) {
  test(`create, inspect and delete ${type} field`, async ({ page }) => {
    await page.goto('/');
    await page.getByLabel('Open PDF', { exact: true }).setInputFiles({ name: 'form.pdf', mimeType: 'application/pdf', buffer: rotatedFormPdf() });
    await expect(page.getByRole('status')).toContainText('indirect objects');
    await page.getByRole('button', { name: 'Form field', exact: true }).click();
    await page.getByLabel('Field type', { exact: true }).selectOption(type);
    await page.getByLabel('Field name', { exact: true }).fill('AppearanceField');
    if (type === 'PushButton') {
      await page.getByLabel('Button caption', { exact: true }).fill('Continue');
    }
    await expect(page.getByLabel('Text placement preview')).toContainText(type === 'PushButton' ? 'Continue' : 'Unsigned signature');
    await page.getByRole('button', { name: 'Insert object', exact: true }).click();
    const widget = page.locator('.object').filter({ hasText: 'AppearanceField' });
    await expect(widget).toHaveCount(1);
    await widget.click();
    await expect(page.locator('.inspector pre')).toContainText(type);
    if (type === 'PushButton') {
      await expect(page.getByLabel('New value', { exact: true })).toHaveValue('Continue');
      await page.getByLabel('New value', { exact: true }).fill('Next step');
      const canvas = page.locator('.page > div > canvas');
      const before = await canvas.evaluate(c => c.toDataURL());
      await page.getByRole('button', { name: 'Save', exact: true }).click();
      await expect(page.locator('.inspector pre')).toHaveCount(0);
      await expect(page.getByRole('status')).toContainText('indirect objects');
      expect(await canvas.evaluate(c => c.toDataURL())).not.toBe(before);
      await widget.click();
      await expect(page.getByLabel('New value', { exact: true })).toHaveValue('Next step');
    } else {
      await expect(page.getByLabel('New value', { exact: true })).toHaveCount(0);
      await expect(page.locator('.inspector-notice')).toContainText('digital signature controls');
    }
    await page.getByRole('button', { name: 'Delete field', exact: true }).click();
    await expect(widget).toHaveCount(0);
    await expect(page.getByRole('alert')).toHaveCount(0);
  });
}

for (const type of ['Password', 'Comb']) {
  test(`create and edit ${type} text field with character limit`, async ({ page }) => {
    await page.goto('/');
    await page.getByLabel('Open PDF', { exact: true }).setInputFiles({ name: 'form.pdf', mimeType: 'application/pdf', buffer: rotatedFormPdf() });
    await expect(page.getByRole('status')).toContainText('indirect objects');
    await page.getByRole('button', { name: 'Form field', exact: true }).click();
    await page.getByLabel('Field type', { exact: true }).selectOption(type);
    await page.getByLabel('Field name', { exact: true }).fill('TextMode');
    await page.getByLabel('Maximum characters', { exact: true }).fill('6');
    await page.getByLabel('Text to insert', { exact: true }).fill(type === 'Password' ? 'Secret' : 'ABC');
    await expect(page.getByLabel('Text to insert', { exact: true })).toHaveAttribute('maxlength', '6');
    const preview = page.getByLabel('Text placement preview');
    if (type === 'Password') {
      await expect(page.getByLabel('Text to insert', { exact: true })).toHaveAttribute('type', 'password');
      await expect(preview).toContainText('******');
      await expect(preview).not.toContainText('Secret');
    } else {
      await expect(preview.locator('text')).toHaveCount(3);
    }
    await page.getByRole('button', { name: 'Insert object', exact: true }).click();
    const widget = page.locator('.object').filter({ hasText: 'TextMode' });
    await expect(widget).toHaveCount(1);
    await widget.click();
    await expect(page.getByLabel('New value', { exact: true })).toHaveAttribute('maxlength', '6');
    if (type === 'Password') {
      await expect(page.getByLabel('New value', { exact: true })).toHaveAttribute('type', 'password');
      await expect(page.locator('.inspector pre')).not.toContainText('Secret');
    }
    await page.getByLabel('New value', { exact: true }).fill('XYZ');
    await page.getByRole('button', { name: 'Save', exact: true }).click();
    await expect(page.locator('.inspector pre')).toHaveCount(0);
    await widget.click();
    await expect(page.getByLabel('New value', { exact: true })).toHaveValue('XYZ');
    await page.getByRole('button', { name: 'PdfObjectKit (experimental)', exact: true }).click();
    await expect(page.locator('.page > div > canvas')).toHaveAttribute('data-renderer', 'native');
    await expect(page.getByRole('alert')).toHaveCount(0);
  });
}

for (const type of ['RichText', 'FileSelect']) {
  test(`create and edit ${type} advanced field`, async ({ page }) => {
    await page.goto('/');
    await page.getByLabel('Open PDF', { exact: true }).setInputFiles({ name: 'form.pdf', mimeType: 'application/pdf', buffer: rotatedFormPdf() });
    await expect(page.getByRole('status')).toContainText('indirect objects');
    await page.getByRole('button', { name: 'Form field', exact: true }).click();
    await page.getByLabel('Field type', { exact: true }).selectOption(type);
    await page.getByLabel('Field name', { exact: true }).fill('Advanced');
    if (type === 'RichText') {
      await page.getByLabel('Rich text XHTML', { exact: true }).fill('<body><p>Hello <b>bold</b></p></body>');
    } else {
      await page.getByLabel('Choose file name', { exact: true }).setInputFiles({ name: 'report.txt', mimeType: 'text/plain', buffer: Buffer.from('not uploaded') });
    }
    await page.getByRole('button', { name: 'Insert object', exact: true }).click();
    const widget = page.locator('.object').filter({ hasText: 'Advanced' });
    await expect(widget).toHaveCount(1);
    await widget.click();
    const value = type === 'RichText' ? '<body><p><i>Updated</i></p></body>' : 'updated.txt';
    await page.getByLabel('New value', { exact: true }).fill(value);
    await page.getByRole('button', { name: 'Save', exact: true }).click();
    await expect(page.locator('.inspector pre')).toHaveCount(0);
    await widget.click();
    if (type === 'RichText') {
      await expect(page.getByLabel('New value', { exact: true })).toHaveValue(/<i>Updated<\/i>/);
    } else {
      await expect(page.getByLabel('New value', { exact: true })).toHaveValue(value);
    }
    await page.getByRole('button', { name: 'PdfObjectKit (experimental)', exact: true }).click();
    await expect(page.locator('.page > div > canvas')).toHaveAttribute('data-renderer', 'native');
    await expect(page.getByRole('alert')).toHaveCount(0);
  });
}

test('configure and remove a pushbutton action', async ({ page }) => {
  await page.goto('/');
  await page.getByLabel('Open PDF', { exact: true }).setInputFiles({ name: 'form.pdf', mimeType: 'application/pdf', buffer: rotatedFormPdf() });
  await expect(page.getByRole('status')).toContainText('indirect objects');
  await page.getByRole('button', { name: 'Form field', exact: true }).click();
  await page.getByLabel('Field type', { exact: true }).selectOption('PushButton');
  await page.getByLabel('Field name', { exact: true }).fill('ActionButton');
  await page.getByRole('button', { name: 'Insert object', exact: true }).click();
  const widget = page.locator('.object').filter({ hasText: 'ActionButton' });
  await widget.click();
  await page.getByLabel('Button action', { exact: true }).selectOption('OpenUrl');
  await page.getByLabel('Action URL', { exact: true }).fill('https://example.com/');
  await page.getByRole('button', { name: 'Save action', exact: true }).click();
  await expect(page.locator('.inspector pre')).toHaveCount(0);
  await widget.click();
  await expect(page.getByLabel('Button action', { exact: true })).toHaveValue('OpenUrl');
  await page.getByLabel('Button action', { exact: true }).selectOption('None');
  await page.getByRole('button', { name: 'Save action', exact: true }).click();
  await expect(page.locator('.inspector pre')).toHaveCount(0);
  await widget.click();
  await expect(page.getByLabel('Button action', { exact: true })).toHaveValue('None');
});

test('sign a PDF with an ephemeral test certificate and reject later edits', async ({ page }) => {
  test.setTimeout(120000);
  const { mkdtemp, readFile, rm, rmdir } = await import('node:fs/promises');
  const { tmpdir } = await import('node:os');
  const { join, resolve } = await import('node:path');
  const { execFileSync } = await import('node:child_process');
  const directory = await mkdtemp(join(tmpdir(), 'pdfobjectkit-signing-test-'));
  const path = join(directory, 'test.pfx');
  try {
    execFileSync('dotnet', ['run', '--project', resolve('../PdfObjectKit.Tests'), '-c', 'SigningBrowserFixture', '--no-restore'], { env: { ...process.env, PDFOBJECTKIT_TEST_CERTIFICATE_PATH: path }, timeout: 60000, stdio: 'pipe' });
    const certificate = await readFile(path);
    await page.goto('/');
    await page.getByLabel('Open PDF', { exact: true }).setInputFiles({ name: 'form.pdf', mimeType: 'application/pdf', buffer: rotatedFormPdf() });
    await expect(page.getByRole('status')).toContainText('indirect objects');
    await page.getByRole('button', { name: 'Form field', exact: true }).click();
    await page.getByLabel('Field type', { exact: true }).selectOption('Signature');
    await page.getByLabel('Field name', { exact: true }).fill('Signature');
    await page.getByRole('button', { name: 'Insert object', exact: true }).click();
    const widget = page.locator('.object').filter({ hasText: 'Signature' });
    await widget.click();
    await page.getByLabel('Signing certificate', { exact: true }).setInputFiles({ name: 'test.pfx', mimeType: 'application/x-pkcs12', buffer: certificate });
    await page.getByLabel('Certificate password', { exact: true }).fill('test-only');
    await page.getByRole('button', { name: 'Sign PDF', exact: true }).click();
    await expect(page.locator('.inspector pre')).toHaveCount(0);
    await widget.click();
    await expect(page.locator('.inspector pre')).toContainText('"isSigned": true');
    await expect(page.getByRole('button', { name: 'Sign PDF', exact: true })).toHaveCount(0);
    await page.getByRole('button', { name: 'Delete field', exact: true }).click();
    await expect(page.getByRole('alert')).toContainText('Editing would invalidate');
  } finally {
    await rm(path, { force: true });
    await rmdir(directory);
  }
});
