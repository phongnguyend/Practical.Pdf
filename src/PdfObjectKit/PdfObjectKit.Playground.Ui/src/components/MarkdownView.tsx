import { markdownOptionHelp } from './markdownOptionHelp';
import { Toast } from './Toast';
import { useEffect, useMemo, useRef, useState } from 'react';
import { Modal } from './Modal';
import './MarkdownView.css';
import { marked } from 'marked';
import DOMPurify from 'dompurify';
import { Download, X, Copy, Check, Eye, Code2, Settings2, RefreshCw } from 'lucide-react';

type Options = { includeImages: boolean; joinHyphenatedWords: boolean; bodyTextHeight: number | null; heuristics: Record<string, number | boolean> };
const defaults: Options = { includeImages: true, joinHyphenatedWords: false, bodyTextHeight: null, heuristics: {} };
type Result = { markdown: string; warnings: string[]; options?: Options };
export function MarkdownView({ pdf, name, onClose }: { pdf: Blob; name: string; onClose: () => void }) {
  const [result, setResult] = useState<Result>();
  const [error, setError] = useState('');
  const [loading, setLoading] = useState(true);
  const [showOptions, setShowOptions] = useState(false);
  const [draft, setDraft] = useState<Options>(defaults);
  const [requested, setRequested] = useState<Options>();
  const [applied, setApplied] = useState<Options>(defaults);
  const [defaultOptions, setDefaultOptions] = useState<Options>(defaults);
  const changed = JSON.stringify(draft) !== JSON.stringify(applied);
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
    setLoading(true);
    setError('');
    void (async () => {
      try {
        const response = await fetch('/api/documents/markdown' + (requested ? '?' + new URLSearchParams({ options: JSON.stringify(requested) }) : ''), { method: 'POST', headers: { 'Content-Type': 'application/pdf' }, body: pdf, signal: controller.signal });
        const data = await response.json();
        if (!response.ok) {
          throw new Error(data.detail ?? 'Markdown conversion failed.');
        }
        if (!controller.signal.aborted) {
          setResult(data);
          const effective = data.options ?? requested ?? defaults;
          setDefaultOptions(data.defaultOptions ?? defaults);
          setApplied(effective);
          setDraft(effective);
        }
      } catch (failure) {
        if (!controller.signal.aborted) {
          setError(failure instanceof Error ? failure.message : 'Markdown conversion failed.');
        }
      } finally {
        if (!controller.signal.aborted) {
          setLoading(false);
        }
      }
    })();
    return () => controller.abort();
  }, [pdf, attempt, requested]);
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
        <div className="markdown-actions"><button type="button" aria-expanded={showOptions} aria-controls="markdown-options" onClick={() => setShowOptions(value => !value)}><Settings2 size={16} />Options</button><button type="button" aria-label="Download Markdown" title="Download .md file" disabled={!result} onClick={download}><Download size={16} aria-hidden="true" />Download</button></div>
      </div>
      <button type="button" aria-label="Close Markdown" onClick={onClose}><X size={20} /></button>
      <Toast message={copyStatus} />
    </header>
    <div className="markdown-body">
    {showOptions && <form id="markdown-options" className="markdown-options" onSubmit={event => {
      event.preventDefault();
      setRequested({ ...draft, heuristics: { ...draft.heuristics } });
    }}>
      <div className="markdown-options-heading"><h3>Conversion options</h3><span>{changed ? 'Unapplied changes' : 'Options used for this result'}</span></div>
      <fieldset disabled={loading}>
        <div className="markdown-options-basic">
          <div className="markdown-option"><label><input aria-describedby="markdown-images-help" type="checkbox" checked={draft.includeImages} onChange={event => setDraft({ ...draft, includeImages: event.target.checked })} />Include image descriptions</label><p id="markdown-images-help">Include image metadata as labeled JSON blocks. Image files are not embedded in the Markdown.</p></div>
          <div className="markdown-option"><label><input aria-describedby="markdown-hyphens-help" type="checkbox" checked={draft.joinHyphenatedWords} onChange={event => setDraft({ ...draft, joinHyphenatedWords: event.target.checked })} />Join hyphenated words</label><p id="markdown-hyphens-help">Remove hyphens between alphabetic words split across lines. This can also change legitimate compound words.</p></div>
          <div className="markdown-option"><label>Body text height<input aria-describedby="markdown-height-help" type="number" min="0.01" step="any" placeholder="Automatic" value={draft.bodyTextHeight ?? ''} onChange={event => setDraft({ ...draft, bodyTextHeight: event.target.value === '' ? null : Number(event.target.value) })} /></label><p id="markdown-height-help">Body text height in PDF units, used to identify headings. Leave empty to estimate it automatically.</p></div>
        </div>
        <details><summary>Advanced layout heuristics</summary><p className="control-help">Distances are relative to text height unless the setting specifies a count or rounding digits. Defaults work for most documents.</p>
          <div className="markdown-heuristics">{Object.entries(draft.heuristics).map(([key, value]) => {
            const label = key.replace(/([A-Z])/g, ' $1').toLowerCase().replace(/^./, letter => letter.toUpperCase());
            return <div key={key} className="markdown-option"><label>{typeof value === 'boolean' ? <><input aria-describedby={`markdown-help-${key}`} type="checkbox" checked={value} onChange={event => setDraft({ ...draft, heuristics: { ...draft.heuristics, [key]: event.target.checked } })} />{label}</> : <>{label}<input aria-describedby={`markdown-help-${key}`} type="number" required step="any" value={value} onChange={event => setDraft({ ...draft, heuristics: { ...draft.heuristics, [key]: Number(event.target.value) } })} /></>}</label><p id={`markdown-help-${key}`}>{markdownOptionHelp[key]}</p></div>;
          })}</div>
        </details>
        <div className="markdown-options-actions"><button type="button" onClick={() => {
          setDraft(applied);
          setError('');
        }} disabled={!changed}>Discard changes</button><button type="button" onClick={() => {
          setDraft(defaultOptions);
          setError('');
        }}>Reset to defaults</button><button type="submit"><RefreshCw size={15} />Reapply conversion</button></div>
      </fieldset>
    </form>}
    {loading && <p role="status">Converting PDF to Markdown...{result ? ' Showing the previous result until conversion finishes.' : ''}</p>}
    {!loading && changed && result && <p className="control-help">Preview, copy and download still use the last applied options.</p>}
    {error && <div role="alert"><p>{error}</p><button type="button" disabled={loading} onClick={() => setAttempt(value => value + 1)}>Retry</button></div>}
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
