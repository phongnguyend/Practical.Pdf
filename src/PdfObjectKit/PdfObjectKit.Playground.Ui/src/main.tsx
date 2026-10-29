import { createRoot } from 'react-dom/client';
import { PlaygroundTabs } from './pages/PlaygroundTabs';
import './style.css';
import './theme.css';
import { applyTheme, readTheme } from './theme';

applyTheme(readTheme());

createRoot(document.getElementById('root')!).render(<PlaygroundTabs />);
