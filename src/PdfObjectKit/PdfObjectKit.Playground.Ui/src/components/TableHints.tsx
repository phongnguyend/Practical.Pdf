import { TableHintTransform } from './TableHintTransform';
import { useEffect, useRef, useState } from 'react';
import { getDocument, type PDFDocumentProxy, type PageViewport, type RenderTask } from 'pdfjs-dist';
import { Check, Columns2, RotateCcw, Trash2 } from 'lucide-react';
import './TableHints.css';

export type TableHint = { pageNumber: number; x: number; y: number; width: number; height: number; firstRowIsHeader: boolean; columnBoundaries?: number[] | null };
type Point = { x: number; y: number };
export function TableHints({ file, hints, onChange, disabled }: { file: Blob; hints: TableHint[]; onChange: (hints: TableHint[]) => void; disabled: boolean }) {
  const [editing, setEditing] = useState<number | null>(null);
  const [separator, setSeparator] = useState<number | null>(null);
  const dragging = useRef<number | null>(null);
  const [open, setOpen] = useState(false);
  const [pdf, setPdf] = useState<PDFDocumentProxy>();
  const [page, setPage] = useState(1);
  const [preview, setPreview] = useState<{ url: string; viewport: PageViewport }>();
  const [error, setError] = useState('');
  const [draft, setDraft] = useState<{ start: Point; end: Point }>();
  const start = useRef<Point | undefined>(undefined);
  useEffect(() => {
    if (editing !== null && !hints[editing]) {
      setEditing(null);
      setSeparator(null);
      dragging.current = null;
    }
  }, [editing, hints]);
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
  function setColumns(index: number, columns: number[] | null) {
    onChange(hints.map((hint, i) => i === index ? { ...hint, columnBoundaries: columns } : hint));
  }
  function moveSeparator(index: number, x: number) {
    if (editing === null || disabled) {
      return;
    }
    const hint = hints[editing];
    const columns = [...(hint?.columnBoundaries ?? [])];
    if (!hint || index >= columns.length) {
      return;
    }
    const min = (index === 0 ? hint.x : columns[index - 1]) + .01;
    const max = (index === columns.length - 1 ? hint.x + hint.width : columns[index + 1]) - .01;
    columns[index] = Math.max(min, Math.min(max, Math.round(x * 1000) / 1000));
    setColumns(editing, columns);
  }
  function pdfPoint(event: React.PointerEvent<HTMLDivElement>) {
    const point = position(event);
    return preview!.viewport.convertToPdfPoint(point.x * Math.ceil(preview!.viewport.width), point.y * Math.ceil(preview!.viewport.height));
  }
  function rectangle(a: Point, b: Point) {
    return { left: `${Math.min(a.x, b.x) * 100}%`, top: `${Math.min(a.y, b.y) * 100}%`, width: `${Math.abs(b.x - a.x) * 100}%`, height: `${Math.abs(b.y - a.y) * 100}%` };
  }
  return <details className="table-hints" onToggle={event => setOpen(event.currentTarget.open)}>
    <summary>Table hints ({hints.length})</summary>
    <p className="control-help">Draw a rectangle around a table. Use its on-page Edit button to adjust separators and the header row. Drag an existing region to move it or use its handles to resize; Escape cancels the gesture. Manual separators move and scale with the region. Text fragments whose centers fall inside become table cells; columns are inferred automatically unless you edit their separators. Mark whole cells, not individual characters. Reapply conversion to use these hints. Up to 20 non-overlapping regions; scanned tables require OCR.</p>
    {open && <>
      {pdf && <label>Table hint page<select aria-label="Table hint page" value={page} disabled={disabled} onChange={event => {
        setPage(Number(event.target.value));
        setEditing(null);
        setSeparator(null);
      }}>{Array.from({ length: pdf.numPages }, (_, i) => <option key={i} value={i + 1}>{i + 1}</option>)}</select></label>}
      {error && <p role="alert">{error}</p>}
      {!preview && !error && <p role="status">Loading table preview...</p>}
      {preview && <div className="table-hint-scroll">{editing !== null && hints[editing] && <div className="table-column-tools">
        <div className="table-column-heading">
          <div className="table-column-title"><Columns2 size={16} aria-hidden="true" /><strong>Table {editing + 1}</strong><span className="table-column-count">{hints[editing].columnBoundaries == null ? 'Automatic columns' : `${hints[editing].columnBoundaries.length + 1} columns`}</span></div>
          <div className="table-column-actions">
            <label className="table-hint-header-toggle"><input type="checkbox" checked={hints[editing].firstRowIsHeader} disabled={disabled} onChange={event => onChange(hints.map((item, i) => i === editing ? { ...item, firstRowIsHeader: event.target.checked } : item))} />First row is header</label>
            <button type="button" aria-label="Use automatic columns" title="Reset to automatic column detection" disabled={disabled} onClick={() => {
              setColumns(editing, null);
              setEditing(null);
              setSeparator(null);
            }}><RotateCcw size={14} aria-hidden="true" />Automatic</button>
            <button type="button" className="table-column-done" aria-label="Done editing table" disabled={disabled} onClick={() => {
              setEditing(null);
              setSeparator(null);
            }}><Check size={14} aria-hidden="true" />Done</button>
          </div>
        </div>
        <div className="table-column-details">
          <p>Click inside the table to add a divider. Drag to adjust.</p>
          {separator !== null && hints[editing].columnBoundaries?.[separator] !== undefined && <div className="table-column-position">
            <label htmlFor="table-separator-position">Divider {separator + 1}</label>
            <div className="table-column-coordinate"><span aria-hidden="true">X</span><input id="table-separator-position" aria-label="Separator X" type="number" step="any" disabled={disabled} value={hints[editing].columnBoundaries![separator]} onChange={event => moveSeparator(separator, Number(event.target.value))} /><span>pt</span></div>
            <button type="button" className="table-column-remove" aria-label="Remove separator" title="Remove selected divider" disabled={disabled} onClick={() => {
              setColumns(editing, hints[editing].columnBoundaries!.filter((_, i) => i !== separator));
              setSeparator(null);
            }}><Trash2 size={15} aria-hidden="true" /></button>
          </div>}
        </div>
      </div>}<div className="table-hint-page" aria-label="Draw table region" onPointerDown={event => {
        if (!disabled && event.button === 0 && editing !== null && hints[editing]) {
          const hint = hints[editing];
          const [x, y] = pdfPoint(event);
          const columns = hint.columnBoundaries ?? [];
          const value = Math.round(x * 1000) / 1000;
          if (value > hint.x && value < hint.x + hint.width && y >= hint.y && y <= hint.y + hint.height && columns.length < 50 && columns.every(item => Math.abs(item - value) > .01)) {
            const next = [...columns, value].sort((a, b) => a - b);
            setColumns(editing, next);
            setSeparator(next.indexOf(value));
          }
          return;
        }
        if (disabled || event.button !== 0 || hints.length >= 20) {
          return;
        }
        start.current = position(event);
        setDraft({ start: start.current, end: start.current });
        event.currentTarget.setPointerCapture(event.pointerId);
      }} onPointerMove={event => {
        if (dragging.current !== null) {
          moveSeparator(dragging.current, pdfPoint(event)[0]);
          return;
        }
        if (start.current) {
          setDraft({ start: start.current, end: position(event) });
        }
      }} onPointerUp={event => {
        if (dragging.current !== null) {
          moveSeparator(dragging.current, pdfPoint(event)[0]);
          dragging.current = null;
          return;
        }
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
        dragging.current = null;
        start.current = undefined;
        setDraft(undefined);
      }}><img src={preview.url} alt={`Page ${page} for table hints`} draggable={false} />{hints.map((hint, index) => {
        if (hint.pageNumber !== page) {
          return null;
        }
        if (editing === null && !disabled) {
          return <TableHintTransform key={index} hint={hint} index={index} viewport={preview.viewport} onEdit={() => {
            setEditing(index);
            setSeparator(null);
          }} onChange={value => onChange(hints.map((item, i) => i === index ? value : item))} onFinish={(value, original) => {
            const overlap = hints.some((other, i) => i !== index && other.pageNumber === value.pageNumber && other.x < value.x + value.width && value.x < other.x + other.width && other.y < value.y + value.height && value.y < other.y + other.height);
            setError(overlap ? 'Table regions must not overlap. The previous position has been restored.' : '');
            onChange(hints.map((item, i) => i === index ? (overlap ? original : value) : item));
          }} />;
        }
        const [x0, y0] = preview.viewport.convertToViewportPoint(hint.x, hint.y);
        const [x1, y1] = preview.viewport.convertToViewportPoint(hint.x + hint.width, hint.y + hint.height);
        const width = Math.ceil(preview.viewport.width);
        const height = Math.ceil(preview.viewport.height);
        return <div key={index} className="table-hint-box" style={rectangle({ x: x0 / width, y: y0 / height }, { x: x1 / width, y: y1 / height })}>Table {index + 1}</div>;
      })}{hints.flatMap((hint, index) => hint.pageNumber !== page ? [] : (hint.columnBoundaries ?? []).map((boundary, column) => {
        const [x0, y0] = preview.viewport.convertToViewportPoint(boundary, hint.y);
        const [x1, y1] = preview.viewport.convertToViewportPoint(boundary, hint.y + hint.height);
        const horizontal = Math.abs(y1 - y0) < .01;
        return <button key={`${index}-${column}`} type="button" className={`table-column-separator ${horizontal ? 'horizontal' : 'vertical'}`} aria-label={`Table ${index + 1} column separator ${column + 1}`} aria-pressed={editing === index && separator === column} disabled={disabled || editing !== index}
          style={{ left: `${Math.min(x0, x1) / Math.ceil(preview.viewport.width) * 100}%`, top: `${Math.min(y0, y1) / Math.ceil(preview.viewport.height) * 100}%`, ...(horizontal ? { width: `${Math.abs(x1 - x0) / Math.ceil(preview.viewport.width) * 100}%` } : { height: `${Math.abs(y1 - y0) / Math.ceil(preview.viewport.height) * 100}%` }) }}
          onPointerDown={event => {
            event.stopPropagation();
            if (event.button !== 0 || disabled) {
              return;
            }
            setSeparator(column);
            dragging.current = column;
            event.currentTarget.setPointerCapture(event.pointerId);
          }} onClick={event => {
            event.stopPropagation();
            setSeparator(column);
          }} onKeyDown={event => {
            if (event.key === 'ArrowLeft' || event.key === 'ArrowRight') {
              event.preventDefault();
              moveSeparator(column, boundary + (event.key === 'ArrowLeft' ? -1 : 1));
            }
          }} />;
      }))}{draft && <div className="table-hint-box" style={rectangle(draft.start, draft.end)} />}</div></div>}
      <div className="table-hint-list">{hints.map((hint, index) => <div key={index}><span>Table {index + 1} / Page {hint.pageNumber}</span><button type="button" disabled={disabled} aria-label={`Remove table hint ${index + 1}`} onClick={() => {
        onChange(hints.filter((_, i) => i !== index));
        setEditing(null);
        setSeparator(null);
      }}><Trash2 size={15} /></button></div>)}</div>
    </>}
  </details>;
}
