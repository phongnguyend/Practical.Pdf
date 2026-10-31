import fs from 'node:fs/promises';
import path from 'node:path';
import { createRequire } from 'node:module';
import { createCanvas, loadImage, ImageData } from '@napi-rs/canvas';
import { getDocument } from 'pdfjs-dist/legacy/build/pdf.mjs';

const require = createRequire(import.meta.url);
const pdfAssets = path.dirname(require.resolve('pdfjs-dist/package.json'));
const request = JSON.parse(await fs.readFile(process.argv[2], 'utf8'));
const directory = path.dirname(process.argv[2]);
const documents = [];

async function write(name, data) {
  await fs.writeFile(path.join(directory, name), data);
}

async function open(filename) {
  const task = getDocument({
    data: new Uint8Array(await fs.readFile(filename)),
    cMapUrl: path.join(pdfAssets, 'cmaps').replaceAll('\\', '/') + '/',
    cMapPacked: true,
    standardFontDataUrl: path.join(pdfAssets, 'standard_fonts').replaceAll('\\', '/') + '/',
    wasmUrl: path.join(pdfAssets, 'wasm').replaceAll('\\', '/') + '/',
    isEvalSupported: false,
  });
  documents.push(task);
  const pdf = await task.promise;
  if (pdf.numPages > 100) {
    throw new Error('Visual commands support up to 100 pages. Extract a smaller document first.');
  }
  return pdf;
}

function canvas(width, height) {
  width = Math.ceil(width);
  height = Math.ceil(height);
  if (!Number.isFinite(width * height) || width <= 0 || height <= 0 || width * height > 16000000) {
    throw new Error('Canvas limit exceeded (16 million pixels). Reduce DPI or extract fewer pages.');
  }
  const result = createCanvas(width, height);
  const ctx = result.getContext('2d');
  ctx.fillStyle = '#fff';
  ctx.fillRect(0, 0, width, height);
  return result;
}

async function render(page, scale, width, height) {
  const viewport = page?.getViewport({ scale });
  const result = canvas(width ?? viewport.width, height ?? viewport.height);
  if (page) {
    await page.render({ canvasContext: result.getContext('2d'), viewport, background: '#ffffff' }).promise;
  }
  return result;
}

async function rasterize(pdf) {
  if (request.regions.length > 1000) {
    throw new Error('Provide at most 1000 redaction regions.');
  }
  for (const region of request.regions) {
    if (!region || !Number.isInteger(region.pageNumber) || region.pageNumber < 1 || region.pageNumber > pdf.numPages
      || ![region.x, region.y, region.width, region.height].every(Number.isFinite) || region.width <= 0 || region.height <= 0) {
      throw new Error('Invalid redaction region or page number.');
    }
  }
  const manifest = [];
  let total = 0;
  for (let number = 1; number <= pdf.numPages; number++) {
    const page = await pdf.getPage(number);
    const base = page.getViewport({ scale: 1 });
    const viewport = page.getViewport({ scale: request.dpi / 72 });
    const width = Math.ceil(viewport.width);
    const height = Math.ceil(viewport.height);
    total += width * height * 3;
    if (total > 64 * 1024 * 1024) {
      throw new Error('Raster pages exceed 64 MB. Reduce DPI or extract fewer pages.');
    }
    const regions = request.regions.filter(region => region.pageNumber === number).map(region => {
      const [a, b, c, d, e, f] = viewport.transform;
      const point = (x, y) => [a * x + c * y + e, b * x + d * y + f];
      const rect = [...point(region.x, region.y), ...point(region.x + region.width, region.y + region.height)];
      const left = Math.min(rect[0], rect[2]);
      const top = Math.min(rect[1], rect[3]);
      const right = Math.max(rect[0], rect[2]);
      const bottom = Math.max(rect[1], rect[3]);
      if (left < -0.001 || top < -0.001 || right > viewport.width + 0.001 || bottom > viewport.height + 0.001) {
        throw new Error(`Redaction region lies outside page ${number}.`);
      }
      const x = Math.max(0, Math.floor(left));
      const y = Math.max(0, Math.floor(top));
      return { x, y, width: Math.min(width, Math.ceil(right)) - x, height: Math.min(height, Math.ceil(bottom)) - y };
    });
    const picture = await render(page, request.dpi / 72);
    const rgba = picture.getContext('2d').getImageData(0, 0, width, height).data;
    const rgb = Buffer.alloc(width * height * 3);
    for (let i = 0, j = 0; i < rgba.length; i += 4, j += 3) {
      rgb[j] = rgba[i];
      rgb[j + 1] = rgba[i + 1];
      rgb[j + 2] = rgba[i + 2];
    }
    // The .NET raster redactor independently overwrites these pixels before encoding.
    await write(`page-${number}.rgb`, rgb);
    manifest.push({ pixelWidth: width, pixelHeight: height, widthPoints: base.width, heightPoints: base.height, regions });
    page.cleanup();
  }
  await write('pages.json', JSON.stringify(manifest));
}

async function exportImage(command) {
  const result = canvas(command.width, command.height);
  const ctx = result.getContext('2d');
  ctx.clearRect(0, 0, result.width, result.height);
  if (command.kind === 'jpeg') {
    ctx.drawImage(await loadImage(Buffer.from(command.data, 'base64')), 0, 0, command.width, command.height);
    if (command.alphaData) {
      const alpha = Buffer.from(command.alphaData, 'base64');
      const pixels = ctx.getImageData(0, 0, result.width, result.height);
      for (let p = 0; p < alpha.length; p++) {
        if (command.matte && alpha[p] > 0) {
          for (let c = 0; c < 3; c++) {
            pixels.data[p * 4 + c] = (pixels.data[p * 4 + c] - (255 - alpha[p]) * command.matte[c]) * 255 / alpha[p];
          }
        }
        pixels.data[p * 4 + 3] = alpha[p];
      }
      ctx.putImageData(pixels, 0, 0);
    }
  } else {
    ctx.putImageData(new ImageData(new Uint8ClampedArray(Buffer.from(command.data, 'base64')), result.width, result.height), 0, 0);
  }
  await write('result.png', result.toBuffer('image/png'));
}

async function compare(a, b) {
  const report = [];
  let html = '<!doctype html><html lang="en"><meta charset="utf-8"><title>PDF comparison</title><style>body{font:16px system-ui;margin:24px;background:#f4f6f9;color:#172b4d}section{display:flex;gap:16px}figure{margin:0;width:33%}img{width:100%;border:1px solid #ccc}h2{margin-top:32px}</style><h1>PDF comparison</h1><p>Green: added in B. Red: removed from A. Amber: changed in both. Pixels only; hidden content and metadata are not compared.</p>';
  for (let number = 1; number <= Math.max(a.numPages, b.numPages); number++) {
    const pages = await Promise.all([a, b].map(pdf => number <= pdf.numPages ? pdf.getPage(number) : null));
    const sizes = pages.map(page => page?.getViewport({ scale: 1 }));
    const width = Math.max(...sizes.map(size => size?.width ?? 0));
    const height = Math.max(...sizes.map(size => size?.height ?? 0));
    const scale = Math.min(1.5, 1400 / Math.max(width, height));
    const pictures = [];
    for (const page of pages) {
      pictures.push(await render(page, scale, width * scale, height * scale));
    }
    const [w, h] = [pictures[0].width, pictures[0].height];
    const [left, right] = pictures.map(picture => picture.getContext('2d').getImageData(0, 0, w, h).data);
    const diff = canvas(w, h);
    const pixels = diff.getContext('2d').createImageData(w, h);
    let changedPixels = 0;
    for (let i = 0; i < left.length; i += 4) {
      const different = Math.max(Math.abs(left[i] - right[i]), Math.abs(left[i + 1] - right[i + 1]), Math.abs(left[i + 2] - right[i + 2])) > 24;
      if (different) {
        changedPixels++;
      }
      const presentA = Math.min(left[i], left[i + 1], left[i + 2]) < 231;
      const presentB = Math.min(right[i], right[i + 1], right[i + 2]) < 231;
      const color = !presentA && presentB ? [22, 163, 74] : presentA && !presentB ? [220, 38, 38] : [217, 119, 6];
      for (let c = 0; c < 3; c++) {
        pixels.data[i + c] = different ? color[c] : Math.round(right[i + c] * 0.3 + 255 * 0.7);
      }
      pixels.data[i + 3] = 255;
    }
    diff.getContext('2d').putImageData(pixels, 0, 0);
    report.push({ pageNumber: number, changedPixels, differentSize: sizes[0]?.width !== sizes[1]?.width || sizes[0]?.height !== sizes[1]?.height, missingA: !pages[0], missingB: !pages[1] });
    html += `<h2>Page ${number}: ${changedPixels} changed pixels</h2><section>`;
    for (const [i, picture] of [...pictures, diff].entries()) {
      const name = `page-${number}-${['a', 'b', 'diff'][i]}.png`;
      await write(name, picture.toBuffer('image/png'));
      html += `<figure><figcaption>${['A', 'B', 'Differences'][i]}</figcaption><img src="${name}" alt="Page ${number}"></figure>`;
    }
    html += '</section>';
    for (const page of pages) {
      page?.cleanup();
    }
  }
  await write('comparison.json', JSON.stringify(report, null, 2));
  await write('index.html', html + '</html>');
}

async function ocr() {
  if (!/^[a-z]{3}$/.test(request.language)) {
    throw new Error('Use a three-letter OCR language, e.g. eng or vie.');
  }
  const languageData = request.dataDirectory ?? require(`@tesseract.js-data/${request.language}`).langPath;
  // Check locally before creating a worker; never silently fetch language data.
  await fs.access(path.join(languageData, request.language + '.traineddata.gz'));
  const source = await loadImage(request.input);
  canvas(source.width, source.height);
  const { createWorker, PSM } = await import('tesseract.js');
  const worker = await createWorker(request.language, 1, { langPath: languageData, cacheMethod: 'none' });
  try {
    await worker.setParameters({ tessedit_pageseg_mode: PSM.AUTO });
    const { data } = await worker.recognize(request.input, {}, { text: true, blocks: true });
    const lines = (data.blocks ?? []).flatMap(block => block.paragraphs.flatMap(paragraph => paragraph.lines))
      .filter(line => line.text.trim()).map(line => {
        const x0 = Math.max(0, Math.min(source.width, line.bbox.x0));
        const y0 = Math.max(0, Math.min(source.height, line.bbox.y0));
        const x1 = Math.max(x0, Math.min(source.width, line.bbox.x1));
        const y1 = Math.max(y0, Math.min(source.height, line.bbox.y1));
        return { text: line.text.trim(), x: x0 / source.width, y: y0 / source.height,
          width: (x1 - x0) / source.width, height: (y1 - y0) / source.height,
          baseline: Math.max(y0, Math.min(y1, line.baseline.y0)) / source.height };
      }).filter(line => line.width > 0 && line.height > 0);
    await write('result.json', JSON.stringify(lines, null, 2));
  } finally {
    await worker.terminate();
  }
}

try {
  if (request.command === 'qr') {
    const text = await fs.readFile(request.input, 'utf8');
    if (!text.trim() || text.length > 4000) {
      throw new Error('QR text must contain 1â€“4000 characters.');
    }
    const { default: QRCode } = await import('qrcode');
    await write('result.png', await QRCode.toBuffer(text, { type: 'png', width: 512, margin: 4 }));
  } else if (request.command === 'ocr') {
    await ocr();
  } else if (request.command === 'export-image') {
    await exportImage(request.imageData);
  } else {
    const pdf = await open(request.input);
    if (request.command === 'compare') {
      await compare(pdf, await open(request.other));
    } else if (request.command === 'render') {
      await write('result.png', (await render(await pdf.getPage(request.page), request.dpi / 72)).toBuffer('image/png'));
    } else {
      await rasterize(pdf);
    }
  }
} catch (error) {
  console.error(error.message);
  process.exitCode = 1;
} finally {
  for (const task of documents) {
    await task.destroy();
  }
}
