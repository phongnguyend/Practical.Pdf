import { test, expect } from '@playwright/test';
import { toolbarAction } from './toolbar.js';

function fixture(tagged = false) {
  const content = (tagged ? '/P <</MCID 0>> BDC ' : '') + 'BT /F1 12 Tf 20 260 Td (Original page) Tj ET 0 0 10 10 re W n' + (tagged ? ' EMC' : '');
  const objects = ['<< /Type /Catalog /Pages 2 0 R >>', '<< /Type /Pages /Kids [3 0 R] /Count 1 >>',
    '<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 300] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>',
    '<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>', `<< /Length ${content.length} >>\nstream\n${content}\nendstream`];
  if (tagged) {
    objects[0] = '<< /Type /Catalog /Pages 2 0 R /MarkInfo << /Marked true >> /StructTreeRoot 6 0 R >>';
    objects[2] = objects[2].slice(0, -2) + ' /StructParents 0 >>';
    objects.push('<< /Type /StructTreeRoot /K [7 0 R] /ParentTree << /Nums [0 [7 0 R]] >> /ParentTreeNextKey 1 >>');
    objects.push('<< /Type /StructElem /S /P /P 6 0 R /Pg 3 0 R /K 0 >>');
  }
  let pdf = '%PDF-1.7\n';
  const offsets = [];
  objects.forEach((object, index) => {
    offsets.push(pdf.length);
    pdf += `${index + 1} 0 obj\n${object}\nendobj\n`;
  });
  const start = pdf.length;
  pdf += `xref\n0 ${objects.length + 1}\n0000000000 65535 f \n${offsets.map(offset => `${String(offset).padStart(10, '0')} 00000 n \n`).join('')}trailer\n<< /Size ${objects.length + 1} /Root 1 0 R >>\nstartxref\n${start}\n%%EOF\n`;
  return Buffer.from(pdf);
}

async function edit(request, bytes, edit) {
  const response = await request.post('/api/documents/edit', { multipart: { file: { name: 'source.pdf', mimeType: 'application/pdf', buffer: bytes }, edit: JSON.stringify({ pageNumber: 1, ...edit }) } });
  expect(response.ok(), await response.text()).toBe(true);
  return response.body();
}

for (const tagged of [false, true]) {
  test(`flatten ${tagged ? 'tagged' : 'untagged'} PDF preserves rendered fields and markup in a separate tab`, async ({ page, request }) => {
    let bytes = await edit(request, fixture(tagged), { kind: 'add-field', fieldName: 'Name', fieldType: 'Text', value: 'Saved value', x: 40, y: 180, width: 160, height: 28 });
    bytes = await edit(request, bytes, { kind: 'add-field', fieldName: 'Approved', fieldType: 'CheckBox', value: 'Yes', x: 40, y: 130, width: 20, height: 20 });
    bytes = await edit(request, bytes, { kind: 'add-highlight', value: 'Review this', regions: [{ x: 20, y: 255, width: 80, height: 14 }] });
    await page.goto('/');
    await page.getByLabel('Open PDF', { exact: true }).setInputFiles({ name: 'forms.pdf', mimeType: 'application/pdf', buffer: bytes });
    const flatten = await toolbarAction(page, 'Flatten PDF');
    await expect(flatten).toBeEnabled();
    const canvas = page.locator('.page > div > canvas').first();
    const before = await canvas.evaluate(element => Array.from(element.getContext('2d').getImageData(0, 0, element.width, element.height).data));
    await flatten.click();
    const dialog = page.getByRole('dialog', { name: 'Flatten PDF' });
    await dialog.getByRole('button', { name: 'Flatten to new tab' }).click();
    await expect(page.getByRole('tab')).toHaveCount(2);
    await expect(page.getByRole('tab', { name: 'forms-flattened.pdf', exact: true })).toHaveAttribute('aria-selected', 'true');
    await expect(page.locator('.object .badge', { hasText: /^field$/ })).toHaveCount(0);
    await expect(page.locator('.object .badge', { hasText: /^annotation$/ })).toHaveCount(0);
    await expect(page.locator('.object').filter({ hasText: 'Saved value' })).toBeVisible();
    await expect(page.locator('.status')).toContainText('indirect objects');
    // PDF.js rasterizes standalone annotation appearances and page Forms through different
    // paths. Permit small edge-antialias differences, not missing/moved content or colors.
    const difference = await canvas.evaluate((element, expected) => {
      const actual = element.getContext('2d').getImageData(0, 0, element.width, element.height).data;
      let changed = 0;
      let maximum = 0;
      let total = 0;
      for (let i = 0; i < actual.length; i += 4) {
        const delta = Math.max(...[0, 1, 2].map(channel => Math.abs(actual[i + channel] - expected[i + channel])));
        changed += delta > 0 ? 1 : 0;
        maximum = Math.max(maximum, delta);
        total += delta;
      }
      return { sameSize: actual.length === expected.length, fraction: changed / (actual.length / 4), mean: total / (actual.length / 4), maximum };
    }, before);
    await test.info().attach('flatten-pixel-difference', {
      body: JSON.stringify(difference, null, 2),
      contentType: 'application/json',
    });
    expect(difference.sameSize).toBe(true);
    // Linux Chrome changes 920/90000 pixels between these two rendering paths.
    // Allow edge coverage variation while retaining the stricter mean and peak
    // color-error bounds below, which catch missing content and wrong colors.
    expect(difference.fraction).toBeLessThan(.015);
    expect(difference.mean).toBeLessThan(.5);
    expect(difference.maximum).toBeLessThan(110);
    await page.getByRole('tab', { name: 'forms.pdf', exact: true }).click();
    await expect(page.locator('.object .badge', { hasText: /^field$/ })).toHaveCount(2);
    await expect(page.locator('.object .badge', { hasText: /^annotation$/ })).toHaveCount(1);
});
}

test('flatten API supports selective flattening and rejects empty selection', async ({ request }) => {
  let bytes = await edit(request, fixture(), { kind: 'add-field', fieldName: 'Name', value: 'Keep', x: 40, y: 180, width: 160, height: 28 });
  bytes = await edit(request, bytes, { kind: 'add-underline', value: 'Note', regions: [{ x: 20, y: 255, width: 80, height: 14 }] });
  for (const forms of [true, false]) {
    const response = await request.post(`/api/documents/flatten?forms=${forms}&annotations=${!forms}`, { headers: { 'Content-Type': 'application/pdf' }, data: bytes });
    expect(response.ok(), await response.text()).toBe(true);
    const inspect = await request.post('/api/documents/inspect', { headers: { 'Content-Type': 'application/pdf' }, data: await response.body() });
    const result = await inspect.json();
    expect(result.objects.filter(object => object.kind === 'field')).toHaveLength(forms ? 0 : 1);
    expect(result.objects.filter(object => object.kind === 'annotation')).toHaveLength(forms ? 1 : 0);
  }
  const invalid = await request.post('/api/documents/flatten?forms=false&annotations=false', { headers: { 'Content-Type': 'application/pdf' }, data: bytes });
  expect(invalid.status()).toBe(422);
});
