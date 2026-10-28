import { useEffect, useRef, useState } from 'react';
import { getDocument } from 'pdfjs-dist';
import { Stamp, Eye, Trash2, X } from 'lucide-react';
import { Modal } from './Modal';
import { parsePageSelection } from './PageAssemblyDialog';
import type { Edit } from './ObjectEditor';
import './PageAssemblyDialog.css';
import './WatermarkDialog.css';

export type WatermarkOptions = { text: string; pageNumbers: number[] | null; fontSize: number; opacity: number; rotation: number; color: string; margin: number; position: string; layer: string };
export type WatermarkInfo = { id: string; pageNumbers: number[]; options: WatermarkOptions };
const defaults: WatermarkOptions = { text: 'CONFIDENTIAL', pageNumbers: null, fontSize: 48, opacity: .2, rotation: 45, color: '#808080', margin: 24, position: 'Center', layer: 'Foreground' };
export function WatermarkDialog({ pdf, page, pageCount, watermarks, onClose, onSave }: { pdf: Blob; page: number; pageCount: number; watermarks: WatermarkInfo[]; onClose: () => void; onSave: (edit: Edit) => Promise<void> }) {
  const [id, setId] = useState('');
  const [options, setOptions] = useState(defaults);
  const [scope, setScope] = useState('all');
  const [range, setRange] = useState('');
  const [error, setError] = useState('');
  const [busy, setBusy] = useState(false);
  const [preview, setPreview] = useState('');
  const canvas = useRef<HTMLCanvasElement>(null);
  const abort = useRef<AbortController | null>(null);
  useEffect(() => () => abort.current?.abort(), []);
  useEffect(() => { setPreview(''); }, [id, options, scope, range]);
  function command(): Edit {
    const pageNumbers = scope === 'all' ? null : scope === 'current' ? [page] : parsePageSelection(range, pageCount);
    return { kind: id ? 'update-watermark' : 'add-watermark', watermarkId: id || undefined, pageNumber: page, value: '', watermark: { ...options, pageNumbers } };
  }
  async function showPreview() {
    setError('');
    let task: ReturnType<typeof getDocument> | undefined;
    try {
      const edit = command();
      setBusy(true);
      const controller = new AbortController();
      abort.current = controller;
      const body = new FormData();
      body.append('file', pdf, 'document.pdf');
      body.append('edit', JSON.stringify(edit));
      const response = await fetch('/api/documents/edit', { method: 'POST', body, signal: controller.signal });
      if (!response.ok) {
        throw new Error((await response.json()).detail ?? 'Preview failed.');
      }
      task = getDocument({ data: await response.arrayBuffer(), cMapUrl: '/pdfjs/cmaps/', cMapPacked: true, standardFontDataUrl: '/pdfjs/standard_fonts/', wasmUrl: '/pdfjs/wasm/' });
      const document = await task.promise;
      const previewPage = edit.watermark!.pageNumbers?.[0] ?? page;
      const source = await document.getPage(previewPage);
      const original = source.getViewport({ scale: 1 });
      const viewport = source.getViewport({ scale: Math.min(420 / original.width, 450 / original.height) });
      if (canvas.current) {
        canvas.current.width = Math.ceil(viewport.width);
        canvas.current.height = Math.ceil(viewport.height);
        await source.render({ canvas: canvas.current, viewport }).promise;
        setPreview('Page ' + previewPage + ' preview');
      }
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : 'Preview failed.');
    } finally {
      await task?.destroy();
      setBusy(false);
    }
  }
  function apply(remove = false) {
    try {
      const edit = remove ? { kind: 'remove-watermark', watermarkId: id, pageNumber: page, value: '' } : command();
      onClose();
      void onSave(edit);
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : 'Invalid settings.');
    }
  }
  return <Modal labelledBy="watermark-title" className="watermark-dialog" onClose={onClose} canClose={!busy} initialFocus="#watermark-text">
    <form onSubmit={event => { event.preventDefault(); apply(); }}>
      <div className="assembly-heading"><div><Stamp size={22} /><h2 id="watermark-title">Watermark</h2></div><button type="button" aria-label="Close watermark dialog" disabled={busy} onClick={onClose}><X size={18} /></button></div>
      <div className="watermark-columns"><fieldset disabled={busy} className="watermark-settings">
        <label>Watermark<select aria-label="Watermark to edit" value={id} onChange={event => {
          setId(event.target.value);
          const found = watermarks.find(item => item.id === event.target.value);
          setOptions(found?.options ?? defaults);
          setScope(found ? 'range' : 'all');
          setRange(found?.pageNumbers.join(',') ?? '');
        }}><option value="">New watermark</option>{watermarks.map(item => <option key={item.id} value={item.id}>{item.options.text} (pages {item.pageNumbers.join(', ')})</option>)}</select></label>
        <label>Text<input id="watermark-text" aria-label="Watermark text" required maxLength={1000} value={options.text} onChange={event => setOptions({ ...options, text: event.target.value })} /></label>
        <p className="control-help">Single-line text using Helvetica. Printable ASCII characters supported.</p>
        <div className="watermark-grid"><label>Font size<input type="number" min="1" max="1000" required value={options.fontSize} onChange={event => setOptions({ ...options, fontSize: Number(event.target.value) })} /></label><label>Color<input type="color" value={options.color} onChange={event => setOptions({ ...options, color: event.target.value })} /></label>
        <label>Opacity<input type="number" min="0" max="1" step="0.05" required value={options.opacity} onChange={event => setOptions({ ...options, opacity: Number(event.target.value) })} /></label><label>Angle<input type="number" required value={options.rotation} onChange={event => setOptions({ ...options, rotation: Number(event.target.value) })} /></label></div>
        <label>Position<select value={options.position} onChange={event => setOptions({ ...options, position: event.target.value })}>{['Center', 'TopLeft', 'TopRight', 'BottomLeft', 'BottomRight'].map(position => <option key={position}>{position}</option>)}</select></label>
        <label>Margin<input type="number" min="0" required value={options.margin} onChange={event => setOptions({ ...options, margin: Number(event.target.value) })} /></label>
        <label>Layer<select value={options.layer} onChange={event => setOptions({ ...options, layer: event.target.value })}><option value="Foreground">Over page content</option><option value="Background">Behind page content</option></select></label>
        <label>Pages<select aria-label="Watermark pages" value={scope} onChange={event => setScope(event.target.value)}><option value="all">All pages</option><option value="current">Current page</option><option value="range">Page range</option></select></label>
        {scope === 'range' && <label>Page range<input required value={range} placeholder="1, 3-5" onChange={event => setRange(event.target.value)} /></label>}
      </fieldset><div className="watermark-preview"><canvas ref={canvas} hidden={!preview} aria-label="Watermark preview" /><p>{preview || 'Preview the selected settings before applying.'}</p><button type="button" disabled={busy} onClick={() => void showPreview()}><Eye size={16} />{busy ? 'Rendering...' : 'Preview'}</button></div></div>
      {error && <p role="alert" className="assembly-error">{error}</p>}
      <div className="assembly-actions">{id && <button type="button" className="danger-action" disabled={busy} onClick={() => apply(true)}><Trash2 size={16} />Remove watermark</button>}<button type="button" disabled={busy} onClick={onClose}>Cancel</button><button type="submit" className="primary-action" disabled={busy}>{id ? 'Update watermark' : 'Add watermark'}</button></div>
    </form>
  </Modal>;
}
