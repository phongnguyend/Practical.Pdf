import type { Command } from './nativeRenderer';

export async function decodeCanvasImage(command: Command): Promise<CanvasImageSource> {
  let image: CanvasImageSource;
  if (command.kind === 'jpeg') {
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
  return image;
}
