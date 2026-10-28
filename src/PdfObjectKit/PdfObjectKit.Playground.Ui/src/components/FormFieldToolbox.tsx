import { useId, useState } from 'react';
import { ChevronDown, TextCursorInput } from 'lucide-react';
import { formFieldTypes, type FormFieldType } from './formFieldTypes';
import './FormFieldToolbox.css';

export function FormFieldToolbox({ disabled, activeType, onChoose }: {
  disabled: boolean; activeType?: FormFieldType; onChoose: (type: FormFieldType) => void;
}) {
  const [expanded, setExpanded] = useState(false);
  const id = useId();
  return <div className="form-field-toolbox">
    <button type="button" className="toolbox-item field-toolbox-toggle" aria-expanded={expanded} aria-controls={id}
      onClick={() => setExpanded(value => !value)}>
      <TextCursorInput size={20} aria-hidden="true" /><span>Form field</span><ChevronDown size={16} className={expanded ? 'expanded' : ''} aria-hidden="true" />
    </button>
    {expanded && <div id={id} className="field-type-tools" role="group" aria-label="Form field types">
      <p>Drag a field onto the page, or click to place.</p>
      {formFieldTypes.map(({ type, label, icon: Icon }) => <button key={type} type="button" data-field-type={type}
        disabled={disabled} draggable={!disabled} aria-pressed={activeType === type} title={`Add ${label.toLowerCase()}`}
        onClick={() => onChoose(type)} onDragStart={event => {
          event.dataTransfer.setData('application/x-pdfobjectkit-type', `add-field:${type}`);
          event.dataTransfer.effectAllowed = 'copy';
        }}><Icon size={16} aria-hidden="true" /><span>{label}</span></button>)}
    </div>}
  </div>;
}
