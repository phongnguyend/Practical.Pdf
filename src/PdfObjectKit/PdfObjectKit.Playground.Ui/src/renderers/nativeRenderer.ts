import type { PageViewport } from 'pdfjs-dist';

type Command = { kind: string; numbers: number[]; fontName?: string; fontId?: string; glyphs?: { text: string; renderText?: string; width: number; advance: number }[]; data?: string; alphaData?: string; matte?: number[]; width: number; height: number };
type DisplayList = { fonts: { id: string; name: string; data: string }[]; commands: Command[]; warnings: string[] };

export async function renderNative(canvas: HTMLCanvasElement, viewport: PageViewport, ratio: number, source: Blob, page: number, signal?: AbortSignal): Promise<string[]> {
  const response = await fetch(`/api/documents/render?page=${page}`, { method: 'POST', headers: { 'Content-Type': 'application/pdf' }, body: source, signal });
  if (!response.ok) {
    throw new Error((await response.json()).detail ?? 'Native rendering failed.');
  }
  const list: DisplayList = await response.json();
  const warnings = new Set(list.warnings);
  const loaded = new Map<string, FontFace>();
  try {
    for (const font of list.fonts ?? []) {
      signal?.throwIfAborted();
      try {
        const bytes = Uint8Array.from(atob(font.data), c => c.charCodeAt(0));
        const face = await new FontFace(font.id, bytes).load();
        document.fonts.add(face);
        loaded.set(font.id, face);
      } catch {
        warnings.add(`Font '${font.name}' was rejected by the browser; using a substitute.`);
      }
    }
    const ctx = canvas.getContext('2d')!;
    ctx.fillStyle = 'white';
    ctx.fillRect(0, 0, canvas.width, canvas.height);
    // Pixel scaling applies to the viewport translation as well.
    const transform = new DOMMatrix([ratio, 0, 0, ratio, 0, 0]).multiply(new DOMMatrix(viewport.transform));
    ctx.setTransform(transform);
    ctx.fillStyle = ctx.strokeStyle = 'black';
    let mode = 0;
    let fillAlpha = 1;
    let strokeAlpha = 1;
    const stack: number[][] = [];
    let clip: CanvasFillRule | undefined;
    let x = 0;
    let y = 0;
    function color(n: number[]) {
      const rgb = n.length === 1 ? [n[0], n[0], n[0]] : n.length === 4 ? n.slice(0, 3).map(v => 1 - Math.min(1, v + n[3])) : n;
      return `rgb(${rgb.map(v => Math.round(Math.max(0, Math.min(1, v)) * 255)).join(',')})`;
    }
    for (const command of list.commands) {
      signal?.throwIfAborted();
      const n = command.numbers;
      const op = command.kind;
      switch (op) {
        case 'q':
          ctx.save();
          stack.push([mode, fillAlpha, strokeAlpha]);
          break;
        case 'Q':
          ctx.restore();
          [mode, fillAlpha, strokeAlpha] = stack.pop() ?? [0, 1, 1];
          break;
        case 'cm':
          ctx.transform(n[0], n[1], n[2], n[3], n[4], n[5]);
          break;
        case 'm':
          ctx.moveTo(n[0], n[1]);
          [x, y] = n;
          break;
        case 'l':
          ctx.lineTo(n[0], n[1]);
          [x, y] = n;
          break;
        case 'c':
          ctx.bezierCurveTo(n[0], n[1], n[2], n[3], n[4], n[5]);
          [x, y] = n.slice(4);
          break;
        case 'v':
          ctx.bezierCurveTo(x, y, n[0], n[1], n[2], n[3]);
          [x, y] = n.slice(2);
          break;
        case 'y':
          ctx.bezierCurveTo(n[0], n[1], n[2], n[3], n[2], n[3]);
          [x, y] = n.slice(2);
          break;
        case 're':
          ctx.rect(n[0], n[1], n[2], n[3]);
          [x, y] = n;
          break;
        case 'h':
          ctx.closePath();
          break;
        case 'formClip': {
          const bounds = new Path2D();
          bounds.rect(n[0], n[1], n[2], n[3]);
          ctx.clip(bounds);
          break;
        }
        case 'W':
        case 'W*':
          clip = op === 'W*' ? 'evenodd' : 'nonzero';
          break;
        case 'S': case 's': case 'f': case 'F': case 'f*': case 'B': case 'B*': case 'b': case 'b*': case 'n':
          if (['s', 'b', 'b*'].includes(op)) {
            ctx.closePath();
          }
          if (['f', 'F', 'f*', 'B', 'B*', 'b', 'b*'].includes(op)) {
            ctx.globalAlpha = fillAlpha;
            ctx.fill(op.endsWith('*') ? 'evenodd' : 'nonzero');
          }
          if (['S', 's', 'B', 'B*', 'b', 'b*'].includes(op)) {
            ctx.globalAlpha = strokeAlpha;
            ctx.stroke();
          }
          if (clip) {
            ctx.clip(clip);
            clip = undefined;
          }
          ctx.beginPath();
          break;
        case 'w':
          ctx.lineWidth = n[0] || 0.25;
          break;
        case 'J':
          ctx.lineCap = (['butt', 'round', 'square'] as CanvasLineCap[])[n[0]];
          break;
        case 'j':
          ctx.lineJoin = (['miter', 'round', 'bevel'] as CanvasLineJoin[])[n[0]];
          break;
        case 'M':
          ctx.miterLimit = n[0];
          break;
        case 'd':
          ctx.lineDashOffset = n[0];
          ctx.setLineDash(n.slice(1));
          break;
        case 'g': case 'rg': case 'k':
          ctx.fillStyle = color(n);
          break;
        case 'fillGradient': case 'strokeGradient': {
          ctx.save();
          ctx.setTransform(transform.multiply(new DOMMatrix(n.slice(4, 10))));
          const gradient = ctx.createLinearGradient(n[0], n[1], n[2], n[3]);
          for (let i = 10; i < n.length; i += 4) {
            gradient.addColorStop(n[i], color(n.slice(i + 1, i + 4)));
          }
          ctx.restore();
          if (op === 'fillGradient') {
            ctx.fillStyle = gradient;
          } else {
            ctx.strokeStyle = gradient;
          }
          break;
        }
        case 'G': case 'RG': case 'K':
          ctx.strokeStyle = color(n);
          break;
        case 'alpha':
          if (n[0] >= 0) {
            fillAlpha = n[0];
          }
          if (n[1] >= 0) {
            strokeAlpha = n[1];
          }
          break;
        case 'Tr':
          mode = n[0];
          if (mode >= 4) {
            warnings.add('Text clipping is not supported.');
          }
          break;
        case 'text': {
          if (mode === 3 || mode === 7) {
            break;
          }
          ctx.save();
          ctx.setTransform(transform.multiply(new DOMMatrix(n.slice(0, 6))));
          ctx.translate(0, n[8]);
          ctx.scale(n[7], -1);
          const font = command.fontName ?? '';
          const embedded = command.fontId && loaded.has(command.fontId) ? command.fontId : undefined;
          const family = /courier/i.test(font) ? 'monospace' : /times/i.test(font) ? 'serif' : 'sans-serif';
          ctx.font = embedded ? `${Math.abs(n[6])}px "${embedded}"` : `${/italic|oblique/i.test(font) ? 'italic ' : ''}${/bold/i.test(font) ? 'bold ' : ''}${Math.abs(n[6])}px ${family}`;
          let advance = 0;
          for (const glyph of command.glyphs ?? []) {
            ctx.save();
            ctx.translate(advance, 0);
            const text = embedded ? glyph.renderText ?? glyph.text : glyph.text;
            const measured = ctx.measureText(text).width;
            if (!embedded && measured > 0 && glyph.width > 0) {
              ctx.scale(glyph.width / measured, 1);
            }
            if ([0, 2, 4, 6].includes(mode)) {
              ctx.globalAlpha = fillAlpha;
              ctx.fillText(text, 0, 0);
            }
            if ([1, 2, 5, 6].includes(mode)) {
              ctx.globalAlpha = strokeAlpha;
              ctx.strokeText(text, 0, 0);
            }
            ctx.restore();
            advance += glyph.advance;
          }
          ctx.restore();
          break;
        }
        case 'jpeg': case 'rgba': {
          let image: CanvasImageSource;
          if (op === 'jpeg') {
            const element = new Image();
            element.src = `data:image/jpeg;base64,${command.data}`;
            await element.decode();
            image = element;
            if (command.alphaData) {
              const alpha = Uint8Array.from(atob(command.alphaData), c => c.charCodeAt(0));
              const offscreen = document.createElement('canvas');
              offscreen.width = command.width;
              offscreen.height = command.height;
              const pixelsContext = offscreen.getContext('2d')!;
              pixelsContext.drawImage(element, 0, 0, command.width, command.height);
              const pixels = pixelsContext.getImageData(0, 0, command.width, command.height);
              for (let pixel = 0; pixel < command.width * command.height; pixel++) {
                const opacity = alpha[pixel];
                if (command.matte && opacity > 0) {
                  for (let channel = 0; channel < 3; channel++) {
                    pixels.data[pixel * 4 + channel] = (pixels.data[pixel * 4 + channel] - (255 - opacity) * command.matte[channel]) * 255 / opacity;
                  }
                }
                pixels.data[pixel * 4 + 3] = opacity;
              }
              pixelsContext.putImageData(pixels, 0, 0);
              image = offscreen;
            }
          } else {
            const buffer = Uint8ClampedArray.from(atob(command.data!), c => c.charCodeAt(0));
            const offscreen = document.createElement('canvas');
            offscreen.width = command.width;
            offscreen.height = command.height;
            offscreen.getContext('2d')!.putImageData(new ImageData(buffer, command.width, command.height), 0, 0);
            image = offscreen;
          }
          ctx.save();
          ctx.globalAlpha = fillAlpha;
          ctx.scale(1, -1);
          ctx.drawImage(image, 0, -1, 1, 1);
          ctx.restore();
          break;
        }
      }
    }
    return [...warnings];
  } finally {
    for (const face of loaded.values()) {
      document.fonts.delete(face);
    }
  }
}
