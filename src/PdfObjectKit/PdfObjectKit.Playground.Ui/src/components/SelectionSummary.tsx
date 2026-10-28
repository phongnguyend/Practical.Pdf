import { X, Move } from 'lucide-react';
import { ObjectTypeIcon } from './ObjectTypeIcon';

export function SelectionSummary({ item, disabled, onClose }: { item: { kind: string; label: string; pageNumber: number; corners: { x: number; y: number }[]; details: unknown }; disabled: boolean; onClose: () => void }) {
  const details = item.details as { pixelWidth?: number; pixelHeight?: number; kind?: string; isReadOnly?: boolean };
  const width = Math.max(...item.corners.map(p => p.x)) - Math.min(...item.corners.map(p => p.x));
  const height = Math.max(...item.corners.map(p => p.y)) - Math.min(...item.corners.map(p => p.y));
  return <section className="selection-summary">
    <div className="selection-heading"><span className={`selection-icon ${item.kind}`}><ObjectTypeIcon kind={`add-${item.kind}`} /></span><div><strong>{item.kind === 'annotation' ? details.kind ?? 'Annotation' : item.kind === 'field' ? 'Form field' : item.kind === 'image' ? 'Image' : 'Text'}</strong><span>Page {item.pageNumber}{details.isReadOnly ? ' · Read-only' : ''}</span></div><button className="icon-button" aria-label="Clear selection" disabled={disabled} onClick={onClose}><X size={16} aria-hidden="true" /></button></div>
    <p className="selection-label" title={item.label}>{item.label || '(empty)'}</p>
    <div className="selection-meta"><span>{Number.isFinite(width) && Number.isFinite(height) ? `${width.toFixed(1)} × ${height.toFixed(1)} pt` : 'Bounds unavailable'}</span>{item.kind === 'image' && <span>{details.pixelWidth} × {details.pixelHeight} px</span>}{item.kind === 'field' && <span>{details.kind}</span>}</div>
    {(['image', 'field', 'text'].includes(item.kind)) && !details.isReadOnly && <p className="selection-move-hint" title="Drag the selected outline to move. Release to save; Escape cancels."><Move size={13} aria-hidden="true" />Drag to move; release to save</p>}
  </section>;
}
