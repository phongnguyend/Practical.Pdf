import { cp, mkdir, readdir } from 'node:fs/promises';

for (const folder of ['cmaps', 'standard_fonts', 'wasm']) {
  await mkdir(`public/pdfjs/${folder}`, { recursive: true });
  await cp(`node_modules/pdfjs-dist/${folder}`, `public/pdfjs/${folder}`, { recursive: true });
}

await mkdir('public/ocr/core', { recursive: true });
await mkdir('public/ocr/lang', { recursive: true });
await cp('node_modules/tesseract.js/dist/worker.min.js', 'public/ocr/worker.min.js');
for (const file of await readdir('node_modules/tesseract.js-core')) {
  if (file.endsWith('.wasm') || file.endsWith('.wasm.js')) {
    await cp(`node_modules/tesseract.js-core/${file}`, `public/ocr/core/${file}`);
  }
}
for (const language of ['eng', 'vie']) {
  await cp(`node_modules/@tesseract.js-data/${language}/4.0.0_best_int/${language}.traineddata.gz`, `public/ocr/lang/${language}.traineddata.gz`);
}
