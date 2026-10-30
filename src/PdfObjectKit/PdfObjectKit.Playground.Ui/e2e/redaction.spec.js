import { test, expect } from '@playwright/test';
import { readFileSync } from 'node:fs';
import { toolbarAction } from './toolbar.js';
const api = process.env.PLAYGROUND_API_URL ?? 'http://localhost:5080';
function fixture(fontName = 'Helvetica') {
  const content = 'BT /F1 18 Tf 20 250 Td (SECRET) Tj 0 -150 Td (PUBLIC) Tj ET';
  const objects = ['<< /Type /Catalog /Pages 2 0 R >>', '<< /Type /Pages /Kids [3 0 R 6 0 R] /Count 2 >>',
    '<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 300] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>',
    `<< /Type /Font /Subtype /Type1 /BaseFont /${fontName} >>`, `<< /Length ${content.length} >>\nstream\n${content}\nendstream`,
    '<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 200] /Rotate 90 /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>',
    '<< /Title (METADATA SECRET) /Author (PRIVATE AUTHOR) >>'];
  let pdf = '%PDF-1.7\n';
  const offsets = [];
  objects.forEach((object, index) => {
    offsets.push(pdf.length);
    pdf += `${index + 1} 0 obj\n${object}\nendobj\n`;
  });
  const xref = pdf.length;
  pdf += `xref\n0 ${objects.length + 1}\n0000000000 65535 f \n` + offsets.map(offset => `${String(offset).padStart(10, '0')} 00000 n \n`).join('');
  pdf += `trailer\n<< /Size ${objects.length + 1} /Root 1 0 R /Info 7 0 R >>\nstartxref\n${xref}\n%%EOF\n`;
  return Buffer.from(pdf);
}

test('image-only conversion preserves visible content and rotated dimensions in a new tab', async ({ page }) => {
  await page.goto('/');
  await page.getByLabel('Open PDF', { exact: true }).setInputFiles({ name: 'convert.pdf', mimeType: 'application/pdf', buffer: fixture() });
  await expect(page.locator('.object').filter({ hasText: 'SECRET' })).toBeVisible();
  await (await toolbarAction(page, 'Convert to image-only PDF')).click();
  const dialog = page.getByRole('dialog', { name: 'Convert to image-only PDF', exact: true });
  await expect(dialog.getByLabel('Image resolution')).toHaveValue('144');
  await dialog.getByLabel('Image resolution').selectOption('72');
  await dialog.screenshot({ path: 'test-results/image-only-conversion.png' });
  const responsePromise = page.waitForResponse(response => response.url().includes('/redact-raster'));
  await dialog.getByRole('button', { name: 'Convert to new tab' }).click();
  const response = await responsePromise;
  expect(response.ok()).toBe(true);
  const bytes = await response.body();
  expect(bytes.includes(Buffer.from('METADATA SECRET'))).toBe(false);
  await expect(page.getByRole('tab', { name: 'convert-image-only.pdf', exact: true })).toHaveAttribute('aria-selected', 'true');
  await expect(page.locator('.object .badge', { hasText: /^text$/ })).toHaveCount(0);
  const canvas = page.locator('.page > div > canvas').first();
  await expect(canvas).toHaveJSProperty('width', 300);
  const darkPixels = await canvas.evaluate(canvas => {
    const pixels = canvas.getContext('2d').getImageData(20, 30, 100, 30).data;
    let count = 0;
    for (let i = 0; i < pixels.length; i += 4) {
      if (pixels[i] < 100 && pixels[i + 1] < 100 && pixels[i + 2] < 100) {
        count++;
      }
    }
    return count;
  });
  expect(darkPixels).toBeGreaterThan(100);
  expect(darkPixels).toBeLessThan(1500);
  await page.getByLabel('Page', { exact: true }).selectOption('2');
  await expect(canvas).toHaveJSProperty('width', 200);
  await expect(canvas).toHaveJSProperty('height', 300);
  await page.getByRole('tab', { name: 'convert.pdf', exact: true }).click();
  await expect(page.locator('.object').filter({ hasText: 'SECRET' })).toBeVisible();
});

test('image-only conversion reports errors and cancels without opening a result', async ({ page }) => {
  await page.goto('/');
  await page.getByLabel('Open PDF', { exact: true }).setInputFiles({ name: 'convert.pdf', mimeType: 'application/pdf', buffer: fixture() });
  await (await toolbarAction(page, 'Convert to image-only PDF')).click();
  const dialog = page.getByRole('dialog');
  await page.route('**/api/documents/redact-raster', route => route.fulfill({ status: 422, contentType: 'application/problem+json', body: JSON.stringify({ detail: 'Choose a lower resolution.' }) }));
  await dialog.getByRole('button', { name: 'Convert to new tab' }).click();
  await expect(dialog.getByRole('alert')).toHaveText('Choose a lower resolution.');
  await expect(page.getByRole('tab')).toHaveCount(1);
  await page.unroute('**/api/documents/redact-raster');
  let release;
  const gate = new Promise(resolve => {
    release = resolve;
  });
  await page.route('**/api/documents/redact-raster', async route => {
    await gate;
    await route.abort().catch(() => {});
  });
  await dialog.getByRole('button', { name: 'Convert to new tab' }).click();
  await dialog.getByRole('button', { name: 'Cancel conversion' }).click();
  release();
  await expect(dialog.getByRole('button', { name: 'Convert to new tab' })).toBeEnabled();
  await expect(page.getByRole('tab')).toHaveCount(1);
  await dialog.getByRole('button', { name: 'Cancel', exact: true }).click();
  await expect(page.locator('.object').filter({ hasText: 'SECRET' })).toBeVisible();
});

test('image-only redaction removes pixels and hidden structures in a separate tab', async ({ page }) => {
  await page.goto('/');
  await page.getByLabel('Open PDF', { exact: true }).setInputFiles({ name: 'sensitive.pdf', mimeType: 'application/pdf', buffer: fixture() });
  await expect(page.locator('.object').filter({ hasText: 'SECRET' })).toBeVisible();
  await (await toolbarAction(page, 'Redact PDF')).click();
  await page.getByRole('dialog').getByLabel('Redaction mode').selectOption('raster');
  const dialog = page.getByRole('dialog', { name: 'Redact PDF (image-only)', exact: true });
  await expect(dialog.getByRole('button', { name: 'Export redacted PDF' })).toBeDisabled();
  const image = dialog.getByRole('img', { name: 'Page 1 to redact' });
  await expect(image).toBeVisible();
  const box = await image.boundingBox();
  await page.mouse.move(box.x + box.width * .03, box.y + box.height * .08);
  await page.mouse.down();
  await page.mouse.move(box.x + box.width * .7, box.y + box.height * .23, { steps: 4 });
  await page.mouse.up();
  await expect(dialog.locator('.redaction-mark')).toHaveCount(1);
  await dialog.getByLabel('Redaction page', { exact: true }).selectOption('2');
  await expect(dialog.getByRole('img', { name: 'Page 2 to redact' })).toBeVisible();
  await dialog.getByRole('button', { name: 'Mark entire page' }).click();
  await dialog.getByLabel('Redaction page', { exact: true }).selectOption('1');
  await expect(dialog.locator('.redaction-mark')).toHaveCount(1);
  await dialog.getByRole('checkbox').check();
  await dialog.screenshot({ path: 'test-results/redaction-review.png' });
  const responsePromise = page.waitForResponse(response => response.url().includes('/redact-raster'));
  await dialog.getByRole('button', { name: 'Export redacted PDF' }).click();
  const response = await responsePromise;
  expect(response.ok(), await response.text()).toBe(true);
  const bytes = await response.body();
  expect(bytes.includes(Buffer.from('METADATA SECRET'))).toBe(false);
  expect(bytes.includes(Buffer.from('PRIVATE AUTHOR'))).toBe(false);
  await expect(page.getByRole('tab', { name: 'sensitive-redacted.pdf', exact: true })).toHaveAttribute('aria-selected', 'true');
  await expect(page.locator('.object .badge', { hasText: /^text$/ })).toHaveCount(0);
  const canvas = page.locator('.page > div > canvas').first();
  const sample = await canvas.evaluate(canvas => {
    const context = canvas.getContext('2d');
    const pixel = (x, y) => Array.from(context.getImageData(Math.floor(canvas.width * x), Math.floor(canvas.height * y), 1, 1).data).slice(0, 3);
    return { masked: pixel(.2, .15), unmasked: pixel(.8, .8) };
  });
  expect(sample.masked).toEqual([0, 0, 0]);
  expect(sample.unmasked).toEqual([255, 255, 255]);
  await page.getByLabel('Page', { exact: true }).selectOption('2');
  await expect(canvas).toHaveJSProperty('width', 200);
  await expect(canvas).toHaveJSProperty('height', 300);
  await page.getByRole('tab', { name: 'sensitive.pdf', exact: true }).click();
  await expect(page.locator('.object').filter({ hasText: 'SECRET' })).toBeVisible();
});

test('raster API rejects invalid pixels and independently overwrites submitted regions', async ({ request }) => {
  const spec = { pixelWidth: 10, pixelHeight: 10, widthPoints: 100, heightPoints: 100, regions: [{ x: 0, y: 0, width: 10, height: 10 }] };
  const invalid = await request.post(api + '/api/documents/redact-raster', { multipart: { manifest: JSON.stringify([spec]), pages: { name: 'page.rgb', mimeType: 'application/octet-stream', buffer: Buffer.from('bad') } } });
  expect(invalid.status()).toBe(400);
  const valid = await request.post(api + '/api/documents/redact-raster', { multipart: { manifest: JSON.stringify([spec]), pages: { name: 'page.rgb', mimeType: 'application/octet-stream', buffer: Buffer.alloc(300, 255) } } });
  expect(valid.ok()).toBe(true);
  const { inflateSync } = await import('node:zlib');
  const bytes = await valid.body();
  const text = bytes.toString('latin1');
  const start = text.indexOf('stream\n') + 7;
  const end = text.indexOf('\nendstream', start);
  expect(Array.from(inflateSync(bytes.subarray(start, end))).every(value => value === 0)).toBe(true);
});

test('failed and cancelled redaction exports preserve marks and original document', async ({ page }) => {
  await page.goto('/');
  await page.getByLabel('Open PDF', { exact: true }).setInputFiles({ name: 'review.pdf', mimeType: 'application/pdf', buffer: fixture() });
  await (await toolbarAction(page, 'Redact PDF')).click();
  await page.getByRole('dialog').getByLabel('Redaction mode').selectOption('raster');
  const dialog = page.getByRole('dialog', { name: 'Redact PDF (image-only)', exact: true });
  await expect(dialog.getByRole('img')).toBeVisible();
  await dialog.getByRole('button', { name: 'Mark entire page' }).click();
  await dialog.getByRole('button', { name: 'Remove redaction 1' }).click();
  await expect(dialog.locator('.redaction-mark')).toHaveCount(0);
  await dialog.getByRole('button', { name: 'Mark entire page' }).click();
  await dialog.getByRole('checkbox').check();
  await page.route('**/api/documents/redact-raster', route => route.fulfill({ status: 422, contentType: 'application/problem+json', body: JSON.stringify({ detail: 'Test export rejection' }) }));
  await dialog.getByRole('button', { name: 'Export redacted PDF' }).click();
  await expect(dialog.getByRole('alert')).toHaveText('Test export rejection');
  await expect(dialog.locator('.redaction-mark')).toHaveCount(1);
  await expect(page.getByRole('tab')).toHaveCount(1);
  await page.unroute('**/api/documents/redact-raster');
  let release;
  const gate = new Promise(resolve => { release = resolve; });
  await page.route('**/api/documents/redact-raster', async route => {
    await gate;
    await route.abort().catch(() => {});
  });
  await dialog.getByRole('button', { name: 'Export redacted PDF' }).click();
  await expect(dialog.getByRole('button', { name: 'Cancel export' })).toBeVisible();
  await dialog.getByRole('button', { name: 'Cancel export' }).click();
  release();
  await expect(dialog.getByRole('button', { name: 'Export redacted PDF' })).toBeEnabled();
  await dialog.getByRole('button', { name: 'Cancel', exact: true }).click();
  await expect(page.locator('.object').filter({ hasText: 'SECRET' })).toBeVisible();
  await expect(page.getByRole('tab')).toHaveCount(1);
});

test('content-preserving export keeps unrelated text searchable and removes selected text', async ({ page }) => {
  await page.goto('/');
  await page.getByLabel('Open PDF', { exact: true }).setInputFiles({ name: 'content.pdf', mimeType: 'application/pdf', buffer: Buffer.from(fixture().toString().replace('/MediaBox [0 0 300 200]', '/MediaBox [0 0 300 300]')) });
  await (await toolbarAction(page, 'Redact PDF')).click();
  const dialog = page.getByRole('dialog');
  await expect(dialog.getByRole('button', { name: 'Previous redaction page' })).toBeDisabled();
  await dialog.getByRole('button', { name: 'Next redaction page' }).click();
  await expect(dialog.getByLabel('Redaction page', { exact: true })).toHaveValue('2');
  await dialog.getByRole('button', { name: 'Previous redaction page' }).click();
  await expect(dialog.getByLabel('Redaction mode')).toHaveValue('content');
  await expect(dialog.getByLabel('Redaction resolution')).toHaveCount(0);
  const image = dialog.getByRole('img', { name: 'Page 1 to redact' });
  await expect(image).toBeVisible();
  const box = await image.boundingBox();
  await page.mouse.move(box.x + box.width * .03, box.y + box.height * .07);
  await page.mouse.down();
  await page.mouse.move(box.x + box.width * .6, box.y + box.height * .25, { steps: 5 });
  await page.mouse.up();
  await expect(dialog.locator('.redaction-mark')).toHaveCount(1);
  await expect(dialog.locator('.redaction-impact').first()).toBeVisible();
  await expect(dialog.getByLabel('Redaction impact')).toContainText('affected areas');
  await dialog.getByRole('button', { name: 'View affected areas on page 2' }).click();
  await expect(dialog.getByRole('img', { name: 'Page 2 to redact' })).toBeVisible();
  await expect(dialog.locator('.redaction-impact').first()).toBeVisible();
  await expect(dialog.locator('.redaction-mark')).toHaveCount(0);
  const sheet = await dialog.locator('.redaction-sheet').boundingBox();
  const affected = await dialog.locator('.redaction-impact').first().boundingBox();
  expect(affected.x).toBeGreaterThanOrEqual(sheet.x);
  expect(affected.x + affected.width).toBeLessThanOrEqual(sheet.x + sheet.width + 1);
  await dialog.getByRole('button', { name: 'View affected areas on page 1' }).click();
  await expect(dialog.getByRole('img', { name: 'Page 1 to redact' })).toBeVisible();
  await dialog.screenshot({ path: 'test-results/redaction-impact-preview.png' });
  await page.evaluate(() => {
    document.documentElement.dataset.theme = 'dark';
  });
  await dialog.screenshot({ path: 'test-results/redaction-popup-dark.png' });
  await page.setViewportSize({ width: 480, height: 900 });
  await expect(dialog.getByRole('button', { name: 'Close redaction' })).toBeVisible();
  await dialog.screenshot({ path: 'test-results/redaction-popup-mobile.png' });
  expect(await dialog.evaluate(element => element.scrollWidth <= element.clientWidth + 1)).toBe(true);
  await page.setViewportSize({ width: 1440, height: 1000 });
  await page.evaluate(() => {
    document.documentElement.dataset.theme = 'light';
  });
  await dialog.getByRole('checkbox').check();
  const responsePromise = page.waitForResponse(response => response.url().endsWith('/redact-content'));
  await dialog.getByRole('button', { name: 'Export redacted PDF' }).click();
  const response = await responsePromise;
  expect(response.ok(), await response.text()).toBe(true);
  await expect(page.getByRole('tab', { name: 'content-redacted.pdf', exact: true })).toHaveAttribute('aria-selected', 'true');
  await expect(page.locator('.object').filter({ hasText: 'PUBLIC' })).toBeVisible();
  await expect(page.locator('.object').filter({ hasText: 'SECRET' })).toHaveCount(0);
  const canvas = page.locator('.page > div > canvas').first();
  expect(await canvas.evaluate(canvas => Array.from(canvas.getContext('2d').getImageData(50, 45, 1, 1).data).slice(0, 3))).toEqual([0, 0, 0]);
  await page.getByRole('tab', { name: 'content.pdf', exact: true }).click();
  await expect(page.locator('.object').filter({ hasText: 'SECRET' })).toBeVisible();
});

test('unsupported content-preserving fonts reject without producing an output', async ({ request }) => {
  const response = await request.post(api + '/api/documents/redact-content', { multipart: {
    file: { name: 'custom.pdf', mimeType: 'application/pdf', buffer: fixture('UnsupportedFont') },
    regions: JSON.stringify([{ pageNumber: 1, x: 0, y: 230, width: 150, height: 60 }]),
  } });
  expect(response.status()).toBe(422);
  expect(await response.text()).toContain('Content redaction rejected');
});


for (const kind of ['CID', 'simple']) {
  test(`embedded ${kind} TrueType redaction keeps the remaining glyph visible and searchable`, async ({ page, request }) => {
    const font = readFileSync(new URL('../../PdfObjectKit.Tests/Fixtures/RenderingTest.ttf', import.meta.url)).toString('hex') + '>';
    const content = kind === 'simple' ? 'BT /F1 18 Tf 20 250 Td (A) Tj 0 -150 Td (B) Tj ET' : 'BT /F1 18 Tf 20 250 Td <0001> Tj 0 -150 Td <0002> Tj ET';
    let cmap = '/CIDInit /ProcSet findresource begin 12 dict begin begincmap /CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def /CMapName /Test def /CMapType 2 def 1 begincodespacerange <0000> <FFFF> endcodespacerange 2 beginbfchar <0001> <0041> <0002> <0042> endbfchar endcmap CMapName currentdict /CMap defineresource pop end end';
    if (kind === 'simple') {
      cmap = cmap.replace('<0000> <FFFF>', '<00> <FF>').replace('<0001> <0041>', '<41> <0041>').replace('<0002> <0042>', '<42> <0042>');
    }
    const stream = value => `<< /Length ${value.length} >>\nstream\n${value}\nendstream`;
    const objects = ['<< /Type /Catalog /Pages 2 0 R >>', '<< /Type /Pages /Kids [3 0 R] /Count 1 >>',
      '<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 300] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>',
      kind === 'simple' ? '<< /Type /Font /Subtype /TrueType /BaseFont /BCDEEE+ArialMT /Encoding /WinAnsiEncoding /FirstChar 65 /LastChar 66 /Widths [600 600] /FontDescriptor 7 0 R /ToUnicode 9 0 R >>' : '<< /Type /Font /Subtype /Type0 /BaseFont /BCDEEE+Calibri /Encoding /Identity-H /DescendantFonts [6 0 R] /ToUnicode 9 0 R >>', stream(content),
      '<< /Type /Font /Subtype /CIDFontType2 /BaseFont /BCDEEE+Calibri /CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> /FontDescriptor 7 0 R /CIDToGIDMap /Identity /W [1 [600 600]] >>',
      '<< /Type /FontDescriptor /FontName /BCDEEE+Calibri /Flags 32 /FontBBox [-100 -300 1000 1000] /Ascent 800 /Descent -200 /CapHeight 700 /ItalicAngle 0 /StemV 80 /FontFile2 8 0 R >>',
      `<< /Length ${font.length} /Filter /ASCIIHexDecode >>\nstream\n${font}\nendstream`, stream(cmap)];
    let pdf = '%PDF-1.7\n';
    const offsets = [];
    objects.forEach((object, index) => {
      offsets.push(pdf.length);
      pdf += `${index + 1} 0 obj\n${object}\nendobj\n`;
    });
    const xref = pdf.length;
    pdf += `xref\n0 ${objects.length + 1}\n0000000000 65535 f \n` + offsets.map(offset => `${String(offset).padStart(10, '0')} 00000 n \n`).join('');
    pdf += `trailer\n<< /Size ${objects.length + 1} /Root 1 0 R >>\nstartxref\n${xref}\n%%EOF\n`;
    const response = await request.post(api + '/api/documents/redact-content', { multipart: {
      file: { name: 'embedded.pdf', mimeType: 'application/pdf', buffer: Buffer.from(pdf) },
      regions: JSON.stringify([{ pageNumber: 1, x: 0, y: 230, width: 150, height: 60 }]),
    } });
    expect(response.ok(), await response.text()).toBe(true);
    await page.goto('/');
    await page.getByLabel('Open PDF', { exact: true }).setInputFiles({ name: 'subset.pdf', mimeType: 'application/pdf', buffer: await response.body() });
    await expect(page.getByRole('button', { name: 'text B', exact: true })).toBeVisible();
    const canvas = page.locator('.page > div > canvas').first();
    await expect.poll(async () => canvas.evaluate(canvas => {
      const context = canvas.getContext('2d');
      const data = context.getImageData(15, 175, 35, 30).data;
      let dark = 0;
      for (let i = 0; i < data.length; i += 4) {
        if (data[i] < 100 && data[i + 1] < 100 && data[i + 2] < 100 && data[i + 3] > 0) {
          dark++;
        }
      }
      return dark;
    })).toBeGreaterThan(15);
  });
}


test('content redaction preserves colors, blending, transparency groups and image alpha', async ({ page, request }) => {
  const commands = '/RGB cs 1 0 0 scn /Paint gs BT /F1 18 Tf 20 250 Td (SECRET) Tj 0 -150 Td (PUBLIC) Tj ET q 20 0 0 20 200 100 cm /Photo Do Q';
  const objects = ['<< /Type /Catalog /Pages 2 0 R >>', '<< /Type /Pages /Kids [3 0 R] /Count 1 >>',
    '<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 300] /Resources << /XObject << /Group 4 0 R >> >> /Contents 5 0 R >>',
    `<< /Type /XObject /Subtype /Form /BBox [0 0 300 300] /Group << /S /Transparency /CS /DeviceRGB /I true >> /Resources << /Font << /F1 6 0 R >> /ColorSpace << /RGB /DeviceRGB >> /ExtGState << /Paint 7 0 R >> /XObject << /Photo 8 0 R >> >> /Length ${commands.length} >>\nstream\n${commands}\nendstream`,
    '<< /Length 9 >>\nstream\n/Group Do\nendstream',
    '<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>',
    '<< /Type /ExtGState /BM /Multiply /D [[2 3] 0] /RI /Perceptual >>',
    '<< /Type /XObject /Subtype /Image /Width 1 /Height 1 /ColorSpace /DeviceRGB /BitsPerComponent 8 /SMask 9 0 R /Filter /ASCIIHexDecode /Length 7 >>\nstream\nFF0000>\nendstream',
    '<< /Type /XObject /Subtype /Image /Width 1 /Height 1 /ColorSpace /DeviceGray /BitsPerComponent 8 /Decode [1 0] /Filter /ASCIIHexDecode /Length 3 >>\nstream\n80>\nendstream'];
  let pdf = '%PDF-1.7\n';
  const offsets = [];
  objects.forEach((object, index) => {
    offsets.push(pdf.length);
    pdf += `${index + 1} 0 obj\n${object}\nendobj\n`;
  });
  const xref = pdf.length;
  pdf += `xref\n0 ${objects.length + 1}\n0000000000 65535 f \n` + offsets.map(offset => `${String(offset).padStart(10, '0')} 00000 n \n`).join('');
  pdf += `trailer\n<< /Size ${objects.length + 1} /Root 1 0 R >>\nstartxref\n${xref}\n%%EOF\n`;
  const response = await request.post(api + '/api/documents/redact-content', { multipart: {
    file: { name: 'colors.pdf', mimeType: 'application/pdf', buffer: Buffer.from(pdf) },
    regions: JSON.stringify([{ pageNumber: 1, x: 0, y: 230, width: 150, height: 60 }]),
  } });
  expect(response.ok(), await response.text()).toBe(true);
  await page.goto('/');
  await page.getByLabel('Open PDF', { exact: true }).setInputFiles({ name: 'colors-redacted.pdf', mimeType: 'application/pdf', buffer: await response.body() });
  await expect(page.locator('.object').filter({ hasText: 'PUBLIC' })).toBeVisible();
  await expect(page.locator('.object').filter({ hasText: 'SECRET' })).toHaveCount(0);
  const canvas = page.locator('.page > div > canvas').first();
  await expect.poll(async () => canvas.evaluate(canvas => {
    const [r, g, b] = canvas.getContext('2d').getImageData(210, 190, 1, 1).data;
    return r > 245 && g > 120 && g < 135 && b > 120 && b < 135;
  })).toBe(true);
  const red = await canvas.evaluate(canvas => {
    const pixels = canvas.getContext('2d').getImageData(15, 175, 100, 30).data;
    let count = 0;
    for (let index = 0; index < pixels.length; index += 4) {
      if (pixels[index] > 200 && pixels[index + 1] < 80 && pixels[index + 2] < 80) {
        count++;
      }
    }
    return count;
  });
  expect(red).toBeGreaterThan(40);
  expect(await canvas.evaluate(canvas => Array.from(canvas.getContext('2d').getImageData(50, 45, 1, 1).data).slice(0, 3))).toEqual([0, 0, 0]);
});


test('selecting 25 previews and removes only the final two digits of N 2025', async ({ page }) => {
  const bytes = Buffer.from(fixture().toString().replace('(SECRET)', '(N 2025)'));
  await page.goto('/');
  await page.getByLabel('Open PDF', { exact: true }).setInputFiles({ name: 'year.pdf', mimeType: 'application/pdf', buffer: bytes });
  await expect(page.locator('.object').filter({ hasText: 'N 2025' })).toBeVisible();
  await (await toolbarAction(page, 'Redact PDF')).click();
  const dialog = page.getByRole('dialog');
  const image = dialog.getByRole('img', { name: 'Page 1 to redact' });
  await expect(image).toBeVisible();
  const box = await image.boundingBox();
  await page.mouse.move(box.x + box.width * 58.016 / 300, box.y + box.height * .10);
  await page.mouse.down();
  await page.mouse.move(box.x + box.width * 79 / 300, box.y + box.height * .20, { steps: 5 });
  await page.mouse.up();
  await expect(dialog.locator('.redaction-impact[data-kind="text"]')).toHaveCount(2);
  await dialog.getByRole('checkbox').check();
  await dialog.screenshot({ path: 'test-results/redaction-precision-preview.png' });
  await dialog.getByRole('button', { name: 'Export redacted PDF' }).click();
  await expect(page.getByRole('tab', { name: 'year-redacted.pdf', exact: true })).toHaveAttribute('aria-selected', 'true');
  await expect(page.locator('.object').filter({ hasText: 'N 20' })).toBeVisible();
  await expect(page.locator('.object').filter({ hasText: 'N 2025' })).toHaveCount(0);
  await expect(page.locator('.object').filter({ hasText: 'PUBLIC' })).toBeVisible();
});
