import { useRef, useState } from 'react';
import { FileKey, FolderOpen } from 'lucide-react';
import type { Edit, ButtonAction } from './ObjectEditor';
import './FileNamePicker.css';

export function FormAdvancedControls({ kind, name, pageNumber, action, signed, busy, onSave }: { kind?: string; name: string; pageNumber: number; action?: ButtonAction; signed?: boolean; busy: boolean; onSave: (edit: Edit) => Promise<void> }) {
  const [actionKind, setActionKind] = useState<ButtonAction['kind'] | 'None'>(action?.kind ?? 'None');
  const [url, setUrl] = useState(action?.url ?? '');
  const [destination, setDestination] = useState(action?.pageNumber ?? 1);
  const [fields, setFields] = useState(action?.fieldNames?.join('\n') ?? '');
  const [certificate, setCertificate] = useState<File>();
  const certificateInput = useRef<HTMLInputElement>(null);
  const [password, setPassword] = useState('');
  const [reason, setReason] = useState('');
  if (kind === 'PushButton') {
    return <form className="editor inspector-section" onSubmit={event => {
      event.preventDefault();
      void onSave({ kind: 'button-action', value: '', fieldName: name, pageNumber, action: actionKind === 'None' ? null : {
        kind: actionKind, url: ['OpenUrl', 'SubmitForm'].includes(actionKind) ? url : undefined,
        pageNumber: destination, fieldNames: ['ResetForm', 'SubmitForm'].includes(actionKind) && fields.trim() ? fields.split('\n').map(name => name.trim()).filter(Boolean) : undefined,
      } });
    }}><fieldset disabled={busy}><legend>Button action</legend>
      <label>Action<select aria-label="Button action" value={actionKind} onChange={event => setActionKind(event.target.value as typeof actionKind)}>
        <option value="None">None</option><option value="OpenUrl">Open URL</option><option value="GoToPage">Go to page</option><option value="ResetForm">Reset form</option><option value="SubmitForm">Submit form (XFDF)</option>
      </select></label>
      {['OpenUrl', 'SubmitForm'].includes(actionKind) && <label>URL<input aria-label="Action URL" type="url" required value={url} onChange={event => setUrl(event.target.value)} /></label>}
      {actionKind === 'GoToPage' && <label>Page<input aria-label="Action page" type="number" min={1} required value={destination} onChange={event => setDestination(Number(event.target.value))} /></label>}
      {['ResetForm', 'SubmitForm'].includes(actionKind) && <label>Field names (one per line; empty means all)<textarea aria-label="Action fields" value={fields} onChange={event => setFields(event.target.value)} /></label>}
      <p className="control-help">Stored in the PDF for supporting viewers. The playground does not execute actions or submit data.</p>
      <button type="submit">Save action</button>
    </fieldset></form>;
  }
  if (kind !== 'Signature') {
    return null;
  }
  if (signed) {
    return <p className="inspector-notice">A signature is present. Use a PDF signature validator to check integrity and certificate trust. Further editing is disabled by the API.</p>;
  }
  return <form className="editor inspector-section" onSubmit={event => {
    event.preventDefault();
    if (certificate) {
      void onSave({ kind: 'sign-field', value: reason, fieldName: name, pageNumber, certificate, certificatePassword: password }).finally(() => {
        setPassword('');
        setCertificate(undefined);
      });
    }
  }}><fieldset disabled={busy}><legend>Digital signature</legend>
    <div className="file-name-picker">
      <span className="control-help">PFX/P12 certificate</span>
      <input ref={certificateInput} hidden aria-label="Signing certificate" type="file" accept=".pfx,.p12" onChange={event => {
        const file = event.target.files?.[0];
        if (file) {
          setCertificate(file);
        }
        event.target.value = '';
      }} />
      <button className="file-name-choose" type="button" onClick={() => certificateInput.current?.click()}>
        <FolderOpen size={18} aria-hidden="true" />
        <span>{certificate ? 'Choose another certificate' : 'Choose a certificate'}</span>
      </button>
      {certificate && <div className="file-name-selected"><FileKey size={16} aria-hidden="true" /><span title={certificate.name}>{certificate.name}</span></div>}
      <p className="control-help">Select a .pfx or .p12 file containing your signing certificate and private key.</p>
    </div>
    <label>Certificate password<input aria-label="Certificate password" type="password" autoComplete="off" value={password} onChange={event => setPassword(event.target.value)} /></label>
    <label>Reason (optional)<input aria-label="Signing reason" value={reason} onChange={event => setReason(event.target.value)} /></label>
    <p className="control-help">Sends the certificate to the local API for this operation. Creates a first RSA/SHA-256 approval signature. Finish editing before signing. No timestamp or long-term validation data is added.</p>
    <button type="submit" disabled={!certificate}>Sign PDF</button>
  </fieldset></form>;
}
