import { Layers, Trash2, X } from 'lucide-react';
import { ObjectTypeIcon } from './ObjectTypeIcon';
import './MultiSelectionSummary.css';

export function MultiSelectionSummary({ items, pageNumber, busy, canDelete, onDelete, onClear }: {
  items: { kind: string }[];
  pageNumber: number;
  busy: boolean;
  canDelete: boolean;
  onDelete: () => void;
  onClear: () => void;
}) {
  const groups = [
    { kind: 'text', label: 'Text lines' },
    { kind: 'image', label: 'Images' },
    { kind: 'field', label: 'Form widgets' },
  ].map(group => ({ ...group, count: items.filter(item => item.kind === group.kind).length }));
  return <section className="multi-selection">
    <div className="multi-selection-heading">
      <span className="multi-selection-icon"><Layers size={20} aria-hidden="true" /></span>
      <div><h3>{items.length} objects selected</h3><span>Page {pageNumber}</span></div>
      <button type="button" className="icon-button" aria-label="Clear selection" title="Clear selection" disabled={busy} onClick={onClear}><X size={16} aria-hidden="true" /></button>
    </div>
    <div className="multi-selection-counts">{groups.filter(group => group.count > 0).map(group =>
      <div key={group.kind}><ObjectTypeIcon kind={`add-${group.kind}`} /><span>{group.label}</span><strong>{group.count}</strong></div>
    )}</div>
    <p className="control-help">Hold <kbd>Ctrl</kbd> / <kbd>Cmd</kbd> and click to add or remove objects.</p>
    <div className="multi-selection-actions">
      <button type="button" className="danger-action" disabled={busy || !canDelete} onClick={onDelete}><Trash2 size={16} aria-hidden="true" />{busy ? 'Saving…' : 'Delete selected objects'}</button>
      {!canDelete ? <p className="control-help">This selection contains objects that cannot be deleted. Deselect them to continue.</p> : <p className="control-help">Shortcut: <kbd>Delete</kbd>{groups.some(group => group.kind === 'field' && group.count > 0) && <span className="multi-selection-field-note">Deleting a form field removes all its widgets.</span>}</p>}
    </div>
  </section>;
}
