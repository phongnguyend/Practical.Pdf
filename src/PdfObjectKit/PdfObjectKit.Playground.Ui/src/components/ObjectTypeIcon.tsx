import { Image, TextCursorInput, Type, MessageSquare, Highlighter, Underline, Strikethrough } from 'lucide-react';

export function ObjectTypeIcon({ kind }: { kind: string }) {
  const Icon = kind === 'add-comment' || kind === 'add-annotation' ? MessageSquare : kind === 'add-highlight' ? Highlighter : kind === 'add-underline' ? Underline : kind === 'add-strikeout' ? Strikethrough : kind === 'add-image' ? Image : kind === 'add-field' ? TextCursorInput : Type;
  return <Icon className="object-type-icon" size={20} strokeWidth={1.7} aria-hidden="true" focusable="false" />;
}
