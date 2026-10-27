import { useEffect, useRef, useState, type PointerEvent } from 'react';

type Point = { x: number; y: number };
type Rectangle = { left: number; top: number; width: number; height: number };

export function useRectangleSelection({ width, height, disabled, canStart, onSelect }: {
  width: number;
  height: number;
  disabled: boolean;
  canStart: (point: Point) => boolean;
  onSelect: (rectangle: Rectangle, additive: boolean) => void;
}) {
  const [rectangle, setRectangle] = useState<Rectangle | null>(null);
  const gesture = useRef<{ start: Point; additive: boolean; dragged: boolean } | null>(null);
  const suppressClick = useRef(false);
  function cancel() {
    if (gesture.current) {
      suppressClick.current = true;
    }
    gesture.current = null;
    setRectangle(null);
  }
  useEffect(() => {
    cancel();
  }, [disabled, width, height]);
  useEffect(() => {
    function escape(event: KeyboardEvent) {
      if (event.key === 'Escape' && gesture.current) {
        event.preventDefault();
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
  function point(event: PointerEvent<HTMLCanvasElement>): Point {
    const bounds = event.currentTarget.getBoundingClientRect();
    return {
      x: Math.max(0, Math.min(width, (event.clientX - bounds.left) * width / bounds.width)),
      y: Math.max(0, Math.min(height, (event.clientY - bounds.top) * height / bounds.height)),
    };
  }
  function bounds(start: Point, end: Point): Rectangle {
    return { left: Math.min(start.x, end.x), top: Math.min(start.y, end.y), width: Math.abs(start.x - end.x), height: Math.abs(start.y - end.y) };
  }
  return {
    rectangle,
    consumeClick: () => {
      const suppressed = suppressClick.current;
      suppressClick.current = false;
      return suppressed;
    },
    handlers: {
      onPointerDown: (event: PointerEvent<HTMLCanvasElement>) => {
        suppressClick.current = false;
        const start = point(event);
        if (disabled || event.button !== 0 || !event.isPrimary || !canStart(start)) {
          return;
        }
        gesture.current = { start, additive: event.ctrlKey || event.metaKey, dragged: false };
        event.currentTarget.setPointerCapture(event.pointerId);
        event.preventDefault();
      },
      onPointerMove: (event: PointerEvent<HTMLCanvasElement>) => {
        const current = gesture.current;
        if (!current) {
          return;
        }
        const end = point(event);
        current.dragged ||= Math.hypot(end.x - current.start.x, end.y - current.start.y) > 4;
        if (current.dragged) {
          setRectangle(bounds(current.start, end));
        }
      },
      onPointerUp: (event: PointerEvent<HTMLCanvasElement>) => {
        const current = gesture.current;
        gesture.current = null;
        setRectangle(null);
        if (event.currentTarget.hasPointerCapture(event.pointerId)) {
          event.currentTarget.releasePointerCapture(event.pointerId);
        }
        if (current?.dragged && !disabled) {
          suppressClick.current = true;
          onSelect(bounds(current.start, point(event)), current.additive);
        }
      },
      onPointerCancel: cancel,
      onLostPointerCapture: () => {
        if (gesture.current) {
          cancel();
        }
      },
    },
  };
}
