import { useEffect, useRef, useState } from 'react';
import { Files, Scissors, X } from 'lucide-react';
import { Modal } from './Modal';
import './PageAssemblyDialog.css';

export function parsePageSelection(text: string, pageCount: number): number[] {
  const result: number[] = [];
  for (const part of text.split(',')) {
    const match = part.trim().match(/^([0-9]+)(?:\s*-\s*([0-9]+))?$/);
    if (!match) {
      throw new Error('Enter page numbers or ranges, for example 1, 3, 5-7.');
    }
    const start = Number(match[1]);
    const end = Number(match[2] ?? match[1]);
    if (!Number.isSafeInteger(start) || !Number.isSafeInteger(end) || start < 1 || end < 1 || start > pageCount || end > pageCount) {
      throw new Error('Page numbers must be between 1 and ' + pageCount + '.');
    }
    const step = start <= end ? 1 : -1;
    for (let page = start; ; page += step) {
      if (result.includes(page)) {
        throw new Error('Choose each page only once.');
      }
      result.push(page);
      if (result.length > 100) {
        throw new Error('Extract at most 100 pages at once.');
      }
      if (page === end) {
        break;
      }
    }
  }
  return result;
}

export function PageAssemblyDialog({ mode, pdf, name, pageCount, onClose, onOpenFiles }: {
  mode: 'split' | 'extract'; pdf: Blob; name: string; pageCount: number;
  onClose: () => void; onOpenFiles: (files: File[]) => void;
}) {
  const [pages, setPages] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const abort = useRef<AbortController | null>(null);
  useEffect(() => () => abort.current?.abort(), []);
  async function submit() {
    setError('');
    try {
      const selection = mode === 'extract' ? parsePageSelection(pages, pageCount) : null;
      setBusy(true);
      const controller = new AbortController();
      abort.current = controller;
      const query = new URLSearchParams();
      if (selection) {
        query.set('pages', selection.join(','));
      }
      const response = await fetch('/api/documents/' + mode + (query.size ? '?' + query.toString() : ''), {
        method: 'POST', headers: { 'Content-Type': 'application/pdf' }, body: pdf, signal: controller.signal,
      });
      if (!response.ok) {
        throw new Error((await response.json()).detail ?? 'Could not assemble pages.');
      }
      const data: { documents: { pageNumber: number; base64: string }[] } = await response.json();
      const stem = name.replace(/\.pdf$/i, '');
      const files = data.documents.map(document => {
        const bytes = Uint8Array.from(atob(document.base64), character => character.charCodeAt(0));
        const suffix = mode === 'split' ? '-page-' + document.pageNumber : '-extracted';
        return new File([bytes], stem + suffix + '.pdf', { type: 'application/pdf' });
      });
      onOpenFiles(files);
      onClose();
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : 'Could not assemble pages.');
    } finally {
      setBusy(false);
    }
  }
  return <Modal labelledBy="assembly-title" onClose={onClose} canClose={!busy} className="page-assembly-dialog">
    <form onSubmit={event => { event.preventDefault(); void submit(); }}>
      <div className="assembly-heading"><div>{mode === 'split' ? <Scissors size={22} aria-hidden="true" /> : <Files size={22} aria-hidden="true" />}<h2 id="assembly-title">{mode === 'split' ? 'Split PDF into tabs' : 'Extract pages into a tab'}</h2></div><button type="button" className="icon-button" aria-label="Close page assembly" disabled={busy} onClick={onClose}><X size={18} aria-hidden="true" /></button></div>
      <p className="assembly-document" title={name}>{name} / {pageCount} pages</p>
      {mode === 'extract' ? <label className="assembly-pages">Pages to extract<input aria-label="Pages to extract" autoFocus required value={pages} disabled={busy} placeholder="1, 3, 5-7" onChange={event => setPages(event.target.value)} /><span>Use commas and ranges. Pages appear in the order you enter.</span></label> : <p>Each page will open as a separate PDF tab.</p>}
      <p className="control-help">Uses the current saved PDF. The original tab stays open. Results are kept in memory until you download them.</p>

      {error && <p className="assembly-error" role="alert">{error}</p>}
      <div className="assembly-actions"><button type="button" disabled={busy} onClick={onClose}>Cancel</button><button className="primary-action" disabled={busy} type="submit">{busy ? 'Preparing pages...' : mode === 'split' ? 'Split into ' + pageCount + ' tabs' : 'Extract to new tab'}</button></div>
    </form>
  </Modal>;
}
