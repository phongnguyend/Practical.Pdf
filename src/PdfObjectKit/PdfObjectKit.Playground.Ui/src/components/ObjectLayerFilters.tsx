import { Type, Image, TextCursorInput, MessageSquare } from 'lucide-react';
import './ObjectLayerFilters.css';

type Layer = 'text' | 'image' | 'field' | 'annotation';

export function ObjectLayerFilters({ layers, onToggle }: {
  layers: Record<Layer, boolean>;
  onToggle: (layer: Layer) => void;
}) {
  return <div className="object-layer-filters" role="group" aria-label="Object visibility">
    {([
      ['text', 'Text', Type],
      ['image', 'Images', Image],
      ['field', 'Fields', TextCursorInput],
      ['annotation', 'Annotations', MessageSquare],
    ] as const).map(([kind, label, Icon]) => <button key={kind} type="button"
      aria-label={`${label} visibility`} aria-pressed={layers[kind]}
      title={`${layers[kind] ? 'Hide' : 'Show'} ${label.toLowerCase()} in the object list and selection outlines`}
      onClick={() => onToggle(kind)}>
      <Icon size={14} strokeWidth={1.8} aria-hidden="true" /><span>{label}</span>
    </button>)}
  </div>;
}
