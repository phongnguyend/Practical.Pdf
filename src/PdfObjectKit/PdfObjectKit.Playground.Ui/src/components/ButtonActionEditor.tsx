import { useId, useState } from 'react';
import { Ban, ExternalLink, FileDown, RotateCcw, Send, Save, Check, Info } from 'lucide-react';
import type { ButtonAction, Edit } from './ObjectEditor';
import './ButtonActionEditor.css';

const choices = [
  { kind: 'None', label: 'No action', icon: Ban, help: 'The button has no assigned action.' },
  { kind: 'OpenUrl', label: 'Open URL', icon: ExternalLink, help: 'Open a web address when the PDF button is clicked.' },
  { kind: 'GoToPage', label: 'Go to page', icon: FileDown, help: 'Jump to a page in this document.' },
  { kind: 'ResetForm', label: 'Reset form', icon: RotateCcw, help: 'Restore the selected fields to their default values.' },
  { kind: 'SubmitForm', label: 'Submit form', icon: Send, help: 'Send field values as XFDF to the specified endpoint.' },
] as const;

export function ButtonActionEditor({ name, pageNumber, action, busy, onSave }: {
  name: string; pageNumber: number; action?: ButtonAction; busy: boolean; onSave: (edit: Edit) => Promise<void>;
}) {
  const id = useId();
  const [kind, setKind] = useState<ButtonAction['kind'] | 'None'>(action?.kind ?? 'None');
  const [url, setUrl] = useState(action?.url ?? '');
  const [destination, setDestination] = useState(action?.pageNumber ?? 1);
  const [fields, setFields] = useState(action?.fieldNames?.join('\n') ?? '');
  const names = fields.split('\n').map(value => value.trim()).filter(Boolean);
  const usesUrl = kind === 'OpenUrl' || kind === 'SubmitForm';
  const usesFields = kind === 'ResetForm' || kind === 'SubmitForm';
  const dirty = kind !== (action?.kind ?? 'None') || (usesUrl && url.trim() !== (action?.url ?? ''))
    || (kind === 'GoToPage' && destination !== (action?.pageNumber ?? 1))
    || (usesFields && JSON.stringify(names) !== JSON.stringify(action?.fieldNames ?? []));
  function reset() {
    setKind(action?.kind ?? 'None');
    setUrl(action?.url ?? '');
    setDestination(action?.pageNumber ?? 1);
    setFields(action?.fieldNames?.join('\n') ?? '');
  }
  return <form className="editor inspector-section button-action-editor" onSubmit={event => {
    event.preventDefault();
    if (!dirty || busy) {
      return;
    }
    void onSave({ kind: 'button-action', value: '', fieldName: name, pageNumber, action: kind === 'None' ? null : {
      kind, url: usesUrl ? url.trim() : undefined,
      pageNumber: kind === 'GoToPage' ? destination : undefined,
      fieldNames: usesFields && names.length ? names : undefined,
    } });
  }}><fieldset disabled={busy}>
    <div className="section-heading"><h3>Button action</h3><span className={dirty ? 'edit-status dirty' : 'edit-status'}>{dirty ? 'Unsaved' : <><Check size={12} aria-hidden="true" />Saved</>}</span></div>
    <div className="button-action-choices" role="radiogroup" aria-label="Button action">
      {choices.map(({ kind: value, label, icon: Icon }) => <label key={value} className="button-action-choice" title={label}>
        <input type="radio" name={id} value={value} checked={kind === value} onChange={() => setKind(value)} />
        <span><Icon size={16} aria-hidden="true" /><span>{label}</span><Check size={12} className="action-choice-check" aria-hidden="true" /></span>
      </label>)}
    </div>
    <p className="action-description">{choices.find(choice => choice.kind === kind)?.help}</p>
    {usesUrl && <label>{kind === 'SubmitForm' ? 'Submission URL' : 'Web address'}<input aria-label="Action URL" type="url" required placeholder="https://example.com/" value={url} onChange={event => setUrl(event.target.value)} /></label>}
    {kind === 'GoToPage' && <label>Destination page<input aria-label="Action page" type="number" min={1} step={1} required value={destination} onChange={event => setDestination(Number(event.target.value))} /></label>}
    {usesFields && <label>Fields (optional)<textarea aria-label="Action fields" rows={3} placeholder="All fields, or enter one name per line" value={fields} onChange={event => setFields(event.target.value)} /><span className="control-help">Leave empty to include every form field.</span></label>}
    <div className="action-viewer-note"><Info size={14} aria-hidden="true" /><span>Runs in supporting PDF viewers. This playground only saves the action.</span></div>
    <div className="editor-actions"><button type="submit" className="primary-action" disabled={!dirty}><Save size={15} aria-hidden="true" />{busy ? 'Saving...' : 'Save action'}</button><button type="button" disabled={!dirty} onClick={reset} title="Discard action changes"><RotateCcw size={14} aria-hidden="true" />Reset</button></div>
  </fieldset></form>;
}
