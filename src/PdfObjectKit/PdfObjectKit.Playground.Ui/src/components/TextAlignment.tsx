import { AlignLeft, AlignCenter, AlignRight } from 'lucide-react';

export type Alignment = 'Left' | 'Center' | 'Right';
export function TextAlignment({ value, onChange }: { value: Alignment; onChange: (value: Alignment) => void }) {
  return <div className="alignment-control"><span>Text alignment</span><div role="group" aria-label="Text alignment">{([['Left', AlignLeft], ['Center', AlignCenter], ['Right', AlignRight]] as const).map(([alignment, Icon]) => <button type="button" key={alignment} aria-label={`Align ${alignment.toLowerCase()}`} aria-pressed={value === alignment} onClick={() => onChange(alignment)}><Icon size={16} aria-hidden="true" />{alignment}</button>)}</div></div>;
}
