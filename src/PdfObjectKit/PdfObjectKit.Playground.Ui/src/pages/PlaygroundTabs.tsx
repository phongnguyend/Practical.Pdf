import { useEffect, useRef, useState } from 'react';
import { FileText, Plus, X, Combine, LoaderCircle, Columns2 } from 'lucide-react';
import { CompareDialog } from '../components/CompareDialog';
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
  const [mergeIds, setMergeIds] = useState<Set<string>>(new Set());
  const [merging, setMerging] = useState(false);
  const [mergeError, setMergeError] = useState('');
  const mergeInFlight = useRef(false);
  const [locked, setLocked] = useState(false);
  const [comparison, setComparison] = useState<[File, File] | null>(null);
  const disabled = locked || merging || comparison !== null;
  const selectedTabs = tabs.filter(tab => tab.file && mergeIds.has(tab.id));
  const draggedTab = useRef<string | null>(null);
  const [dropTarget, setDropTarget] = useState<{ id: string; after: boolean } | null>(null);
  const reorderFocus = useRef<string | null>(null);
  const [announcement, setAnnouncement] = useState('');
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
  useEffect(() => {
    if (reorderFocus.current) {
      const element = document.getElementById('tab-' + reorderFocus.current);
      element?.focus();
      element?.scrollIntoView({ block: 'nearest', inline: 'nearest' });
      reorderFocus.current = null;
    }
  }, [tabs]);
  useEffect(() => {
    if (disabled) {
      draggedTab.current = null;
      setDropTarget(null);
    }
  }, [disabled]);
  function moveTab(id: string, destination: number, focus = false) {
    if (disabled) {
      return;
    }
    const source = tabs.findIndex(tab => tab.id === id);
    const target = Math.max(0, Math.min(tabs.length - 1, destination));
    if (source < 0 || source === target) {
      return;
    }
    const reordered = [...tabs];
    const [moved] = reordered.splice(source, 1);
    reordered.splice(target, 0, moved);
    setActiveId(active.id);
    if (focus) {
      reorderFocus.current = id;
    }
    setTabs(reordered);
    setAnnouncement(`${moved.file?.name ?? 'New tab'} moved to position ${target + 1} of ${tabs.length}.`);
  }
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
    if (disabled) {
      return;
    }
    const index = tabs.findIndex(tab => tab.id === id);
    const remaining = tabs.filter(tab => tab.id !== id);
    if (!remaining.length) {
      remaining.push(emptyTab());
    }
    setTabs(remaining);
    setMergeIds(current => new Set([...current].filter(selected => selected !== id)));
    if (active.id === id) {
      setActiveId(remaining[Math.min(index, remaining.length - 1)].id);
    }
  }
  function toggleMerge(id: string, includeActive = false) {
    if (disabled || !tabs.find(tab => tab.id === id)?.file) {
      return;
    }
    setMergeError('');
    setMergeIds(current => {
      const next = new Set(current);
      if (includeActive && next.size === 0 && active.file && active.id !== id) {
        next.add(active.id);
      }
      if (next.has(id)) {
        next.delete(id);
      } else {
        next.add(id);
      }
      return next;
    });
  }
  async function mergeSelected() {
    if (disabled || mergeInFlight.current || selectedTabs.length < 2) {
      return;
    }
    setMergeError('');
    if (selectedTabs.length > 32 || selectedTabs.reduce((total, tab) => total + tab.file!.size, 0) > 64 * 1024 * 1024) {
      setMergeError('Select up to 32 PDFs totaling at most 64 MB.');
      return;
    }
    if (tabs.length >= 128) {
      setMergeError('Close a tab first to make room for the merged PDF.');
      return;
    }
    mergeInFlight.current = true;
    setMerging(true);
    try {
      const data = new FormData();
      for (const tab of selectedTabs) {
        data.append('files', tab.file!, tab.file!.name);
      }
      const response = await fetch('/api/documents/merge', { method: 'POST', body: data });
      if (!response.ok) {
        const problem = await response.json().catch(() => null);
        throw new Error(problem?.detail ?? `Merge failed (${response.status}).`);
      }
      const result = new File([await response.blob()], 'merged.pdf', { type: 'application/pdf' });
      const added = emptyTab(result);
      setTabs(current => [...current, added]);
      setActiveId(added.id);
      setMergeIds(new Set());
    } catch (cause) {
      setMergeError(cause instanceof Error ? cause.message : 'Could not merge the selected PDFs.');
    } finally {
      mergeInFlight.current = false;
      setMerging(false);
    }
  }
  const strip = <><span className="tab-reorder-announcement" aria-live="polite">{announcement}</span><div className="browser-tabs-bar">
    <div className="browser-tabs" role="tablist" aria-label="PDF documents">
      {tabs.map(tab => <div className={`browser-tab${tab.id === active.id ? ' active' : ''}${mergeIds.has(tab.id) ? ' merge-selected' : ''}${dropTarget?.id === tab.id ? dropTarget.after ? ' drop-after' : ' drop-before' : ''}`} key={tab.id}
        onDragOver={event => {
          if (disabled || !draggedTab.current || !event.dataTransfer.types.includes('application/x-pdfobjectkit-tab')) {
            return;
          }
          event.preventDefault();
          event.stopPropagation();
          event.dataTransfer.dropEffect = 'move';
          const bounds = event.currentTarget.getBoundingClientRect();
          setDropTarget({ id: tab.id, after: event.clientX > bounds.left + bounds.width / 2 });
          const list = event.currentTarget.parentElement!;
          const listBounds = list.getBoundingClientRect();
          if (event.clientX < listBounds.left + 36) {
            list.scrollLeft -= 24;
          } else if (event.clientX > listBounds.right - 36) {
            list.scrollLeft += 24;
          }
        }} onDrop={event => {
          const id = draggedTab.current;
          if (!id || disabled || !event.dataTransfer.types.includes('application/x-pdfobjectkit-tab')) {
            return;
          }
          event.preventDefault();
          event.stopPropagation();
          const bounds = event.currentTarget.getBoundingClientRect();
          const after = event.clientX > bounds.left + bounds.width / 2;
          const source = tabs.findIndex(item => item.id === id);
          const insertion = tabs.findIndex(item => item.id === tab.id) + (after ? 1 : 0);
          moveTab(id, insertion - (source < insertion ? 1 : 0));
          draggedTab.current = null;
          setDropTarget(null);
        }}>
        {tab.file && <input className="tab-merge-check" type="checkbox" aria-label={`Select ${tab.file.name} for merge`} title="Select for merge or comparison (or Ctrl/Cmd+click the tab)" checked={mergeIds.has(tab.id)} disabled={disabled} onChange={() => toggleMerge(tab.id)} />}
        <button type="button" role="tab" draggable={!disabled} aria-keyshortcuts="Alt+ArrowLeft Alt+ArrowRight"
          onDragStart={event => {
            if (disabled) {
              event.preventDefault();
              return;
            }
            draggedTab.current = tab.id;
            event.dataTransfer.setData('application/x-pdfobjectkit-tab', tab.id);
            event.dataTransfer.effectAllowed = 'move';
          }} onDragEnd={() => {
            draggedTab.current = null;
            setDropTarget(null);
          }} id={'tab-' + tab.id} aria-controls="document-panel" aria-selected={tab.id === active.id} tabIndex={tab.id === active.id ? 0 : -1} aria-disabled={disabled} title={`${tab.file?.name ?? 'New tab'} - Drag to reorder; Alt+Left/Right moves this tab`} onClick={event => {
          if (disabled) {
            return;
          }
          if (event.ctrlKey || event.metaKey) {
            toggleMerge(tab.id, true);
          } else {
            setActiveId(tab.id);
          }
        }} onKeyDown={event => {
          if (disabled) {
            return;
          }
          if (event.altKey && ['ArrowLeft', 'ArrowRight'].includes(event.key)) {
            event.preventDefault();
            event.stopPropagation();
            const index = tabs.findIndex(item => item.id === tab.id);
            moveTab(tab.id, index + (event.key === 'ArrowLeft' ? -1 : 1), true);
            return;
          }
          if (!['ArrowLeft', 'ArrowRight', 'Home', 'End'].includes(event.key)) {
            return;
          }
          event.preventDefault();
          const index = tabs.findIndex(item => item.id === tab.id);
          const next = event.key === 'Home' ? 0 : event.key === 'End' ? tabs.length - 1 : (index + (event.key === 'ArrowLeft' ? -1 : 1) + tabs.length) % tabs.length;
          keyboardFocus.current = tabs[next].id;
          setActiveId(tabs[next].id);
        }}><FileText size={15} aria-hidden="true" /><span>{tab.file?.name ?? 'New tab'}</span>{tab.modified && <span className="tab-modified" title="Edited PDF held in this tab" aria-label="Modified" />}</button>
        <button className="tab-close" type="button" disabled={disabled} aria-label={'Close ' + (tab.file?.name ?? 'new tab')} title="Close tab" onClick={() => close(tab.id)}><X size={14} aria-hidden="true" /></button>
      </div>)}
    </div>
    <button className="tab-add" type="button" aria-label="New tab" title="New tab" disabled={disabled || tabs.length >= 128} onClick={() => {
      const tab = emptyTab();
      setTabs([...tabs, tab]);
      setActiveId(tab.id);
    }}><Plus size={18} aria-hidden="true" /></button>
    <div className="tab-merge-actions">
      <button type="button" disabled={disabled || selectedTabs.length !== 2} title="Select exactly two PDFs to compare their current saved versions" onClick={() => setComparison([selectedTabs[0].file!, selectedTabs[1].file!])}><Columns2 size={16} aria-hidden="true" />Compare selected</button>
      <button type="button" disabled={disabled || selectedTabs.length < 2} title="Merge checked PDFs in left-to-right tab order" onClick={() => void mergeSelected()}>
        {merging ? <LoaderCircle size={16} className="merge-spinner" aria-hidden="true" /> : <Combine size={16} aria-hidden="true" />}
        {merging ? 'Merging...' : `Merge selected${selectedTabs.length ? ` (${selectedTabs.length})` : ''}`}
      </button>
      {selectedTabs.length > 0 && <button type="button" className="tab-clear-merge" disabled={disabled} aria-label="Clear merge selection" title="Clear merge selection" onClick={() => setMergeIds(new Set())}><X size={14} aria-hidden="true" /></button>}
    </div>
  </div>{selectedTabs.length > 0 && <div className="merge-order-hint">{selectedTabs.length} selected. Select two to compare. Merge and comparison use saved edits in left-to-right tab order.</div>}
  {mergeError && <div className="merge-error" role="alert"><span>{mergeError}</span><button type="button" aria-label="Dismiss merge error" onClick={() => setMergeError('')}><X size={15} aria-hidden="true" /></button></div>}</>;
  return <><PlaygroundPage key={active.id} initialFile={active.file} initialView={active.view} tabStrip={strip} externalBusy={merging} tabId={active.id}
    onOpenFiles={openFiles}
    onSaved={file => setTabs(current => current.map(tab => tab.id === active.id ? { ...tab, file, modified: true } : tab))}
    onViewChange={view => setTabs(current => current.map(tab => tab.id === active.id ? { ...tab, view } : tab))}
    onBusyChange={setLocked} />{comparison && <CompareDialog files={comparison} onClose={() => setComparison(null)} />}</>;
}
