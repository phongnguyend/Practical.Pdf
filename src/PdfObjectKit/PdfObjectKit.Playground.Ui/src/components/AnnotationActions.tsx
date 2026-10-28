import { Highlighter, Underline, Strikethrough } from 'lucide-react';
import './AnnotationActions.css';

export function AnnotationActions({ label, disabled, onApply }: {
  label: string; disabled: boolean; onApply: (kind: string) => void;
}) {
  return <section className="markup-tools" aria-label={label}>
    <div className="markup-tools-heading"><h3>Text markup</h3><span>Apply to selection</span></div>
    <div className="markup-tools-buttons" role="group" aria-label={label}>
      {([
        ['highlight', 'Highlight', Highlighter],
        ['underline', 'Underline', Underline],
        ['strikeout', 'Strikethrough', Strikethrough],
      ] as const).map(([kind, name, Icon]) => <button key={kind} type="button" aria-label={name}
        title={`${name} selected text`} disabled={disabled}
        onPointerDown={event => event.preventDefault()} onClick={() => onApply(kind)}>
        <Icon size={19} strokeWidth={1.8} aria-hidden="true" /><span>{name}</span>
      </button>)}
    </div>
  </section>;
}
