import { test, expect } from '@playwright/test';
import { readFile } from 'node:fs/promises';
import path from 'node:path';
import { toolbarAction } from './toolbar.js';

test('Markdown options reapply, reset, and preserve the result on validation failure', async ({ page }) => {
  await page.goto('/');
  await page.getByLabel('Open PDF', { exact: true }).setInputFiles(path.resolve('../PdfObjectKit.SnapshotTests/Fixtures/images.pdf'));
  await (await toolbarAction(page, 'View Markdown')).click();
  const dialog = page.getByRole('dialog', { name: 'Markdown', exact: true });
  await expect(dialog.locator('.markdown-preview')).toContainText('Image content');
  await dialog.getByRole('button', { name: 'Options', exact: true }).click();
  await expect(dialog.getByLabel('Include image descriptions')).toBeChecked();
  await dialog.getByLabel('Include image descriptions').uncheck();
  await dialog.getByLabel('Join hyphenated words').check();
  await dialog.getByLabel('Body text height').fill('12');
  await expect(dialog.getByText('Unapplied changes', { exact: true })).toBeVisible();
  await expect(dialog.locator('.markdown-preview')).toContainText('Image content');
  const responsePromise = page.waitForResponse(response => response.url().includes('/markdown?'));
  await dialog.getByRole('button', { name: 'Reapply conversion' }).click();
  const result = await (await responsePromise).json();
  expect(result.options).toMatchObject({ includeImages: false, joinHyphenatedWords: true, bodyTextHeight: 12 });
  expect(result.markdown).not.toContain('Image content');
  await expect(dialog.getByText('Options used for this result', { exact: true })).toBeVisible();
  await dialog.getByRole('button', { name: 'Reset to defaults' }).click();
  await expect(dialog.getByLabel('Body text height')).toHaveValue('');
  await dialog.getByRole('button', { name: 'Reapply conversion' }).click();
  await expect(dialog.locator('.markdown-preview')).toContainText('Image content');
  await dialog.getByRole('button', { name: 'Source', exact: true }).click();
  const original = await dialog.locator('.markdown-source').innerText();
  await dialog.getByText('Advanced layout heuristics', { exact: true }).click();
  await dialog.getByLabel('Heading size ratio', { exact: true }).fill('-1');
  await dialog.getByRole('button', { name: 'Reapply conversion' }).click();
  await expect(dialog.getByRole('alert')).toContainText('HeadingSizeRatio');
  await expect(dialog.locator('.markdown-source')).toHaveText(original);
  await dialog.getByRole('button', { name: 'Discard changes' }).click();
  await expect(dialog.getByLabel('Heading size ratio', { exact: true })).toHaveValue('1.25');
  await expect(dialog.getByRole('alert')).toHaveCount(0);
  await dialog.getByText('Advanced layout heuristics', { exact: true }).click();
  await dialog.screenshot({ path: 'test-results/markdown-options.png' });
});

test('Markdown API rejects malformed and unknown options', async ({ request }) => {
  const data = await readFile(path.resolve('../PdfObjectKit.SnapshotTests/Fixtures/images.pdf'));
  for (const options of ['{', 'null', '{"unknown":1}', '{"heuristics":{"unknown":1}}', '{"heuristics":null}', '{"bodyTextHeight":-1}']) {
    const response = await request.post('/api/documents/markdown?' + new URLSearchParams({ options }), { data, headers: { 'Content-Type': 'application/pdf' } });
    expect(response.status(), await response.text()).toBe(422);
  }
});
