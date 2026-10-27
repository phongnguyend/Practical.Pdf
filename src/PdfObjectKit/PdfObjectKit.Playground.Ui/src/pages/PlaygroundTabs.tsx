import { useEffect, useRef, useState } from 'react';
import { FileText, Plus, X } from 'lucide-react';
import { PlaygroundPage } from './PlaygroundPage';
import './PlaygroundTabs.css';

export type DocumentView = { page: number; zoom: number; renderer: 'pdfjs' | 'native' };
type Tab = { id: string; file?: File; modified?: boolean; view: DocumentView };
function emptyTab(file?: File): Tab {
  return { id: crypto.randomUUID(), file, view: { page: 1, zoom: 1, renderer: 'pdfjs' } };
}

export function PlaygroundTabs() {
  const [tabs, setTabs] = useState<Tab[]>(() => [emptyTab()]);
  const [activeId, setActiveId] = useState('');
  const [locked, setLocked] = useState(false);
  const keyboardFocus = useRef<string | null>(null);
  const active = tabs.find(tab => tab.id === activeId) ?? tabs[0];
  useEffect(() => {
    document.getElementById('tab-' + active.id)?.scrollIntoView({ block: 'nearest', inline: 'nearest' });
  }, [active.id]);
  useEffect(() => {
    if (!locked && keyboardFocus.current === active.id) {
      document.getElementById('tab-' + active.id)?.focus();
      keyboardFocus.current = null;
    }
  }, [active.id, locked]);
  function openFiles(files: File[]) {
    if (!files.length) {
      return;
    }
    const retained = tabs.filter(tab => tab.file || tab.id !== active.id);
    if (retained.length + files.length > 128) {
      throw new Error('Close some tabs first. The playground supports up to 128 open tabs.');
    }
    const added = files.map(file => emptyTab(file));
    setTabs([...retained, ...added]);
    setActiveId(added[0].id);
    setLocked(false);
  }
  function close(id: string) {
    if (locked) {
      return;
    }
    const index = tabs.findIndex(tab => tab.id === id);
    const remaining = tabs.filter(tab => tab.id !== id);
    if (!remaining.length) {
      remaining.push(emptyTab());
    }
    setTabs(remaining);
    if (active.id === id) {
      setActiveId(remaining[Math.min(index, remaining.length - 1)].id);
    }
  }
  const strip = <div className="browser-tabs-bar">
    <div className="browser-tabs" role="tablist" aria-label="PDF documents">
      {tabs.map(tab => <div className={tab.id === active.id ? 'browser-tab active' : 'browser-tab'} key={tab.id}>
        <button type="button" role="tab" id={'tab-' + tab.id} aria-controls="document-panel" aria-selected={tab.id === active.id} tabIndex={tab.id === active.id ? 0 : -1} disabled={locked} title={tab.file?.name ?? 'New tab'} onClick={() => setActiveId(tab.id)} onKeyDown={event => {
          if (!['ArrowLeft', 'ArrowRight', 'Home', 'End'].includes(event.key)) {
            return;
          }
          event.preventDefault();
          const index = tabs.findIndex(item => item.id === tab.id);
          const next = event.key === 'Home' ? 0 : event.key === 'End' ? tabs.length - 1 : (index + (event.key === 'ArrowLeft' ? -1 : 1) + tabs.length) % tabs.length;
          keyboardFocus.current = tabs[next].id;
          setActiveId(tabs[next].id);
        }}><FileText size={15} aria-hidden="true" /><span>{tab.file?.name ?? 'New tab'}</span>{tab.modified && <span className="tab-modified" title="Edited PDF held in this tab" aria-label="Modified" />}</button>
        <button className="tab-close" type="button" disabled={locked} aria-label={'Close ' + (tab.file?.name ?? 'new tab')} title="Close tab" onClick={() => close(tab.id)}><X size={14} aria-hidden="true" /></button>
      </div>)}
    </div>
    <button className="tab-add" type="button" aria-label="New tab" title="New tab" disabled={locked || tabs.length >= 128} onClick={() => {
      const tab = emptyTab();
      setTabs([...tabs, tab]);
      setActiveId(tab.id);
    }}><Plus size={18} aria-hidden="true" /></button>
  </div>;
  return <PlaygroundPage key={active.id} initialFile={active.file} initialView={active.view} tabStrip={strip} tabId={active.id}
    onOpenFiles={openFiles}
    onSaved={file => setTabs(current => current.map(tab => tab.id === active.id ? { ...tab, file, modified: true } : tab))}
    onViewChange={view => setTabs(current => current.map(tab => tab.id === active.id ? { ...tab, view } : tab))}
    onBusyChange={setLocked} />;
}
