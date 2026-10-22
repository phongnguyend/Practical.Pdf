import { useEffect, useState } from 'react';
import type { Bounds } from './Placement';
import type { Edit } from './ObjectEditor';

export function AddObject({ kind, onKindChange, pageNumber, point, onBoundsChange, droppedImage, onImageChange, busy, onSave, onCancel }: { kind: string; onKindChange: (kind: string) => void; pageNumber: number; point: Bounds; onBoundsChange: (bounds: Bounds) => void; droppedImage?: File; onImageChange: (image?: File) => void; busy: boolean; onSave: (edit: Edit) => Promise<void>; onCancel: () => void }) {
  const [value, setValue] = useState('');
  const [fieldName, setFieldName] = useState('NewField');
  const image = droppedImage;
  const { x, y, width, height } = point;
  const [fontSize, setFontSize] = useState(12);
  useEffect(() => { setFontSize(Math.max(1, Math.min(500, Math.round(point.height * .8)))); }, [point.height]);
  useEffect(() => {
    if (droppedImage) {
      onKindChange('add-image');
    }
  }, [droppedImage]);
  return <form className="editor" onSubmit={event => {
    event.preventDefault();
    void onSave({ kind, value, pageNumber, fieldName, image, x, y, width, height, fontSize });
  }}><fieldset disabled={busy}><legend>Add object · page {pageNumber}</legend>
    <label>Object type<select aria-label="Object type" value={kind} onChange={event => {
      onKindChange(event.target.value);
      if (event.target.value !== 'add-image') {
        onImageChange(undefined);
      }
    }}><option value="add-text">Text</option><option value="add-image">Image</option><option value="add-field">Text form field</option></select></label>
    {kind === 'add-image' ? <label>PNG or JPEG<input aria-label="Image file" type="file" accept="image/png,image/jpeg" required={!image} onChange={event => onImageChange(event.target.files?.[0])} /></label> : <label>Text<input aria-label="Text to insert" maxLength={10000} value={value} onChange={event => setValue(event.target.value)} /></label>}
    {kind === 'add-field' && <label>Field name<input aria-label="Field name" required value={fieldName} onChange={event => setFieldName(event.target.value)} /></label>}
    <p className="hint">Drag the placed object to move it. Drag its corner handles to resize its width and height. Coordinates stay editable below. Text uses a baseline; images and fields use their bottom-left corner.</p>
    <label>X<input aria-label="X" type="number" step="any" required value={x} onChange={event => onBoundsChange({ ...point, x: Number(event.target.value) })} /></label>
    <label>Y<input aria-label="Y" type="number" step="any" required value={y} onChange={event => onBoundsChange({ ...point, y: Number(event.target.value) })} /></label>
    {kind === 'add-text' && <label>Font size<input aria-label="Font size" type="number" min="1" max="500" value={fontSize} onChange={event => setFontSize(Number(event.target.value))} /></label>}
      <label>Width<input aria-label="Width" type="number" min="1" value={width} onChange={event => onBoundsChange({ ...point, width: Number(event.target.value) })} /></label>
      <label>Height<input aria-label="Height" type="number" min="1" value={height} onChange={event => onBoundsChange({ ...point, height: Number(event.target.value) })} /></label>
    {image && kind === 'add-image' && <p className="hint">{image.name}</p>}<button type="submit">Insert object</button><button type="button" onClick={onCancel}>Cancel</button>
    <p className="hint">Text insertion uses black Helvetica and printable ASCII. Existing page content is preserved.</p>
  </fieldset></form>;
}
