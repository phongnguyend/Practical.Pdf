import { useEffect, useRef, useState } from 'react';
import { Download, FileDown, Files, LoaderCircle, X } from 'lucide-react';
import { Modal } from './Modal';
import './PageAssemblyDialog.css';
import './CompressionDialog.css';

type Result = { base64: string; originalBytes: number; outputBytes: number; removedObjects: number; compressedStreams: number; deduplicatedResources: number; usedOriginal: boolean; notes: string[] };
const labels = { removeUnusedObjects: 'Remove unreachable objects', compressStreams: 'Compress document streams', deduplicateResources: 'Share identical images and embedded font data' };
const size = (bytes: number) => new Intl.NumberFormat(undefined, { maximumFractionDigits: 1 }).format(bytes / 1024) + ' KB';

export function CompressionDialog({ pdf, name, onClose, onOpenFiles }: { pdf: Blob; name: string; onClose: () => void; onOpenFiles: (files: File[]) => void }) {
  const [options, setOptions] = useState({ removeUnusedObjects: true, compressStreams: true, deduplicateResources: true });
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const [result, setResult] = useState<Result | null>(null);
  const abort = useRef<AbortController | null>(null);
  useEffect(() => () => abort.current?.abort(), []);
  function resultFile() {
    if (!result) {
      throw new Error('Compress the PDF first.');
    }
    return new File([Uint8Array.from(atob(result.base64), character => character.charCodeAt(0))], name.replace(/\.pdf$/i, '') + '-compressed.pdf', { type: 'application/pdf' });
  }
  async function compress() {
    setBusy(true);
    setError('');
    setResult(null);
    const controller = new AbortController();
    abort.current = controller;
    try {
      const body = new FormData();
      body.append('file', pdf, name);
      body.append('options', JSON.stringify(options));
      const response = await fetch('/api/documents/compress', { method: 'POST', body, signal: controller.signal });
      const data = await response.json();
      if (!response.ok) {
        throw new Error(data.detail ?? 'Could not compress this PDF.');
      }
      setResult(data);
    } catch (cause) {
      if (!controller.signal.aborted) {
        setError(cause instanceof Error ? cause.message : 'Could not compress this PDF.');
      }
    } finally {
      setBusy(false);
    }
  }
  return <Modal labelledBy="compression-title" className="page-assembly-dialog compression-dialog" onClose={onClose} canClose={!busy}>
    <div className="assembly-heading"><div><FileDown size={22} aria-hidden="true" /><h2 id="compression-title">Compress PDF</h2></div><button aria-label="Close compression" disabled={busy} onClick={onClose}><X size={18} /></button></div>
    <p className="assembly-document">{name} &middot; {size(pdf.size)}</p>
    <p>Reduce file size without changing image quality, text or document features.</p>
    <fieldset className="compression-options" disabled={busy}><legend>Lossless optimization</legend>{Object.entries(labels).map(([key, label]) => <label key={key}><input type="checkbox" checked={options[key as keyof typeof options]} onChange={event => {
      setOptions(current => ({ ...current, [key]: event.target.checked }));
      setResult(null);
    }} />{label}</label>)}</fieldset>
    <p className="control-help">Uses the current saved PDF and keeps the original tab. Already compressed images are preserved. If no space is saved, the original file is retained.</p>
    {error && <p role="alert" className="assembly-error">{error}</p>}
    {result && <section className="compression-result" aria-label="Compression result" aria-live="polite">
      <strong>{result.usedOriginal ? 'Already compact' : `${((1 - result.outputBytes / result.originalBytes) * 100).toFixed(1)}% smaller`}</strong>
      <p>{size(result.originalBytes)} → {size(result.outputBytes)}</p>
      {!result.usedOriginal && <p>{result.removedObjects} unused objects removed · {result.compressedStreams} streams compressed · {result.deduplicatedResources} duplicate resources shared</p>}
      {result.notes.map(note => <p className="control-help" key={note}>{note}</p>)}
    </section>}
    <div className="assembly-actions">
      <button disabled={busy} onClick={onClose}>Close</button>
      {!result && <button className="primary-action" disabled={busy || !Object.values(options).some(Boolean)} onClick={() => void compress()}>{busy ? <LoaderCircle size={16} /> : <FileDown size={16} />}{busy ? 'Compressing…' : 'Compress'}</button>}
      {result && <><button onClick={() => {
        const url = URL.createObjectURL(resultFile());
        const link = document.createElement('a');
        link.href = url;
        link.download = resultFile().name;
        link.click();
        setTimeout(() => URL.revokeObjectURL(url), 1000);
      }}><Download size={16} />Download</button><button className="primary-action" onClick={() => {
        try {
          onOpenFiles([resultFile()]);
          onClose();
        } catch (cause) {
          setError(cause instanceof Error ? cause.message : 'Could not open the result.');
        }
      }}><Files size={16} />Open in new tab</button></>}
    </div>
  </Modal>;
}
