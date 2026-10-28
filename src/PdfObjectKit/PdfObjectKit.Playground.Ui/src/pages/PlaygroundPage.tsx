import { WatermarkDialog, type WatermarkInfo } from '../components/WatermarkDialog';
import { PdfSecurityDialog } from '../components/PdfSecurityDialog';
import { Modal } from '../components/Modal';
import { FormFieldToolbox } from '../components/FormFieldToolbox';
import { fieldDefaults, formFieldTypes, type FormFieldType } from '../components/formFieldTypes';
import { ObjectLayerFilters } from '../components/ObjectLayerFilters';
import { AnnotationActions } from '../components/AnnotationActions';
import { SelectableText, type TextSelection } from '../components/SelectableText';
import { useRectangleSelection } from '../components/useRectangleSelection';
import { PageAssemblyDialog } from '../components/PageAssemblyDialog';
import type { DocumentView } from './PlaygroundTabs';
import { downloadImage } from '../downloadImage';
import { renderNative } from '../renderers/nativeRenderer';
import { useEffect, useRef, useState, type ReactNode } from 'react';
import { Upload, Hand, TextCursor, MousePointer2, Download, FileText, ChevronLeft, ChevronRight, X, FolderOpen, Scissors, Files, Trash2, RotateCcw, RotateCw, Copy, LockKeyhole, Stamp } from 'lucide-react';
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
import { MultiSelectionSummary } from '../components/MultiSelectionSummary';
import { SelectionSummary } from '../components/SelectionSummary';
import { ObjectEditor, type Edit } from '../components/ObjectEditor';

GlobalWorkerOptions.workerSrc = workerUrl;
type Kind = 'text' | 'image' | 'field' | 'annotation';
type CanvasObject = { id: string; pageNumber: number; kind: Kind; label: string; corners: { x: number; y: number }[]; details: unknown };
type Inspection = { watermarks: WatermarkInfo[]; pdfVersion: string; pageCount: number; objectCount: number; objects: CanvasObject[]; warnings: string[] };
const colors: Record<Kind, string> = { text: '#2563eb', image: '#be6514', field: '#008577', annotation: '#a16207' };

export function PlaygroundPage({ initialFile, initialView, tabStrip, externalBusy = false, tabId, onOpenFiles, onSaved, onViewChange, onBusyChange }: {
  externalBusy?: boolean;
  initialFile?: File;
  initialView: DocumentView;
  tabStrip: ReactNode;
  tabId: string;
  onOpenFiles: (files: File[]) => void;
  onSaved: (file: File) => void;
  onViewChange: (view: DocumentView) => void;
  onBusyChange: (busy: boolean) => void;
}) {
  const [mode, setMode] = useState<'objects' | 'text' | 'pan'>('objects');
  const [textSelection, setTextSelection] = useState<TextSelection | null>(null);
  const panGesture = useRef<{ x: number; y: number; left: number; top: number } | null>(null);
  const [renderer, setRenderer] = useState<'pdfjs' | 'native'>(initialView.renderer);
  const [renderNotes, setRenderNotes] = useState<string[]>([]);
  const [pdf, setPdf] = useState<PDFDocumentProxy | null>(null);
  const [inspection, setInspection] = useState<Inspection | null>(null);
  const [name, setName] = useState('');
  const [page, setPage] = useState(initialView.page);
  const [zoom, setZoom] = useState(initialView.zoom);
  const [loading, setBusy] = useState(false);
  const busy = loading || externalBusy;
  const [fileDrag, setFileDrag] = useState<'pdf' | 'image' | null>(null);
  const fileDragDepth = useRef(0);
  const [creationFieldType, setCreationFieldType] = useState<FormFieldType>('Text');
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
  const [securityFile, setSecurityFile] = useState<File | null>(null);
  const [showWatermark, setShowWatermark] = useState(false);
  const [protectPdf, setProtectPdf] = useState(false);
  const [showMarkdown, setShowMarkdown] = useState(false);
  const [confirmDeletePage, setConfirmDeletePage] = useState(false);
  const [assembly, setAssembly] = useState<'split' | 'extract' | null>(null);
  const [pdfBytes, setPdfBytes] = useState<Blob | null>(null);
  const [error, setError] = useState('');
  const [selection, setSelection] = useState<CanvasObject[]>([]);
  const selected = selection.length === 1 ? selection[0] : null;
  function setSelected(item: CanvasObject | null) {
    setSelection(item ? [item] : []);
  }
  const selectedIds = new Set(selection.map(item => item.id));
  const [layers, setLayers] = useState<Record<Kind, boolean>>({ text: true, image: true, field: true, annotation: true });
  const [viewport, setViewport] = useState<PageViewport | null>(null);
  const [rendering, setRendering] = useState(false);
  const host = useRef<HTMLDivElement>(null);
  const prepared = useRef<{ pdf: PDFDocumentProxy; canvas: HTMLCanvasElement; viewport: PageViewport; renderer: string; notes: string[] } | null>(null);
  const overlay = useRef<HTMLCanvasElement>(null);
  const generation = useRef(0);
  const abort = useRef<AbortController | null>(null);
  const deletingByKeyboard = useRef(false);
  const objects = (inspection?.objects ?? []).filter(item => item.pageNumber === page && layers[item.kind]);

  const marquee = useRectangleSelection({
    width: viewport?.width ?? 0,
    height: viewport?.height ?? 0,
    disabled: mode !== 'objects' || !viewport || adding || saving || busy || rendering || exporting,
    canStart: point => !!viewport && !hitTest(objects, viewport, point.x, point.y),
    onSelect: (rectangle, additive) => {
      if (!viewport) {
        return;
      }
      const enclosed = objects.filter(item => item.corners.length > 0 && item.corners.every(corner => {
        const [x, y] = viewport.convertToViewportPoint(corner.x, corner.y);
        return x >= rectangle.left && x <= rectangle.left + rectangle.width
          && y >= rectangle.top && y <= rectangle.top + rectangle.height;
      }));
      setSelection(current => additive
        ? [...current, ...enclosed.filter(item => !current.some(existing => existing.id === item.id))]
        : enclosed);
    },
  });

  useEffect(() => () => { void pdf?.loadingTask.destroy(); }, [pdf]);
  useEffect(() => () => { generation.current++; abort.current?.abort(); }, []);
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

  useEffect(() => {
    if (initialFile) {
      void openFile(initialFile, true);
    }
  }, []);
  useEffect(() => {
    onViewChange({ page, zoom, renderer });
  }, [page, zoom, renderer]);
  useEffect(() => {
    onBusyChange(busy || saving || rendering || exporting || adding || showMarkdown || showWatermark || securityFile !== null || protectPdf || confirmDeletePage || assembly !== null);
  }, [busy, saving, rendering, exporting, adding, showMarkdown, showWatermark, securityFile, protectPdf, confirmDeletePage, assembly]);

  function openFiles(files: File[]) {
    if (saving || busy) {
      return;
    }
    try {
      onOpenFiles(files);
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : 'Could not open tabs.');
    }
  }

  async function openFile(file?: File, fromTab = false) {
    if (!file || saving) {
      return;
    }
    if (!fromTab) {
      openFiles([file]);
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
      if (response.status === 401) {
        setSecurityFile(file);
        return;
      }
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
      setPage(Math.min(initialView.page, loaded.numPages));
      setSelected(null);
      setViewport(null);
      setZoom(initialView.zoom);
      return true;
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
      const { image, certificate, ...command } = edit;
      body.append('edit', JSON.stringify(command));
      if (certificate) {
        body.append('certificate', certificate);
      }
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
      const nextPage = Math.min(edit.kind === 'duplicate-page' ? page + 1 : page, loaded.numPages);
      const preview = await loaded.getPage(nextPage);
      const canvas = document.createElement('canvas');
      const view = preview.getViewport({ scale: zoom });
      const ratio = Math.min(window.devicePixelRatio || 1, 2, Math.sqrt(16_000_000 / (view.width * view.height)));
      canvas.width = Math.ceil(view.width * ratio);
      canvas.height = Math.ceil(view.height * ratio);
      canvas.style.width = `${view.width}px`;
      canvas.style.height = `${view.height}px`;
      let notes: string[] = [];
      if (renderer === 'native') {
        notes = await renderNative(canvas, view, ratio, updated, nextPage);
      } else {
        await preview.render({ canvas, viewport: view, transform: [ratio, 0, 0, ratio, 0, 0] }).promise;
      }
      canvas.dataset.renderer = renderer;
      prepared.current = { pdf: loaded, canvas, viewport: view, renderer, notes };
      setPdf(loaded);
      setPdfBytes(updated);
      setPage(nextPage);
      setTextSelection(null);
      onSaved(new File([updated], name, { type: 'application/pdf' }));
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

  function deletionEdit(): Edit | null {
    const edits: Edit[] = [];
    for (const item of selection) {
      const details = item.details as { annotationNumber?: number; isReadOnly?: boolean; imageNumber?: number; name?: string; runs?: unknown[] };
      if (details.isReadOnly) {
        return null;
      }
      if (item.kind === 'text' && details.runs?.length) {
        edits.push({ kind: 'delete-text-line', value: '', pageNumber: item.pageNumber, runs: details.runs });
      } else if (item.kind === 'image' && details.imageNumber) {
        edits.push({ kind: 'delete-image', value: '', pageNumber: item.pageNumber, imageNumber: details.imageNumber });
      } else if (item.kind === 'annotation' && details.annotationNumber) {
        edits.push({ kind: 'delete-annotation', value: '', pageNumber: item.pageNumber, annotationNumber: details.annotationNumber });
      } else if (item.kind === 'field' && details.name) {
        edits.push({ kind: 'delete-field', value: '', pageNumber: item.pageNumber, fieldName: details.name });
      } else {
        return null;
      }
    }
    return edits.length ? { kind: 'delete-objects', value: '', pageNumber: page, deletions: edits } : null;
  }

  useEffect(() => {
    function deleteSelection(event: KeyboardEvent) {
      if (event.key !== 'Delete' || event.defaultPrevented || event.repeat || event.isComposing
        || event.ctrlKey || event.metaKey || event.altKey || event.shiftKey
        || mode !== 'objects' || !selection.length || adding || saving || busy || rendering || exporting || deletingByKeyboard.current) {
        return;
      }
      const target = event.target instanceof Element ? event.target : document.activeElement;
      if (document.querySelector('dialog[open], [role="dialog"][aria-modal="true"]')
        || target?.closest('input, textarea, select, [contenteditable], [role="textbox"]')) {
        return;
      }
      const edit = deletionEdit();
      if (!edit) {
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
  }, [selection, mode, adding, saving, busy, rendering, exporting, saveEdit]);

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
      context.fillStyle = `${colors[item.kind]}${selectedIds.has(item.id) ? '40' : '0c'}`;
      context.lineWidth = selectedIds.has(item.id) ? 2.5 : 1;
      context.fill();
      context.stroke();
    }
  }, [viewport, inspection, page, layers, selection, mode]);

  function selectObject(item: CanvasObject | null, toggle = false) {
    if (saving || busy || rendering) {
      return;
    }
    setAdding(false);
    setDroppedImage(undefined);
    if (toggle && item) {
      setSelection(current => current.some(entry => entry.id === item.id)
        ? current.filter(entry => entry.id !== item.id) : [...current, item]);
    } else if (!toggle) {
      setSelected(item);
    }
  }

  function startCreation(tool: string, x = placement.x, y = placement.y) {
    const fieldType = formFieldTypes.find(item => tool === `add-field:${item.type}`)?.type ?? 'Text';
    const kind = tool.startsWith('add-field') ? 'add-field' : tool;
    const defaults = fieldDefaults(fieldType);
    setCreationFieldType(fieldType);
    setFieldPreview(undefined);
    setMode('objects');
    setTextSelection(null);
    window.getSelection()?.removeAllRanges();
    setCreationKind(kind);
    setDraftText(kind === 'add-field' ? defaults.value : '');
    setDraftFontSize(12);
    setDraftAlignment('Left');
    setCreationId(id => id + 1);
    setDroppedImage(undefined);
    setPlacement({ x, y, width: kind === 'add-field' ? defaults.width : kind === 'add-comment' ? 24 : 150, height: kind === 'add-field' ? defaults.height : kind === 'add-comment' ? 24 : kind === 'add-image' ? 100 : 20 });
    setSelected(null);
    setAdding(true);
  }

  function changePage(value: number) {
    setAdding(false);
    setPage(value);
    setSelected(null);
    setViewport(null);
  }

  const markupActions = selection.length > 0 && selection.every(item => item.kind === 'text')
    ? <AnnotationActions label="Annotate selected text" disabled={saving || busy || rendering} onApply={kind => {
      void saveEdit({ kind: `add-${kind}`, value: '', pageNumber: page, regions: selection.map(item => {
        const xs = item.corners.map(point => point.x);
        const ys = item.corners.map(point => point.y);
        return { x: Math.min(...xs), y: Math.min(...ys), width: Math.max(...xs) - Math.min(...xs), height: Math.max(...ys) - Math.min(...ys) };
      }) });
    }} /> : null;

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
      openFiles(Array.from(event.dataTransfer.files));
    }
  }}>
    <header><div className="brand"><span className="logo">P</span><div><strong>PdfObjectKit</strong><span className="subtitle">OBJECT PLAYGROUND</span></div></div><label className="upload"><FolderOpen size={17} aria-hidden="true" />{busy ? 'Opening…' : 'Open PDF'}<input aria-label="Open PDF" disabled={saving || busy} type="file" multiple accept="application/pdf,.pdf" onChange={event => { openFiles(Array.from(event.target.files ?? [])); event.target.value = ''; }} /></label></header>{tabStrip}
    <div id="document-panel" role="tabpanel" aria-labelledby={"tab-" + tabId} className="document-panel"><div className="toolbar"><span className="filename">{name || 'No document open'}</span>{pdf && <><div className="interaction-mode" role="group" aria-label="Interaction mode">{([
      ['objects', 'Select objects', MousePointer2], ['text', 'Select text', TextCursor], ['pan', 'Pan', Hand],
    ] as const).map(([value, label, Icon]) => <button key={value} type="button" className="icon-button" aria-label={label} title={label} aria-pressed={mode === value} disabled={saving || busy || rendering} onClick={() => {
      setMode(value);
      setSelected(null);
      setAdding(false);
      setTextSelection(null);
      window.getSelection()?.removeAllRanges();
      panGesture.current = null;
    }}><Icon size={19} aria-hidden="true" /></button>)}</div><div className="renderer-switch" role="group" aria-label="Page renderer">{(['pdfjs', 'native'] as const).map(value => <button key={value} aria-pressed={renderer === value} disabled={saving || busy || rendering} onClick={() => { setRenderer(value); setError(''); }}>{value === 'pdfjs' ? 'PDF.js' : 'PdfObjectKit (experimental)'}</button>)}</div><button disabled={saving || busy || rendering} onClick={() => setAssembly('split')}><Scissors size={16} aria-hidden="true" />Split PDF</button><button disabled={saving || busy || rendering} onClick={() => setAssembly('extract')}><Files size={16} aria-hidden="true" />Extract pages</button><button disabled={saving || busy || rendering || adding} onClick={() => setShowWatermark(true)}><Stamp size={16} aria-hidden="true" />Watermark</button><button disabled={saving || busy || rendering} onClick={() => setProtectPdf(true)}><LockKeyhole size={16} aria-hidden="true" />Protect PDF</button><button disabled={saving || busy} onClick={download}><Download size={16} aria-hidden="true" />Download PDF</button><button disabled={saving || busy} onClick={() => setShowMarkdown(true)}><FileText size={16} aria-hidden="true" />View Markdown</button><button aria-label="Previous page" title="Previous page" disabled={saving || page === 1} onClick={() => changePage(page - 1)}><ChevronLeft size={18} aria-hidden="true" /></button><label>Page <select aria-label="Page" disabled={saving} value={page} onChange={event => changePage(Number(event.target.value))}>{Array.from({ length: pdf.numPages }, (_, i) => <option key={i} value={i + 1}>{i + 1}</option>)}</select> / {pdf.numPages}</label><button aria-label="Next page" title="Next page" disabled={saving || page === pdf.numPages} onClick={() => changePage(page + 1)}><ChevronRight size={18} aria-hidden="true" /></button>{([{ label: 'Rotate left', degrees: -90, Icon: RotateCcw }, { label: 'Rotate right', degrees: 90, Icon: RotateCw }]).map(({ label, degrees, Icon }) => <button key={label} aria-label={label} title={label + ' 90 degrees'} disabled={saving || busy || rendering || adding || exporting} onClick={() => {
      void saveEdit({ kind: 'rotate-page', pageNumber: page, rotation: degrees, value: '' });
    }}><Icon size={16} aria-hidden="true" /></button>)}<button aria-label="Duplicate page" title={pdf.numPages >= 100 ? 'The playground supports up to 100 pages' : 'Duplicate current page'} disabled={pdf.numPages >= 100 || saving || busy || rendering || adding || exporting} onClick={() => {
      void saveEdit({ kind: 'duplicate-page', pageNumber: page, value: '' });
    }}><Copy size={16} aria-hidden="true" />Duplicate page</button><button aria-label="Delete page" title={pdf.numPages === 1 ? 'Cannot delete the last page' : 'Delete current page'} disabled={pdf.numPages <= 1 || saving || busy || rendering || adding || exporting} onClick={() => setConfirmDeletePage(true)}><Trash2 size={16} aria-hidden="true" />Delete page</button><select aria-label="Zoom" disabled={saving} value={zoom} onChange={event => setZoom(Number(event.target.value))}>{[.5, .75, 1, 1.25, 1.5, 2].map(value => <option key={value} value={value}>{value * 100}%</option>)}</select></>}</div>
    {error && <div className="error" role="alert">{error}<button onClick={() => setError('')}><X size={15} aria-hidden="true" />Dismiss</button></div>}
    <main><aside className="objects"><section className="toolbox" aria-label="Object toolbox"><h2>Create objects</h2><p className="hint">Drag a type onto the page. Move and resize it, then insert.</p>{[['add-text', 'Text'], ['add-image', 'Image'], ['add-comment', 'Comment']].map(([kind, label]) => <button key={kind} className="toolbox-item" disabled={!pdf || saving || busy || rendering} draggable={!!pdf && !saving && !busy && !rendering} onDragStart={event => {
      event.dataTransfer.setData('application/x-pdfobjectkit-type', kind);
      event.dataTransfer.effectAllowed = 'copy';
    }} onClick={() => startCreation(kind)}><ObjectTypeIcon kind={kind} /><span>{label}</span></button>)}<FormFieldToolbox disabled={!pdf || saving || busy || rendering} activeType={adding && creationKind === 'add-field' ? creationFieldType : undefined} onChoose={type => startCreation(`add-field:${type}`)} /></section><h2>Page objects <span>{objects.length}</span></h2><ObjectLayerFilters layers={layers} onToggle={kind => {
      setLayers(current => ({ ...current, [kind]: !current[kind] }));
      setSelected(null);
    }} /><p className="hint">Drag on empty space to enclose objects. Ctrl/Cmd+click toggles selection; Ctrl/Cmd+drag adds objects. Alt+click selects annotations over text. Escape cancels.</p><div className="object-list">{objects.map(item => <button className={selectedIds.has(item.id) ? 'object selected' : 'object'} key={item.id} aria-pressed={selectedIds.has(item.id)} onClick={event => { setMode('objects'); setTextSelection(null); selectObject(item, event.ctrlKey || event.metaKey); }}><span className="badge" style={{ color: colors[item.kind] }}>{item.kind}</span><span>{item.label || '(empty)'}</span></button>)}{pdf && !objects.length && <p className="hint">No extracted objects on this page with the enabled layers.</p>}</div></aside>
    <section className={`workspace${mode === 'pan' ? ' pan-mode' : ''}`} aria-label="PDF canvas" onPointerDown={event => {
      if (mode !== 'pan' || event.button !== 0 || saving || busy || rendering) {
        return;
      }
      event.preventDefault();
      const surface = event.currentTarget;
      panGesture.current = { x: event.clientX, y: event.clientY, left: surface.scrollLeft, top: surface.scrollTop };
      surface.setPointerCapture(event.pointerId);
    }} onPointerMove={event => {
      const start = panGesture.current;
      if (mode === 'pan' && start) {
        event.currentTarget.scrollLeft = start.left - event.clientX + start.x;
        event.currentTarget.scrollTop = start.top - event.clientY + start.y;
      }
    }} onPointerUp={event => {
      panGesture.current = null;
      if (event.currentTarget.hasPointerCapture(event.pointerId)) {
        event.currentTarget.releasePointerCapture(event.pointerId);
      }
    }} onPointerCancel={() => { panGesture.current = null; }} onLostPointerCapture={() => { panGesture.current = null; }}>{showFileDrop && <div className="file-drop-feedback"><div className="file-drop-card" role="status"><Upload size={30} aria-hidden="true" /><strong>{fileDrag === 'image' && pdf ? 'Drop image to place it' : pdf ? 'Drop PDF to open it' : 'Drop your PDF here'}</strong><span>{fileDrag === 'image' && pdf ? 'Choose its position and size on the page' : 'Release the file to upload and open it'}</span></div></div>}<div className="status" role="status">{saving ? 'Saving and rebuilding PDF?' : busy ? 'Uploading and extracting objects…' : rendering ? 'Rendering page…' : pdf ? `PDF ${inspection?.pdfVersion} · ${inspection?.objectCount} indirect objects` : 'LOCAL DEVELOPMENT PLAYGROUND'}</div>{!pdf && <div className="empty"><div className="document-icon">PDF</div><h1>Look inside your PDF.</h1><p>Drop a file here or choose Open PDF.<br />Explore text, image placements, and interactive fields.</p><span>Up to 20 MB · 100 pages</span></div>}<div className="page" onDragOver={event => {
      if (event.dataTransfer.types.includes('application/x-pdfobjectkit-type')) {
        event.preventDefault();
        event.dataTransfer.dropEffect = 'copy';
      }
    }} onDrop={event => {
      const kind = event.dataTransfer.getData('application/x-pdfobjectkit-type');
      if (!['add-text', 'add-image', 'add-field', 'add-comment'].includes(kind) && !formFieldTypes.some(item => kind === `add-field:${item.type}`)) {
        return;
      }
      event.preventDefault();
      event.stopPropagation();
      if (!viewport || saving || busy || rendering) {
        return;
      }
      const rect = event.currentTarget.getBoundingClientRect();
      const [x, y] = viewport.convertToPdfPoint((event.clientX - rect.left) * viewport.width / rect.width, (event.clientY - rect.top) * viewport.height / rect.height);
      const fieldType = formFieldTypes.find(item => kind === `add-field:${item.type}`)?.type;
      const size = fieldType ? fieldDefaults(fieldType) : { width: 150, height: kind === 'add-image' ? 100 : 30 };
      startCreation(kind, Math.round((x - size.width / 2) * 100) / 100, Math.round((y - size.height / 2) * 100) / 100);
    }} style={{ width: viewport?.width, height: viewport?.height, display: pdf ? 'block' : 'none' }}><div ref={host} />{viewport && !rendering && mode === 'objects' && <canvas ref={overlay} className="overlay" {...marquee.handlers} aria-label="Extracted object outlines; use the object list to select with a keyboard" style={{ width: viewport.width, height: viewport.height }} onClick={event => {
      if (marquee.consumeClick()) {
        return;
      }
      const bounds = event.currentTarget.getBoundingClientRect();
      if (adding) {
        const [x, y] = viewport.convertToPdfPoint((event.clientX - bounds.left) * viewport.width / bounds.width, (event.clientY - bounds.top) * viewport.height / bounds.height);
        setPlacement({ ...placement, x: Math.round(x * 100) / 100, y: Math.round(y * 100) / 100 });
        return;
      }
      selectObject(hitTest(objects, viewport, (event.clientX - bounds.left) * viewport.width / bounds.width, (event.clientY - bounds.top) * viewport.height / bounds.height, event.altKey) ?? null, event.ctrlKey || event.metaKey);
    }} />}{mode === 'text' && viewport && pdf && !rendering && !saving && !busy && <SelectableText pdf={pdf} pageNumber={page} viewport={viewport} onSelection={setTextSelection} />}{marquee.rectangle && <div className="selection-rectangle" aria-hidden="true" style={marquee.rectangle} />}{viewport && adding && !rendering && <Placement fieldPreview={fieldPreview} alignment={draftAlignment} field={creationKind === 'add-field'} annotation={creationKind.replace('add-', '')} text={['add-text', 'add-field'].includes(creationKind) ? draftText : undefined} fontSize={draftFontSize} resizable={creationKind !== 'add-text'} viewport={viewport} bounds={placement} onChange={setPlacement} image={droppedImage} disabled={saving || busy} />}{mode === 'objects' && viewport && selected && !adding && !rendering && (['image', 'field', 'text'].includes(selected.kind)) && !(selected.details as { isReadOnly?: boolean }).isReadOnly && <ExistingObjectDrag onAnnotationPick={(clientX, clientY) => {
      const bounds = overlay.current!.getBoundingClientRect();
      selectObject(hitTest(objects, viewport, (clientX - bounds.left) * viewport.width / bounds.width, (clientY - bounds.top) * viewport.height / bounds.height, true) ?? null, false);
    }} onToggle={() => selectObject(selected, true)} key={`${selected.id}-${zoom}-${pdf?.fingerprints[0]}`} viewport={viewport} corners={selected.corners} disabled={saving || busy} onMove={(deltaX, deltaY) => {
      const details = selected.details as { imageNumber?: number; name?: string; widgetIndex?: number; runs?: unknown[] };
      return saveEdit({ kind: `move-${selected.kind}`, runs: details.runs, value: '', pageNumber: page, imageNumber: details.imageNumber, fieldName: details.name, widgetIndex: details.widgetIndex, deltaX, deltaY });
    }} />}</div></section>
    <aside className="inspector"><div className="inspector-heading"><h2>Inspector</h2><span>{adding ? 'NEW OBJECT' : selection.length ? 'SELECTION' : 'PROPERTIES'}</span></div>{mode === 'text' ? <section className="editor"><h3>Select text to annotate</h3><p className="control-help">Drag across a word or passage on the page. Text selection does not move objects. Escape clears the selection.</p>{textSelection && <><p className="selected-text-preview">{textSelection.text}</p><AnnotationActions label="Annotate selected passage" disabled={saving || busy || rendering} onApply={kind => void saveEdit({ kind: `add-${kind}`, value: '', pageNumber: page, regions: textSelection.regions })} /></>}</section> : mode === 'pan' ? <p className="control-help">Drag the page to scroll. Switch to Select text or Select objects to edit.</p> : adding ? <AddObject fieldType={creationFieldType} onFieldPreviewChange={setFieldPreview} alignment={draftAlignment} onAlignmentChange={setDraftAlignment} value={draftText} setValue={setDraftText} fontSize={draftFontSize} setFontSize={setDraftFontSize} key={creationId} kind={creationKind} pageNumber={page} point={placement} onBoundsChange={setPlacement} droppedImage={droppedImage} onImageChange={setDroppedImage} busy={saving || busy || rendering} onSave={saveEdit} onCancel={() => setAdding(false)} /> : selection.length > 1 ? <><MultiSelectionSummary items={selection} pageNumber={page} busy={saving || busy || rendering || exporting} canDelete={!!deletionEdit()} onClear={() => setSelected(null)} onDelete={() => {
      const edit = deletionEdit();
      if (edit) {
        void saveEdit(edit);
      }
    }} />{markupActions}</> : selected ? <div key={selected.id} className="selected-object-editor"><SelectionSummary item={selected} disabled={saving} onClose={() => setSelected(null)} />{markupActions}<ObjectEditor item={selected} busy={saving || busy || rendering || exporting} onSave={saveEdit} onExportImage={exportImage} /><details className="technical-details"><summary>Technical details</summary><pre>{JSON.stringify(selected.details, (key, value) => key === 'values' && selected.kind === 'field' && (((selected.details as { flags?: number }).flags ?? 0) & (1 << 13)) !== 0 ? (value as string[]).map(text => '*'.repeat(text.length)) : value, 2)}</pre></details></div> : <div className="inspector-empty"><MousePointer2 size={28} aria-hidden="true" /><h3>Select an object</h3><p>Click an outline on the page or an item in the object list to edit its properties.</p><span>Drag a tool onto the page to create something new.</span></div>}{renderer === 'native' && pdf && <details className="renderer-notes"><summary>Experimental renderer - Rendering notes ({renderNotes.length})</summary>{renderNotes.map((note, index) => <p key={index}>{note}</p>)}</details>}{!!inspection?.warnings.length && <details className="warnings"><summary>Extraction notes ({inspection.warnings.length})</summary>{inspection.warnings.map((warning, i) => <p key={i}>{warning}</p>)}</details>}</aside></main>
    </div>{confirmDeletePage && <Modal labelledBy="delete-page-title" initialFocus="[data-delete-cancel]" className="page-assembly-dialog" onClose={() => setConfirmDeletePage(false)}>
      <div className="assembly-heading"><div><Trash2 size={22} aria-hidden="true" /><h2 id="delete-page-title">Delete page {page}?</h2></div><button type="button" className="icon-button" aria-label="Close delete page confirmation" onClick={() => setConfirmDeletePage(false)}><X size={18} aria-hidden="true" /></button></div>
      <p className="assembly-document">{name}</p>
      <p>This removes page {page} from the current tab. This action cannot be undone.</p>
      <p className="control-help">Your original file on disk is unchanged. Download the updated PDF to keep the result.</p>
      <div className="assembly-actions"><button type="button" data-delete-cancel onClick={() => setConfirmDeletePage(false)}>Cancel</button><button type="button" className="danger-action" onClick={() => {
        setConfirmDeletePage(false);
        void saveEdit({ kind: 'delete-page', pageNumber: page, value: '' });
      }}><Trash2 size={16} aria-hidden="true" />Delete page</button></div>
    </Modal>}{assembly && pdfBytes && pdf && <PageAssemblyDialog mode={assembly} pdf={pdfBytes} name={name} pageCount={pdf.numPages} onClose={() => setAssembly(null)} onOpenFiles={onOpenFiles} />}
    {(securityFile || (protectPdf && pdfBytes)) && <PdfSecurityDialog file={securityFile ?? new File([pdfBytes!], name, { type: 'application/pdf' })} unlock={!!securityFile} onClose={() => { setSecurityFile(null); setProtectPdf(false); }} onUnlocked={async file => {
      if (!await openFile(file, true)) {
        throw new Error('Could not inspect the decrypted PDF.');
      }
      onSaved(file);
    }} />}
    {showWatermark && pdfBytes && pdf && <WatermarkDialog pdf={pdfBytes} page={page} pageCount={pdf.numPages} watermarks={inspection?.watermarks ?? []} onClose={() => setShowWatermark(false)} onSave={saveEdit} />}
    {showMarkdown && pdfBytes && <MarkdownView pdf={pdfBytes} name={name} onClose={() => setShowMarkdown(false)} />}
    <footer>PdfObjectKit extracts · {renderer === 'native' ? 'PdfObjectKit renders (experimental)' : 'PDF.js renders'}<span>Add, edit and delete · Files are not saved by the API</span></footer>
  </div>;
}


