import { TextCursorInput, AlignLeft, Bold, FolderOpen, LockKeyhole, Columns3, List, ListChecks, SquareCheck, CircleDot, ChevronDown, MousePointerClick, Signature } from 'lucide-react';

export const formFieldTypes = [
  { type: 'Text', label: 'Text field', icon: TextCursorInput },
  { type: 'MultilineText', label: 'Multiline text', icon: AlignLeft },
  { type: 'CheckBox', label: 'Checkbox', icon: SquareCheck },
  { type: 'RadioButton', label: 'Radio group', icon: CircleDot },
  { type: 'Choice', label: 'Dropdown', icon: ChevronDown },
  { type: 'ListBox', label: 'List box', icon: List },
  { type: 'MultiSelectListBox', label: 'Multi-select list', icon: ListChecks },
  { type: 'PushButton', label: 'Pushbutton', icon: MousePointerClick },
  { type: 'Signature', label: 'Signature field', icon: Signature },
  { type: 'RichText', label: 'Rich text', icon: Bold },
  { type: 'FileSelect', label: 'File select', icon: FolderOpen },
  { type: 'Password', label: 'Password', icon: LockKeyhole },
  { type: 'Comb', label: 'Comb field', icon: Columns3 },
] as const;

export type FormFieldType = typeof formFieldTypes[number]['type'];

export function fieldDefaults(type: FormFieldType) {
  return {
    width: type === 'CheckBox' ? 20 : 150,
    height: type === 'CheckBox' ? 20 : ['RichText', 'MultilineText', 'ListBox', 'MultiSelectListBox'].includes(type) ? 80 : type === 'RadioButton' ? 48 : 30,
    value: type === 'CheckBox' || type === 'RadioButton' ? 'Off' : type === 'PushButton' ? 'Button' : type === 'RichText' ? '<body><p>Rich <b>text</b></p></body>' : '',
  };
}
