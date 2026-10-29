import { formatMetadataDate, metadataDateToIso } from '../metadataDates.mjs';
import { useState } from 'react';
import { FilePenLine, Save, X } from 'lucide-react';
import { Modal } from './Modal';
import type { Edit } from './ObjectEditor';
import './PageAssemblyDialog.css';
import './DocumentPropertiesDialog.css';

export type Metadata = { values: Record<string, string>; warnings: string[] };
const fields = { Title: 'Title', Author: 'Author', Subject: 'Subject', Keywords: 'Keywords', Creator: 'Creator application', Producer: 'PDF producer', CreationDate: 'Creation date', ModDate: 'Modification date' };
export function DocumentPropertiesDialog({ metadata, name, pageCount, pdfVersion, page, onClose, onSave }: { metadata: Metadata; name: string; pageCount: number; pdfVersion: string; page: number; onClose: () => void; onSave: (edit: Edit) => Promise<void> }) {
  const [error, setError] = useState('');
  const originalValue = (key: string) => key.endsWith('Date') ? formatMetadataDate(metadata.values[key] ?? '') : metadata.values[key] ?? '';
  const [changes, setChanges] = useState<Record<string, string | null>>({});
  return <Modal labelledBy="properties-title" className="page-assembly-dialog" onClose={onClose} initialFocus="#metadata-Title">
    <form onSubmit={event => {
      event.preventDefault();
      try {
        const fields = { ...changes };
        for (const key of ['CreationDate', 'ModDate']) {
          if (typeof fields[key] === 'string') {
            fields[key] = metadataDateToIso(fields[key]);
          }
        }
        onClose();
        void onSave({ kind: 'metadata', pageNumber: page, value: '', metadata: { fields } });
      } catch (cause) {
        setError(cause instanceof Error ? cause.message : 'Invalid date.');
      }
    }}>
      <div className="assembly-heading"><div><FilePenLine size={22} aria-hidden="true" /><h2 id="properties-title">Document properties</h2></div><button type="button" aria-label="Close document properties" onClick={onClose}><X size={18} /></button></div>
      <p className="assembly-document">{name} &middot; {pageCount} pages &middot; PDF {pdfVersion}</p>
      <p className="control-help">Only changed fields are saved. Clearing a field removes it. Modification date updates automatically unless you set or clear it.</p>
      <div className="metadata-fields">{Object.entries(fields).map(([key, label]) => <label key={key} htmlFor={'metadata-' + key}>{label}<input id={'metadata-' + key} maxLength={10000} value={key in changes ? changes[key] ?? '' : originalValue(key)} onChange={event => {
        const next = { ...changes };
        if (event.target.value === (originalValue(key))) {
          delete next[key];
        } else {
          next[key] = event.target.value || null;
        }
        setChanges(next);
      }} /></label>)}</div>
      <p className="control-help">Dates keep their original timezone. Example: 2026-04-02 11:44:20 +07:00. Incomplete dates stay unchanged unless edited.</p>
      {error && <p className="assembly-error" role="alert">{error}</p>}
      {metadata.warnings.map((warning, index) => <p className="assembly-error" key={index}>{warning}</p>)}
      <div className="assembly-actions"><button type="button" onClick={onClose}>Cancel</button><button type="submit" className="primary-action" disabled={!Object.keys(changes).length}><Save size={16} />Save properties</button></div>
    </form>
  </Modal>;
}
