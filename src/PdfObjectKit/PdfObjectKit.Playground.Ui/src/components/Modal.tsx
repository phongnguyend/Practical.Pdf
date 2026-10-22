import { useEffect, useRef, type ReactNode } from 'react';
import { createPortal } from 'react-dom';
import './Modal.css';

export function Modal({ children, className, labelledBy, onClose, canClose = true }: { children: ReactNode; className?: string; labelledBy: string; onClose: () => void; canClose?: boolean }) {
  const dialog = useRef<HTMLDialogElement>(null);
  useEffect(() => {
    const previous = document.activeElement as HTMLElement | null;
    const element = dialog.current;
    element?.showModal();
    return () => {
      element?.close();
      previous?.focus();
    };
  }, []);
  return createPortal(<dialog ref={dialog} className={`modal ${className ?? ''}`} aria-labelledby={labelledBy} onCancel={event => {
    event.preventDefault();
    if (canClose) {
      onClose();
    }
  }} onKeyDown={event => {
    if (event.key === 'Escape') {
      event.stopPropagation();
    }
  }}>{children}</dialog>, document.body);
}
