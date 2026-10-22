import './Toast.css';

export function Toast({ message }: { message: string }) {
  return <div className="toast" role="status" aria-live="polite" aria-atomic="true">{message}</div>;
}
