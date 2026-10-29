export type Theme = 'light' | 'dark' | 'system';
const key = 'pdfobjectkit-theme';
export function readTheme(): Theme {
  try {
    const value = localStorage.getItem(key);
    return value === 'light' || value === 'dark' ? value : 'system';
  } catch {
    return 'system';
  }
}
export function applyTheme(theme: Theme) {
  document.documentElement.dataset.theme = theme === 'system' ? (matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light') : theme;
}
export function saveTheme(theme: Theme) {
  try {
    localStorage.setItem(key, theme);
  } catch {
    // Theme switching still works when browser storage is unavailable.
  }
  applyTheme(theme);
}
