import type { Edit } from './ObjectEditor';
import { useEffect, useRef, useState, type PointerEvent } from 'react';
import type { Worker, Line } from 'tesseract.js';
import { Copy, Maximize2, Minimize2, RefreshCw, ScanText, Save, X, Type, LayoutTemplate, MousePointer2, LoaderCircle, Check } from 'lucide-react';
import { extractImage } from '../downloadImage';
import { Modal } from './Modal';
import './ImageOcrDialog.css';

type Result = { text: string; lines: Line[]; width: number; height: number; confidence: number };
export function ImageOcrDialog({ pdf, page, image, onClose, onApply }: { pdf: Blob; page: number; image: number; onClose: () => void; onApply: (edit: Edit) => Promise<void> }) {
  const [applying, setApplying] = useState(false);
  const [replaceImage, setReplaceImage] = useState(false);
  const [applyError, setApplyError] = useState('');
  const [language, setLanguage] = useState('eng');
  const [attempt, setAttempt] = useState(0);
  const [result, setResult] = useState<Result>();
  const [status, setStatus] = useState('Preparing image');
  const [error, setError] = useState('');
  const [loading, setLoading] = useState(true);
  const [source, setSource] = useState(false);
  const [maximized, setMaximized] = useState(false);
  const [copyStatus, setCopyStatus] = useState('');
  const [selected, setSelected] = useState<number | null>(null);
  const drag = useRef<{ index: number; x: number; y: number; scaleX: number; scaleY: number; original: Line['bbox'] } | null>(null);
  const canvas = useRef<HTMLCanvasElement>(null);
  useEffect(() => {
    const controller = new AbortController();
    let worker: Worker | undefined;
    let cancelled = false;
    setLoading(true);
    setResult(undefined);
    setSelected(null);
    drag.current = null;
    setError('');
    setCopyStatus('');
    setStatus('Preparing image');
    void (async () => {
      try {
        const { blob } = await extractImage(pdf, page, image, controller.signal);
        const bitmap = await createImageBitmap(blob);
        const input = document.createElement('canvas');
        const scale = Math.min(1, 4096 / Math.max(bitmap.width, bitmap.height));
        input.width = Math.max(1, Math.round(bitmap.width * scale));
        input.height = Math.max(1, Math.round(bitmap.height * scale));
        const context = input.getContext('2d')!;
        context.fillStyle = '#fff';
        context.fillRect(0, 0, input.width, input.height);
        context.drawImage(bitmap, 0, 0, input.width, input.height);
        bitmap.close();
        if (cancelled) {
          return;
        }
        const { createWorker, PSM } = await import('tesseract.js');
        worker = await createWorker(language, 1, {
          workerPath: '/ocr/worker.min.js', corePath: '/ocr/core', langPath: '/ocr/lang',
          logger: message => {
            if (!cancelled) {
              setStatus(`${message.status} ${Math.round((message.progress ?? 0) * 100)}%`);
            }
          },
        });
        if (cancelled) {
          return;
        }
        await worker.setParameters({ tessedit_pageseg_mode: PSM.AUTO });
        const { data } = await worker.recognize(input, {}, { text: true, blocks: true });
        if (!cancelled) {
          setResult({ text: data.text, lines: (data.blocks ?? []).flatMap(block => block.paragraphs.flatMap(paragraph => paragraph.lines)).map(line => ({ ...line, text: line.text.trimEnd() })), width: input.width, height: input.height, confidence: data.confidence });
        }
      } catch (failure) {
        if (!cancelled) {
          setError(failure instanceof Error ? failure.message : 'Could not recognize this image.');
        }
      } finally {
        const finishedWorker = worker;
        worker = undefined;
        if (finishedWorker) {
          await finishedWorker.terminate();
        }
        if (!cancelled) {
          setLoading(false);
        }
      }
    })();
    return () => {
      cancelled = true;
      controller.abort();
      const activeWorker = worker;
      worker = undefined;
      if (activeWorker) {
        void activeWorker.terminate();
      }
    };
  }, [pdf, page, image, language, attempt]);
  useEffect(() => {
    if (!result || !canvas.current || source) {
      return;
    }
    const output = canvas.current;
    output.width = result.width;
    output.height = result.height;
    const context = output.getContext('2d')!;
    context.fillStyle = '#fff';
    context.fillRect(0, 0, output.width, output.height);
    context.fillStyle = '#111';
    context.textBaseline = 'top';
    for (const line of result.lines) {
      const { x0, y0, x1, y1 } = line.bbox;
      context.font = `${Math.max(1, y1 - y0)}px Arial, sans-serif`;
      context.fillText(line.text.trim(), x0, y0, Math.max(1, x1 - x0));
    }
  }, [result, source]);
  function moveLine(index: number, original: Line['bbox'], dx: number, dy: number) {
    setResult(current => {
      if (!current) {
        return current;
      }
      const x = Math.max(-original.x0, Math.min(dx, current.width - original.x1));
      const y = Math.max(-original.y0, Math.min(dy, current.height - original.y1));
      return { ...current, lines: current.lines.map((line, i) => i === index ? { ...line, baseline: { ...line.baseline, x0: line.baseline.x0 + original.x0 + x - line.bbox.x0, x1: line.baseline.x1 + original.x0 + x - line.bbox.x0, y0: line.baseline.y0 + original.y0 + y - line.bbox.y0, y1: line.baseline.y1 + original.y0 + y - line.bbox.y0 }, bbox: { x0: original.x0 + x, y0: original.y0 + y, x1: original.x1 + x, y1: original.y1 + y } } : line) };
    });
  }
  function startDrag(event: PointerEvent<HTMLButtonElement>, index: number) {
    if (event.button !== 0 || !result || !canvas.current) {
      return;
    }
    event.preventDefault();
    event.stopPropagation();
    event.currentTarget.focus({ preventScroll: true });
    setSelected(index);
    const bounds = canvas.current.getBoundingClientRect();
    drag.current = { index, x: event.clientX, y: event.clientY, scaleX: result.width / bounds.width, scaleY: result.height / bounds.height, original: { ...result.lines[index].bbox } };
    event.currentTarget.setPointerCapture(event.pointerId);
  }
  function updateDrag(event: PointerEvent<HTMLButtonElement>) {
    const gesture = drag.current;
    if (gesture) {
      moveLine(gesture.index, gesture.original, (event.clientX - gesture.x) * gesture.scaleX, (event.clientY - gesture.y) * gesture.scaleY);
    }
  }
  function cancelDrag() {
    const gesture = drag.current;
    drag.current = null;
    if (gesture) {
      moveLine(gesture.index, gesture.original, 0, 0);
    }
  }
  function editText(text: string) {
    if (selected === null) {
      return;
    }
    setResult(current => {
      if (!current) {
        return current;
      }
      const lines = current.lines.map((line, index) => index === selected ? { ...line, text } : line);
      return { ...current, lines, text: lines.map(line => line.text.trimEnd()).join('\n') };
    });
    setCopyStatus('');
  }
  async function apply() {
    if (!result || applying) {
      return;
    }
    setApplying(true);
    setApplyError('');
    try {
      const response = await fetch('/fonts/NotoSans-Regular.ttf');
      if (!response.ok) {
        throw new Error('Could not load the font for PDF text.');
      }
      const ocrFont = new File([await response.blob()], 'NotoSans-Regular.ttf', { type: 'font/ttf' });
      const ocrLines = result.lines.filter(line => line.text.trim()).map(line => ({
        text: line.text.trim(), x: line.bbox.x0 / result.width, y: line.bbox.y0 / result.height,
        width: (line.bbox.x1 - line.bbox.x0) / result.width, height: (line.bbox.y1 - line.bbox.y0) / result.height,
        baseline: Math.max(line.bbox.y0, Math.min(line.bbox.y1, line.baseline.y0)) / result.height,
      }));
      await onApply({ kind: 'apply-image-ocr', value: '', pageNumber: page, imageNumber: image, ocrLines, ocrFont, replaceOcrImage: replaceImage });
      onClose();
    } catch (failure) {
      setApplyError(failure instanceof Error ? failure.message : 'Could not apply OCR text.');
    } finally {
      setApplying(false);
    }
  }
  async function copy() {
    try {
      await navigator.clipboard.writeText(result?.text ?? '');
      setCopyStatus('Text copied');
    } catch {
      setCopyStatus('Could not copy. Select and copy the plain text instead.');
      setSource(true);
    }
  }
  return <Modal className={`image-ocr-dialog${maximized ? ' maximized' : ''}`} labelledBy="image-ocr-title" onClose={() => {
    if (!applying) {
      onClose();
    }
  }}>
    <header><div><h2 id="image-ocr-title"><ScanText size={20} />Image OCR</h2><p>Page {page} &middot; Image {image}{result && <span className="ocr-result-badge">{result.lines.length} lines &middot; {Math.round(result.confidence)}% confidence</span>}</p></div><div className="image-ocr-actions">
      <button type="button" aria-label={maximized ? 'Restore OCR size' : 'Maximize OCR'} onClick={() => setMaximized(value => !value)}>{maximized ? <Minimize2 size={18} /> : <Maximize2 size={18} />}</button>
      <button type="button" aria-label="Close OCR" disabled={applying} onClick={onClose}><X size={18} /></button>
    </div></header>
    <div className="image-ocr-toolbar"><label>Language<select value={language} disabled={loading || applying} onChange={event => setLanguage(event.target.value)}><option value="eng">English</option><option value="vie">Vietnamese</option><option value="eng+vie">English + Vietnamese</option></select></label>
      <div className="image-ocr-actions"><div className="ocr-view-switch" role="group" aria-label="OCR view"><button type="button" aria-pressed={!source} onClick={() => setSource(false)}><LayoutTemplate size={15} aria-hidden="true" />Canvas</button><button type="button" aria-pressed={source} onClick={() => setSource(true)}><Type size={15} aria-hidden="true" />Text</button></div><button type="button" disabled={loading || applying} onClick={() => setAttempt(value => value + 1)}><RefreshCw size={15} />Retry OCR</button><button type="button" disabled={!result?.text} onClick={() => void copy()}><Copy size={15} />Copy text</button></div>
    </div>
    <div className="image-ocr-workspace">
    <div className="image-ocr-body">
      {loading && <div className="ocr-empty" role="status"><LoaderCircle className="ocr-spinner" size={28} aria-hidden="true" /><h3>Recognizing text</h3><p>{status}</p><small>Processing locally in your browser. Close to cancel.</small></div>}
      {error && <p role="alert">OCR failed: {error} Try again or choose another language.</p>}
      {result && !result.text.trim() && <div className="ocr-empty" role="status"><ScanText size={28} aria-hidden="true" /><h3>No text detected</h3><p>Try another language or a clearer image.</p></div>}
      {result && (source ? <textarea aria-label="Extracted text" readOnly value={result.text} /> : <div className="image-ocr-stage" inert={applying} style={{ width: result.width, maxWidth: '100%' }} onPointerDown={() => setSelected(null)}>
        <canvas ref={canvas} aria-label="Extracted text canvas" role="img" />
        {result.lines.map((line, index) => <button type="button" key={index} className="image-ocr-line" aria-label={`OCR line ${index + 1}`} title={line.text.trim()} aria-pressed={selected === index}
          style={{ left: `${line.bbox.x0 / result.width * 100}%`, top: `${line.bbox.y0 / result.height * 100}%`, width: `${(line.bbox.x1 - line.bbox.x0) / result.width * 100}%`, height: `${(line.bbox.y1 - line.bbox.y0) / result.height * 100}%` }}
          onClick={event => {
            event.stopPropagation();
            setSelected(index);
          }}
          onPointerDown={event => startDrag(event, index)} onPointerMove={updateDrag} onPointerUp={event => {
            updateDrag(event);
            drag.current = null;
          }} onPointerCancel={cancelDrag} onLostPointerCapture={cancelDrag}
          onKeyDown={event => {
            if (event.key === 'Escape') {
              event.preventDefault();
              event.stopPropagation();
              if (drag.current) {
                cancelDrag();
              } else {
                setSelected(null);
              }
            } else if (['ArrowLeft', 'ArrowRight', 'ArrowUp', 'ArrowDown'].includes(event.key)) {
              event.preventDefault();
              setSelected(index);
              const step = event.shiftKey ? 10 : 1;
              moveLine(index, line.bbox, event.key === 'ArrowLeft' ? -step : event.key === 'ArrowRight' ? step : 0, event.key === 'ArrowUp' ? -step : event.key === 'ArrowDown' ? step : 0);
            }
          }} />)}
      </div>)}
    </div>
    {result && !source && <div className="image-ocr-selection">
      <div className="ocr-inspector-heading"><h3><Type size={16} aria-hidden="true" />Text inspector</h3>{selected !== null && <button type="button" aria-label="Clear OCR selection" title="Clear selection" onClick={() => setSelected(null)}><X size={15} /></button>}</div>
      {selected === null ? <div className="ocr-selection-empty"><MousePointer2 size={24} aria-hidden="true" /><strong>Select a text line</strong><p>Click text on the canvas to correct it or drag it into position.</p></div> : <div className="ocr-line-properties">
        <span className="ocr-line-label">LINE {selected + 1}</span>
        <label>Text<input aria-label="Selected OCR text" disabled={applying} value={result.lines[selected]?.text ?? ''} onChange={event => editText(event.target.value)} /></label>
        <p className="ocr-inline-note"><Check size={14} aria-hidden="true" />Changes appear in the preview immediately.</p>
      </div>}
      <div className="ocr-shortcuts"><strong>Position text</strong><p>Drag a line to move it.</p><p><kbd>Arrow keys</kbd> Nudge 1 px</p><p><kbd>Shift</kbd> + arrows Move 10 px</p><p><kbd>Esc</kbd> Cancel a drag</p></div>
      <p className="ocr-local-note">Review recognition errors before applying. Closing or retrying discards unapplied edits.</p>
    </div>}
    </div>
    <div className="image-ocr-apply">
      <div className="ocr-output-settings"><label>Output<select aria-label="OCR apply mode" disabled={applying} value={replaceImage ? 'replace' : 'searchable'} onChange={event => setReplaceImage(event.target.value === 'replace')}><option value="searchable">Searchable text layer (keep image)</option><option value="replace">Replace image with visible text</option></select></label><p>{replaceImage ? 'Replaces this image, including its graphics and background.' : 'Preserves the image and adds invisible, selectable text.'}</p></div>
      <div className="image-ocr-actions"><span className="ocr-copy-status" role="status">{copyStatus}</span><button type="button" disabled={applying} onClick={onClose}>Cancel</button><button type="button" className="ocr-apply-primary" disabled={loading || applying || !result?.lines.some(line => line.text.trim())} onClick={() => void apply()}>{applying ? <LoaderCircle className="ocr-spinner" size={16} /> : <Save size={16} />}{applying ? 'Applying...' : 'Apply to PDF'}</button></div>
      {applyError && <p role="alert">{applyError}</p>}
    </div>

  </Modal>;
}
