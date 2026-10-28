import { Plus } from 'lucide-react';
import type { Bounds } from './Placement';
import type { Edit } from './ObjectEditor';

export function AnnotationDraft({ kind, value, setValue, pageNumber, bounds, onChange, busy, onSave, onCancel }: {
  kind: string; value: string; setValue: (value: string) => void; pageNumber: number; bounds: Bounds;
  onChange: (bounds: Bounds) => void; busy: boolean; onSave: (edit: Edit) => Promise<void>; onCancel: () => void;
}) {
  const label = 'Comment';
  return <form className="editor" onSubmit={event => {
    event.preventDefault();
    void onSave({ kind, value, pageNumber, ...bounds });
  }}><fieldset disabled={busy}><legend>{label} · page {pageNumber}</legend>
    <label>Comment<textarea aria-label="Annotation comment" rows={4} maxLength={10000} required placeholder="Add a note…" value={value} onChange={event => setValue(event.target.value)} /></label>
    <p className="control-help">Click the page to place the comment. Drag to move or resize its icon. To highlight, underline or strike through text, select text lines and use the Inspector actions.</p>
    <div className="placement-inputs">{(['x', 'y', 'width', 'height'] as const).map(key => <label key={key}>{key}<input aria-label={`Annotation ${key}`} type="number" step="any" min={key === 'width' || key === 'height' ? .1 : undefined} required value={bounds[key]} onChange={event => onChange({ ...bounds, [key]: Number(event.target.value) })} /></label>)}</div>
    <div className="editor-actions"><button type="submit" className="primary-action"><Plus size={16} aria-hidden="true" />{busy ? 'Saving…' : 'Add annotation'}</button><button type="button" onClick={onCancel}>Cancel</button></div>
  </fieldset></form>;
}
