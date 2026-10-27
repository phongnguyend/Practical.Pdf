import { useRef } from 'react';
import { File, FolderOpen } from 'lucide-react';
import './FileNamePicker.css';

export function FileNamePicker({ value, onChange }: { value: string; onChange: (value: string) => void }) {
  const input = useRef<HTMLInputElement>(null);
  return <div className="file-name-picker">
    <input ref={input} hidden aria-label="Choose file name" type="file" onChange={event => {
      const file = event.target.files?.[0];
      if (file) {
        onChange(file.name);
      }
      event.target.value = '';
    }} />
    <button className="file-name-choose" type="button" onClick={() => input.current?.click()}>
      <FolderOpen size={18} aria-hidden="true" />
      <span>{value ? 'Choose another file' : 'Choose a file'}</span>
    </button>
    {value && <div className="file-name-selected"><File size={16} aria-hidden="true" /><span title={value}>{value}</span></div>}
    <p className="control-help">Uses the file name only. The file itself is not uploaded or embedded.</p>
  </div>;
}
