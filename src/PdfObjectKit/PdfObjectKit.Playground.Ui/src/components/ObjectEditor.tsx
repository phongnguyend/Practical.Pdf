import { FormAdvancedControls } from './FormAdvancedControls';
import { ImagePicker } from './ImagePicker';
import { TextAlignment, type Alignment } from './TextAlignment';
import { useState } from 'react';
import { Check, Pencil, Save, Trash2, RefreshCw, Download } from 'lucide-react';

export type ButtonAction = { kind: 'OpenUrl' | 'GoToPage' | 'ResetForm' | 'SubmitForm'; url?: string; pageNumber?: number; fieldNames?: string[] };
export type Edit = { rotation?: number; annotationNumber?: number; regions?: { x: number; y: number; width: number; height: number }[]; deletions?: Edit[]; action?: ButtonAction | null; certificate?: File; certificatePassword?: string; richText?: boolean; fileSelect?: boolean; password?: boolean; comb?: boolean; maxLength?: number; multiSelect?: boolean; values?: string[]; multiline?: boolean; listBox?: boolean; fieldType?: 'Text' | 'CheckBox' | 'RadioButton' | 'Choice' | 'PushButton' | 'Signature'; choices?: { exportValue: string; displayValue: string }[]; kind: string; value: string; textAlignment?: Alignment; pageNumber: number; run?: unknown; runs?: unknown[]; fieldName?: string; imageNumber?: number; widgetIndex?: number; deltaX?: number; deltaY?: number; imageFit?: 'Fit' | 'Fill' | 'Stretch'; image?: File; x?: number; y?: number; width?: number; height?: number; fontSize?: number };
type Run = { text: string; fontName?: string | null; fontSize?: number; formOccurrenceId?: number | null };
type Details = { annotationNumber?: number; contents?: string; maxLength?: number; richText?: string; isSigned?: boolean; widget?: { caption?: string; action?: ButtonAction }; flags?: number; corners?: { x: number; y: number }[]; textAlignment?: Alignment; imageNumber?: number; runs?: Run[]; name?: string; kind?: string; values?: string[]; isReadOnly?: boolean; choices?: { exportValue: string; displayValue: string }[]; states?: string[] };

export function ObjectEditor({ item, busy, onSave, onExportImage }: { item: { kind: string; pageNumber: number; details: unknown }; busy: boolean; onSave: (edit: Edit) => Promise<void>; onExportImage: (page: number, image: number) => Promise<void> }) {
  const details = item.details as Details;
  const runs = details.runs ?? [];
  const [replacementImage, setReplacementImage] = useState<File>();
  const [imageFit, setImageFit] = useState<'Fit' | 'Fill' | 'Stretch'>('Fit');
  const corners = details.corners;
  const imageWidth = corners?.length === 4 ? Math.hypot(corners[1].x - corners[0].x, corners[1].y - corners[0].y) : 150;
  const imageHeight = corners?.length === 4 ? Math.hypot(corners[3].x - corners[0].x, corners[3].y - corners[0].y) : 100;
  const [runIndex, setRunIndex] = useState(0);
  const partialName = details.name?.split('.').at(-1) ?? '';
  const [newName, setNewName] = useState(partialName);
  const richText = details.kind === 'Text' && ((details.flags ?? 0) & (1 << 25)) !== 0;
  const fileSelect = details.kind === 'Text' && ((details.flags ?? 0) & (1 << 20)) !== 0;
  const initialValue = item.kind === 'annotation' ? details.contents ?? '' : item.kind === 'text' ? runs[runIndex]?.text ?? '' : richText ? details.richText ?? '<body/>' : details.kind === 'PushButton' ? details.widget?.caption ?? '' : details.values?.[0] ?? (['CheckBox', 'RadioButton'].includes(details.kind ?? '') ? 'Off' : '');
  const initialAlignment = details.textAlignment ?? 'Left';
  const [alignment, setAlignment] = useState<Alignment>(initialAlignment);
  const password = details.kind === 'Text' && ((details.flags ?? 0) & (1 << 13)) !== 0;
  const comb = details.kind === 'Text' && ((details.flags ?? 0) & (1 << 24)) !== 0;
  const multiSelect = details.kind === 'Choice' && ((details.flags ?? 0) & (1 << 21)) !== 0;
  const [selectedValues, setSelectedValues] = useState(details.values ?? []);
  const [value, setValue] = useState(initialValue);
  const supported = item.kind === 'annotation' ? !details.isReadOnly : item.kind === 'text'
    ? runs.length > 0 && runs[runIndex]?.formOccurrenceId == null
    : !details.isReadOnly && ['Text', 'Choice', 'CheckBox', 'RadioButton', 'PushButton'].includes(details.kind ?? '');
  const button = details.kind === 'CheckBox' || details.kind === 'RadioButton';
  const dirty = (multiSelect ? selectedValues.length !== (details.values ?? []).length || selectedValues.some(value => !details.values?.includes(value)) : value !== initialValue) || alignment !== initialAlignment;
  const canDelete = item.kind === 'image' || (item.kind === 'text' ? supported : !details.isReadOnly);
  function remove() {
    void onSave({ kind: item.kind === 'text' ? 'text' : `delete-${item.kind}`, value: '', pageNumber: item.pageNumber,
      run: item.kind === 'text' ? runs[runIndex] : undefined, annotationNumber: details.annotationNumber, fieldName: details.name, imageNumber: details.imageNumber });
  }
  return <div className="object-editor">
    {item.kind === 'image' && <section className="editor inspector-section image-export-section">
      <div className="section-heading"><h3>Current image</h3><button className="secondary-action" aria-label="Download image" title="Save the full image at original resolution, without page cropping or rotation" disabled={busy} onClick={() => void onExportImage(item.pageNumber, details.imageNumber!)}><Download size={15} aria-hidden="true" />Download</button></div>
      <p className="control-help">Original resolution · JPEG or transparent PNG</p>
    </section>}
    {item.kind === 'image' && <form className="editor" onSubmit={event => {
      event.preventDefault();
      if (replacementImage) {
        void onSave({ kind: 'replace-image', value: '', pageNumber: item.pageNumber, imageNumber: details.imageNumber, image: replacementImage, imageFit });
      }
    }}><fieldset disabled={busy}><h3>Replace image</h3>
      <ImagePicker image={replacementImage} width={imageWidth} height={imageHeight} onChange={setReplacementImage} />
      <label>Image sizing<select aria-label="Image sizing" value={imageFit} onChange={event => setImageFit(event.target.value as 'Fit' | 'Fill' | 'Stretch')}><option value="Fit">Fit - keep whole image</option><option value="Fill">Fill - crop to placement</option><option value="Stretch">Stretch - match placement</option></select></label>
      <p className="control-help">Replaces only this occurrence. Position, rotation and drawing order stay unchanged. Fit leaves transparent space; Fill crops the edges.</p>
      <div className="editor-actions"><button type="submit" className="primary-action" disabled={!replacementImage}><RefreshCw size={15} aria-hidden="true" />{busy ? 'Replacing...' : 'Replace image'}</button></div>
    </fieldset></form>}

    {item.kind !== 'image' && <form className="editor" onSubmit={event => {
      event.preventDefault();
      if (supported && dirty) {
        void onSave({ annotationNumber: details.annotationNumber, kind: richText ? 'rich-text' : details.kind === 'PushButton' ? 'button-caption' : item.kind, value, values: multiSelect ? selectedValues : undefined, textAlignment: item.kind === 'field' && !button && details.kind !== 'PushButton' ? alignment : undefined, pageNumber: item.pageNumber, run: item.kind === 'text' ? runs[runIndex] : undefined, fieldName: details.name });
      }
    }}><fieldset disabled={busy}>
      <div className="section-heading"><h3>{item.kind === 'annotation' ? 'Annotation comment' : item.kind === 'text' ? 'Text content' : details.kind === 'PushButton' ? 'Button caption' : 'Field value'}</h3><span className={dirty ? 'edit-status dirty' : 'edit-status'}>{dirty ? 'Unsaved' : <><Check size={12} aria-hidden="true" /> Saved</>}</span></div>
      {item.kind === 'text' && runs.length > 1 && <label>Text segment<select aria-label="Source run" value={runIndex} onChange={event => {
        const index = Number(event.target.value);
        setRunIndex(index);
        setValue(runs[index].text);
      }}>{runs.map((run, index) => <option key={index} value={index}>{index + 1} of {runs.length}: {run.text || '(empty)'}</option>)}</select><span className="control-help">This line contains multiple editable segments.</span></label>}
      {item.kind === 'text' && <dl className="text-font-details">
        <div><dt>Font name</dt><dd aria-label="Font name">{runs[runIndex]?.fontName || 'Unknown'}</dd></div>
        <div><dt>Font size</dt><dd aria-label="Font size">{Number.isFinite(runs[runIndex]?.fontSize) ? `${runs[runIndex].fontSize} pt` : 'Unknown'}</dd></div>
      </dl>}
      {supported ? <>
        {multiSelect ? <fieldset className="field-selections"><legend>Selections</legend>
          {details.choices?.map((choice, index) => <label key={index}><input type="checkbox" checked={selectedValues.includes(choice.exportValue)} onChange={event => setSelectedValues(event.target.checked ? [...selectedValues, choice.exportValue] : selectedValues.filter(value => value !== choice.exportValue))} />{choice.displayValue}</label>)}
        </fieldset> : <label>{richText ? 'Rich text (XHTML)' : fileSelect ? 'File name / path' : button ? 'Selection' : details.kind === 'PushButton' ? 'Caption' : 'Content'}{button ? <select aria-label="New value" value={value || 'Off'} onChange={event => setValue(event.target.value)}><option value="Off">Off</option>{details.states?.filter(state => state !== 'Off').map(state => <option key={state}>{state}</option>)}</select>
          : password || comb ? <input type={password ? 'password' : 'text'} autoComplete="off" aria-label="New value" maxLength={details.maxLength ?? 10000} value={value} onChange={event => setValue(event.target.value)} /> : details.kind === 'PushButton' ? <input aria-label="New value" maxLength={10000} value={value} onChange={event => setValue(event.target.value)} /> : details.kind === 'Choice' && ((details.flags ?? 0) & (1 << 17)) === 0 ? <select aria-label="New value" value={value} onChange={event => setValue(event.target.value)}><option value="">No selection</option>{details.choices?.map(choice => <option key={choice.exportValue} value={choice.exportValue}>{choice.displayValue}</option>)}</select> : details.kind === 'Choice' ? <input aria-label="New value" maxLength={10000} value={value} list="field-choices" onChange={event => setValue(event.target.value)} />
          : <textarea aria-label="New value" rows={4} maxLength={richText ? 10000 : details.maxLength ?? 10000} value={value} onChange={event => setValue(event.target.value)} />}</label>}
        {details.kind === 'Choice' && <datalist id="field-choices">{details.choices?.map(choice => <option key={choice.exportValue} value={choice.exportValue}>{choice.displayValue}</option>)}</datalist>}
        <p className="control-help">{item.kind === 'annotation' ? 'Saved as the annotation comment in the PDF.' : item.kind === 'text' ? 'Edits the selected segment. Single-line text only.' : richText ? 'XHTML subset: body, p, span, b, i, u, br; Helvetica, pt sizes and #RRGGBB colors.' : fileSelect ? 'Stores a file name or path only. No file content is uploaded or embedded.' : password ? 'Display is masked, but the PDF stores the actual value. This is not encryption.' : comb ? `One character per cell; maximum ${details.maxLength ?? 'unknown'} characters.` : details.kind === 'PushButton' ? 'Updates the caption. This does not add or execute button actions.' : button ? 'Choose the state to display in the PDF.' : multiSelect ? 'Select any number of options. Clear all to remove the selection.' : details.kind === 'Choice' ? 'Choose or enter the option export value.' : ((details.flags ?? 0) & (1 << 12)) !== 0 ? 'Multiline text wraps to the field width. Saved using 12 pt Helvetica.' : 'Single-line text. Saved using 12 pt Helvetica.'}</p>
        {item.kind === 'field' && !button && details.kind !== 'PushButton' && <TextAlignment value={alignment} onChange={setAlignment} />}
        <div className="editor-actions"><button className="primary-action" type="submit" disabled={!dirty}><Save size={15} aria-hidden="true" />{busy ? 'Saving...' : 'Save'}</button><button type="button" disabled={!dirty} onClick={() => {
          setValue(initialValue);
          setSelectedValues(details.values ?? []);
          setAlignment(initialAlignment);
        }}>Reset</button></div>
      </> : <div className="inspector-notice">{details.isReadOnly ? 'This field is read-only.' : details.kind === 'Signature' ? 'Use the digital signature controls below to sign this field.' : 'This selection cannot be edited with the current text editor.'}</div>}
    </fieldset></form>}
    {item.kind === 'field' && !details.isReadOnly && <FormAdvancedControls kind={details.kind} name={details.name!} pageNumber={item.pageNumber} action={details.widget?.action} signed={details.isSigned} busy={busy} onSave={onSave} />}
    {item.kind === 'field' && !details.isReadOnly && <section className="editor inspector-section"><fieldset disabled={busy}><h3>Field identity</h3>
      <label>Field name<input aria-label="Rename field" value={newName} maxLength={10000} onChange={event => setNewName(event.target.value)} /></label>
      <p className="control-help">Rename only this part of the name. Parent groups and the value stay unchanged.</p>
      <button className="secondary-action" type="button" disabled={!newName.trim() || newName === partialName} onClick={() => void onSave({ kind: 'rename-field', value: newName, fieldName: details.name, pageNumber: item.pageNumber })}><Pencil size={14} aria-hidden="true" />Rename</button>
    </fieldset></section>}
    {(canDelete || (item.kind === 'text' && runs.length > 0)) && <section className="remove-section"><h3>Remove object</h3><p className="control-help">{item.kind === 'annotation' ? 'Removes this annotation without changing the underlying text.' : item.kind === 'field' ? 'Removes this field and all its widgets from every page.' : item.kind === 'image' ? 'Removes this placement. Other copies of the image stay in place.' : 'Delete the selected segment or every segment in this line.'}</p>
      {canDelete && <button className="danger-action" type="button" disabled={busy} onClick={remove}><Trash2 size={14} aria-hidden="true" />{item.kind === 'annotation' ? 'Delete annotation' : item.kind === 'text' ? 'Delete text run' : item.kind === 'image' ? 'Delete image occurrence' : 'Delete field'}</button>}
      {item.kind === 'text' && runs.length > 0 && <button className="danger-action" type="button" disabled={busy} onClick={() => void onSave({ kind: 'delete-text-line', value: '', pageNumber: item.pageNumber, runs })}><Trash2 size={14} aria-hidden="true" />Delete whole line</button>}
    </section>}
  </div>;
}
