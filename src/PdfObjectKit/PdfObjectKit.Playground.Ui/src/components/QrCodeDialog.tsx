import { useEffect, useState } from 'react';
import QRCode from 'qrcode';
import { X, QrCode } from 'lucide-react';
import { Modal } from './Modal';
import './QrCodeDialog.css';

export function QrCodeDialog({ onClose, onUse }: { onClose: () => void; onUse: (file: File) => void }) {
  const [text, setText] = useState('');
  const [result, setResult] = useState<{ text: string; url: string; canvas: HTMLCanvasElement }>();
  const [error, setError] = useState('');
  const [exporting, setExporting] = useState(false);
  useEffect(() => {
    let cancelled = false;
    setError('');
    setResult(undefined);
    const timer = setTimeout(() => {
      if (!text.trim()) {
        return;
      }
      const canvas = document.createElement('canvas');
      void QRCode.toCanvas(canvas, text, { errorCorrectionLevel: 'M', margin: 4, scale: 8 }).then(() => {
        if (!cancelled) {
          setResult({ text, url: canvas.toDataURL('image/png'), canvas });
        }
      }).catch(() => {
        if (!cancelled) {
          setError('This content is too long to encode. Try shorter text or a shorter URL.');
        }
      });
    }, 150);
    return () => {
      cancelled = true;
      clearTimeout(timer);
    };
  }, [text]);
  const ready = result?.text === text ? result : undefined;
  return <Modal className="qr-dialog" labelledBy="qr-title" onClose={onClose} canClose={!exporting}>
    <header><div><h2 id="qr-title">Generate QR code</h2><p>Enter a URL or text. Generated locally in your browser.</p></div><button type="button" aria-label="Close QR generator" disabled={exporting} onClick={onClose}><X size={20} /></button></header>
    <label className="qr-content-label">URL or text<textarea aria-label="QR content" value={text} maxLength={4000} rows={3} disabled={exporting} placeholder="https://example.com" onChange={event => setText(event.target.value)} /></label>
    <div className="qr-preview">{ready ? <img src={ready.url} alt="QR code preview" /> : <span>{text.trim() && !error ? 'Generating preview...' : 'Your QR code will appear here'}</span>}</div>
    {error && <p role="alert">{error}</p>}
    <p className="qr-help">Black on white, with a clear border for scanning. Keep the QR code square when resizing.</p>
    <footer><button type="button" disabled={exporting} onClick={onClose}>Cancel</button><button type="button" className="qr-use" disabled={!ready || exporting} onClick={() => {
      if (!ready) {
        return;
      }
      setExporting(true);
      ready.canvas.toBlob(blob => {
        if (blob) {
          onUse(new File([blob], 'qr-code.png', { type: 'image/png' }));
        } else {
          setError('Could not create the QR image. Please try again.');
          setExporting(false);
        }
      }, 'image/png');
    }}><QrCode size={16} aria-hidden="true" />{exporting ? 'Preparing...' : 'Use QR code'}</button></footer>
  </Modal>;
}
