import { Image, TextCursorInput, Type } from 'lucide-react';

export function ObjectTypeIcon({ kind }: { kind: string }) {
  const Icon = kind === 'add-image' ? Image : kind === 'add-field' ? TextCursorInput : Type;
  return <Icon className="object-type-icon" size={20} strokeWidth={1.7} aria-hidden="true" focusable="false" />;
}
