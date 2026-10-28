import { useEffect, useRef, useState, type CSSProperties } from 'react';
import { TextLayer, type PDFDocumentProxy, type PageViewport } from 'pdfjs-dist';
import type { Bounds } from './Placement';
import './SelectableText.css';

export type TextSelection = { text: string; regions: Bounds[] };

export function SelectableText({ pdf, pageNumber, viewport, onSelection }: {
  pdf: PDFDocumentProxy; pageNumber: number; viewport: PageViewport;
  onSelection: (selection: TextSelection | null) => void;
}) {
  const host = useRef<HTMLDivElement>(null);
  const frame = useRef<HTMLDivElement>(null);
  const [error, setError] = useState('');
  useEffect(() => {
    const container = host.current!;
    let cancelled = false;
    let layer: TextLayer | undefined;
    onSelection(null);
    setError('');
    void (async () => {
      try {
        const page = await pdf.getPage(pageNumber);
        const content = await page.getTextContent();
        if (cancelled) {
          return;
        }
        layer = new TextLayer({ textContentSource: content, container, viewport });
        await layer.render();
        if (!cancelled && layer.textContentItemsStr.every(text => !text.trim())) {
          setError('No selectable text on this page. Scanned pages need OCR.');
        }
      } catch (cause) {
        if (!cancelled) {
          setError(cause instanceof Error ? cause.message : 'Text selection is unavailable.');
        }
      }
    })();
    function selectionChanged() {
      const selection = window.getSelection();
      if (!selection || selection.isCollapsed || selection.rangeCount !== 1
        || !container.contains(selection.anchorNode) || !container.contains(selection.focusNode)) {
        onSelection(null);
        return;
      }
      const selected = selection.getRangeAt(0);
      const bounds = frame.current!.getBoundingClientRect();
      const walker = document.createTreeWalker(container, NodeFilter.SHOW_TEXT);
      const regions: Bounds[] = [];
      while (walker.nextNode()) {
        const node = walker.currentNode;
        if (!selected.intersectsNode(node) || !node.textContent?.trim()) {
          continue;
        }
        const range = document.createRange();
        range.selectNodeContents(node);
        if (selected.startContainer === node) {
          range.setStart(node, selected.startOffset);
        }
        if (selected.endContainer === node) {
          range.setEnd(node, selected.endOffset);
        }
        if (range.collapsed) {
          continue;
        }
        for (const rect of range.getClientRects()) {
          if (rect.width < .1 || rect.height < .1) {
            continue;
          }
          const points = [[rect.left, rect.top], [rect.right, rect.top], [rect.left, rect.bottom], [rect.right, rect.bottom]]
            .map(([x, y]) => viewport.convertToPdfPoint((x - bounds.left) * viewport.width / bounds.width, (y - bounds.top) * viewport.height / bounds.height));
          const xs = points.map(p => p[0]);
          const ys = points.map(p => p[1]);
          regions.push({ x: Math.min(...xs), y: Math.min(...ys), width: Math.max(...xs) - Math.min(...xs), height: Math.max(...ys) - Math.min(...ys) });
          if (regions.length > 1000) {
            onSelection(null);
            setError('Select a smaller passage (up to 1000 text regions).');
            return;
          }
        }
      }
      onSelection(regions.length ? { text: selection.toString(), regions } : null);
    }
    document.addEventListener('selectionchange', selectionChanged);
    function escape(event: KeyboardEvent) {
      if (event.key === 'Escape') {
        window.getSelection()?.removeAllRanges();
        onSelection(null);
      }
    }
    window.addEventListener('keydown', escape);
    return () => {
      cancelled = true;
      layer?.cancel();
      document.removeEventListener('selectionchange', selectionChanged);
      window.removeEventListener('keydown', escape);
      container.replaceChildren();
      onSelection(null);
    };
  }, [pdf, pageNumber, viewport, onSelection]);
  return <div ref={frame} className="selectable-text-frame" style={{ width: viewport.width, height: viewport.height }}>
    <div ref={host} className="selectable-text-layer" aria-label="Selectable page text" style={{ '--total-scale-factor': viewport.scale, '--scale-round-x': '1px', '--scale-round-y': '1px' } as CSSProperties} />
    {error && <p className="text-selection-notice" role="status">{error}</p>}
  </div>;
}
