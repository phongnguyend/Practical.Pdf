import { useEffect, useRef, useState } from 'react';
import type { PageViewport } from 'pdfjs-dist';

export function ExistingObjectDrag({ viewport, corners, disabled, onMove, onToggle, onAnnotationPick }: {
  onToggle?: () => void;
  onAnnotationPick?: (clientX: number, clientY: number) => void;
  viewport: PageViewport;
  corners: { x: number; y: number }[];
  disabled: boolean;
  onMove: (x: number, y: number) => Promise<void>;
}) {
  const gesture = useRef<{ x: number; y: number; clientX: number; clientY: number } | null>(null);
  const [delta, setDelta] = useState({ x: 0, y: 0 });
  function cancel() {
    gesture.current = null;
    setDelta({ x: 0, y: 0 });
  }
  useEffect(() => {
    function escape(event: KeyboardEvent) {
      if (event.key === 'Escape') {
        cancel();
      }
    }
    window.addEventListener('keydown', escape);
    window.addEventListener('blur', cancel);
    return () => {
      window.removeEventListener('keydown', escape);
      window.removeEventListener('blur', cancel);
    };
  }, []);
  const points = corners.map(p => viewport.convertToViewportPoint(p.x + delta.x, p.y + delta.y).join(',')).join(' ');
  return <svg className="existing-object-drag" width={viewport.width} height={viewport.height} style={{ position: 'absolute', inset: 0, pointerEvents: 'none', overflow: 'visible' }}>
    <polygon aria-label="Move selected object" points={points} fill="#2563eb18" stroke="#2563eb" strokeWidth={2} strokeDasharray="5 3"
      style={{ pointerEvents: disabled ? 'none' : 'all', cursor: gesture.current ? 'grabbing' : 'grab', touchAction: 'none' }}
      onClick={event => {
        event.stopPropagation();
        if (!disabled && event.altKey) {
          onAnnotationPick?.(event.clientX, event.clientY);
          return;
        }
        if (!disabled && (event.ctrlKey || event.metaKey)) {
          onToggle?.();
        }
      }}
      onPointerDown={event => {
        if (disabled || event.button !== 0 || event.ctrlKey || event.metaKey || event.altKey) {
          return;
        }
        event.preventDefault();
        const rect = event.currentTarget.ownerSVGElement!.getBoundingClientRect();
        const [x, y] = viewport.convertToPdfPoint((event.clientX - rect.left) * viewport.width / rect.width, (event.clientY - rect.top) * viewport.height / rect.height);
        gesture.current = { x, y, clientX: event.clientX, clientY: event.clientY };
        event.currentTarget.setPointerCapture(event.pointerId);
      }}
      onPointerMove={event => {
        if (!gesture.current) {
          return;
        }
        const rect = event.currentTarget.ownerSVGElement!.getBoundingClientRect();
        const [x, y] = viewport.convertToPdfPoint((event.clientX - rect.left) * viewport.width / rect.width, (event.clientY - rect.top) * viewport.height / rect.height);
        setDelta({ x: x - gesture.current.x, y: y - gesture.current.y });
      }}
      onPointerUp={event => {
        const start = gesture.current;
        if (!start) {
          return;
        }
        const rect = event.currentTarget.ownerSVGElement!.getBoundingClientRect();
        const [x, y] = viewport.convertToPdfPoint((event.clientX - rect.left) * viewport.width / rect.width, (event.clientY - rect.top) * viewport.height / rect.height);
        cancel();
        event.currentTarget.releasePointerCapture(event.pointerId);
        if (!disabled && Math.hypot(event.clientX - start.clientX, event.clientY - start.clientY) > 3) {
          void onMove(x - start.x, y - start.y);
        }
      }} onPointerCancel={cancel} onLostPointerCapture={cancel}>
      <title>Drag to move. Release to save. Escape to cancel.</title>
    </polygon>
  </svg>;
}
