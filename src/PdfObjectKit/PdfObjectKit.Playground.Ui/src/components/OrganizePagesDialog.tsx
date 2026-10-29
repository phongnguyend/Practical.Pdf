import { useEffect, useRef, useState } from 'react';
import { ArrowLeft, ArrowRight, GripVertical, LayoutGrid, RotateCcw, Save, X } from 'lucide-react';
import type { PDFDocumentProxy, RenderTask } from 'pdfjs-dist';
import { Modal } from './Modal';
import './PageAssemblyDialog.css';
import './OrganizePagesDialog.css';

export function OrganizePagesDialog({ pdf, currentPage, onClose, onApply }: { pdf: PDFDocumentProxy; currentPage: number; onClose: () => void; onApply: (order: number[]) => void }) {
  const original = () => Array.from({ length: pdf.numPages }, (_, index) => index + 1);
  const [order, setOrder] = useState(original);
  const [previews, setPreviews] = useState<Record<number, string | null>>({});
  const [target, setTarget] = useState<{ page: number; after: boolean } | null>(null);
  const [announcement, setAnnouncement] = useState('');
  const dragged = useRef<number | null>(null);
  const focusPage = useRef<number | null>(null);
  const grid = useRef<HTMLOListElement>(null);
  const dirty = order.some((page, index) => page !== index + 1);
  useEffect(() => {
    let cancelled = false;
    let task: RenderTask | undefined;
    void (async () => {
      // Render sequentially at thumbnail resolution to bound memory for large documents.
      for (let number = 1; number <= pdf.numPages && !cancelled; number++) {
        try {
          const page = await pdf.getPage(number);
          if (cancelled) {
            break;
          }
          const dimensions = page.getViewport({ scale: 1 });
          const viewport = page.getViewport({ scale: Math.min(240 / dimensions.width, 280 / dimensions.height) });
          const canvas = document.createElement('canvas');
          canvas.width = Math.ceil(viewport.width);
          canvas.height = Math.ceil(viewport.height);
          task = page.render({ canvas, viewport });
          await task.promise;
          if (!cancelled) {
            const image = canvas.toDataURL('image/png');
            setPreviews(current => ({ ...current, [number]: image }));
          }
        } catch {
          if (!cancelled) {
            setPreviews(current => ({ ...current, [number]: null }));
          }
        }
      }
    })();
    return () => {
      cancelled = true;
      task?.cancel();
    };
  }, [pdf]);
  useEffect(() => {
    if (focusPage.current !== null) {
      grid.current?.querySelector<HTMLButtonElement>(`[data-page="${focusPage.current}"] [data-handle]`)?.focus();
      focusPage.current = null;
    }
  }, [order]);
  function move(page: number, position: number, focus = true) {
    const start = order.indexOf(page);
    if (start < 0 || position < 0 || position >= order.length || start === position) {
      return;
    }
    const next = [...order];
    next.splice(start, 1);
    next.splice(position, 0, page);
    if (focus) {
      focusPage.current = page;
    }
    setOrder(next);
    setAnnouncement(`Original page ${page} moved to position ${position + 1} of ${order.length}.`);
  }
  return <Modal labelledBy="organize-title" className="organize-dialog" onClose={onClose}>
    <div className="assembly-heading"><div><LayoutGrid size={22} aria-hidden="true" /><h2 id="organize-title">Organize pages</h2></div><button type="button" aria-label="Close organize pages" onClick={onClose}><X size={18} /></button></div>
    <p className="organize-help">Drag pages into order, or use the arrow buttons. Page labels refer to the original document. Alt+Left/Right moves a focused page.</p>
    <div className="organize-summary"><span>{order.length} pages &middot; {dirty ? 'Order changed' : 'Original order'}</span><button disabled={!dirty} onClick={() => { setOrder(original()); setAnnouncement('Original order restored.'); }}><RotateCcw size={14} />Reset order</button></div>
    <ol className="organize-grid" ref={grid} aria-label="Page order">{order.map((page, index) => <li key={page} data-page={page} className={`organize-card${page === currentPage ? ' current-page' : ''}${target?.page === page ? target.after ? ' drop-after' : ' drop-before' : ''}`} draggable onDragStart={event => {
      dragged.current = page;
      event.dataTransfer.setData('application/x-pdf-page', String(page));
      event.dataTransfer.effectAllowed = 'move';
    }} onDragEnd={() => { dragged.current = null; setTarget(null); }} onDragOver={event => {
      if (dragged.current === null) {
        return;
      }
      event.preventDefault();
      event.dataTransfer.dropEffect = 'move';
      const bounds = event.currentTarget.getBoundingClientRect();
      setTarget({ page, after: event.clientX > bounds.left + bounds.width / 2 });
    }} onDrop={event => {
      if (dragged.current === null) {
        return;
      }
      event.preventDefault();
      const bounds = event.currentTarget.getBoundingClientRect();
      const insertion = index + (event.clientX > bounds.left + bounds.width / 2 ? 1 : 0);
      const source = order.indexOf(dragged.current);
      move(dragged.current, insertion - (source < insertion ? 1 : 0), false);
      dragged.current = null;
      setTarget(null);
    }} onKeyDown={event => {
      if (event.altKey && (event.key === 'ArrowLeft' || event.key === 'ArrowRight')) {
        event.preventDefault();
        move(page, index + (event.key === 'ArrowLeft' ? -1 : 1));
      }
    }}>
      <div className="organize-card-heading"><span>Position {index + 1}</span><button data-handle aria-label={`Move original page ${page}`} title="Drag this page or press Alt+Left/Right"><GripVertical size={16} /></button></div>
      <div className="organize-thumbnail">{previews[page] ? <img src={previews[page]!} alt={`Preview of original page ${page}`} draggable={false} /> : <span>{previews[page] === null ? 'Preview unavailable' : 'Loading preview…'}</span>}</div>
      <div className="organize-page-label">Original page {page}{page === currentPage && <span>Viewing</span>}</div>
      <div className="organize-move-actions"><button disabled={index === 0} aria-label={`Move page ${page} earlier`} onClick={() => move(page, index - 1)}><ArrowLeft size={15} /></button><button disabled={index === order.length - 1} aria-label={`Move page ${page} later`} onClick={() => move(page, index + 1)}><ArrowRight size={15} /></button></div>
    </li>)}</ol>
    <p className="organize-announcement" role="status">{announcement}</p>
    <div className="assembly-actions"><button onClick={onClose}>Cancel</button><button className="primary-action" disabled={!dirty} onClick={() => onApply(order)}><Save size={16} />Apply order</button></div>
  </Modal>;
}
