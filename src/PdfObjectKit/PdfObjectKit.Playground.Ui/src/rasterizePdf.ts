import { getDocument, type PDFDocumentProxy, type RenderTask } from 'pdfjs-dist';

type Region = { x: number; y: number; width: number; height: number };

/** Builds a fresh image-only PDF from rendered pages, optionally masking redaction marks. */
export async function rasterizePdf(pdf: PDFDocumentProxy, dpi: number, marks: Record<number, Region[]>, setProgress: (message: string) => void, signal: AbortSignal): Promise<ArrayBuffer> {
  if (pdf.numPages > 100) {
    throw new Error('Convert at most 100 pages at a time. Extract fewer pages first.');
  }
  let renderTask: RenderTask | null = null;
  let verification: ReturnType<typeof getDocument> | undefined;
  const cancel = () => renderTask?.cancel();
  signal.addEventListener('abort', cancel);
  try {
    const data = new FormData();
    const manifest = [];
    let total = 0;
    for (let number = 1; number <= pdf.numPages; number++) {
      signal.throwIfAborted();
      setProgress(`Rendering page ${number} of ${pdf.numPages}...`);
      const source = await pdf.getPage(number);
      signal.throwIfAborted();
      const base = source.getViewport({ scale: 1 });
      const viewport = source.getViewport({ scale: dpi / 72 });
      const width = Math.ceil(viewport.width);
      const height = Math.ceil(viewport.height);
      total += width * height * 3;
      if (!Number.isFinite(total) || width * height > 16000000 || total > 64 * 1024 * 1024) {
        throw new Error('Raster limit exceeded (16 million pixels per page, 64 MB total). Choose a lower resolution or extract fewer pages first.');
      }
      const canvas = document.createElement('canvas');
      canvas.width = width;
      canvas.height = height;
      const context = canvas.getContext('2d')!;
      renderTask = source.render({ canvas, viewport, background: '#ffffff' });
      await renderTask.promise;
      signal.throwIfAborted();
      const regions = (marks[number] ?? []).map(mark => {
        const x = Math.max(0, Math.floor(mark.x * viewport.width));
        const y = Math.max(0, Math.floor(mark.y * viewport.height));
        return { x, y, width: Math.min(width, Math.ceil((mark.x + mark.width) * viewport.width)) - x, height: Math.min(height, Math.ceil((mark.y + mark.height) * viewport.height)) - y };
      });
      context.fillStyle = '#000';
      for (const region of regions) {
        context.fillRect(region.x - 1, region.y - 1, region.width + 2, region.height + 2);
      }
      const rgba = context.getImageData(0, 0, width, height).data;
      const rgb = new Uint8Array(width * height * 3);
      for (let i = 0, j = 0; i < rgba.length; i += 4, j += 3) {
        rgb[j] = rgba[i];
        rgb[j + 1] = rgba[i + 1];
        rgb[j + 2] = rgba[i + 2];
      }
      data.append('pages', new Blob([rgb], { type: 'application/octet-stream' }), `page-${number}.rgb`);
      manifest.push({ pixelWidth: width, pixelHeight: height, widthPoints: base.width, heightPoints: base.height, regions });
      canvas.width = canvas.height = 0;
    }
    data.append('manifest', JSON.stringify(manifest));
    setProgress('Building a fresh image-only PDF...');
    const response = await fetch('/api/documents/redact-raster', { method: 'POST', body: data, signal });
    if (!response.ok) {
      const problem = await response.json().catch(() => null);
      throw new Error(problem?.detail ?? `Export failed (${response.status}).`);
    }
    const bytes = await response.arrayBuffer();
    signal.throwIfAborted();
    setProgress('Verifying page count and removal of text and annotations...');
    verification = getDocument({ data: bytes.slice(0) });
    const output = await verification.promise;
    if (output.numPages !== pdf.numPages || await output.getAttachments()) {
      throw new Error('Output verification failed. No result was opened.');
    }
    for (let number = 1; number <= output.numPages; number++) {
      signal.throwIfAborted();
      const resultPage = await output.getPage(number);
      if ((await resultPage.getTextContent()).items.length || (await resultPage.getAnnotations()).length) {
        throw new Error('Output still contains text or annotations. No result was opened.');
      }
    }
    signal.throwIfAborted();
    return bytes;
  } finally {
    signal.removeEventListener('abort', cancel);
    await verification?.destroy();
  }
}
