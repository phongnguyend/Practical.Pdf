import { useEffect, useRef, useState } from 'react';
import { getDocument, type PDFDocumentProxy, type PageViewport, type RenderTask } from 'pdfjs-dist';
import { Trash2 } from 'lucide-react';
import './TableHints.css';

export type TableHint = { pageNumber: number; x: number; y: number; width: number; height: number; firstRowIsHeader: boolean };
type Point = { x: number; y: number };
export function TableHints({ file, hints, onChange, disabled }: { file: Blob; hints: TableHint[]; onChange: (hints: TableHint[]) => void; disabled: boolean }) {
  const [open, setOpen] = useState(false);
  const [pdf, setPdf] = useState<PDFDocumentProxy>();
  const [page, setPage] = useState(1);
  const [preview, setPreview] = useState<{ url: string; viewport: PageViewport }>();
  const [error, setError] = useState('');
  const [draft, setDraft] = useState<{ start: Point; end: Point }>();
  const start = useRef<Point | undefined>(undefined);
  useEffect(() => {
    if (!open) {
      return;
    }
    let cancelled = false;
    let task: ReturnType<typeof getDocument> | undefined;
    setPdf(undefined);
    setPreview(undefined);
    setError('');
    void file.arrayBuffer().then(async bytes => {
      if (cancelled) {
        return;
      }
      task = getDocument({ data: bytes, cMapUrl: '/pdfjs/cmaps/', cMapPacked: true, standardFontDataUrl: '/pdfjs/standard_fonts/', wasmUrl: '/pdfjs/wasm/' });
      const loaded = await task.promise;
      if (!cancelled) {
        setPdf(loaded);
      }
    }).catch(cause => {
      if (!cancelled) {
        setError(String(cause));
      }
    });
    return () => {
      cancelled = true;
      void task?.destroy();
    };
  }, [file, open]);
  useEffect(() => {
    setPreview(undefined);
    setDraft(undefined);
    start.current = undefined;
    if (!pdf || !open) {
      return;
    }
    let cancelled = false;
    let task: RenderTask | undefined;
    void (async () => {
      const source = await pdf.getPage(page);
      if (cancelled) {
        return;
      }
      const base = source.getViewport({ scale: 1 });
      const viewport = source.getViewport({ scale: Math.min(1.5, 1000 / Math.max(base.width, base.height)) });
      const canvas = document.createElement('canvas');
      canvas.width = Math.ceil(viewport.width);
      canvas.height = Math.ceil(viewport.height);
      task = source.render({ canvas, viewport, background: '#fff' });
      await task.promise;
      if (!cancelled) {
        setPreview({ url: canvas.toDataURL(), viewport });
      }
      canvas.width = canvas.height = 0;
    })().catch(cause => {
      if (!cancelled) {
        setError(String(cause));
      }
    });
    return () => {
      cancelled = true;
      task?.cancel();
    };
  }, [pdf, page, open]);
  function position(event: React.PointerEvent<HTMLDivElement>): Point {
    const bounds = event.currentTarget.getBoundingClientRect();
    return { x: Math.max(0, Math.min(1, (event.clientX - bounds.left) / bounds.width)), y: Math.max(0, Math.min(1, (event.clientY - bounds.top) / bounds.height)) };
  }
  function rectangle(a: Point, b: Point) {
    return { left: `${Math.min(a.x, b.x) * 100}%`, top: `${Math.min(a.y, b.y) * 100}%`, width: `${Math.abs(b.x - a.x) * 100}%`, height: `${Math.abs(b.y - a.y) * 100}%` };
  }
  return <details className="table-hints" onToggle={event => setOpen(event.currentTarget.open)}>
    <summary>Table hints ({hints.length})</summary>
    <p className="control-help">Draw a rectangle around a table. Text fragments whose centers fall inside become table cells; columns are inferred automatically. Mark whole cells, not individual characters. Reapply conversion to use these hints. Up to 20 non-overlapping regions; scanned tables require OCR.</p>
    {open && <>
      {pdf && <label>Table hint page<select aria-label="Table hint page" value={page} disabled={disabled} onChange={event => setPage(Number(event.target.value))}>{Array.from({ length: pdf.numPages }, (_, i) => <option key={i} value={i + 1}>{i + 1}</option>)}</select></label>}
      {error && <p role="alert">{error}</p>}
      {!preview && !error && <p role="status">Loading table preview...</p>}
      {preview && <div className="table-hint-scroll"><div className="table-hint-page" aria-label="Draw table region" onPointerDown={event => {
        if (disabled || event.button !== 0 || hints.length >= 20) {
          return;
        }
        start.current = position(event);
        setDraft({ start: start.current, end: start.current });
        event.currentTarget.setPointerCapture(event.pointerId);
      }} onPointerMove={event => {
        if (start.current) {
          setDraft({ start: start.current, end: position(event) });
        }
      }} onPointerUp={event => {
        const a = start.current;
        start.current = undefined;
        setDraft(undefined);
        if (!a || disabled) {
          return;
        }
        const b = position(event);
        const width = Math.ceil(preview.viewport.width);
        const height = Math.ceil(preview.viewport.height);
        const [x0, y0] = preview.viewport.convertToPdfPoint(a.x * width, a.y * height);
        const [x1, y1] = preview.viewport.convertToPdfPoint(b.x * width, b.y * height);
        const round = (value: number) => Math.round(value * 1000) / 1000;
        const hint = { pageNumber: page, x: round(Math.min(x0, x1)), y: round(Math.min(y0, y1)), width: round(Math.abs(x1 - x0)), height: round(Math.abs(y1 - y0)), firstRowIsHeader: true };
        if (hint.width < 2 || hint.height < 2) {
          return;
        }
        if (hints.some(other => other.pageNumber === page && other.x < hint.x + hint.width && hint.x < other.x + other.width && other.y < hint.y + hint.height && hint.y < other.y + other.height)) {
          setError('Table regions must not overlap. Remove the previous hint before drawing a replacement.');
          return;
        }
        setError('');
        onChange([...hints, hint]);
      }} onPointerCancel={() => {
        start.current = undefined;
        setDraft(undefined);
      }}><img src={preview.url} alt={`Page ${page} for table hints`} draggable={false} />{hints.map((hint, index) => {
        if (hint.pageNumber !== page) {
          return null;
        }
        const [x0, y0] = preview.viewport.convertToViewportPoint(hint.x, hint.y);
        const [x1, y1] = preview.viewport.convertToViewportPoint(hint.x + hint.width, hint.y + hint.height);
        const width = Math.ceil(preview.viewport.width);
        const height = Math.ceil(preview.viewport.height);
        return <div key={index} className="table-hint-box" style={rectangle({ x: x0 / width, y: y0 / height }, { x: x1 / width, y: y1 / height })}>Table {index + 1}</div>;
      })}{draft && <div className="table-hint-box" style={rectangle(draft.start, draft.end)} />}</div></div>}
      <div className="table-hint-list">{hints.map((hint, index) => <div key={index}><span>Table {index + 1} / Page {hint.pageNumber}</span><label><input type="checkbox" checked={hint.firstRowIsHeader} disabled={disabled} onChange={event => onChange(hints.map((item, i) => i === index ? { ...item, firstRowIsHeader: event.target.checked } : item))} />First row is header for table {index + 1}</label><button type="button" disabled={disabled} aria-label={`Remove table hint ${index + 1}`} onClick={() => onChange(hints.filter((_, i) => i !== index))}><Trash2 size={15} /></button></div>)}</div>
    </>}
  </details>;
}
