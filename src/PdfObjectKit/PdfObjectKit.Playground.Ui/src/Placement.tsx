import { useEffect, useRef, useState, type PointerEvent } from 'react';
import type { PageViewport } from 'pdfjs-dist';

export type Bounds = { x: number; y: number; width: number; height: number };

export function Placement({ viewport, bounds, onChange, image, disabled }: { viewport: PageViewport; bounds: Bounds; onChange: (bounds: Bounds) => void; image?: File; disabled: boolean }) {
  const surface = useRef<HTMLDivElement>(null);
  const drag = useRef<{ x: number; y: number; original: Bounds; mode: 'draw' | 'move' } | null>(null);
  const [url, setUrl] = useState('');
  useEffect(() => {
    if (!image) {
      setUrl('');
      return;
    }
    const next = URL.createObjectURL(image);
    setUrl(next);
    return () => URL.revokeObjectURL(next);
  }, [image]);
  const corners = [...viewport.convertToViewportPoint(bounds.x, bounds.y), ...viewport.convertToViewportPoint(bounds.x + bounds.width, bounds.y + bounds.height)];
  const left = Math.min(corners[0], corners[2]);
  const top = Math.min(corners[1], corners[3]);
  const width = Math.abs(corners[2] - corners[0]);
  const height = Math.abs(corners[3] - corners[1]);
  function point(event: PointerEvent) {
    const rect = surface.current!.getBoundingClientRect();
    return viewport.convertToPdfPoint(Math.max(0, Math.min(viewport.width, (event.clientX - rect.left) * viewport.width / rect.width)), Math.max(0, Math.min(viewport.height, (event.clientY - rect.top) * viewport.height / rect.height)));
  }
  function start(event: PointerEvent, mode: 'draw' | 'move', fixed?: number[]) {
    if (disabled || event.button !== 0) {
      return;
    }
    event.preventDefault();
    event.stopPropagation();
    const [x, y] = fixed ?? point(event);
    drag.current = { x, y, mode, original: bounds };
    surface.current!.setPointerCapture(event.pointerId);
  }
  function update(event: PointerEvent) {
    const current = drag.current;
    if (!current) {
      return;
    }
    const [x, y] = point(event);
    const round = (n: number) => Math.round(n * 100) / 100;
    if (current.mode === 'move') {
      onChange({ ...current.original, x: round(current.original.x + x - current.x), y: round(current.original.y + y - current.y) });
    } else {
      onChange({ x: round(Math.min(current.x, x)), y: round(Math.min(current.y, y)), width: Math.max(1, round(Math.abs(x - current.x))), height: Math.max(1, round(Math.abs(y - current.y))) });
    }
  }
  return <div ref={surface} className="placement-surface" aria-label="Draw object placement" style={{ width: viewport.width, height: viewport.height, pointerEvents: disabled ? 'none' : 'auto' }}
    onPointerDown={event => start(event, 'draw')} onPointerMove={update} onPointerUp={event => { update(event); drag.current = null; }} onPointerCancel={() => { drag.current = null; }}>
    <div className="placement-box" style={{ left, top, width, height }} onPointerDown={event => start(event, 'move')}>
      {url && <img src={url} alt="Image placement preview" draggable={false} style={{ position: 'absolute', left: '50%', top: '50%', width: viewport.rotation % 180 ? height : width, height: viewport.rotation % 180 ? width : height, transform: `translate(-50%, -50%) rotate(${viewport.rotation}deg)` }} />}
      <button type="button" className="placement-move" aria-label="Move placement" onPointerDown={event => start(event, 'move')}>Move</button>
      {[[0, 0], [1, 0], [1, 1], [0, 1]].map(([cx, cy], index) => <button type="button" key={index} aria-label={`Resize ${['top left', 'top right', 'bottom right', 'bottom left'][index]}`} className="placement-handle" style={{ left: `${cx * 100}%`, top: `${cy * 100}%` }} onPointerDown={event => start(event, 'draw', viewport.convertToPdfPoint(left + (1 - cx) * width, top + (1 - cy) * height))} />)}
    </div>
  </div>;
}
