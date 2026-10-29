import { useEffect, useRef, useState } from 'react';
import { Layers, X } from 'lucide-react';
import { Modal } from './Modal';
import './PageAssemblyDialog.css';
import './FlattenDialog.css';

export function FlattenDialog({ pdf, name, onClose, onOpenFiles }: { pdf: Blob; name: string; onClose: () => void; onOpenFiles: (files: File[]) => void }) {
  const [forms, setForms] = useState(true);
  const [annotations, setAnnotations] = useState(true);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const abort = useRef<AbortController | null>(null);
  useEffect(() => () => abort.current?.abort(), []);
  async function flatten() {
    setBusy(true);
    setError('');
    const controller = new AbortController();
    abort.current = controller;
    try {
      const query = new URLSearchParams({ forms: String(forms), annotations: String(annotations) });
      const response = await fetch('/api/documents/flatten?' + query, { method: 'POST', headers: { 'Content-Type': 'application/pdf' }, body: pdf, signal: controller.signal });
      if (!response.ok) {
        throw new Error((await response.json()).detail ?? 'Could not flatten this PDF.');
      }
      const file = new File([await response.blob()], name.replace(/\.pdf$/i, '') + '-flattened.pdf', { type: 'application/pdf' });
      onOpenFiles([file]);
      onClose();
    } catch (cause) {
      if (!controller.signal.aborted) {
        setError(cause instanceof Error ? cause.message : 'Could not flatten this PDF.');
      }
    } finally {
      setBusy(false);
    }
  }
  return <Modal labelledBy="flatten-title" className="page-assembly-dialog" onClose={onClose} canClose={!busy}>
    <div className="assembly-heading"><div><Layers size={22} aria-hidden="true" /><h2 id="flatten-title">Flatten PDF</h2></div><button aria-label="Close flatten PDF" disabled={busy} onClick={onClose}><X size={18} /></button></div>
    <p className="assembly-document">{name}</p>
    <p>Make saved field values and annotation appearances part of the page.</p>
    <div className="flatten-options">
      <label><input type="checkbox" checked={forms} disabled={busy} onChange={event => setForms(event.target.checked)} /><span><strong>Form fields</strong><small>Keep their saved appearance. Remove input fields, buttons and form actions.</small></span></label>
      <label><input type="checkbox" checked={annotations} disabled={busy} onChange={event => setAnnotations(event.target.checked)} /><span><strong>Annotations</strong><small>Keep visible markup and comment icons. Remove editable annotations and comment messages. Hyperlinks remain interactive.</small></span></label>
    </div>
    <p className="control-help">Opens a new tab and keeps the original. The result uses saved normal appearances; hidden objects are removed. Visible flattened content prints as page content. Missing appearances, signed documents and unsupported features are rejected.</p>
    <p className="control-help">Flattening is not redaction or protection against editing.</p>
    {error && <p className="assembly-error" role="alert">{error}</p>}
    <div className="assembly-actions"><button disabled={busy} onClick={onClose}>Cancel</button><button className="primary-action" disabled={busy || (!forms && !annotations)} onClick={() => void flatten()}><Layers size={16} />{busy ? 'Flattening…' : 'Flatten to new tab'}</button></div>
  </Modal>;
}
