import { toolbarAction } from './toolbar.js';
import { test, expect } from '@playwright/test';
import { readFile } from 'node:fs/promises';

test('properties edit saves Unicode title, author and date in current tab', async ({ page, request }) => {
  const fixture = await readFile(new URL('../../PdfObjectKit.Tests/Fixtures/Security/AES-256.pdf', import.meta.url));
  const response = await request.post('/api/documents/security/unlock', { multipart: { file: { name: 'source.pdf', mimeType: 'application/pdf', buffer: fixture }, password: 'owner-password' } });
  expect(response.ok()).toBe(true);
  await page.goto('/');
  await page.getByLabel('Open PDF', { exact: true }).setInputFiles({ name: 'properties.pdf', mimeType: 'application/pdf', buffer: await response.body() });
  await (await toolbarAction(page, 'Properties')).click();
  const dialog = page.getByRole('dialog');
  await dialog.getByLabel('Title', { exact: true }).fill('Report \u4e2d');
  await dialog.getByLabel('Author', { exact: true }).fill('Phong');
  await dialog.getByLabel('Creation date', { exact: true }).fill('2026-10-29T09:00:00+07:00');
  await dialog.getByRole('button', { name: 'Save properties' }).click();
  await expect(page.getByRole('tab')).toHaveAccessibleName('properties.pdf Modified');
  await (await toolbarAction(page, 'Properties')).click();
  await expect(dialog.getByLabel('Title', { exact: true })).toHaveValue('Report \u4e2d');
  await expect(dialog.getByLabel('Author', { exact: true })).toHaveValue('Phong');
  await expect(dialog.getByLabel('Creation date', { exact: true })).toHaveValue("2026-10-29 09:00:00 +07:00");
  await dialog.getByLabel('Author', { exact: true }).fill('');
  await dialog.getByRole('button', { name: 'Save properties' }).click();
  await (await toolbarAction(page, 'Properties')).click();
  await expect(dialog.getByLabel('Author', { exact: true })).toHaveValue('');
  await expect(dialog.getByLabel('Title', { exact: true })).toHaveValue('Report \u4e2d');
});
