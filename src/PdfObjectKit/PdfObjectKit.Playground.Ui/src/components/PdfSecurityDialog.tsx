import { useState } from 'react';
import { LockKeyhole, Download, X } from 'lucide-react';
import { Modal } from './Modal';
import './PageAssemblyDialog.css';

export function PdfSecurityDialog({ file, unlock, onClose, onUnlocked }: {
  file: File; unlock: boolean; onClose: () => void; onUnlocked: (file: File) => Promise<void>;
}) {
  const [password, setPassword] = useState('');
  const [owner, setOwner] = useState('');
  const [permissions, setPermissions] = useState({ allowPrinting: true, allowHighQualityPrinting: true, allowCopying: true, allowModifying: true, allowAnnotations: true, allowFormFilling: true, allowDocumentAssembly: true });
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  async function submit() {
    setBusy(true);
    setError('');
    try {
      const body = new FormData();
      body.append('file', file);
      if (unlock) {
        body.append('password', password);
      } else {
        body.append('options', JSON.stringify({ ...permissions, userPassword: password, ownerPassword: owner }));
      }
      const response = await fetch('/api/documents/security/' + (unlock ? 'unlock' : 'encrypt'), { method: 'POST', body });
      if (!response.ok) {
        throw new Error((await response.json()).detail ?? 'Security operation failed.');
      }
      const result = await response.blob();
      if (unlock) {
        await onUnlocked(new File([result], file.name, { type: 'application/pdf' }));
      } else {
        const url = URL.createObjectURL(result);
        const link = document.createElement('a');
        link.href = url;
        link.download = file.name.replace(/\.pdf$/i, '') + '-protected.pdf';
        link.click();
        setTimeout(() => URL.revokeObjectURL(url), 1000);
      }
      setPassword('');
      setOwner('');
      onClose();
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : 'Security operation failed.');
    } finally {
      setBusy(false);
    }
  }
  return <Modal labelledBy="security-title" className="page-assembly-dialog" onClose={onClose} canClose={!busy} initialFocus="#pdf-password">
    <form onSubmit={event => { event.preventDefault(); void submit(); }}>
      <div className="assembly-heading"><div><LockKeyhole size={22} aria-hidden="true" /><h2 id="security-title">{unlock ? 'Unlock PDF' : 'Protect PDF download'}</h2></div><button type="button" aria-label="Close security dialog" disabled={busy} onClick={onClose}><X size={18} aria-hidden="true" /></button></div>
      <p className="assembly-document">{file.name}</p>
      <p className="control-help">{unlock ? 'Enter the PDF password. Restricted documents require the owner password. This opens a decrypted working copy; use Protect PDF when downloading to apply encryption again.' : 'AES-256 encryption. The working tab stays unencrypted. Permissions are requests enforced by supporting PDF viewers.'}</p>
      <label className="assembly-pages">{unlock ? 'PDF password' : 'Open password (optional)'}<input id="pdf-password" type="password" autoComplete="off" maxLength={127} disabled={busy} value={password} onChange={event => setPassword(event.target.value)} /></label>
      {!unlock && <><label className="assembly-pages">Owner password<input aria-label="Owner password" type="password" autoComplete="new-password" required maxLength={127} disabled={busy} value={owner} onChange={event => setOwner(event.target.value)} /><span>Use a different password to manage document permissions. Store it somewhere safe.</span></label>
        <fieldset disabled={busy}><legend>Allow recipients to</legend>{(Object.entries({ allowPrinting: 'Print', allowHighQualityPrinting: 'Print at high quality', allowCopying: 'Copy text and images', allowModifying: 'Modify content', allowAnnotations: 'Add annotations', allowFormFilling: 'Fill form fields', allowDocumentAssembly: 'Assemble pages' }) as [keyof typeof permissions, string][]).map(([key, label]) => <label key={key} style={{ display: 'flex', gap: 8, margin: '8px 0' }}><input type="checkbox" checked={permissions[key]} onChange={event => setPermissions({ ...permissions, [key]: event.target.checked })} />{label}</label>)}</fieldset></>}
      {error && <p className="assembly-error" role="alert">{error}</p>}
      <div className="assembly-actions"><button type="button" disabled={busy} onClick={onClose}>Cancel</button><button type="submit" className="primary-action" disabled={busy || (!unlock && (!owner || owner === password))}>{unlock ? <LockKeyhole size={16} /> : <Download size={16} />}{busy ? 'Processing...' : unlock ? 'Unlock and open' : 'Download protected PDF'}</button></div>
    </form>
  </Modal>;
}
