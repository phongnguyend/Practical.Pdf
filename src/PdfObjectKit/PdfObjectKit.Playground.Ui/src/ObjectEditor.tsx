import { useState } from 'react';

export type Edit = { kind: string; value: string; pageNumber: number; run?: unknown; fieldName?: string; imageNumber?: number; image?: File; x?: number; y?: number; width?: number; height?: number; fontSize?: number };
type Run = { text: string; formOccurrenceId?: number | null };
type Details = { imageNumber?: number; runs?: Run[]; name?: string; kind?: string; values?: string[]; isReadOnly?: boolean; choices?: { exportValue: string; displayValue: string }[]; states?: string[] };

export function ObjectEditor({ item, busy, onSave }: { item: { kind: string; pageNumber: number; details: unknown }; busy: boolean; onSave: (edit: Edit) => Promise<void> }) {
  const details = item.details as Details;
  const runs = details.runs ?? [];
  const [runIndex, setRunIndex] = useState(0);
  const [value, setValue] = useState(item.kind === 'text' ? runs[0]?.text ?? '' : details.values?.[0] ?? (['CheckBox', 'RadioButton'].includes(details.kind ?? '') ? 'Off' : ''));
  if (item.kind === 'image') {
    return <button disabled={busy} onClick={() => void onSave({ kind: 'delete-image', value: '', pageNumber: item.pageNumber, imageNumber: details.imageNumber })}>Delete image occurrence</button>;
  }
  const supported = item.kind === 'text'
    ? runs.length > 0 && runs[runIndex]?.formOccurrenceId == null
    : !details.isReadOnly && ['Text', 'Choice', 'CheckBox', 'RadioButton'].includes(details.kind ?? '');
  const button = details.kind === 'CheckBox' || details.kind === 'RadioButton';
  return <form className="editor" onSubmit={event => {
    event.preventDefault();
    void onSave({ kind: item.kind, value, pageNumber: item.pageNumber, run: item.kind === 'text' ? runs[runIndex] : undefined, fieldName: details.name });
  }}>
    <fieldset disabled={busy}>
      <legend>{item.kind === 'text' ? 'Edit text run' : 'Fill field'}</legend>
      {item.kind === 'text' && <label>Source run<select aria-label="Source run" value={runIndex} onChange={event => {
        const index = Number(event.target.value);
        setRunIndex(index);
        setValue(runs[index].text);
      }}>{runs.map((run, index) => <option key={index} value={index}>{index + 1}: {run.text || '(empty)'}</option>)}</select></label>}
      {supported ? <>
        <label>New value{button ? <select aria-label="New value" value={value || 'Off'} onChange={event => setValue(event.target.value)}><option value="Off">Off</option>{details.states?.filter(state => state !== 'Off').map(state => <option key={state}>{state}</option>)}</select>
          : <input aria-label="New value" maxLength={10000} value={value} list={details.kind === 'Choice' ? 'field-choices' : undefined} onChange={event => setValue(event.target.value)} />}</label>
        {details.kind === 'Choice' && <datalist id="field-choices">{details.choices?.map(choice => <option key={choice.exportValue} value={choice.exportValue}>{choice.displayValue}</option>)}</datalist>}
        <p className="hint">{item.kind === 'text' ? 'Changes this run only. Existing font restrictions apply.' : 'Text fields use plain 12pt Helvetica. Unsupported field features are rejected.'}</p>
        <button type="submit">{busy ? 'Saving…' : 'Save'}</button>
        {item.kind === 'text' && <button type="button" onClick={() => void onSave({ kind: 'text', value: '', pageNumber: item.pageNumber, run: runs[runIndex] })}>Delete text run</button>}
      </> : <p className="hint">This selection is read-only or is not supported by the current editor.</p>}
      {item.kind === 'field' && !details.isReadOnly && <><p className="hint">Deleting a field removes all its widgets from all pages.</p><button type="button" onClick={() => void onSave({ kind: 'delete-field', value: '', pageNumber: item.pageNumber, fieldName: details.name })}>Delete field</button></>}
    </fieldset>
  </form>;
}
