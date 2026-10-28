import { test, expect } from '@playwright/test';
import { readFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';

const fixture = fileURLToPath(new URL('../../PdfObjectKit.Tests/Fixtures/Security/AES-256.pdf', import.meta.url));

test('password prompt retries, opens decrypted working copy and exports PDF.js-readable encrypted PDF', async ({ page }) => {
  await page.goto('/');
  await page.getByLabel('Open PDF', { exact: true }).setInputFiles(fixture);
  const dialog = page.getByRole('dialog');
  await expect(dialog).toContainText('Unlock PDF');
  await dialog.getByLabel('PDF password', { exact: true }).fill('wrong');
  await dialog.getByRole('button', { name: 'Unlock and open' }).click();
  await expect(dialog.getByRole('alert')).toContainText('incorrect');
  await dialog.getByLabel('PDF password', { exact: true }).fill('owner-password');
  await dialog.getByRole('button', { name: 'Unlock and open' }).click();
  await expect(dialog).toHaveCount(0);
  await expect(page.locator('.object')).toContainText('Independent encryption fixture');
  await page.getByRole('button', { name: 'Protect PDF', exact: true }).click();
  await dialog.getByLabel('Open password (optional)', { exact: true }).fill('export-reader');
  await dialog.getByLabel('Owner password', { exact: true }).fill('export-owner');
  await dialog.getByLabel('Copy text and images', { exact: true }).uncheck();
  const downloadEvent = page.waitForEvent('download');
  await dialog.getByRole('button', { name: 'Download protected PDF' }).click();
  const download = await downloadEvent;
  const bytes = await readFile(await download.path());
  const { getDocument } = await import('pdfjs-dist/legacy/build/pdf.mjs');
  const task = getDocument({ data: new Uint8Array(bytes), password: 'export-reader', useSystemFonts: true });
  const pdf = await task.promise;
  try {
    const text = await (await pdf.getPage(1)).getTextContent();
    expect(text.items.map(item => item.str ?? '').join(' ')).toContain('Independent encryption fixture');
    expect((await pdf.getPermissions()).has(16)).toBe(false);
  } finally {
    await task.destroy();
  }
  await expect(dialog).toHaveCount(0);
  await page.getByRole('button', { name: 'New tab', exact: true }).click();
  await page.getByRole('tab', { name: /AES-256.pdf/ }).click();
  await expect(page.locator('.object')).toContainText('Independent encryption fixture');
  await expect(dialog).toHaveCount(0);
});

test('security API enforces owner access for restricted documents and validates export options', async ({ request }) => {
  const encrypted = await readFile(fixture);
  const opened = await request.post('/api/documents/security/unlock', { multipart: { file: { name: 'encrypted.pdf', mimeType: 'application/pdf', buffer: encrypted }, password: 'owner-password' } });
  expect(opened.ok()).toBe(true);
  const file = { name: 'plain.pdf', mimeType: 'application/pdf', buffer: await opened.body() };
  const protectedResponse = await request.post('/api/documents/security/encrypt', { multipart: { file, options: JSON.stringify({ userPassword: '', ownerPassword: 'owner', allowCopying: false }) } });
  expect(protectedResponse.ok()).toBe(true);
  const protectedFile = { ...file, buffer: await protectedResponse.body() };
  const denied = await request.post('/api/documents/security/unlock', { multipart: { file: protectedFile, password: '' } });
  expect(denied.status()).toBe(403);
  const edit = await request.post('/api/documents/edit', { multipart: { file: protectedFile, edit: JSON.stringify({ kind: 'rotate-page', pageNumber: 1, rotation: 90, value: '' }) } });
  expect(edit.status()).toBe(422);
  const allowed = await request.post('/api/documents/security/unlock', { multipart: { file: protectedFile, password: 'owner' } });
  expect(allowed.ok()).toBe(true);
  const invalid = await request.post('/api/documents/security/encrypt', { multipart: { file, options: JSON.stringify({ userPassword: 'same', ownerPassword: 'same' }) } });
  expect(invalid.status()).toBe(422);
});
