import { rasterizePdf } from '../rasterizePdf';
import { useEffect, useRef, useState } from 'react';
import { getDocument, type PDFDocumentProxy, type RenderTask } from 'pdfjs-dist';
import { ScanLine, Trash2, X, ChevronLeft, ChevronRight, Info, MousePointer2, LoaderCircle, CheckCircle2 } from 'lucide-react';
import { Modal } from './Modal';
import './RedactionDialog.css';

type Region = { x: number; y: number; width: number; height: number };
type Impact = { pageNumber: number; kind: string; x: number; y: number; width: number; height: number };
async function pdfRegions(pdf: PDFDocumentProxy, marks: Record<number, Region[]>) {
  const regions = [];
  for (const [number, pageMarks] of Object.entries(marks)) {
    const source = await pdf.getPage(Number(number));
    const viewport = source.getViewport({ scale: 1 });
    for (const mark of pageMarks) {
      const [x0, y0] = viewport.convertToPdfPoint(mark.x * viewport.width, mark.y * viewport.height);
      const [x1, y1] = viewport.convertToPdfPoint((mark.x + mark.width) * viewport.width, (mark.y + mark.height) * viewport.height);
      regions.push({ pageNumber: Number(number), x: Math.min(x0, x1), y: Math.min(y0, y1), width: Math.abs(x1 - x0), height: Math.abs(y1 - y0) });
    }
  }
  return regions;
}
export function RedactionDialog({ pdf, name, onClose, onOpenFiles }: { pdf: PDFDocumentProxy; name: string; onClose: () => void; onOpenFiles: (files: File[]) => void }) {
  const [page, setPage] = useState(1);
  const [marks, setMarks] = useState<Record<number, Region[]>>({});
  const [draft, setDraft] = useState<Region | null>(null);
  const start = useRef<{ x: number; y: number } | null>(null);
  const [preview, setPreview] = useState('');
  const [rasterRatio, setRasterRatio] = useState({ x: 1, y: 1 });
  const [error, setError] = useState('');
  const [mode, setMode] = useState<'raster' | 'content'>('content');
  const [impacts, setImpacts] = useState<Impact[]>([]);
  const [impactBoxes, setImpactBoxes] = useState<(Region & { kind: string })[]>([]);
  const [impactStatus, setImpactStatus] = useState<'idle' | 'loading' | 'ready' | 'error'>('idle');
  const [impactError, setImpactError] = useState('');
  const [dpi, setDpi] = useState(144);
  const [confirmed, setConfirmed] = useState(false);
  const [busy, setBusy] = useState(false);
  const [progress, setProgress] = useState('');
  const abort = useRef<AbortController | null>(null);
  const count = Object.values(marks).reduce((total, list) => total + list.length, 0);
  useEffect(() => () => {
    abort.current?.abort();
  }, []);
  useEffect(() => {
    let cancelled = false;
    let task: RenderTask | undefined;
    setPreview('');
    setDraft(null);
    start.current = null;
    setError('');
    void (async () => {
      try {
        const source = await pdf.getPage(page);
        if (cancelled) {
          return;
        }
        const base = source.getViewport({ scale: 1 });
        const viewport = source.getViewport({ scale: Math.min(1.5, 1400 / Math.max(base.width, base.height)) });
        const canvas = document.createElement('canvas');
        canvas.width = Math.ceil(viewport.width);
        canvas.height = Math.ceil(viewport.height);
        task = source.render({ canvas, viewport, background: '#ffffff' });
        await task.promise;
        if (!cancelled) {
          setRasterRatio({ x: canvas.width / viewport.width, y: canvas.height / viewport.height });
          setPreview(canvas.toDataURL());
        }
      } catch (cause) {
        if (!cancelled) {
          setError(cause instanceof Error ? cause.message : 'Cannot render page.');
        }
      }
    })();
    return () => {
      cancelled = true;
      task?.cancel();
    };
  }, [pdf, page]);
  useEffect(() => {
    const controller = new AbortController();
    setImpacts([]);
    setImpactError('');
    setImpactStatus(mode === 'content' && count > 0 ? 'loading' : 'idle');
    if (mode !== 'content' || count === 0) {
      return () => controller.abort();
    }
    const timer = window.setTimeout(() => {
      void (async () => {
        try {
          const data = new FormData();
          data.append('file', new Blob([new Uint8Array(await pdf.getData())], { type: 'application/pdf' }), name);
          data.append('regions', JSON.stringify(await pdfRegions(pdf, marks)));
          controller.signal.throwIfAborted();
          const response = await fetch('/api/documents/redact-content/preview', { method: 'POST', body: data, signal: controller.signal });
          if (!response.ok) {
            const problem = await response.json().catch(() => null);
            throw new Error(problem?.detail ?? `Cannot analyze redaction (${response.status}).`);
          }
          const result: Impact[] = await response.json();
          controller.signal.throwIfAborted();
          setImpacts(result);
          setImpactStatus('ready');
        } catch (cause) {
          if (!controller.signal.aborted) {
            setImpactError(cause instanceof Error ? cause.message : 'Cannot analyze redaction.');
            setImpactStatus('error');
          }
        }
      })();
    }, 350);
    return () => {
      window.clearTimeout(timer);
      controller.abort();
    };
  }, [pdf, name, marks, mode, count]);
  useEffect(() => {
    let cancelled = false;
    setImpactBoxes([]);
    void pdf.getPage(page).then(source => {
      if (cancelled) {
        return;
      }
      const viewport = source.getViewport({ scale: 1 });
      setImpactBoxes(impacts.filter(impact => impact.pageNumber === page).map(impact => {
        const [x0, y0] = viewport.convertToViewportPoint(impact.x, impact.y);
        const [x1, y1] = viewport.convertToViewportPoint(impact.x + impact.width, impact.y + impact.height);
        return { kind: impact.kind, x: Math.min(x0, x1) / viewport.width, y: Math.min(y0, y1) / viewport.height, width: Math.abs(x1 - x0) / viewport.width, height: Math.abs(y1 - y0) / viewport.height };
      }));
    }).catch(() => {
      // The page renderer reports page-loading failures.
    });
    return () => {
      cancelled = true;
    };
  }, [pdf, page, impacts]);
  function cancelExport() {
    abort.current?.abort();
  }
  async function apply() {
    if (busy || !confirmed || !count || mode === 'content' && impactStatus !== 'ready') {
      return;
    }
    setBusy(true);
    setError('');
    const controller = new AbortController();
    abort.current = controller;
    let verification: ReturnType<typeof getDocument> | undefined;
    try {
      if (mode === 'content') {
        const regions = await pdfRegions(pdf, marks);
        controller.signal.throwIfAborted();
        setProgress('Removing supported content and rebuilding the PDF...');
        const data = new FormData();
        data.append('file', new Blob([new Uint8Array(await pdf.getData())], { type: 'application/pdf' }), name);
        data.append('regions', JSON.stringify(regions));
        const response = await fetch('/api/documents/redact-content', { method: 'POST', body: data, signal: controller.signal });
        if (!response.ok) {
          const problem = await response.json().catch(() => null);
          throw new Error(problem?.detail ?? `Content redaction failed (${response.status}).`);
        }
        const bytes = await response.arrayBuffer();
        controller.signal.throwIfAborted();
        setProgress('Checking the rebuilt PDF...');
        verification = getDocument({ data: bytes.slice(0) });
        const output = await verification.promise;
        if (output.numPages !== pdf.numPages || await output.getAttachments()) {
          throw new Error('Output verification failed. No result was opened.');
        }
        for (let number = 1; number <= output.numPages; number++) {
          controller.signal.throwIfAborted();
          if ((await (await output.getPage(number)).getAnnotations()).length) {
            throw new Error('Output still contains annotations. No result was opened.');
          }
        }
        controller.signal.throwIfAborted();
        onOpenFiles([new File([bytes], name.replace(/\.pdf$/i, '') + '-redacted.pdf', { type: 'application/pdf' })]);
        onClose();
        return;
      }
      const bytes = await rasterizePdf(pdf, dpi, marks, setProgress, controller.signal);
      controller.signal.throwIfAborted();
      onOpenFiles([new File([bytes], name.replace(/\.pdf$/i, '') + '-redacted.pdf', { type: 'application/pdf' })]);
      onClose();
    } catch (cause) {
      if (!controller.signal.aborted) {
        setError(cause instanceof Error ? cause.message : 'Redaction export failed.');
      }
    } finally {
      await verification?.destroy();
      setBusy(false);
      setProgress('');
    }
  }
  return <Modal labelledBy="redaction-title" className="redaction-dialog" canClose={!busy} onClose={onClose}>
    <header className="redaction-header">
      <div className="redaction-heading"><h2 id="redaction-title"><ScanLine size={20} />Redact PDF<span className="redaction-sr-only"> ({mode === 'raster' ? 'image-only' : 'content-preserving'})</span></h2><p title={name}>{name}</p></div>
      <button aria-label="Close redaction" title="Close" disabled={busy} onClick={onClose}><X size={18} /></button>
    </header>
    <div className="redaction-intro">
      <span><MousePointer2 size={16} aria-hidden="true" />Drag on the page to mark sensitive content.</span>
      <div className="redaction-legend" aria-label="Preview legend"><span><i className="redaction-legend-mark" />Your marks</span>{mode === 'content' && <span><i className="redaction-legend-impact" />Content to remove</span>}</div>
      <details className="redaction-guide"><summary><Info size={14} />What will change?</summary><p>{mode === 'raster' ? 'All pages become images and lose searchable text. Only their visible appearance is retained.' : 'Whole intersecting paths/images and nearby glyphs may be removed. Shared source content is removed at every placement, including on other pages. Amber bounds show this impact and can extend beyond your marks. Unsupported documents are rejected; choose image-only if needed.'} Review every affected page before sharing.</p></details>
    </div>
    <div className="redaction-toolbar">
      <label className="redaction-mode-control"><span>Output mode</span><select aria-label="Redaction mode" value={mode} disabled={busy} onChange={event => {
        setMode(event.target.value as 'raster' | 'content');
        setConfirmed(false);
        setError('');
      }}><option value="content">Content-preserving (limited)</option><option value="raster">Image-only</option></select><small>{mode === 'content' ? 'Keep supported text searchable.' : 'Every page becomes an image.'}</small></label>
      <div className="redaction-page-control" role="group" aria-label="Preview page navigation">
        <span>Preview page</span><div><button aria-label="Previous redaction page" title="Previous page" disabled={busy || page === 1} onClick={() => setPage(value => value - 1)}><ChevronLeft size={16} /></button><select aria-label="Redaction page" disabled={busy} value={page} onChange={event => setPage(Number(event.target.value))}>{Array.from({ length: pdf.numPages }, (_, index) => <option key={index} value={index + 1}>{index + 1}</option>)}</select><span>of {pdf.numPages}</span><button aria-label="Next redaction page" title="Next page" disabled={busy || page === pdf.numPages} onClick={() => setPage(value => value + 1)}><ChevronRight size={16} /></button></div>
      </div>
      <div className="redaction-mark-controls" role="group" aria-label="Marking actions"><span className="redaction-count">{count ? `${count} pending ${count === 1 ? 'mark' : 'marks'}` : 'No marks yet'}</span><div><button disabled={busy || !preview || (marks[page]?.length ?? 0) >= 1000} onClick={() => {
        setMarks(current => ({ ...current, [page]: [...(current[page] ?? []), { x: 0, y: 0, width: 1, height: 1 }] }));
        setConfirmed(false);
      }}><ScanLine size={16} />Mark entire page</button><button disabled={busy || !marks[page]?.length} onClick={() => {
        setMarks(current => ({ ...current, [page]: [] }));
        setConfirmed(false);
      }}><Trash2 size={16} />Clear page marks</button></div></div>
    </div>

    {mode === 'content' && <div className={`redaction-impact-summary redaction-impact-summary--${impactStatus}`} aria-label="Redaction impact">
      <span role="status">{impactStatus === 'loading' ? <LoaderCircle size={15} className="redaction-spinner" aria-hidden="true" /> : impactStatus === 'ready' ? <CheckCircle2 size={15} aria-hidden="true" /> : <Info size={15} aria-hidden="true" />}{impactStatus === 'loading' ? 'Checking what will be removed...' : impactStatus === 'ready' ? `${impacts.length} affected areas. Review highlighted content before exporting.` : impactStatus === 'error' ? 'Preview unavailable. Adjust marks or choose image-only.' : 'Affected content will be highlighted after you draw a mark.'}</span>
      {impacts.length > 0 && <div className="redaction-affected-pages" aria-label="Affected pages"><span>Affected pages</span>{Array.from(new Set(impacts.map(impact => impact.pageNumber))).sort((a, b) => a - b).map(number => <button key={number} disabled={busy} aria-label={`View affected areas on page ${number}`} aria-pressed={page === number} onClick={() => setPage(number)}>Page {number}<span>{impacts.filter(impact => impact.pageNumber === number).length}</span></button>)}</div>}
      {impactError && <p role="alert">{impactError}</p>}
    </div>}
    <div className="redaction-scroll">{preview ? <div className={`redaction-sheet ${mode === 'content' ? 'redaction-sheet--content' : ''}`} onPointerDown={event => {
      if (busy || event.button !== 0 || (marks[page]?.length ?? 0) >= 1000) {
        return;
      }
      const bounds = event.currentTarget.getBoundingClientRect();
      start.current = { x: Math.min(1, (event.clientX - bounds.left) / bounds.width * rasterRatio.x), y: Math.min(1, (event.clientY - bounds.top) / bounds.height * rasterRatio.y) };
      event.currentTarget.setPointerCapture(event.pointerId);
      setDraft(null);
    }} onPointerMove={event => {
      if (!start.current) {
        return;
      }
      const bounds = event.currentTarget.getBoundingClientRect();
      const x = Math.max(0, Math.min(1, (event.clientX - bounds.left) / bounds.width * rasterRatio.x));
      const y = Math.max(0, Math.min(1, (event.clientY - bounds.top) / bounds.height * rasterRatio.y));
      setDraft({ x: Math.min(start.current.x, x), y: Math.min(start.current.y, y), width: Math.abs(x - start.current.x), height: Math.abs(y - start.current.y) });
    }} onPointerUp={() => {
      if (draft && draft.width > 0.001 && draft.height > 0.001) {
        setMarks(current => ({ ...current, [page]: [...(current[page] ?? []), draft] }));
        setConfirmed(false);
      }
      start.current = null;
      setDraft(null);
    }} onPointerCancel={() => {
      start.current = null;
      setDraft(null);
    }}><img src={preview} alt={`Page ${page} to redact`} draggable={false} />{mode === 'content' && impactBoxes.map((box, index) => <div key={`impact-${index}`} className="redaction-impact" data-kind={box.kind} aria-label={`Affected ${box.kind}`} style={{ left: `${box.x / rasterRatio.x * 100}%`, top: `${box.y / rasterRatio.y * 100}%`, width: `${box.width / rasterRatio.x * 100}%`, height: `${box.height / rasterRatio.y * 100}%` }} />)}{[...(marks[page] ?? []), ...(draft ? [draft] : [])].map((mark, index) => <div key={index} className="redaction-mark" style={{ left: `${mark.x / rasterRatio.x * 100}%`, top: `${mark.y / rasterRatio.y * 100}%`, width: `${mark.width / rasterRatio.x * 100}%`, height: `${mark.height / rasterRatio.y * 100}%` }} />)}</div> : <p>Rendering page...</p>}</div>
    {(marks[page]?.length ?? 0) > 0 && <div className="redaction-mark-list" aria-label="Pending redactions">{marks[page].map((_, index) => <button key={index} disabled={busy} aria-label={`Remove redaction ${index + 1}`} onClick={() => {
      setMarks(current => ({ ...current, [page]: current[page].filter((__, item) => item !== index) }));
      setConfirmed(false);
    }}>Mark {index + 1}<X size={14} /></button>)}</div>}
    <div className="redaction-confirm"><label><input type="checkbox" checked={confirmed} disabled={busy || mode === 'content' && impactStatus !== 'ready'} onChange={event => setConfirmed(event.target.checked)} />I reviewed my marks. {mode === 'raster' ? 'The new PDF will lose searchable text, forms, links, attachments, metadata, layers, tags and signature validity.' : 'The new PDF keeps supported text and graphics, but removes document metadata, bookmarks, links, attachments and tags. Saved form/annotation appearances become page content. Signed documents are rejected.'} It will not retain password protection. The original stays open separately.</label></div>
    {error && <p className="redaction-error" role="alert">{error}</p>}
    <footer className="redaction-footer">{mode === 'raster' && <label>Resolution <select aria-label="Redaction resolution" disabled={busy} value={dpi} onChange={event => setDpi(Number(event.target.value))}>{[72, 144, 216].map(value => <option key={value} value={value}>{value} DPI</option>)}</select></label>}<span role="status">{progress}</span>{busy ? <button onClick={cancelExport}>Cancel export</button> : <button onClick={onClose}>Cancel</button>}<button className="redaction-export" disabled={busy || !confirmed || !count || !preview || mode === 'content' && impactStatus !== 'ready'} onClick={() => void apply()}><ScanLine size={16} />Export redacted PDF</button></footer>
  </Modal>;
}
