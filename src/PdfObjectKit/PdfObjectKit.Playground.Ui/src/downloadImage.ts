import { decodeCanvasImage } from './renderers/decodeCanvasImage';
import type { Command } from './renderers/nativeRenderer';

export async function downloadImage(pdf: Blob, page: number, imageNumber: number, documentName: string) {
  const response = await fetch(`/api/documents/image?page=${page}&image=${imageNumber}`, {
    method: 'POST', headers: { 'Content-Type': 'application/pdf' }, body: pdf,
  });
  if (!response.ok) {
    throw new Error((await response.json()).detail ?? 'Unable to export this image.');
  }
  const command: Command = await response.json();
  let blob: Blob;
  let extension: string;
  if (command.kind === 'jpeg' && !command.alphaData) {
    blob = new Blob([Uint8Array.from(atob(command.data!), c => c.charCodeAt(0))], { type: 'image/jpeg' });
    extension = 'jpg';
  } else {
    const image = await decodeCanvasImage(command);
    const canvas = document.createElement('canvas');
    canvas.width = command.width;
    canvas.height = command.height;
    canvas.getContext('2d')!.drawImage(image, 0, 0);
    blob = await new Promise<Blob>((resolve, reject) => canvas.toBlob(value => {
      if (value) {
        resolve(value);
      } else {
        reject(new Error('Could not encode the exported image.'));
      }
    }, 'image/png'));
    extension = 'png';
  }
  const url = URL.createObjectURL(blob);
  const link = document.createElement('a');
  link.href = url;
  link.download = `${documentName.replace(/\.pdf$/i, '')}-page-${page}-image-${imageNumber}.${extension}`;
  link.click();
  setTimeout(() => URL.revokeObjectURL(url), 1000);
}
