import { useEffect, useRef, useState } from 'react';
import type { PDFDocumentProxy } from 'pdfjs-dist';
import { Image, X } from 'lucide-react';
import { Modal } from './Modal';
import { rasterizePdf } from '../rasterizePdf';
import './PageAssemblyDialog.css';

export function RasterizeDialog({ pdf, name, onClose, onOpenFiles }: { pdf: PDFDocumentProxy; name: string; onClose: () => void; onOpenFiles: (files: File[]) => void }) {
  const [dpi, setDpi] = useState(144);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const [progress, setProgress] = useState('');
  const abort = useRef<AbortController | null>(null);
  useEffect(() => () => abort.current?.abort(), []);

  async function convert() {
    if (busy) {
      return;
    }
    const controller = new AbortController();
    abort.current = controller;
    setBusy(true);
    setError('');
    try {
      const bytes = await rasterizePdf(pdf, dpi, {}, setProgress, controller.signal);
      controller.signal.throwIfAborted();
      onOpenFiles([new File([bytes], name.replace(/\.pdf$/i, '') + '-image-only.pdf', { type: 'application/pdf' })]);
      onClose();
    } catch (cause) {
      if (!controller.signal.aborted) {
        setError(cause instanceof Error ? cause.message : 'Could not convert this PDF.');
      }
    } finally {
      setBusy(false);
      setProgress('');
    }
  }

  return <Modal labelledBy="rasterize-title" className="page-assembly-dialog" onClose={onClose} canClose={!busy}>
    <div className="assembly-heading"><div><Image size={22} aria-hidden="true" /><h2 id="rasterize-title">Convert to image-only PDF</h2></div><button aria-label="Close conversion" disabled={busy} onClick={onClose}><X size={18} /></button></div>
    <p className="assembly-document">{name} / {pdf.numPages} pages</p>
    <p>Render every page as an image and open the result in a new tab. The original stays unchanged.</p>
    <label>Resolution <select aria-label="Image resolution" value={dpi} disabled={busy} onChange={event => setDpi(Number(event.target.value))}>
      <option value={72}>72 DPI - smaller output</option>
      <option value={144}>144 DPI - balanced</option>
      <option value={216}>216 DPI - sharper detail</option>
    </select></label>
    <p className="control-help">Higher resolution keeps more detail but uses more memory. Limits: 100 pages, 16 million pixels per page, 64 MB of rendered RGB pixels, and 20 MB output.</p>
    <p>Only the visible page appearance is retained. Text will no longer be searchable or selectable. Forms and annotations become pixels; links, attachments, metadata, layers, accessibility tags, signatures and password protection are not retained.</p>
    <p className="control-help">This conversion does not hide sensitive information visible on a page. Use Redact PDF to remove it.</p>
    <p role="status" aria-live="polite">{progress}</p>
    {error && <p className="assembly-error" role="alert">{error}</p>}
    <div className="assembly-actions"><button onClick={busy ? () => abort.current?.abort() : onClose}>{busy ? 'Cancel conversion' : 'Cancel'}</button><button className="primary-action" disabled={busy} onClick={() => void convert()}><Image size={16} />{busy ? 'Converting...' : 'Convert to new tab'}</button></div>
  </Modal>;
}
