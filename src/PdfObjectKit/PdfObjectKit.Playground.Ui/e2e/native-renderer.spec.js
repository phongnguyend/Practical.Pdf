import { toolbarAction } from './toolbar.js';
import { test, expect } from '@playwright/test';
import { readFile } from 'node:fs/promises';

function fixture(rotated = false, jpeg = null, gradient = false, masked = true) {
  const content = gradient ? '/Pattern cs /P scn 10 10 180 30 re f BT /F1 16 Tf 1 0 0 1 20 120 Tm (Hello) Tj ET' : '1 0 0 rg 10 10 30 30 re f q 20 0 0 20 60 10 cm /Im Do Q 0 g BT /F1 16 Tf 1 0 0 1 20 120 Tm (Hello) Tj ET q 1 0 0 1 100 0 cm /Fm Do Q';
  const form = '0 0 1 rg 10 10 30 30 re f';
  const objects = [
    '<< /Type /Catalog /Pages 2 0 R >>',
    '<< /Type /Pages /Kids [3 0 R] /Count 1 >>',
    `<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] ${rotated ? '/Rotate 90 /CropBox [0 0 180 180]' : ''} /Resources << ${gradient ? '/Pattern << /P << /PatternType 2 /Shading << /ShadingType 2 /ColorSpace /DeviceRGB /Coords [0 0 200 0] /Extend [true true] /Function << /FunctionType 2 /Domain [0 1] /C0 [1 0 0] /C1 [0 0 1] /N 1 >> >> >> >>' : ''} /Font << /F1 5 0 R >> /XObject << /Im 6 0 R /Fm 7 0 R >> >> /Contents 4 0 R >>`,
    `<< /Length ${content.length} >>\nstream\n${content}\nendstream`,
    '<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>',
    jpeg ? `<< /Type /XObject /Subtype /Image /Width 3 /Height 1 /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode ${masked ? '/SMask 8 0 R' : ''} /Length ${jpeg.length} >>\nstream\n${jpeg}\nendstream` : '<< /Type /XObject /Subtype /Image /Width 1 /Height 1 /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /ASCIIHexDecode /Length 7 >>\nstream\n00FF00>\nendstream',
    `<< /Type /XObject /Subtype /Form /BBox [0 0 50 50] /Length ${form.length} >>\nstream\n${form}\nendstream`,
  ];
  if (jpeg) {
    objects.push('<< /Type /XObject /Subtype /Image /Width 3 /Height 1 /ColorSpace /DeviceGray /BitsPerComponent 8 /Matte [0 0 0] /Filter /ASCIIHexDecode /Length 7 >>\nstream\n0080FF>\nendstream');
  }
  let pdf = '%PDF-1.7\n';
  const offsets = [];
  objects.forEach((value, index) => {
    offsets.push(Buffer.byteLength(pdf, 'latin1'));
    pdf += `${index + 1} 0 obj\n${value}\nendobj\n`;
  });
  const xref = Buffer.byteLength(pdf, 'latin1');
  pdf += `xref\n0 ${objects.length + 1}\n0000000000 65535 f \n${offsets.map(n => `${String(n).padStart(10, '0')} 00000 n \n`).join('')}trailer\n<< /Size ${objects.length + 1} /Root 1 0 R >>\nstartxref\n${xref}\n%%EOF\n`;
  return Buffer.from(pdf, 'latin1');
}

for (const rotated of [false, true]) {
  test(`native renderer paints paths, images, Forms and text; switch preserves geometry (rotated=${rotated})`, async ({ page }) => {
    await page.goto('/');
    await page.getByLabel('Open PDF', { exact: true }).setInputFiles({ name: 'render.pdf', mimeType: 'application/pdf', buffer: fixture(rotated) });
    await expect(page.getByRole('status')).toContainText('indirect objects');
    const canvas = page.locator('.page > div > canvas');
    await expect(canvas).toHaveAttribute('data-renderer', 'pdfjs');
    const baseline = await canvas.evaluate(c => c.toDataURL());
    const dimensions = await canvas.evaluate(c => [c.width, c.height]);
    await page.getByRole('button', { name: 'PdfObjectKit (experimental)', exact: true }).click();
    await expect(page.getByRole('status')).toContainText('indirect objects');
    await expect(canvas).toHaveAttribute('data-renderer', 'native');
    await expect(page.getByRole('alert')).toHaveCount(0);
    expect(await canvas.evaluate(c => [c.width, c.height])).toEqual(dimensions);
    const colors = await canvas.evaluate(c => {
      const counts = { red: 0, green: 0, blue: 0, black: 0 };
      const pixels = c.getContext('2d').getImageData(0, 0, c.width, c.height).data;
      for (let i = 0; i < pixels.length; i += 4) {
        const [r, g, b] = pixels.slice(i, i + 3);
        if (r > 200 && g < 20 && b < 20) {
          counts.red++;
        }
        if (g > 200 && r < 20 && b < 20) {
          counts.green++;
        }
        if (b > 200 && r < 20 && g < 20) {
          counts.blue++;
        }
        if (r < 100 && g < 100 && b < 100) {
          counts.black++;
        }
      }
      return counts;
    });
    expect(colors.red).toBeGreaterThan(500);
    expect(colors.green).toBeGreaterThan(200);
    expect(colors.blue).toBeGreaterThan(500);
    expect(colors.black).toBeGreaterThan(50);
    const samples = await canvas.evaluate((c, rotated) => {
      const scale = c.width / (rotated ? 180 : 200);
      return [[25, 25], [70, 20], [125, 25]].map(([x, y]) => {
        const point = rotated ? [y, x] : [x, 200 - y];
        return [...c.getContext('2d').getImageData(point[0] * scale, point[1] * scale, 1, 1).data].slice(0, 3);
      });
    }, rotated);
    expect(samples).toEqual([[255, 0, 0], [0, 255, 0], [0, 0, 255]]);
    await page.screenshot({ path: `test-results/native-renderer-${rotated}.png` });
    await page.getByRole('button', { name: 'PDF.js', exact: true }).click();
    await expect(page.getByRole('status')).toContainText('indirect objects');
    await expect(canvas).toHaveAttribute('data-renderer', 'pdfjs');
    expect(await canvas.evaluate(c => c.toDataURL())).toBe(baseline);
    await page.getByRole('button', { name: 'PdfObjectKit (experimental)', exact: true }).click();
    await expect(page.getByRole('status')).toContainText('indirect objects');
    await page.locator('.object').filter({ hasText: 'Hello' }).click();
    await page.getByRole('button', { name: 'Delete whole line', exact: true }).click();
    await expect(page.locator('.object').filter({ hasText: 'Hello' })).toHaveCount(0);
    await expect(page.getByRole('status')).toContainText('indirect objects');
    expect(await canvas.evaluate(c => {
      const pixels = c.getContext('2d').getImageData(0, 0, c.width, c.height).data;
      let black = 0;
      for (let i = 0; i < pixels.length; i += 4) {
        if (pixels[i] < 100 && pixels[i + 1] < 100 && pixels[i + 2] < 100) {
          black++;
        }
      }
      return black;
    })).toBe(0);
    await page.getByLabel('Zoom', { exact: true }).selectOption('1.5');
    await expect(canvas).toHaveJSProperty('width', dimensions[0] * 1.5);
    await expect(page.getByRole('alert')).toHaveCount(0);
  });
}


test('native JPEG soft mask preserves transparency and removes matte', async ({ page }) => {
  await page.goto('/');
  const jpeg = await page.evaluate(() => {
    const canvas = document.createElement('canvas');
    canvas.width = 3;
    canvas.height = 1;
    const context = canvas.getContext('2d');
    context.fillStyle = 'rgb(128,0,0)';
    context.fillRect(0, 0, 3, 1);
    return canvas.toDataURL('image/jpeg', 1).split(',')[1];
  });
  await page.getByLabel('Open PDF', { exact: true }).setInputFiles({ name: 'masked-jpeg.pdf', mimeType: 'application/pdf', buffer: fixture(false, Buffer.from(jpeg, 'base64').toString('latin1')) });
  await expect(page.getByRole('status')).toContainText('indirect objects');
  await page.getByRole('button', { name: 'PdfObjectKit (experimental)', exact: true }).click();
  const canvas = page.locator('.page > div > canvas');
  await expect(canvas).toHaveAttribute('data-renderer', 'native');
  const pixels = await canvas.evaluate(c => [62, 70, 78].map(x => [...c.getContext('2d').getImageData(x, 180, 1, 1).data]));
  expect(pixels[0]).toEqual([255, 255, 255, 255]);
  expect(pixels[1][0]).toBeGreaterThan(240);
  expect(pixels[1][1]).toBeGreaterThan(100);
  expect(pixels[1][1]).toBeLessThan(150);
  expect(pixels[2][0]).toBeGreaterThan(110);
  expect(pixels[2][0]).toBeLessThan(145);
  expect(pixels[2][1]).toBeLessThan(10);
  await expect(page.locator('.renderer-notes')).not.toContainText('DCTDecode');
  await expect(page.getByRole('alert')).toHaveCount(0);
});


test('native shading patterns color paths and text instead of black', async ({ page }) => {
  await page.goto('/');
  await page.getByLabel('Open PDF', { exact: true }).setInputFiles({ name: 'gradient.pdf', mimeType: 'application/pdf', buffer: fixture(false, null, true) });
  await page.locator('.page > div > canvas[data-renderer="pdfjs"]').waitFor();
  const sample = () => page.locator('.page > div > canvas').evaluate(c => [20, 100, 180].map(x => [...c.getContext('2d').getImageData(x, 175, 1, 1).data]));
  const reference = await sample();
  await page.getByRole('button', { name: 'PdfObjectKit (experimental)', exact: true }).click();
  await page.locator('.page > div > canvas[data-renderer="native"]').waitFor();
  const native = await sample();
  for (let i = 0; i < reference.length; i++) {
    for (let channel = 0; channel < 3; channel++) {
      expect(Math.abs(reference[i][channel] - native[i][channel])).toBeLessThan(4);
    }
  }
  const redText = await page.locator('.page > div > canvas').evaluate(c => {
    const data = c.getContext('2d').getImageData(20, 60, 50, 25).data;
    let count = 0;
    for (let i = 0; i < data.length; i += 4) {
      if (data[i] > 150 && data[i + 1] < 100 && data[i + 2] < 140) {
        count++;
      }
    }
    return count;
  });
  expect(redText).toBeGreaterThan(40);
  await expect(page.locator('.renderer-notes')).not.toContainText('Unsupported graphics operator');
});

async function fontFixture() {
  const font = await readFile('../PdfObjectKit.Tests/Fixtures/RenderingTest.ttf');
  const content = 'BT /F1 100 Tf 1 0 0 1 20 80 Tm <00010002> Tj ET';
  // Both characters extract as A, but must draw different glyphs.
  const cmap = 'begincmap 1 begincodespacerange <0000> <FFFF> endcodespacerange 2 beginbfchar <0001> <0041> <0002> <0041> endbfchar endcmap';
  const stream = data => `<< /Length ${data.length} >>\nstream\n${data}\nendstream`;
  const objects = [
    '<< /Type /Catalog /Pages 2 0 R >>',
    '<< /Type /Pages /Kids [3 0 R] /Count 1 >>',
    '<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>',
    stream(content),
    '<< /Type /Font /Subtype /Type0 /BaseFont /PdfKitTest-Regular /Encoding /Identity-H /DescendantFonts [6 0 R] /ToUnicode 9 0 R >>',
    '<< /Type /Font /Subtype /CIDFontType2 /BaseFont /PdfKitTest-Regular /CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> /FontDescriptor 7 0 R /DW 600 /CIDToGIDMap 10 0 R >>',
    '<< /Type /FontDescriptor /FontName /PdfKitTest-Regular /Flags 32 /FontBBox [0 0 500 700] /ItalicAngle 0 /Ascent 800 /Descent -200 /CapHeight 700 /StemV 80 /FontFile2 8 0 R >>',
    stream(font.toString('latin1')),
    stream(cmap),
    stream(Buffer.from([0, 0, 0, 1, 0, 2]).toString('latin1')),
  ];
  let pdf = '%PDF-1.7\n';
  const offsets = [];
  objects.forEach((object, index) => {
    offsets.push(Buffer.byteLength(pdf, 'latin1'));
    pdf += `${index + 1} 0 obj\n${object}\nendobj\n`;
  });
  const xref = Buffer.byteLength(pdf, 'latin1');
  pdf += `xref\n0 11\n0000000000 65535 f \n${offsets.map(n => `${String(n).padStart(10, '0')} 00000 n \n`).join('')}trailer\n<< /Size 11 /Root 1 0 R >>\nstartxref\n${xref}\n%%EOF\n`;
  return Buffer.from(pdf, 'latin1');
}

test('native embedded TrueType renders distinct CID glyphs with identical Unicode', async ({ page }) => {
  await page.goto('/');
  await page.getByLabel('Open PDF', { exact: true }).setInputFiles({ name: 'font.pdf', mimeType: 'application/pdf', buffer: await fontFixture() });
  const canvas = page.locator('.page > div > canvas');
  await expect(canvas).toHaveAttribute('data-renderer', 'pdfjs');
  const sample = () => canvas.evaluate(c => [[25, 60], [85, 60], [105, 60], [75, 90]].map(([x, y]) => [...c.getContext('2d').getImageData(x, y, 1, 1).data]));
  const reference = await sample();
  expect(reference).toEqual([[0, 0, 0, 255], [255, 255, 255, 255], [0, 0, 0, 255], [255, 255, 255, 255]]);
  await page.getByRole('button', { name: 'PdfObjectKit (experimental)', exact: true }).click();
  await expect(canvas).toHaveAttribute('data-renderer', 'native');
  expect(await sample()).toEqual(reference);
  await expect(page.locator('.renderer-notes')).not.toContainText('substitute');
  await page.getByLabel('Zoom', { exact: true }).selectOption('1.5');
  await expect(canvas).toHaveJSProperty('width', 300);
  await expect(page.locator('.renderer-notes')).not.toContainText('substitute');
});

test('native renderer reports browser font rejection and falls back', async ({ page }) => {
  await page.route('**/api/documents/render?*', async route => {
    const response = await route.fetch();
    const data = await response.json();
    expect(data.fonts).toHaveLength(1);
    data.fonts[0].data = 'AA==';
    await route.fulfill({ response, json: data });
  });
  await page.goto('/');
  await page.getByLabel('Open PDF', { exact: true }).setInputFiles({ name: 'font.pdf', mimeType: 'application/pdf', buffer: await fontFixture() });
  const canvas = page.locator('.page > div > canvas');
  await expect(canvas).toHaveAttribute('data-renderer', 'pdfjs');
  await page.getByRole('button', { name: 'PdfObjectKit (experimental)', exact: true }).click();
  await expect(canvas).toHaveAttribute('data-renderer', 'native');
  await expect(page.locator('.renderer-notes')).toContainText('rejected by the browser');
  await expect(page.getByRole('alert')).toHaveCount(0);
});


test('selected image downloads source JPEG or transparent PNG at original dimensions', async ({ page }) => {
  await page.goto('/');
  const jpeg = await page.evaluate(() => {
    const canvas = document.createElement('canvas');
    canvas.width = 3;
    canvas.height = 1;
    canvas.getContext('2d').fillRect(0, 0, 3, 1);
    return canvas.toDataURL('image/jpeg').split(',')[1];
  });
  for (const kind of ['raw', 'jpeg', 'masked']) {
    const bytes = fixture(false, kind === 'raw' ? null : Buffer.from(jpeg, 'base64').toString('latin1'), false, kind === 'masked');
    await page.getByLabel('Open PDF', { exact: true }).setInputFiles({ name: 'export.pdf', mimeType: 'application/pdf', buffer: bytes });
    await expect(page.getByRole('status')).toContainText('indirect objects');
    await page.locator('.object').filter({ hasText: 'Image 1' }).click();
    const promise = page.waitForEvent('download');
    await page.getByRole('button', { name: 'Download image', exact: true }).click();
    const download = await promise;
    const stream = await download.createReadStream();
    const chunks = [];
    for await (const chunk of stream) {
      chunks.push(chunk);
    }
    const output = Buffer.concat(chunks);
    expect(download.suggestedFilename()).toBe(`export-page-1-image-1.${kind === 'jpeg' ? 'jpg' : 'png'}`);
    if (kind === 'jpeg') {
      expect(output.equals(Buffer.from(jpeg, 'base64'))).toBe(true);
    } else {
      const pixels = await page.evaluate(async base64 => {
        const image = new Image();
        image.src = `data:image/png;base64,${base64}`;
        await image.decode();
        const canvas = document.createElement('canvas');
        canvas.width = image.width;
        canvas.height = image.height;
        canvas.getContext('2d').drawImage(image, 0, 0);
        return { width: image.width, height: image.height, data: [...canvas.getContext('2d').getImageData(0, 0, image.width, image.height).data] };
      }, output.toString('base64'));
      expect(pixels.height).toBe(1);
      expect(pixels.width).toBe(kind === 'raw' ? 1 : 3);
      if (kind === 'raw') {
        expect(pixels.data).toEqual([0, 255, 0, 255]);
      } else {
        expect([pixels.data[3], pixels.data[7], pixels.data[11]]).toEqual([0, 128, 255]);
      }
    }
    await expect(page.getByRole('alert')).toHaveCount(0);
  }
});


test('Delete removes selected image and whole text line but ignores editing and dialogs', async ({ page }) => {
  const edits = [];
  page.on('request', request => {
    if (request.url().endsWith('/api/documents/edit')) {
      edits.push(request);
    }
  });
  await page.goto('/');
  await page.getByLabel('Open PDF', { exact: true }).setInputFiles({ name: 'delete.pdf', mimeType: 'application/pdf', buffer: fixture() });
  await expect(page.getByRole('status')).toContainText('indirect objects');
  await page.locator('.object').filter({ hasText: 'Hello' }).click();
  await page.getByLabel('New value', { exact: true }).focus();
  await page.keyboard.press('Delete');
  expect(edits).toHaveLength(0);
  await (await toolbarAction(page, 'View Markdown')).click();
  await expect(page.getByRole('dialog')).toBeVisible();
  await page.keyboard.press('Delete');
  expect(edits).toHaveLength(0);
  await page.keyboard.press('Escape');
  await expect(page.getByRole('dialog')).toHaveCount(0);
  await page.locator('.object').filter({ hasText: 'Hello' }).click();
  await page.keyboard.press('Control+Delete');
  expect(edits).toHaveLength(0);
  await page.keyboard.down('Delete');
  await page.keyboard.down('Delete');
  await page.keyboard.up('Delete');
  await expect(page.locator('.object').filter({ hasText: 'Hello' })).toHaveCount(0);
  expect(edits).toHaveLength(1);
  await expect(page.getByRole('status')).toContainText('indirect objects');
  await page.locator('.object').filter({ hasText: 'Image 1' }).click();
  await page.keyboard.press('Delete');
  await expect(page.locator('.object').filter({ hasText: 'Image 1' })).toHaveCount(0);
  expect(edits).toHaveLength(2);
  await expect(page.getByRole('status')).toContainText('indirect objects');
  await page.keyboard.press('Delete');
  expect(edits).toHaveLength(2);
  await expect(page.getByRole('alert')).toHaveCount(0);
});

for (const rotated of [false, true]) {
  test(`native renderer paints saved widget text and updates after editing (rotated=${rotated})`, async ({ page }) => {
    await page.goto('/');
    let bytes = fixture(rotated);
    async function upload() {
      await page.getByLabel('Open PDF', { exact: true }).setInputFiles({ name: 'widgets.pdf', mimeType: 'application/pdf', buffer: bytes });
      await expect(page.getByRole('status')).toContainText('indirect objects');
      await page.getByRole('button', { name: 'PdfObjectKit (experimental)', exact: true }).click();
      await expect(page.locator('.page > div > canvas')).toHaveAttribute('data-renderer', 'native');
    }
    async function edit(change) {
      const response = await page.request.post('/api/documents/edit', { multipart: {
        file: { name: 'widgets.pdf', mimeType: 'application/pdf', buffer: bytes },
        edit: JSON.stringify({ value: '', pageNumber: 1, ...change }),
      } });
      expect(response.ok()).toBeTruthy();
      bytes = await response.body();
    }
    async function ink() {
      return page.locator('.page > div > canvas').evaluate(canvas => {
        const pixels = canvas.getContext('2d').getImageData(0, 0, canvas.width, canvas.height).data;
        let count = 0;
        for (let i = 0; i < pixels.length; i += 4) {
          if (pixels[i] < 80 && pixels[i + 1] < 80 && pixels[i + 2] < 80) {
            count++;
          }
        }
        return count;
      });
    }
    await upload();
    const before = await ink();
    await edit({ kind: 'add-field', fieldType: 'Text', fieldName: 'Notes', value: 'Visible widget text', x: 5, y: 50, width: 170, height: 24 });
    await edit({ kind: 'add-field', fieldType: 'PushButton', fieldName: 'Button', value: 'Continue', x: 5, y: 76, width: 170, height: 24 });
    await edit({ kind: 'add-field', fieldType: 'Signature', fieldName: 'Signature', x: 5, y: 140, width: 170, height: 24 });
    await upload();
    expect(await ink()).toBeGreaterThan(before + 150);
    const canvas = page.locator('.page > div > canvas');
    const rendered = await canvas.evaluate(c => c.toDataURL());
    await edit({ kind: 'field', fieldName: 'Notes', value: 'Updated text' });
    await upload();
    expect(await canvas.evaluate(c => c.toDataURL())).not.toBe(rendered);
    await expect(page.getByRole('alert')).toHaveCount(0);
  });
}
