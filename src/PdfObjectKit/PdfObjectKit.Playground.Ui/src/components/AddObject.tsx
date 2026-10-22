import { useEffect, useState } from 'react';
import { Plus, Move } from 'lucide-react';
import { TextAlignment, type Alignment } from './TextAlignment';
import { ImagePicker } from './ImagePicker';
import type { Bounds } from './Placement';
import type { Edit } from './ObjectEditor';

export function AddObject({ kind, alignment, onAlignmentChange, value, setValue, fontSize, setFontSize, pageNumber, point, onBoundsChange, droppedImage, onImageChange, busy, onSave, onCancel }: { kind: string; alignment: Alignment; onAlignmentChange: (alignment: Alignment) => void; value: string; setValue: (value: string) => void; fontSize: number; setFontSize: (size: number) => void; pageNumber: number; point: Bounds; onBoundsChange: (bounds: Bounds) => void; droppedImage?: File; onImageChange: (image?: File) => void; busy: boolean; onSave: (edit: Edit) => Promise<void>; onCancel: () => void }) {
  const [fieldName, setFieldName] = useState('NewField');
  const image = droppedImage;
  const { x, y, width, height } = point;
  useEffect(() => {
    if (kind !== 'add-text') {
      return;
    }
    const context = document.createElement('canvas').getContext('2d');
    if (!context) {
      return;
    }
    // Browser Helvetica metrics approximate the PDF's standard Helvetica font.
    context.font = `${fontSize}px Helvetica, Arial, sans-serif`;
    const nextWidth = Math.max(fontSize, context.measureText(value || 'Text').width);
    const nextHeight = Math.max(1, fontSize);
    if (width !== nextWidth || height !== nextHeight) {
      onBoundsChange({ x, y, width: nextWidth, height: nextHeight });
    }
  }, [kind, value, fontSize, x, y, width, height, onBoundsChange]);
  return <form className="editor" onSubmit={event => {
    event.preventDefault();
    void onSave({ kind, value, pageNumber, fieldName, textAlignment: kind === 'add-field' ? alignment : undefined, image, x, y, width: kind === 'add-text' ? undefined : width, height: kind === 'add-text' ? undefined : height, fontSize });
  }}><fieldset disabled={busy}><legend>Add {kind === 'add-image' ? 'image' : kind === 'add-field' ? 'form field' : 'text'} · page {pageNumber}</legend>
    <div className="inspector-section"><h3>Content</h3>
    {kind === 'add-image' ? <ImagePicker onUseSize={(imageWidth, imageHeight) => onBoundsChange({ ...point, width: imageWidth, height: imageHeight })} width={width} height={height} image={image} onChange={onImageChange} /> : <label>Text<input aria-label="Text to insert" maxLength={10000} value={value} onChange={event => setValue(event.target.value)} /></label>}
    {kind === 'add-field' && <label>Field name<input aria-label="Field name" required value={fieldName} onChange={event => setFieldName(event.target.value)} /></label>}
    {kind === 'add-field' && <TextAlignment value={alignment} onChange={onAlignmentChange} />}
    </div><div className="inspector-section"><div className="section-heading"><h3>{kind === 'add-text' ? 'Position & font' : 'Position & size'}</h3><span className="unit-label">PDF POINTS</span></div>
    <p className="control-help"><Move size={13} aria-hidden="true" /> {kind === 'add-text' ? 'Drag to move. Preview bounds follow the text and font size.' : 'Drag to move; use corner handles to resize.'}</p>
    <div className="placement-inputs">
    <label>X<input aria-label="X" type="number" step="any" required value={x} onChange={event => onBoundsChange({ ...point, x: Number(event.target.value) })} /></label>
    <label>Y<input aria-label="Y" type="number" step="any" required value={y} onChange={event => onBoundsChange({ ...point, y: Number(event.target.value) })} /></label>

      {kind !== 'add-text' && <><label>Width<input aria-label="Width" type="number" min="1" value={width} onChange={event => onBoundsChange({ ...point, width: Number(event.target.value) })} /></label>
      <label>Height<input aria-label="Height" type="number" min="1" value={height} onChange={event => onBoundsChange({ ...point, height: Number(event.target.value) })} /></label></>}
    </div>
    {kind === 'add-text' && <label>Font size<input aria-label="Font size" type="number" required min="1" max="500" value={fontSize} onChange={event => setFontSize(Number(event.target.value))} /></label>}
    </div>
    {kind === 'add-image' && <p className="hint">Other supported raster formats are converted to PNG. Animated images use one frame.</p>}{image && kind === 'add-image' && <p className="hint">{image.name}</p>}<div className="editor-actions"><button className="primary-action" type="submit" disabled={kind === 'add-image' && !image}><Plus size={16} aria-hidden="true" />{busy ? 'Inserting...' : 'Insert object'}</button><button type="button" onClick={onCancel}>Cancel</button></div>
    {kind !== 'add-image' && <p className="control-help">Black Helvetica, single-line ASCII text</p>}
  </fieldset></form>;
}
