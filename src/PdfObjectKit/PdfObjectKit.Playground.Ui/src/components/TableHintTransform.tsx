import { Pencil } from 'lucide-react';
import { useRef, type PointerEvent } from 'react';
import type { PageViewport } from 'pdfjs-dist';
import type { TableHint } from './TableHints';

type Handle = 'move' | 'n' | 'ne' | 'e' | 'se' | 's' | 'sw' | 'w' | 'nw';
export function TableHintTransform({ hint, index, viewport, onChange, onFinish, onEdit }: {
  hint: TableHint; index: number; viewport: PageViewport;
  onEdit: () => void;
  onChange: (hint: TableHint) => void;
  onFinish: (hint: TableHint, original: TableHint) => void;
}) {
  const gesture = useRef<{ original: TableHint; handle: Handle; x: number; y: number; page: DOMRect; left: number; top: number; right: number; bottom: number; latest: TableHint } | undefined>(undefined);
  const width = Math.ceil(viewport.width);
  const height = Math.ceil(viewport.height);
  const [x0, y0] = viewport.convertToViewportPoint(hint.x, hint.y);
  const [x1, y1] = viewport.convertToViewportPoint(hint.x + hint.width, hint.y + hint.height);
  function begin(event: PointerEvent<HTMLButtonElement>, handle: Handle) {
    event.stopPropagation();
    if (event.button !== 0) {
      return;
    }
    event.currentTarget.focus({ preventScroll: true });
    gesture.current = { original: hint, latest: hint, handle, x: event.clientX, y: event.clientY,
      page: event.currentTarget.parentElement!.parentElement!.getBoundingClientRect(),
      left: Math.min(x0, x1), right: Math.max(x0, x1), top: Math.min(y0, y1), bottom: Math.max(y0, y1) };
    event.currentTarget.setPointerCapture(event.pointerId);
  }
  function update(event: PointerEvent<HTMLDivElement>) {
    const g = gesture.current;
    if (!g) {
      return;
    }
    event.stopPropagation();
    const dx = (event.clientX - g.x) / g.page.width * width;
    const dy = (event.clientY - g.y) / g.page.height * height;
    let { left, right, top, bottom } = g;
    if (g.handle === 'move') {
      const shiftX = Math.max(-left, Math.min(width - right, dx));
      const shiftY = Math.max(-top, Math.min(height - bottom, dy));
      left += shiftX;
      right += shiftX;
      top += shiftY;
      bottom += shiftY;
    } else {
      if (g.handle.includes('w')) {
        left = Math.max(0, Math.min(right - 2, left + dx));
      }
      if (g.handle.includes('e')) {
        right = Math.min(width, Math.max(left + 2, right + dx));
      }
      if (g.handle.includes('n')) {
        top = Math.max(0, Math.min(bottom - 2, top + dy));
      }
      if (g.handle.includes('s')) {
        bottom = Math.min(height, Math.max(top + 2, bottom + dy));
      }
    }
    const [a, b] = viewport.convertToPdfPoint(left, top);
    const [c, d] = viewport.convertToPdfPoint(right, bottom);
    const x = Math.min(a, c);
    const newWidth = Math.abs(c - a);
    g.latest = { ...g.original, x, y: Math.min(b, d), width: newWidth, height: Math.abs(d - b),
      columnBoundaries: g.original.columnBoundaries?.map(value => x + (value - g.original.x) / g.original.width * newWidth) ?? g.original.columnBoundaries };
    onChange(g.latest);
  }
  function cancel() {
    const g = gesture.current;
    gesture.current = undefined;
    if (g) {
      onChange(g.original);
    }
  }
  return <div className="table-hint-transform" style={{ left: `${Math.min(x0, x1) / width * 100}%`, top: `${Math.min(y0, y1) / height * 100}%`, width: `${Math.abs(x1 - x0) / width * 100}%`, height: `${Math.abs(y1 - y0) / height * 100}%` }}
    onPointerMove={update} onPointerUp={event => {
      if (!gesture.current) {
        return;
      }
      update(event);
      const g = gesture.current;
      gesture.current = undefined;
      onFinish(g.latest, g.original);
    }} onPointerCancel={event => {
      event.stopPropagation();
      cancel();
    }} onLostPointerCapture={cancel} onKeyDown={event => {
      if (event.key === 'Escape' && gesture.current) {
        event.preventDefault();
        event.stopPropagation();
        cancel();
      }
    }}>
    <button type="button" className="table-hint-move" aria-label={`Move table hint ${index + 1}`} onPointerDown={event => begin(event, 'move')}>Table {index + 1}</button>
    <button type="button" className="table-hint-edit" aria-label={`Edit table ${index + 1}`} title="Edit table hint" style={Math.min(y0, y1) < 34 ? { top: '100%', marginTop: 6 } : { bottom: '100%', marginBottom: 6 }} onPointerDown={event => event.stopPropagation()} onClick={event => {
      event.stopPropagation();
      onEdit();
    }}><Pencil size={14} aria-hidden="true" />Edit</button>
    {(['nw', 'n', 'ne', 'e', 'se', 's', 'sw', 'w'] as const).map(handle => <button key={handle} type="button" className={`table-hint-handle handle-${handle}`} aria-label={`Resize table hint ${index + 1} ${handle}`} onPointerDown={event => begin(event, handle)} />)}
  </div>;
}
