import { useEffect, useMemo, useState } from 'react';
import { ChevronLeft, ChevronRight, Columns2, ArrowLeftRight, Maximize2, Minimize2, X } from 'lucide-react';
import { getDocument, type PDFDocumentProxy, type RenderTask } from 'pdfjs-dist';
import { Modal } from './Modal';
import './CompareDialog.css';

type Preview = { left: string; right: string; difference: string; status: string };

export function CompareDialog({ files, onClose }: { files: [File, File]; onClose: () => void }) {
  const [documents, setDocuments] = useState<PDFDocumentProxy[]>([]);
  const [pageNumber, setPageNumber] = useState(1);
  const [preview, setPreview] = useState<Preview | null>(null);
  const [error, setError] = useState('');
  const [showDifference, setShowDifference] = useState(true);
  const [maximized, setMaximized] = useState(false);
  const [swapped, setSwapped] = useState(false);
  const orderedDocuments = useMemo(() => swapped ? [...documents].reverse() : documents, [documents, swapped]);
  const orderedFiles = swapped ? [...files].reverse() : files;
  useEffect(() => {
    let cancelled = false;
    const tasks: ReturnType<typeof getDocument>[] = [];
    void (async () => {
      try {
        const loaded: PDFDocumentProxy[] = [];
        for (const file of files) {
          const data = await file.arrayBuffer();
          if (cancelled) {
            return;
          }
          const task = getDocument({ data, cMapUrl: '/pdfjs/cmaps/', cMapPacked: true, standardFontDataUrl: '/pdfjs/standard_fonts/', wasmUrl: '/pdfjs/wasm/' });
          tasks.push(task);
          loaded.push(await task.promise);
        }
        if (!cancelled) {
          setDocuments(loaded);
        }
      } catch (cause) {
        if (!cancelled) {
          setError(cause instanceof Error ? cause.message : 'Could not open PDFs for comparison.');
        }
      }
    })();
    return () => {
      cancelled = true;
      for (const task of tasks) {
        void task.destroy();
      }
    };
  }, [files]);
  useEffect(() => {
    if (documents.length !== 2) {
      return;
    }
    let cancelled = false;
    let task: RenderTask | undefined;
    setPreview(null);
    setError('');
    void (async () => {
      try {
        const pages = await Promise.all(orderedDocuments.map(pdf => pageNumber <= pdf.numPages ? pdf.getPage(pageNumber) : null));
        if (cancelled) {
          return;
        }
        const dimensions = pages.map(page => page?.getViewport({ scale: 1 }));
        const maxWidth = Math.max(...dimensions.map(view => view?.width ?? 0));
        const maxHeight = Math.max(...dimensions.map(view => view?.height ?? 0));
        // One common scale and origin preserves differences in page dimensions. Bound canvas memory.
        const scale = Math.min(1.5, 1400 / Math.max(maxWidth, maxHeight));
        const width = Math.ceil(maxWidth * scale);
        const height = Math.ceil(maxHeight * scale);
        const canvases: HTMLCanvasElement[] = [];
        for (const page of pages) {
          const canvas = document.createElement('canvas');
          canvas.width = width;
          canvas.height = height;
          const context = canvas.getContext('2d')!;
          context.fillStyle = '#fff';
          context.fillRect(0, 0, width, height);
          if (page) {
            task = page.render({ canvas, viewport: page.getViewport({ scale }), background: '#ffffff' });
            await task.promise;
          }
          if (cancelled) {
            return;
          }
          canvases.push(canvas);
        }
        const left = canvases[0].getContext('2d')!.getImageData(0, 0, width, height);
        const right = canvases[1].getContext('2d')!.getImageData(0, 0, width, height);
        const diff = document.createElement('canvas');
        diff.width = width;
        diff.height = height;
        const ctx = diff.getContext('2d')!;
        const pixels = ctx.createImageData(width, height);
        let changed = 0;
        for (let i = 0; i < left.data.length; i += 4) {
          const different = Math.max(Math.abs(left.data[i] - right.data[i]), Math.abs(left.data[i + 1] - right.data[i + 1]), Math.abs(left.data[i + 2] - right.data[i + 2])) > 24;
          if (different) {
            changed++;
          }
          // White is the rendered page background. Overlapping non-background changes
          // are modifications, rather than claiming an addition or removal.
          const presentA = Math.min(left.data[i], left.data[i + 1], left.data[i + 2]) < 231;
          const presentB = Math.min(right.data[i], right.data[i + 1], right.data[i + 2]) < 231;
          const color = !presentA && presentB ? [22, 163, 74] : presentA && !presentB ? [220, 38, 38] : [217, 119, 6];
          pixels.data[i] = different ? color[0] : Math.round(right.data[i] * 0.3 + 255 * 0.7);
          pixels.data[i + 1] = different ? color[1] : Math.round(right.data[i + 1] * 0.3 + 255 * 0.7);
          pixels.data[i + 2] = different ? color[2] : Math.round(right.data[i + 2] * 0.3 + 255 * 0.7);
          pixels.data[i + 3] = 255;
        }
        ctx.putImageData(pixels, 0, 0);
        const differentSize = dimensions[0]?.width !== dimensions[1]?.width || dimensions[0]?.height !== dimensions[1]?.height;
        const status = !pages[0] ? 'Page exists only in the right document.' : !pages[1] ? 'Page exists only in the left document.' : `${changed ? `${changed.toLocaleString()} changed pixels (${(100 * changed / (width * height)).toFixed(2)}%).` : 'No visual differences detected.'}${differentSize ? ' Page sizes differ.' : ''}`;
        setPreview({ left: canvases[0].toDataURL(), right: canvases[1].toDataURL(), difference: diff.toDataURL(), status });
      } catch (cause) {
        if (!cancelled) {
          setError(cause instanceof Error ? cause.message : 'Could not compare these pages.');
        }
      }
    })();
    return () => {
      cancelled = true;
      task?.cancel();
    };
  }, [orderedDocuments, pageNumber]);
  const total = Math.max(0, ...documents.map(pdf => pdf.numPages));
  return <Modal labelledBy="compare-title" className={`compare-dialog${maximized ? ' compare-dialog-maximized' : ''}`} onClose={onClose}>
    <header className="compare-header"><h2 id="compare-title"><Columns2 size={21} />Compare PDFs</h2><div className="compare-window-actions"><button type="button" aria-label={maximized ? 'Restore comparison size' : 'Maximize comparison'} title={maximized ? 'Restore size' : 'Maximize'} onClick={() => setMaximized(value => !value)}>{maximized ? <Minimize2 size={18} aria-hidden="true" /> : <Maximize2 size={18} aria-hidden="true" />}</button><button type="button" aria-label="Close comparison" title="Close" onClick={onClose}><X size={18} aria-hidden="true" /></button></div></header>
    <p className="compare-help">A and B follow the labels below. Swap A/B reverses additions and removals. Pages are paired by page number. Colors compare rendered pixels against the white page background. This comparison does not check metadata, hidden content or form behavior.</p>
    <p role="status" className="compare-status">{error ? '' : preview?.status ?? 'Rendering comparison...'}</p>
    {error && <p role="alert">{error}</p>}
    <div className="compare-body" aria-busy={!preview && !error}>
      {orderedFiles.map((file, index) => <section key={index}><h3 title={file.name}>{index === 0 ? 'A' : 'B'}: {file.name}</h3><span>{orderedDocuments[index] ? `${orderedDocuments[index].numPages} pages` : 'Loading...'}</span>{preview && (pageNumber > orderedDocuments[index].numPages ? <p className="compare-missing">No page {pageNumber}</p> : <img src={index === 0 ? preview.left : preview.right} alt={`${index === 0 ? 'Left' : 'Right'} document page ${pageNumber}`} />)}</section>)}
      {showDifference && <section><h3>Differences</h3><div className="compare-legend"><span className="compare-added">+ Added in B</span><span className="compare-removed">- Removed from A</span><span className="compare-changed">Changed in both</span></div>{preview && <img src={preview.difference} alt={`Visual differences on page ${pageNumber}`} />}</section>}
    </div>
    <footer className="compare-controls" aria-label="Comparison controls"><div className="compare-pagination" role="group" aria-label="Comparison page navigation"><button aria-label="Previous comparison page" disabled={pageNumber <= 1} onClick={() => setPageNumber(value => value - 1)}><ChevronLeft size={18} /></button><label>Page <select aria-label="Comparison page" value={pageNumber} disabled={!total} onChange={event => setPageNumber(Number(event.target.value))}>{Array.from({ length: total }, (_, index) => <option key={index} value={index + 1}>{index + 1}</option>)}</select> of {total || '-'}</label><button aria-label="Next comparison page" disabled={pageNumber >= total} onClick={() => setPageNumber(value => value + 1)}><ChevronRight size={18} /></button></div><div className="compare-view-actions"><label><input type="checkbox" checked={showDifference} onChange={event => setShowDifference(event.target.checked)} />Show differences</label><button type="button" disabled={documents.length !== 2} title="Swap documents A and B; reverse additions and removals" onClick={() => {
      setPreview(null);
      setSwapped(value => !value);
    }}><ArrowLeftRight size={16} aria-hidden="true" />Swap A/B</button></div></footer>
  </Modal>;
}
