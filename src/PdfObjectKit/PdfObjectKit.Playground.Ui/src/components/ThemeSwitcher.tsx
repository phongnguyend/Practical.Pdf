import { useEffect, useState } from 'react';
import { Sun, Moon, Monitor } from 'lucide-react';
import { applyTheme, readTheme, saveTheme, type Theme } from '../theme';

export function ThemeSwitcher() {
  const [theme, setTheme] = useState<Theme>(readTheme);
  useEffect(() => {
    const media = matchMedia('(prefers-color-scheme: dark)');
    const update = () => applyTheme(theme);
    const sync = (event: StorageEvent) => {
      if (event.key === 'pdfobjectkit-theme' || event.key === null) {
        setTheme(readTheme());
      }
    };
    update();
    media.addEventListener('change', update);
    window.addEventListener('storage', sync);
    return () => {
      media.removeEventListener('change', update);
      window.removeEventListener('storage', sync);
    };
  }, [theme]);
  return <div className="theme-switcher" role="group" aria-label="Color theme">
    {([{ value: 'light', label: 'Light theme', Icon: Sun }, { value: 'dark', label: 'Dark theme', Icon: Moon }, { value: 'system', label: 'System theme', Icon: Monitor }] as const).map(({ value, label, Icon }) => <button key={value} type="button" title={label} aria-label={label} aria-pressed={theme === value} onClick={() => {
      setTheme(value);
      saveTheme(value);
    }}><Icon size={17} aria-hidden="true" /></button>)}
  </div>;
}
