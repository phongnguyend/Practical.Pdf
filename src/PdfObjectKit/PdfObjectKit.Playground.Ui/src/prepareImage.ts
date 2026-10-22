/** Preserve native PNG/JPEG bytes; normalize other browser-decodable raster formats. */
export async function prepareImageForPdf(file: File): Promise<File> {
  if (file.size === 0 || file.size > 5 * 1024 * 1024) {
    throw new Error('Choose a non-empty image up to 5 MB.');
  }
  const header = new Uint8Array(await file.slice(0, 16).arrayBuffer());
  const png = [137, 80, 78, 71, 13, 10, 26, 10].every((value, index) => header[index] === value);
  const jpeg = header[0] === 255 && header[1] === 216;
  if (png || jpeg) {
    return file;
  }

  let bitmap: ImageBitmap;
  try {
    // Empty MIME lets the decoder inspect the bytes rather than trust a misleading extension.
    bitmap = await createImageBitmap(new Blob([file]));
  } catch {
    throw new Error(`Cannot read "${file.name}" as a raster image. Choose PNG, JPEG, WebP, GIF, BMP or AVIF; renaming a file does not convert it.`);
  }
  try {
    if (bitmap.width * bitmap.height > 16_000_000) {
      throw new Error('Images must contain at most 16 million pixels.');
    }
    const canvas = document.createElement('canvas');
    canvas.width = bitmap.width;
    canvas.height = bitmap.height;
    const context = canvas.getContext('2d');
    if (!context) {
      throw new Error('Image conversion is unavailable in this browser.');
    }
    context.drawImage(bitmap, 0, 0);
    const pngBlob = await new Promise<Blob>((resolve, reject) => {
      canvas.toBlob(blob => {
        if (blob) {
          resolve(blob);
        } else {
          reject(new Error('Could not convert the image to PNG.'));
        }
      }, 'image/png');
    });
    if (pngBlob.size > 5 * 1024 * 1024) {
      throw new Error('The converted PNG exceeds 5 MB. Reduce the image dimensions and try again.');
    }
    return new File([pngBlob], file.name.replace(/\.[^.]*$/, '') + '.png', { type: 'image/png' });
  } finally {
    bitmap.close();
  }
}
