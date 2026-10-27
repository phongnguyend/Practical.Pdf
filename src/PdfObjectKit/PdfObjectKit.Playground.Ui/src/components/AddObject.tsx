import { useEffect, useState } from 'react';
import { Plus, Move } from 'lucide-react';
import { TextAlignment, type Alignment } from './TextAlignment';
import { ImagePicker } from './ImagePicker';
import type { Bounds, FieldPreview } from './Placement';
import type { Edit } from './ObjectEditor';

export function AddObject({ kind, alignment, onAlignmentChange, value, setValue, fontSize, setFontSize, pageNumber, point, onBoundsChange, droppedImage, onImageChange, busy, onSave, onCancel, onFieldPreviewChange }: { kind: string; alignment: Alignment; onAlignmentChange: (alignment: Alignment) => void; value: string; setValue: (value: string) => void; fontSize: number; setFontSize: (size: number) => void; pageNumber: number; point: Bounds; onBoundsChange: (bounds: Bounds) => void; droppedImage?: File; onImageChange: (image?: File) => void; busy: boolean; onSave: (edit: Edit) => Promise<void>; onCancel: () => void; onFieldPreviewChange: (preview: FieldPreview) => void }) {
  const [fieldType, setFieldType] = useState<NonNullable<Edit['fieldType']> | 'MultilineText' | 'ListBox' | 'MultiSelectListBox'>('Text');
  const [selectedValues, setSelectedValues] = useState<string[]>([]);
  const [optionText, setOptionText] = useState('Option 1\nOption 2');
  const choices = optionText.split('\n').map(line => line.trim()).filter(Boolean).map((label, index) => ({ exportValue: fieldType === 'RadioButton' ? `Choice${index + 1}` : label, displayValue: label }));
  useEffect(() => {
    onFieldPreviewChange({ type: fieldType, value, values: selectedValues, choices: optionText.split('\n').map(line => line.trim()).filter(Boolean).map((label, index) => ({ exportValue: fieldType === 'RadioButton' ? `Choice${index + 1}` : label, displayValue: label })) });
  }, [fieldType, value, selectedValues, optionText, onFieldPreviewChange]);
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
    void onSave({ kind, fieldType: fieldType === 'MultilineText' ? 'Text' : fieldType === 'ListBox' || fieldType === 'MultiSelectListBox' ? 'Choice' : fieldType, multiline: fieldType === 'MultilineText', listBox: fieldType === 'ListBox' || fieldType === 'MultiSelectListBox', multiSelect: fieldType === 'MultiSelectListBox', values: fieldType === 'MultiSelectListBox' ? selectedValues : undefined, choices: fieldType === 'Choice' || fieldType === 'ListBox' || fieldType === 'MultiSelectListBox' || fieldType === 'RadioButton' ? choices : undefined, value, pageNumber, fieldName, textAlignment: kind === 'add-field' ? alignment : undefined, image, x, y, width: kind === 'add-text' ? undefined : width, height: kind === 'add-text' ? undefined : height, fontSize });
  }}><fieldset disabled={busy}><legend>Add {kind === 'add-image' ? 'image' : kind === 'add-field' ? 'form field' : 'text'} · page {pageNumber}</legend>
    <div className="inspector-section"><h3>Content</h3>
    {kind === 'add-field' && <label>Field type<select aria-label="Field type" value={fieldType} onChange={event => {
      const next = event.target.value as typeof fieldType;
      setFieldType(next);
      setSelectedValues([]);
      setValue(next === 'CheckBox' || next === 'RadioButton' ? 'Off' : next === 'PushButton' ? 'Button' : '');
      onBoundsChange({ ...point, width: next === 'CheckBox' ? 20 : 150, height: next === 'CheckBox' ? 20 : next === 'MultilineText' || next === 'ListBox' || next === 'MultiSelectListBox' ? 80 : next === 'RadioButton' ? Math.max(40, choices.length * 24) : 30 });
    }}><option value="Text">Text field</option><option value="MultilineText">Multiline text</option><option value="ListBox">List box</option><option value="MultiSelectListBox">Multi-select list box</option><option value="CheckBox">Checkbox</option><option value="RadioButton">Radio group</option><option value="Choice">Dropdown</option><option value="PushButton">Pushbutton</option><option value="Signature">Signature field</option></select></label>}
    {kind === 'add-field' && (fieldType === 'Choice' || fieldType === 'ListBox' || fieldType === 'MultiSelectListBox' || fieldType === 'RadioButton') && <label>Options (one per line)<textarea aria-label="Field options" rows={4} required value={optionText} maxLength={10000} onChange={event => { setOptionText(event.target.value); setSelectedValues([]); setValue(fieldType === 'RadioButton' ? 'Off' : ''); }} /></label>}
    {kind === 'add-field' && fieldType === 'Signature' ? <p className="control-help">Creates an unsigned field for use with a signing tool. This does not sign the PDF.</p> : kind === 'add-field' && fieldType === 'PushButton' ? <label>Caption<input aria-label="Button caption" maxLength={10000} value={value} onChange={event => setValue(event.target.value)} /><span className="control-help">No action is attached to this button.</span></label> : kind === 'add-field' && fieldType === 'MultiSelectListBox' ? <fieldset className="field-selections"><legend>Initial selections</legend>
      {choices.map((choice, index) => <label key={index}><input type="checkbox" checked={selectedValues.includes(choice.exportValue)} onChange={event => setSelectedValues(event.target.checked ? [...selectedValues, choice.exportValue] : selectedValues.filter(value => value !== choice.exportValue))} />{choice.displayValue}</label>)}
    </fieldset> : kind === 'add-field' && fieldType !== 'Text' && fieldType !== 'MultilineText' ? <label>Initial value<select aria-label="Initial field value" value={value} onChange={event => setValue(event.target.value)}>
      <option value={fieldType === 'Choice' || fieldType === 'ListBox' ? '' : 'Off'}>{fieldType === 'CheckBox' ? 'Unchecked' : 'No selection'}</option>
      {fieldType === 'CheckBox' ? <option value="Yes">Checked</option> : choices.map((choice, index) => <option key={index} value={choice.exportValue}>{choice.displayValue}</option>)}
    </select></label> : kind === 'add-field' && fieldType === 'MultilineText' ? <label>Text<textarea aria-label="Text to insert" rows={4} maxLength={10000} value={value} onChange={event => setValue(event.target.value)} /></label> : kind === 'add-image' ? <ImagePicker onUseSize={(imageWidth, imageHeight) => onBoundsChange({ ...point, width: imageWidth, height: imageHeight })} width={width} height={height} image={image} onChange={onImageChange} /> : <label>Text<input aria-label="Text to insert" maxLength={10000} value={value} onChange={event => setValue(event.target.value)} /></label>}
    {kind === 'add-field' && <label>Field name<input aria-label="Field name" required value={fieldName} onChange={event => setFieldName(event.target.value)} /></label>}
    {kind === 'add-field' && (fieldType === 'Text' || fieldType === 'MultilineText' || fieldType === 'ListBox' || fieldType === 'MultiSelectListBox' || fieldType === 'Choice') && <TextAlignment value={alignment} onChange={onAlignmentChange} />}
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
    {kind !== 'add-image' && <p className="control-help">{fieldType === 'RadioButton' && kind === 'add-field' ? 'One option per row. Allow at least 16 pt height per option. Labels use ASCII text.' : 'Text and option labels use ASCII. Multiline fields wrap text; list boxes allow one or multiple selections. Preview is approximate; use PDF.js to view saved fields.'}</p>}
  </fieldset></form>;
}
