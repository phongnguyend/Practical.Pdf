import { StrictMode, useEffect, useRef, useState } from 'react';
import { createRoot } from 'react-dom/client';
import './style.css';

const engines = [
  { id: 'chrome', name: 'Chrome', description: 'Google Chrome headless' },
  { id: 'playwright', name: 'Playwright', description: 'Playwright Chromium' },
  { id: 'puppeteer', name: 'PuppeteerSharp', description: 'Chrome via PuppeteerSharp' },
];
const maxSize = 10 * 1024 * 1024;

function App() {
  const [file, setFile] = useState(null);
  const [sourceType, setSourceType] = useState('file');
  const [websiteUrl, setWebsiteUrl] = useState('');
  const [selectedEngines, setSelectedEngines] = useState(['chrome']);
  const [busy, setBusy] = useState(false);
  const [dragging, setDragging] = useState(false);
  const [error, setError] = useState('');
  const [results, setResults] = useState([]);
  const input = useRef(null);
  const requests = useRef(new Set());
  const urls = useRef(new Set());

  useEffect(() => {
    return () => {
      for (const controller of requests.current) {
        controller.abort();
      }
      for (const url of urls.current) {
        URL.revokeObjectURL(url);
      }
      urls.current.clear();
    };
  }, []);

  function clearResults() {
    for (const url of urls.current) {
      URL.revokeObjectURL(url);
    }
    urls.current.clear();
    setResults([]);
  }

  function selectFiles(files) {
    if (busy || files.length === 0) {
      return;
    }
    setError('');
    clearResults();
    setFile(null);
    if (files.length !== 1) {
      setError('Choose one HTML file at a time.');
      return;
    }
    const selected = files[0];
    if (!/\.html?$/i.test(selected.name)) {
      setError('Choose a file with an .html or .htm extension.');
      return;
    }
    if (selected.size === 0 || selected.size > maxSize) {
      setError(selected.size === 0 ? 'This file is empty. Choose an HTML file with content.' : 'This file exceeds the 10 MB limit.');
      return;
    }
    setFile(selected);
  }

  async function convert(event) {
    event.preventDefault();
    if ((sourceType === 'file' && !file) || busy || selectedEngines.length === 0) {
      return;
    }
    let targetUrl = '';
    if (sourceType === 'url') {
      try {
        const parsed = new URL(websiteUrl.trim());
        if (!['http:', 'https:'].includes(parsed.protocol) || parsed.username || parsed.password) {
          throw new Error('Invalid URL');
        }
        targetUrl = parsed.href;
      } catch {
        setError('Enter an absolute HTTP or HTTPS URL without embedded credentials.');
        return;
      }
    }
    setBusy(true);
    setError('');
    clearResults();
    const selected = engines.filter((engine) => selectedEngines.includes(engine.id));
    setResults(selected.map((engine) => ({ engine, status: 'pending' })));
    await Promise.all(selected.map((engine) => convertWithEngine(engine, selected.length > 1, targetUrl)));
    setBusy(false);
  }

  async function convertWithEngine(engine, multiple, targetUrl) {
    function updateResult(update) {
      setResults((current) => current.map((result) => result.engine.id === engine.id ? { ...result, ...update } : result));
    }
    const controller = new AbortController();
    requests.current.add(controller);
    const timeout = setTimeout(() => controller.abort(), 120000);
    try {
      let body;
      let headers;
      if (targetUrl) {
        body = JSON.stringify({ url: targetUrl });
        headers = { 'Content-Type': 'application/json' };
      } else {
        body = new FormData();
        body.append('file', file);
      }
      const response = await fetch(`/api/${engine.id}/${targetUrl ? 'convert-url' : 'convert-file'}`, { method: 'POST', body, headers, signal: controller.signal });
      if (!response.ok) {
        const text = await response.text();
        let message = '';
        try {
          const data = JSON.parse(text);
          message = typeof data === 'string' ? data : data.detail || data.title;
        } catch {
          if (!text.trim().startsWith('<')) {
            message = text;
          }
        }
        throw new Error(message || `Conversion failed (${response.status}). Check that the proxy and selected converter are running.`);
      }
      if (!response.headers.get('content-type')?.includes('application/pdf')) {
        throw new Error('The server did not return a PDF. Check the proxy configuration.');
      }
      const blob = await response.blob();
      if (blob.size === 0) {
        throw new Error('The server returned an empty PDF. Please try again.');
      }
      controller.signal.throwIfAborted();
      const url = URL.createObjectURL(blob);
      urls.current.add(url);
      const baseName = targetUrl ? new URL(targetUrl).hostname.replace(/[^a-z0-9.-]/gi, '_') : file.name.replace(/\.html?$/i, '');
      updateResult({ status: 'success', url, name: `${baseName}${multiple ? `.${engine.id}` : ''}.pdf`, size: blob.size });
    } catch (failure) {
      updateResult({ status: 'error', error: failure.name === 'AbortError' ? 'Conversion timed out. Please try again.' : failure instanceof TypeError ? 'Could not reach the conversion service. Check that the proxy is running and try again.' : failure.message });
    } finally {
      clearTimeout(timeout);
      requests.current.delete(controller);
    }
  }

  return (
    <div className="shell">
      <header className="topbar"><a href="/" className="brand"><span className="brand-icon">H</span> HTML to PDF</a><span className="badge">PLAYGROUND</span></header>
      <main>
        <div className="intro"><p className="eyebrow">FROM BROWSER TO PAPER</p><h1>Your HTML.<br /><span>Ready for print.</span></h1><p>Turn an HTML file or webpage into a downloadable PDF.<br /> Choose one or more rendering engines to compare results.</p></div>
        <form onSubmit={convert} className="workspace" aria-busy={busy} noValidate>
          <section className="upload-panel" aria-labelledby="upload-heading">
            <div className="section-title"><span className="step">01</span><h2 id="upload-heading">Add your HTML</h2></div>
            <div className="source-options" role="group" aria-label="HTML source">
              {['file', 'url'].map((type) => (
                <button key={type} type="button" className="secondary" aria-pressed={sourceType === type} disabled={busy} onClick={() => {
                  setSourceType(type);
                  clearResults();
                  setError('');
                }}>{type === 'file' ? 'Upload file' : 'Website URL'}</button>
              ))}
            </div>
            {sourceType === 'url' ? (
              <div className="url-source">
                <label htmlFor="website-url">Website URL</label>
                <input id="website-url" type="url" placeholder="https://example.com" value={websiteUrl} disabled={busy} onChange={(event) => {
                  setWebsiteUrl(event.target.value);
                  clearResults();
                  setError('');
                }} />
                <p className="hint">Enter a full HTTP or HTTPS address. The page opens in a fresh browser on the converter, without your signed-in session.</p>
              </div>
            ) : <>
            <div className={`dropzone ${dragging ? 'dragging' : ''}`}
              onDragOver={(event) => {
                event.preventDefault();
                if (!busy) {
                  setDragging(true);
                }
              }}
              onDragLeave={(event) => {
                if (!event.currentTarget.contains(event.relatedTarget)) {
                  setDragging(false);
                }
              }}
              onDrop={(event) => {
                event.preventDefault();
                setDragging(false);
                selectFiles(event.dataTransfer.files);
              }}>
              <span className="file-icon" aria-hidden="true">&lt;/&gt;</span>
              <h3>{file ? file.name : 'Drop your HTML file here'}</h3>
              <p>{file ? `${(file.size / 1024).toFixed(1)} KB · Ready to convert` : 'or choose a file from your device'}</p>
              <button type="button" className="secondary" disabled={busy} onClick={() => input.current.click()}>{file ? 'Replace file' : 'Browse files'} <span aria-hidden="true">↗</span></button>
              <input ref={input} type="file" accept=".html,.htm,text/html" aria-label="HTML file" disabled={busy} hidden onChange={(event) => {
                selectFiles(event.target.files);
                event.target.value = '';
              }} />
              <small>.HTML or .HTM · Up to 10 MB · One file at a time</small>
            </div>
            <p className="hint">For best results, embed styles and images in your HTML or use absolute URLs. Relative assets are not uploaded with the file.</p>
            </>}
          </section>
          <section className="settings-panel" aria-labelledby="engine-heading">
            <div className="section-title"><span className="step">02</span><h2 id="engine-heading">Choose engines</h2></div>
            <fieldset disabled={busy}><legend className="sr-only">Rendering engines (select one or more)</legend>{engines.map((item) => (
              <label key={item.id} className={`engine ${selectedEngines.includes(item.id) ? 'selected' : ''}`}>
                <input type="checkbox" name="engine" value={item.id} checked={selectedEngines.includes(item.id)} onChange={() => {
                  setSelectedEngines((current) => current.includes(item.id) ? current.filter((id) => id !== item.id) : [...current, item.id]);
                  clearResults();
                  setError('');
                }} />
                <span><strong>{item.name}</strong><small>{item.description}</small></span>
              </label>
            ))}</fieldset>
            <button className="primary" type="submit" disabled={(sourceType === 'file' ? !file : !websiteUrl.trim()) || busy || selectedEngines.length === 0}>{busy ? 'Converting…' : 'Convert to PDF'}<span aria-hidden="true">→</span></button>
            <p className="quiet">Select one or more engines. View or download each result below.</p>
          </section>
          <div className="feedback" aria-live="polite" role="status">
            {busy && <p>Rendering your document. This may take a moment…</p>}
            {results.map((result) => (
              <section key={result.engine.id} aria-label={`${result.engine.name} result`}>
                {result.status === 'pending' && <p className="pending">{result.engine.name}: Converting…</p>}
                {result.status === 'error' && <p className="error" role="alert">{result.engine.name}: {result.error}</p>}
                {result.status === 'success' && <div className="success">
                <div><strong>{result.engine.name}: Your PDF is ready</strong><p>{result.name} · {(result.size / 1024).toFixed(1)} KB</p></div>
                <div className="result-actions">
                  <a className="secondary view-pdf" href={result.url} target="_blank" rel="noopener noreferrer" aria-label="View PDF (opens in a new tab)">View PDF <span aria-hidden="true">↗</span></a>
                  <a className="primary download" href={result.url} download={result.name}>Download PDF <span aria-hidden="true">↓</span></a>
                </div>
                </div>}
              </section>
            ))}
          </div>
          {error && <p className="error" role="alert">{error}</p>}
        </form>
        <footer><span>HTML in. PDF out.</span><span>Powered by Chrome, Playwright &amp; PuppeteerSharp</span></footer>
      </main>
    </div>
  );
}

createRoot(document.getElementById('root')).render(<StrictMode><App /></StrictMode>);
