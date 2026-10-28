import { ButtonActionEditor } from './ButtonActionEditor';
import { useRef, useState } from 'react';
import { FileKey, FolderOpen } from 'lucide-react';
import type { Edit, ButtonAction } from './ObjectEditor';
import './FileNamePicker.css';

export function FormAdvancedControls({ kind, name, pageNumber, action, signed, busy, onSave }: { kind?: string; name: string; pageNumber: number; action?: ButtonAction; signed?: boolean; busy: boolean; onSave: (edit: Edit) => Promise<void> }) {
  const [certificate, setCertificate] = useState<File>();
  const certificateInput = useRef<HTMLInputElement>(null);
  const [password, setPassword] = useState('');
  const [reason, setReason] = useState('');
  if (kind === 'PushButton') {
    return <ButtonActionEditor name={name} pageNumber={pageNumber} action={action} busy={busy} onSave={onSave} />;
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
