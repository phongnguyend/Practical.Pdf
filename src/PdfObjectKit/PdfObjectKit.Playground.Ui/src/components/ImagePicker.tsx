import { useEffect, useRef, useState, type PointerEvent } from 'react';
import { QrCodeDialog } from './QrCodeDialog';
import { Modal } from './Modal';
import './ImagePicker.css';
import { Upload, Pencil, Trash2, Undo2, X, Maximize2, QrCode } from 'lucide-react';

type Point = { x: number; y: number };
type Stroke = { points: Point[]; color: string; width: number };

export function ImagePicker({ image, width, height, onChange, onUseSize }: { image?: File; width: number; height: number; onChange: (file?: File) => void; onUseSize?: (width: number, height: number) => void }) {
  const input = useRef<HTMLInputElement>(null);
  const [url, setUrl] = useState('');
  const [dimensions, setDimensions] = useState<{ file: File; width: number; height: number }>();
  const [dragging, setDragging] = useState(false);
  const [drawing, setDrawing] = useState(false);
  const [qrOpen, setQrOpen] = useState(false);
  useEffect(() => {
    if (!image) {
      setUrl('');
      return;
    }
    const next = URL.createObjectURL(image);
    setUrl(next);
    return () => URL.revokeObjectURL(next);
  }, [image]);
  useEffect(() => {
    let cancelled = false;
    setDimensions(undefined);
    if (image) {
      void createImageBitmap(new Blob([image])).then(bitmap => {
        if (!cancelled) {
          setDimensions({ file: image, width: bitmap.width, height: bitmap.height });
        }
        bitmap.close();
      }).catch(() => {
        // Invalid images are reported by the insertion flow.
      });
    }
    return () => { cancelled = true; };
  }, [image]);
  const size = dimensions?.file === image ? dimensions : undefined;
  return <div className="image-picker">
    <input ref={input} className="image-file-input" aria-label="Image file" type="file" accept="image/png,image/jpeg,image/webp,image/gif,image/bmp,image/avif" onChange={event => {
      const file = event.target.files?.[0];
      if (file) {
        onChange(file);
      }
      event.target.value = '';
    }} />
    <button type="button" className={`image-drop-zone${dragging ? ' dragging' : ''}`} onClick={() => input.current?.click()} onDragEnter={event => {
      event.preventDefault();
      event.stopPropagation();
      setDragging(true);
    }} onDragOver={event => {
      event.preventDefault();
      event.stopPropagation();
      setDragging(true);
    }} onDragLeave={() => setDragging(false)} onDrop={event => {
      event.preventDefault();
      event.stopPropagation();
      setDragging(false);
      const file = event.dataTransfer.files[0];
      if (file) {
        onChange(file);
      }
    }}>
      {url ? <img src={url} alt="Selected image preview" /> : <Upload size={26} aria-hidden="true" />}
      <strong>{image ? 'Replace image' : 'Choose or drop an image'}</strong>
      <span>{image ? image.name : 'PNG, JPEG, WebP and more. Up to 5 MB.'}</span>
    </button>
    <div className="image-picker-actions"><button type="button" onClick={() => setDrawing(true)}><Pencil size={15} />Draw image</button><button type="button" onClick={() => setQrOpen(true)}><QrCode size={15} aria-hidden="true" />Generate QR code</button>{image && <button type="button" onClick={() => onChange(undefined)}><Trash2 size={15} />Remove</button>}</div>
    {image && onUseSize && <div className="image-size-action"><button type="button" disabled={!size} onClick={() => {
      if (size) {
        onUseSize(size.width, size.height);
      }
    }}><Maximize2 size={15} aria-hidden="true" />Use image size</button><p className="control-help">{size ? `${size.width} x ${size.height} px. Uses 1 pixel = 1 PDF point.` : 'Image dimensions unavailable until decoded.'}</p></div>}
    {qrOpen && <QrCodeDialog onClose={() => setQrOpen(false)} onUse={file => {
      onChange(file);
      if (onUseSize) {
        const side = Math.max(1, Math.min(width, height));
        onUseSize(side, side);
      }
      setQrOpen(false);
    }} />}
    {drawing && <DrawingDialog width={width} height={height} onClose={() => setDrawing(false)} onUse={file => {
      onChange(file);
      setDrawing(false);
    }} />}
  </div>;
}

function DrawingDialog({ width: placementWidth, height: placementHeight, onClose, onUse }: { width: number; height: number; onClose: () => void; onUse: (file: File) => void }) {
  const objectWidth = Number.isFinite(placementWidth) && placementWidth > 0 ? placementWidth : 150;
  const objectHeight = Number.isFinite(placementHeight) && placementHeight > 0 ? placementHeight : 100;
  const scale = Math.min(2, 2048 / Math.max(objectWidth, objectHeight));
  const canvasWidth = Math.max(1, Math.round(objectWidth * scale));
  const canvasHeight = Math.max(1, Math.round(objectHeight * scale));
  const canvas = useRef<HTMLCanvasElement>(null);
  const strokes = useRef<Stroke[]>([]);
  const active = useRef<Stroke | null>(null);
  const [count, setCount] = useState(0);
  const [color, setColor] = useState('#111827');
  const [width, setWidth] = useState(4);
  const [exporting, setExporting] = useState(false);
  const [error, setError] = useState('');
  function redraw() {
    const context = canvas.current?.getContext('2d');
    if (!context) {
      return;
    }
    context.clearRect(0, 0, canvasWidth, canvasHeight);
    for (const stroke of strokes.current) {
      context.strokeStyle = stroke.color;
      context.fillStyle = stroke.color;
      context.lineWidth = stroke.width;
      context.lineCap = 'round';
      context.lineJoin = 'round';
      const first = stroke.points[0];
      context.beginPath();
      context.arc(first.x, first.y, stroke.width / 2, 0, Math.PI * 2);
      context.fill();
      context.beginPath();
      context.moveTo(first.x, first.y);
      for (const point of stroke.points.slice(1)) {
        context.lineTo(point.x, point.y);
      }
      context.stroke();
    }
    setCount(strokes.current.length);
  }
  function point(event: PointerEvent<HTMLCanvasElement>) {
    const rect = event.currentTarget.getBoundingClientRect();
    return { x: (event.clientX - rect.left) * canvasWidth / rect.width, y: (event.clientY - rect.top) * canvasHeight / rect.height };
  }
  function finish() {
    active.current = null;
  }
  return <Modal className="drawing-dialog" labelledBy="drawing-title" onClose={onClose} canClose={!exporting}>
    <header><div><h2 id="drawing-title">Draw an image</h2><p>Matches your {objectWidth} x {objectHeight} pt image placement. Saved as a transparent PNG.</p></div><button type="button" aria-label="Close drawing" disabled={exporting} onClick={onClose}><X size={20} /></button></header>
    <div className="drawing-tools"><label>Ink<input aria-label="Ink color" type="color" value={color} onChange={event => setColor(event.target.value)} /></label><label>Brush size<input aria-label="Brush size" type="range" min="1" max="30" value={width} onChange={event => setWidth(Number(event.target.value))} /><span>{width} px</span></label><button type="button" disabled={!count || exporting} onClick={() => {
      strokes.current.pop();
      redraw();
    }}><Undo2 size={16} />Undo</button><button type="button" disabled={!count || exporting} onClick={() => {
      strokes.current = [];
      redraw();
    }}><Trash2 size={16} />Clear</button></div>
    <canvas ref={canvas} width={canvasWidth} height={canvasHeight} style={{ width: `min(100%, ${52 * canvasWidth / canvasHeight}vh)` }} aria-label="Drawing canvas" onPointerDown={event => {
      if (event.button !== 0 || exporting || active.current) {
        return;
      }
      event.preventDefault();
      event.currentTarget.setPointerCapture(event.pointerId);
      active.current = { points: [point(event)], color, width };
      strokes.current.push(active.current);
      redraw();
    }} onPointerMove={event => {
      if (active.current) {
        active.current.points.push(point(event));
        redraw();
      }
    }} onPointerUp={finish} onPointerCancel={finish} onLostPointerCapture={finish} />
    {error && <p role="alert">{error}</p>}
    <footer><button type="button" disabled={exporting} onClick={onClose}>Cancel</button><button type="button" className="use-drawing" disabled={!count || exporting} onClick={() => {
      setExporting(true);
      canvas.current?.toBlob(blob => {
        if (blob) {
          onUse(new File([blob], 'drawing.png', { type: 'image/png' }));
        } else {
          setError('Could not capture the drawing. Please try again.');
          setExporting(false);
        }
      }, 'image/png');
    }}>{exporting ? 'Preparing...' : 'Use drawing'}</button></footer>
  </Modal>;
}
