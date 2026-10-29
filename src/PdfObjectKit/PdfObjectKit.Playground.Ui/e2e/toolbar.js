export async function toolbarAction(page, name) {
  const action = page.locator('.toolbar').getByRole('button', { name, exact: true });
  if (!await action.isVisible()) {
    const pages = ['Organize pages', 'Split PDF', 'Extract pages', 'Rotate left', 'Rotate right', 'Duplicate page', 'Delete page'];
    await page.locator('.toolbar-menu > summary').filter({ hasText: new RegExp('^' + (pages.includes(name) ? 'Pages' : 'Document') + '$') }).click();
  }
  return action;
}
