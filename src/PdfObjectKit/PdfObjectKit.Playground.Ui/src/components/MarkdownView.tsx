import { Toast } from './Toast';
import { useEffect, useMemo, useRef, useState } from 'react';
import { Modal } from './Modal';
import './MarkdownView.css';
import { marked } from 'marked';
import DOMPurify from 'dompurify';
import { Download, X, Copy, Check, Eye, Code2 } from 'lucide-react';

type Result = { markdown: string; warnings: string[] };
export function MarkdownView({ pdf, name, onClose }: { pdf: Blob; name: string; onClose: () => void }) {
  const [result, setResult] = useState<Result>();
  const [error, setError] = useState('');
  const [source, setSource] = useState(false);
  const [attempt, setAttempt] = useState(0);
  const [copyStatus, setCopyStatus] = useState('');
  const [copying, setCopying] = useState(false);
  const copyTimer = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);
  useEffect(() => () => clearTimeout(copyTimer.current), []);
  async function copyMarkdown() {
    if (!result || copying) {
      return;
    }
    clearTimeout(copyTimer.current);
    setCopying(true);
    try {
      await navigator.clipboard.writeText(result.markdown);
      setCopyStatus('Copied Markdown');
      copyTimer.current = setTimeout(() => setCopyStatus(''), 2500);
    } catch {
      setCopyStatus('Could not copy. Select the Source text and copy manually.');
    } finally {
      setCopying(false);
    }
  }
  useEffect(() => {
    const controller = new AbortController();
    setResult(undefined);
    setError('');
    void (async () => {
      try {
        const response = await fetch('/api/documents/markdown', { method: 'POST', headers: { 'Content-Type': 'application/pdf' }, body: pdf, signal: controller.signal });
        const data = await response.json();
        if (!response.ok) {
          throw new Error(data.detail ?? 'Markdown conversion failed.');
        }
        if (!controller.signal.aborted) {
          setResult(data);
        }
      } catch (failure) {
        if (!controller.signal.aborted) {
          setError(failure instanceof Error ? failure.message : 'Markdown conversion failed.');
        }
      }
    })();
    return () => controller.abort();
  }, [pdf, attempt]);
  const html = useMemo(() => DOMPurify.sanitize(marked.parse(result?.markdown ?? '', { async: false, gfm: true }), { USE_PROFILES: { html: true }, FORBID_TAGS: ['img', 'video', 'audio', 'iframe', 'form', 'input', 'style'], FORBID_ATTR: ['style'] }), [result]);
  function download() {
    if (!result) {
      return;
    }
    const url = URL.createObjectURL(new Blob([result.markdown], { type: 'text/markdown;charset=utf-8' }));
    const link = document.createElement('a');
    link.href = url;
    link.download = name.replace(/\.pdf$/i, '') + '.md';
    link.click();
    setTimeout(() => URL.revokeObjectURL(url), 1000);
  }
  return <Modal className="markdown-dialog" labelledBy="markdown-title" onClose={onClose}>
    <header><div className="markdown-heading"><h2 id="markdown-title">Markdown</h2><p title={name}>{name} &middot; Current saved PDF</p></div>
      <div className="markdown-toolbar"><div className="markdown-view-switch" role="group" aria-label="Markdown display"><button type="button" aria-pressed={!source} onClick={() => setSource(false)}><Eye size={15} aria-hidden="true" />Preview</button><button type="button" aria-pressed={source} onClick={() => setSource(true)}><Code2 size={15} aria-hidden="true" />Source</button></div>
        <div className="markdown-actions"><button type="button" aria-label="Download Markdown" title="Download .md file" disabled={!result} onClick={download}><Download size={16} aria-hidden="true" />Download</button></div>
      </div>
      <button type="button" aria-label="Close Markdown" onClick={onClose}><X size={20} /></button>
      <Toast message={copyStatus} />
    </header>
    <div className="markdown-body">
    {!result && !error && <p role="status">Converting PDF to Markdown...</p>}
    {error && <div role="alert"><p>{error}</p><button type="button" onClick={() => setAttempt(value => value + 1)}>Retry</button></div>}
    {result && <>
      {!!result.warnings.length && <details className="markdown-notes"><summary>Conversion notes ({result.warnings.length})</summary>{result.warnings.map((warning, index) => <p key={index}>{warning}</p>)}</details>}
      {!result.markdown.trim() ? <p>No Markdown content was extracted from this PDF.</p> : source ? <div className="markdown-source-block"><button type="button" className="markdown-copy" aria-label="Copy Markdown" title="Copy Markdown source" disabled={!result || copying} onClick={() => void copyMarkdown()}>{copyStatus === 'Copied Markdown' ? <Check size={17} aria-hidden="true" /> : <Copy size={17} aria-hidden="true" />}</button><pre className="markdown-source">{result.markdown}</pre></div> : <article className="markdown-preview" onClick={event => {
        const link = (event.target as Element).closest('a');
        if (link) {
          event.preventDefault();
          const href = link.getAttribute('href') ?? '';
          if (/^https?:\/\//i.test(href)) {
            window.open(href, '_blank', 'noopener,noreferrer');
          }
        }
      }} dangerouslySetInnerHTML={{ __html: html }} />}
    </>}
    </div>
  </Modal>;
}
