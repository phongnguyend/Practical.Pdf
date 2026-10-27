import { downloadImage } from '../downloadImage';
import { renderNative } from '../renderers/nativeRenderer';
import { useEffect, useRef, useState } from 'react';
import { Upload, MousePointer2, Download, FileText, ChevronLeft, ChevronRight, X, FolderOpen } from 'lucide-react';
import { getDocument, GlobalWorkerOptions, type PDFDocumentProxy, type PageViewport, type RenderTask } from 'pdfjs-dist';
import workerUrl from 'pdfjs-dist/build/pdf.worker.min.mjs?url';
import { hitTest } from '../geometry.mjs';
import type { Alignment } from '../components/TextAlignment';
import { MarkdownView } from '../components/MarkdownView';
import { AddObject } from '../components/AddObject';
import { prepareImageForPdf } from '../prepareImage';
import { ObjectTypeIcon } from '../components/ObjectTypeIcon';
import { ExistingObjectDrag } from '../components/ExistingObjectDrag';
import { Placement, type FieldPreview } from '../components/Placement';
import { SelectionSummary } from '../components/SelectionSummary';
import { ObjectEditor, type Edit } from '../components/ObjectEditor';

GlobalWorkerOptions.workerSrc = workerUrl;
type Kind = 'text' | 'image' | 'field';
type CanvasObject = { id: string; pageNumber: number; kind: Kind; label: string; corners: { x: number; y: number }[]; details: unknown };
type Inspection = { pdfVersion: string; pageCount: number; objectCount: number; objects: CanvasObject[]; warnings: string[] };
const colors: Record<Kind, string> = { text: '#2563eb', image: '#be6514', field: '#008577' };

export function PlaygroundPage() {
  const [renderer, setRenderer] = useState<'pdfjs' | 'native'>('pdfjs');
  const [renderNotes, setRenderNotes] = useState<string[]>([]);
  const [pdf, setPdf] = useState<PDFDocumentProxy | null>(null);
  const [inspection, setInspection] = useState<Inspection | null>(null);
  const [name, setName] = useState('');
  const [page, setPage] = useState(1);
  const [zoom, setZoom] = useState(1);
  const [busy, setBusy] = useState(false);
  const [fileDrag, setFileDrag] = useState<'pdf' | 'image' | null>(null);
  const fileDragDepth = useRef(0);
  const [creationKind, setCreationKind] = useState('add-text');
  const [creationId, setCreationId] = useState(0);
  const [fieldPreview, setFieldPreview] = useState<FieldPreview>();
  const [draftText, setDraftText] = useState('');
  const [draftAlignment, setDraftAlignment] = useState<Alignment>('Left');
  const [draftFontSize, setDraftFontSize] = useState(12);
  const [adding, setAdding] = useState(false);
  const [placement, setPlacement] = useState({ x: 40, y: 40, width: 150, height: 30 });
  const [droppedImage, setDroppedImage] = useState<File>();
  const [saving, setSaving] = useState(false);
  const [exporting, setExporting] = useState(false);
  const [showMarkdown, setShowMarkdown] = useState(false);
  const [pdfBytes, setPdfBytes] = useState<Blob | null>(null);
  const [error, setError] = useState('');
  const [selected, setSelected] = useState<CanvasObject | null>(null);
  const [layers, setLayers] = useState<Record<Kind, boolean>>({ text: true, image: true, field: true });
  const [viewport, setViewport] = useState<PageViewport | null>(null);
  const [rendering, setRendering] = useState(false);
  const host = useRef<HTMLDivElement>(null);
  const prepared = useRef<{ pdf: PDFDocumentProxy; canvas: HTMLCanvasElement; viewport: PageViewport; renderer: string; notes: string[] } | null>(null);
  const overlay = useRef<HTMLCanvasElement>(null);
  const generation = useRef(0);
  const abort = useRef<AbortController | null>(null);
  const deletingByKeyboard = useRef(false);
  const objects = (inspection?.objects ?? []).filter(item => item.pageNumber === page && layers[item.kind]);

  useEffect(() => () => { void pdf?.loadingTask.destroy(); }, [pdf]);
  useEffect(() => () => { abort.current?.abort(); }, []);
  useEffect(() => {
    function clearFileDrag() {
      fileDragDepth.current = 0;
      setFileDrag(null);
    }
    function onKeyDown(event: KeyboardEvent) {
      if (event.key === 'Escape') {
        clearFileDrag();
      }
    }
    window.addEventListener('drop', clearFileDrag, true);
    window.addEventListener('dragend', clearFileDrag);
    window.addEventListener('blur', clearFileDrag);
    window.addEventListener('keydown', onKeyDown);
    return () => {
      window.removeEventListener('drop', clearFileDrag, true);
      window.removeEventListener('dragend', clearFileDrag);
      window.removeEventListener('blur', clearFileDrag);
      window.removeEventListener('keydown', onKeyDown);
    };
  }, []);

  useEffect(() => {
    function cancelOnEscape(event: KeyboardEvent) {
      if (event.key === 'Escape' && adding && !saving && !busy) {
        event.preventDefault();
        setAdding(false);
        setDroppedImage(undefined);
        setDraftText('');
      }
    }
    window.addEventListener('keydown', cancelOnEscape);
    return () => window.removeEventListener('keydown', cancelOnEscape);
  }, [adding, saving, busy]);

  async function openFile(file?: File) {
    if (!file || saving) {
      return;
    }
    const version = ++generation.current;
    abort.current?.abort();
    const controller = new AbortController();
    abort.current = controller;
    setError('');
    if (file.size > 20 * 1024 * 1024) {
      setBusy(false);
      setError('Choose a PDF smaller than 20 MB.');
      return;
    }
    setBusy(true);
    let task: ReturnType<typeof getDocument> | undefined;
    try {
      const bytes = await file.arrayBuffer();
      const source = new Blob([bytes], { type: 'application/pdf' });
      const response = await fetch('/api/documents/inspect', { method: 'POST', headers: { 'Content-Type': 'application/pdf' }, body: bytes, signal: controller.signal });
      if (!response.ok) {
        const problem = await response.json().catch(() => null);
        throw new Error(problem?.detail ?? `Upload failed (${response.status}). Is the API running?`);
      }
      const data: Inspection = await response.json();
      task = getDocument({ data: bytes, cMapUrl: '/pdfjs/cmaps/', cMapPacked: true, standardFontDataUrl: '/pdfjs/standard_fonts/', wasmUrl: '/pdfjs/wasm/' });
      const loaded = await task.promise;
      if (version !== generation.current) {
        await loaded.loadingTask.destroy();
        return;
      }
      setPdf(loaded);
      setPdfBytes(source);
      setInspection(data);
      setAdding(false);
      setName(file.name);
      setPage(1);
      setSelected(null);
      setViewport(null);
      setZoom(1);
    } catch (cause) {
      await task?.destroy();
      if (version === generation.current && !controller.signal.aborted) {
        setError(cause instanceof Error ? cause.message : 'Unable to open this PDF.');
      }
    } finally {
      if (version === generation.current) {
        setBusy(false);
      }
    }
  }

  async function saveEdit(edit: Edit) {
    if (!pdfBytes || saving || busy) {
      return;
    }
    setSaving(true);
    setError('');
    let task: ReturnType<typeof getDocument> | undefined;
    try {
      const body = new FormData();
      body.append('file', pdfBytes, 'document.pdf');
      const { image, ...command } = edit;
      body.append('edit', JSON.stringify(command));
      if (image) {
        body.append('image', await prepareImageForPdf(image));
      }
      const response = await fetch('/api/documents/edit', { method: 'POST', body });
      if (!response.ok) {
        throw new Error((await response.json()).detail ?? 'Edit failed.');
      }
      const updated = await response.blob();
      const checked = await fetch('/api/documents/inspect', { method: 'POST', headers: { 'Content-Type': 'application/pdf' }, body: updated });
      if (!checked.ok) {
        throw new Error((await checked.json()).detail ?? 'Could not refresh the edited document.');
      }
      const data: Inspection = await checked.json();
      task = getDocument({ data: await updated.arrayBuffer(), cMapUrl: '/pdfjs/cmaps/', cMapPacked: true, standardFontDataUrl: '/pdfjs/standard_fonts/', wasmUrl: '/pdfjs/wasm/' });
      const loaded = await task.promise;
      // Check rendering before replacing the current document. A failed save keeps the old bytes.
      const preview = await loaded.getPage(page);
      const canvas = document.createElement('canvas');
      const view = preview.getViewport({ scale: zoom });
      const ratio = Math.min(window.devicePixelRatio || 1, 2, Math.sqrt(16_000_000 / (view.width * view.height)));
      canvas.width = Math.ceil(view.width * ratio);
      canvas.height = Math.ceil(view.height * ratio);
      canvas.style.width = `${view.width}px`;
      canvas.style.height = `${view.height}px`;
      let notes: string[] = [];
      if (renderer === 'native') {
        notes = await renderNative(canvas, view, ratio, updated, page);
      } else {
        await preview.render({ canvas, viewport: view, transform: [ratio, 0, 0, ratio, 0, 0] }).promise;
      }
      canvas.dataset.renderer = renderer;
      prepared.current = { pdf: loaded, canvas, viewport: view, renderer, notes };
      setPdf(loaded);
      setPdfBytes(updated);
      setInspection(data);
      setAdding(false);
      if (edit.kind === 'move-text' && selected) {
        // Line IDs follow reading order, which can change after a move.
        setSelected(data.objects.find(item => item.kind === 'text' && item.pageNumber === page && item.label === selected.label
          && Math.abs(item.corners[0].x - selected.corners[0].x - (edit.deltaX ?? 0)) < .1
          && Math.abs(item.corners[0].y - selected.corners[0].y - (edit.deltaY ?? 0)) < .1) ?? null);
      } else {
        setSelected(edit.kind.startsWith('move-') ? data.objects.find(item => item.id === selected?.id) ?? null : null);
      }
    } catch (cause) {
      await task?.destroy();
      setError(cause instanceof Error ? cause.message : 'Edit failed.');
    } finally {
      setSaving(false);
    }
  }

  useEffect(() => {
    function deleteSelection(event: KeyboardEvent) {
      if (event.key !== 'Delete' || event.defaultPrevented || event.repeat || event.isComposing
        || event.ctrlKey || event.metaKey || event.altKey || event.shiftKey
        || !selected || adding || saving || busy || rendering || exporting || deletingByKeyboard.current) {
        return;
      }
      const target = event.target instanceof Element ? event.target : document.activeElement;
      if (document.querySelector('dialog[open], [role="dialog"][aria-modal="true"]')
        || target?.closest('input, textarea, select, [contenteditable], [role="textbox"]')) {
        return;
      }
      const details = selected.details as { isReadOnly?: boolean; imageNumber?: number; name?: string; runs?: unknown[] };
      if (details.isReadOnly) {
        return;
      }
      let edit: Edit;
      if (selected.kind === 'text' && details.runs?.length) {
        edit = { kind: 'delete-text-line', value: '', pageNumber: selected.pageNumber, runs: details.runs };
      } else if (selected.kind === 'image' && details.imageNumber) {
        edit = { kind: 'delete-image', value: '', pageNumber: selected.pageNumber, imageNumber: details.imageNumber };
      } else if (selected.kind === 'field' && details.name) {
        edit = { kind: 'delete-field', value: '', pageNumber: selected.pageNumber, fieldName: details.name };
      } else {
        return;
      }
      event.preventDefault();
      deletingByKeyboard.current = true;
      void saveEdit(edit).finally(() => {
        deletingByKeyboard.current = false;
      });
    }
    window.addEventListener('keydown', deleteSelection);
    return () => window.removeEventListener('keydown', deleteSelection);
  }, [selected, adding, saving, busy, rendering, exporting, saveEdit]);

  async function exportImage(pageNumber: number, imageNumber: number) {
    if (!pdfBytes || exporting) {
      return;
    }
    setExporting(true);
    setError('');
    try {
      await downloadImage(pdfBytes, pageNumber, imageNumber, name);
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : 'Image export failed.');
    } finally {
      setExporting(false);
    }
  }

  function download() {
    if (!pdfBytes) {
      return;
    }
    const url = URL.createObjectURL(pdfBytes);
    const link = document.createElement('a');
    link.href = url;
    link.download = name.replace(/\.pdf$/i, '') + '-edited.pdf';
    link.click();
    setTimeout(() => URL.revokeObjectURL(url), 1000);
  }

  useEffect(() => {
    if (!pdf || !pdfBytes || !host.current) {
      return;
    }
    if (prepared.current?.pdf === pdf && prepared.current.renderer === renderer) {
      host.current.replaceChildren(prepared.current.canvas);
      setViewport(prepared.current.viewport);
      setRenderNotes(prepared.current.notes);
      prepared.current = null;
      setRendering(false);
      return;
    }
    const controller = new AbortController();
    let cancelled = false;
    let task: RenderTask | undefined;
    const canvas = document.createElement('canvas');
    setRendering(true);
    setRenderNotes([]);
    void (async () => {
      try {
        const pdfPage = await pdf.getPage(page);
        if (cancelled) {
          return;
        }
        const view = pdfPage.getViewport({ scale: zoom });
        // Cap backing pixels for very large pages and high-DPI displays.
        const ratio = Math.min(window.devicePixelRatio || 1, 2, Math.sqrt(16_000_000 / (view.width * view.height)));
        canvas.width = Math.ceil(view.width * ratio);
        canvas.height = Math.ceil(view.height * ratio);
        canvas.style.width = `${view.width}px`;
        canvas.style.height = `${view.height}px`;
        let notes: string[] = [];
        if (renderer === 'native') {
          notes = await renderNative(canvas, view, ratio, pdfBytes, page, controller.signal);
        } else {
          task = pdfPage.render({ canvas, viewport: view, transform: [ratio, 0, 0, ratio, 0, 0] });
          await task.promise;
        }
        if (!cancelled) {
          canvas.dataset.renderer = renderer;
          host.current?.replaceChildren(canvas);
          setViewport(view);
          setRenderNotes(notes);
        }
      } catch (cause) {
        if (!cancelled) {
          host.current?.replaceChildren();
          setViewport(null);
          setError(cause instanceof Error ? cause.message : 'Page rendering failed.');
        }
      } finally {
        if (!cancelled) {
          setRendering(false);
        }
      }
    })();
    return () => {
      cancelled = true;
      controller.abort();
      task?.cancel();
    };
  }, [pdf, pdfBytes, page, zoom, renderer]);

  useEffect(() => {
    const canvas = overlay.current;
    if (!canvas || !viewport) {
      return;
    }
    const ratio = Math.min(window.devicePixelRatio || 1, 2, Math.sqrt(16_000_000 / (viewport.width * viewport.height)));
    canvas.width = Math.ceil(viewport.width * ratio);
    canvas.height = Math.ceil(viewport.height * ratio);
    const context = canvas.getContext('2d')!;
    context.scale(ratio, ratio);
    for (const item of objects) {
      const points = item.corners.map(p => viewport.convertToViewportPoint(p.x, p.y));
      if (points.length < 3) {
        continue;
      }
      context.beginPath();
      points.forEach(([x, y], index) => {
        if (index === 0) {
          context.moveTo(x, y);
        } else {
          context.lineTo(x, y);
        }
      });
      context.closePath();
      context.strokeStyle = colors[item.kind];
      context.fillStyle = `${colors[item.kind]}${selected?.id === item.id ? '40' : '0c'}`;
      context.lineWidth = selected?.id === item.id ? 2.5 : 1;
      context.fill();
      context.stroke();
    }
  }, [viewport, inspection, page, layers, selected]);

  function selectObject(item: CanvasObject | null) {
    if (saving || busy || rendering) {
      return;
    }
    setAdding(false);
    setDroppedImage(undefined);
    setSelected(item);
  }

  function startCreation(kind: string, x = placement.x, y = placement.y) {
    setCreationKind(kind);
    setDraftText('');
    setDraftFontSize(12);
    setDraftAlignment('Left');
    setCreationId(id => id + 1);
    setDroppedImage(undefined);
    setPlacement({ x, y, width: 150, height: kind === 'add-image' ? 100 : 30 });
    setSelected(null);
    setAdding(true);
  }

  function changePage(value: number) {
    setAdding(false);
    setPage(value);
    setSelected(null);
    setViewport(null);
  }

  const showFileDrop = fileDrag !== null && !busy && !saving;
  return <div className={`app${showFileDrop ? ' file-drag-active' : ''}`} onDragEnter={event => {
    if (!event.dataTransfer.types.includes('Files')) {
      return;
    }
    event.preventDefault();
    fileDragDepth.current++;
    const isImage = Array.from(event.dataTransfer.items).some(item => /^image\/(png|jpeg|webp|gif|bmp|avif)$/.test(item.type));
    setFileDrag(isImage ? 'image' : 'pdf');
  }} onDragLeave={event => {
    if (!event.dataTransfer.types.includes('Files')) {
      return;
    }
    fileDragDepth.current = Math.max(0, fileDragDepth.current - 1);
    if (fileDragDepth.current === 0) {
      setFileDrag(null);
    }
  }} onDragOver={event => {
    event.preventDefault();
    if (event.dataTransfer.types.includes('Files')) {
      event.dataTransfer.dropEffect = saving ? 'none' : 'copy';
    }
  }} onDrop={event => {
    event.preventDefault();
    const file = event.dataTransfer.files[0];
    if (file && pdf && /^image\/(png|jpeg|webp|gif|bmp|avif)$/.test(file.type) && !saving && !busy) {
      startCreation('add-image');
      setDroppedImage(file);
    } else {
      void openFile(file);
    }
  }}>
    <header><div className="brand"><span className="logo">P</span><div><strong>PdfObjectKit</strong><span className="subtitle">OBJECT PLAYGROUND</span></div></div><label className="upload"><FolderOpen size={17} aria-hidden="true" />{busy ? 'Opening…' : 'Open PDF'}<input aria-label="Open PDF" disabled={saving} type="file" accept="application/pdf,.pdf" onChange={event => { void openFile(event.target.files?.[0]); event.target.value = ''; }} /></label></header>
    <div className="toolbar"><span className="filename">{name || 'No document open'}</span>{pdf && <><div className="renderer-switch" role="group" aria-label="Page renderer">{(['pdfjs', 'native'] as const).map(value => <button key={value} aria-pressed={renderer === value} disabled={saving || busy || rendering} onClick={() => { setRenderer(value); setError(''); }}>{value === 'pdfjs' ? 'PDF.js' : 'PdfObjectKit (experimental)'}</button>)}</div><button disabled={saving || busy} onClick={download}><Download size={16} aria-hidden="true" />Download PDF</button><button disabled={saving || busy} onClick={() => setShowMarkdown(true)}><FileText size={16} aria-hidden="true" />View Markdown</button><button aria-label="Previous page" title="Previous page" disabled={saving || page === 1} onClick={() => changePage(page - 1)}><ChevronLeft size={18} aria-hidden="true" /></button><label>Page <select aria-label="Page" disabled={saving} value={page} onChange={event => changePage(Number(event.target.value))}>{Array.from({ length: pdf.numPages }, (_, i) => <option key={i} value={i + 1}>{i + 1}</option>)}</select> / {pdf.numPages}</label><button aria-label="Next page" title="Next page" disabled={saving || page === pdf.numPages} onClick={() => changePage(page + 1)}><ChevronRight size={18} aria-hidden="true" /></button><select aria-label="Zoom" disabled={saving} value={zoom} onChange={event => setZoom(Number(event.target.value))}>{[.5, .75, 1, 1.25, 1.5, 2].map(value => <option key={value} value={value}>{value * 100}%</option>)}</select></>}</div>
    {error && <div className="error" role="alert">{error}<button onClick={() => setError('')}><X size={15} aria-hidden="true" />Dismiss</button></div>}
    <main><aside className="objects"><section className="toolbox" aria-label="Object toolbox"><h2>Create objects</h2><p className="hint">Drag a type onto the page. Move and resize it, then insert.</p>{[['add-text', 'Text'], ['add-image', 'Image'], ['add-field', 'Form field']].map(([kind, label]) => <button key={kind} className="toolbox-item" disabled={!pdf || saving || busy || rendering} draggable={!!pdf && !saving && !busy && !rendering} onDragStart={event => {
      event.dataTransfer.setData('application/x-pdfobjectkit-type', kind);
      event.dataTransfer.effectAllowed = 'copy';
    }} onClick={() => startCreation(kind)}><ObjectTypeIcon kind={kind} /><span>{label}</span></button>)}</section><h2>Page objects <span>{objects.length}</span></h2><div className="layers">{(['text', 'image', 'field'] as Kind[]).map(kind => <label key={kind} style={{ color: colors[kind] }}><input type="checkbox" checked={layers[kind]} onChange={event => { setLayers({ ...layers, [kind]: event.target.checked }); setSelected(null); }} />{kind}</label>)}</div><p className="hint">Select a canvas outline or an item below to inspect its source.</p><div className="object-list">{objects.map(item => <button className={selected?.id === item.id ? 'object selected' : 'object'} key={item.id} onClick={() => selectObject(item)}><span className="badge" style={{ color: colors[item.kind] }}>{item.kind}</span><span>{item.label || '(empty)'}</span></button>)}{pdf && !objects.length && <p className="hint">No extracted objects on this page with the enabled layers.</p>}</div></aside>
    <section className="workspace" aria-label="PDF canvas">{showFileDrop && <div className="file-drop-feedback"><div className="file-drop-card" role="status"><Upload size={30} aria-hidden="true" /><strong>{fileDrag === 'image' && pdf ? 'Drop image to place it' : pdf ? 'Drop PDF to open it' : 'Drop your PDF here'}</strong><span>{fileDrag === 'image' && pdf ? 'Choose its position and size on the page' : 'Release the file to upload and open it'}</span></div></div>}<div className="status" role="status">{saving ? 'Saving and rebuilding PDF?' : busy ? 'Uploading and extracting objects…' : rendering ? 'Rendering page…' : pdf ? `PDF ${inspection?.pdfVersion} · ${inspection?.objectCount} indirect objects` : 'LOCAL DEVELOPMENT PLAYGROUND'}</div>{!pdf && <div className="empty"><div className="document-icon">PDF</div><h1>Look inside your PDF.</h1><p>Drop a file here or choose Open PDF.<br />Explore text, image placements, and interactive fields.</p><span>Up to 20 MB · 100 pages</span></div>}<div className="page" onDragOver={event => {
      if (event.dataTransfer.types.includes('application/x-pdfobjectkit-type')) {
        event.preventDefault();
        event.dataTransfer.dropEffect = 'copy';
      }
    }} onDrop={event => {
      const kind = event.dataTransfer.getData('application/x-pdfobjectkit-type');
      if (!['add-text', 'add-image', 'add-field'].includes(kind)) {
        return;
      }
      event.preventDefault();
      event.stopPropagation();
      if (!viewport || saving || busy || rendering) {
        return;
      }
      const rect = event.currentTarget.getBoundingClientRect();
      const [x, y] = viewport.convertToPdfPoint((event.clientX - rect.left) * viewport.width / rect.width, (event.clientY - rect.top) * viewport.height / rect.height);
      startCreation(kind, Math.round((x - 75) * 100) / 100, Math.round((y - (kind === 'add-image' ? 50 : 15)) * 100) / 100);
    }} style={{ width: viewport?.width, height: viewport?.height, display: pdf ? 'block' : 'none' }}><div ref={host} />{viewport && !rendering && <canvas ref={overlay} className="overlay" aria-label="Extracted object outlines; use the object list to select with a keyboard" style={{ width: viewport.width, height: viewport.height }} onClick={event => {
      const bounds = event.currentTarget.getBoundingClientRect();
      if (adding) {
        const [x, y] = viewport.convertToPdfPoint((event.clientX - bounds.left) * viewport.width / bounds.width, (event.clientY - bounds.top) * viewport.height / bounds.height);
        setPlacement({ ...placement, x: Math.round(x * 100) / 100, y: Math.round(y * 100) / 100 });
        return;
      }
      selectObject(hitTest(objects, viewport, (event.clientX - bounds.left) * viewport.width / bounds.width, (event.clientY - bounds.top) * viewport.height / bounds.height) ?? null);
    }} />}{viewport && adding && !rendering && <Placement fieldPreview={fieldPreview} alignment={draftAlignment} field={creationKind === 'add-field'} text={creationKind !== 'add-image' ? draftText : undefined} fontSize={draftFontSize} resizable={creationKind !== 'add-text'} viewport={viewport} bounds={placement} onChange={setPlacement} image={droppedImage} disabled={saving || busy} />}{viewport && selected && !adding && !rendering && (['image', 'field', 'text'].includes(selected.kind)) && !(selected.details as { isReadOnly?: boolean }).isReadOnly && <ExistingObjectDrag key={`${selected.id}-${zoom}-${pdf?.fingerprints[0]}`} viewport={viewport} corners={selected.corners} disabled={saving || busy} onMove={(deltaX, deltaY) => {
      const details = selected.details as { imageNumber?: number; name?: string; widgetIndex?: number; runs?: unknown[] };
      return saveEdit({ kind: `move-${selected.kind}`, runs: details.runs, value: '', pageNumber: page, imageNumber: details.imageNumber, fieldName: details.name, widgetIndex: details.widgetIndex, deltaX, deltaY });
    }} />}</div></section>
    <aside className="inspector"><div className="inspector-heading"><h2>Inspector</h2><span>{adding ? 'NEW OBJECT' : selected ? 'SELECTION' : 'PROPERTIES'}</span></div>{adding ? <AddObject onFieldPreviewChange={setFieldPreview} alignment={draftAlignment} onAlignmentChange={setDraftAlignment} value={draftText} setValue={setDraftText} fontSize={draftFontSize} setFontSize={setDraftFontSize} key={creationId} kind={creationKind} pageNumber={page} point={placement} onBoundsChange={setPlacement} droppedImage={droppedImage} onImageChange={setDroppedImage} busy={saving || busy || rendering} onSave={saveEdit} onCancel={() => setAdding(false)} /> : selected ? <div key={selected.id} className="selected-object-editor"><SelectionSummary item={selected} disabled={saving} onClose={() => setSelected(null)} /><ObjectEditor item={selected} busy={saving || busy || rendering || exporting} onSave={saveEdit} onExportImage={exportImage} /><details className="technical-details"><summary>Technical details</summary><pre>{JSON.stringify(selected.details, null, 2)}</pre></details></div> : <div className="inspector-empty"><MousePointer2 size={28} aria-hidden="true" /><h3>Select an object</h3><p>Click an outline on the page or an item in the object list to edit its properties.</p><span>Drag a tool onto the page to create something new.</span></div>}{renderer === 'native' && pdf && <details className="renderer-notes"><summary>Experimental renderer - Rendering notes ({renderNotes.length})</summary>{renderNotes.map((note, index) => <p key={index}>{note}</p>)}</details>}{!!inspection?.warnings.length && <details className="warnings"><summary>Extraction notes ({inspection.warnings.length})</summary>{inspection.warnings.map((warning, i) => <p key={i}>{warning}</p>)}</details>}</aside></main>
    {showMarkdown && pdfBytes && <MarkdownView pdf={pdfBytes} name={name} onClose={() => setShowMarkdown(false)} />}
    <footer>PdfObjectKit extracts · {renderer === 'native' ? 'PdfObjectKit renders (experimental)' : 'PDF.js renders'}<span>Add, edit and delete · Files are not saved by the API</span></footer>
  </div>;
}


