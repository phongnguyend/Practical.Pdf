import { StrictMode, useEffect, useRef, useState } from 'react';
import { createRoot } from 'react-dom/client';
import './style.css';

const engines = [
  { id: 'libreoffice', name: 'LibreOffice', description: 'Calc spreadsheet to PDF' },
];
const maxSize = 10 * 1024 * 1024;

function App() {
  const [file, setFile] = useState(null);
  const [selectedEngines, setSelectedEngines] = useState(['libreoffice']);
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
      setError('Choose one Excel file at a time.');
      return;
    }
    const selected = files[0];
    if (!/\.(xlsx|xls)$/i.test(selected.name)) {
      setError('Choose a file with an .xlsx or .xls extension.');
      return;
    }
    if (selected.size === 0 || selected.size > maxSize) {
      setError(selected.size === 0 ? 'This file is empty. Choose an Excel file with content.' : 'This file exceeds the 10 MB limit.');
      return;
    }
    setFile(selected);
  }

  async function convert(event) {
    event.preventDefault();
    if (!file || busy || selectedEngines.length === 0) {
      return;
    }
    setBusy(true);
    setError('');
    clearResults();
    const selected = engines.filter((engine) => selectedEngines.includes(engine.id));
    setResults(selected.map((engine) => ({ engine, status: 'pending' })));
    await Promise.all(selected.map((engine) => convertWithEngine(engine, selected.length > 1)));
    setBusy(false);
  }

  async function convertWithEngine(engine, multiple) {
    function updateResult(update) {
      setResults((current) => current.map((result) => result.engine.id === engine.id ? { ...result, ...update } : result));
    }
    const controller = new AbortController();
    requests.current.add(controller);
    const timeout = setTimeout(() => controller.abort(), 120000);
    try {
      const body = new FormData();
      body.append('file', file);
      const response = await fetch(`/api/${engine.id}/convert`, { method: 'POST', body, signal: controller.signal });
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
      updateResult({ status: 'success', url, name: file.name.replace(/\.(xlsx|xls)$/i, multiple ? `.${engine.id}.pdf` : '.pdf'), size: blob.size });
    } catch (failure) {
      updateResult({ status: 'error', error: failure.name === 'AbortError' ? 'Conversion timed out. Please try again.' : failure instanceof TypeError ? 'Could not reach the conversion service. Check that the proxy is running and try again.' : failure.message });
    } finally {
      clearTimeout(timeout);
      requests.current.delete(controller);
    }
  }

  return (
    <div className="shell">
      <header className="topbar"><a href="/" className="brand"><span className="brand-icon">E</span> Excel to PDF</a><span className="badge">PLAYGROUND</span></header>
      <main>
        <div className="intro"><p className="eyebrow">FROM SPREADSHEET TO PDF</p><h1>Your workbook.<br /><span>Ready for print.</span></h1><p>Turn an Excel file into a downloadable PDF.<br /> Convert with LibreOffice, then view or download your PDF.</p></div>
        <form onSubmit={convert} className="workspace" aria-busy={busy}>
          <section className="upload-panel" aria-labelledby="upload-heading">
            <div className="section-title"><span className="step">01</span><h2 id="upload-heading">Add your spreadsheet</h2></div>
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
              <span className="file-icon" aria-hidden="true">XLS</span>
              <h3>{file ? file.name : 'Drop your Excel file here'}</h3>
              <p>{file ? `${(file.size / 1024).toFixed(1)} KB · Ready to convert` : 'or choose a file from your device'}</p>
              <button type="button" className="secondary" disabled={busy} onClick={() => input.current.click()}>{file ? 'Replace file' : 'Browse files'} <span aria-hidden="true">↗</span></button>
              <input ref={input} type="file" accept=".xlsx,.xls" aria-label="Excel file" disabled={busy} hidden onChange={(event) => {
                selectFiles(event.target.files);
                event.target.value = '';
              }} />
              <small>.XLSX or .XLS · Up to 10 MB · One file at a time</small>
            </div>
            <p className="hint">Set print areas, page orientation, and scaling in your workbook before uploading. These settings affect the PDF layout.</p>
          </section>
          <section className="settings-panel" aria-labelledby="engine-heading">
            <div className="section-title"><span className="step">02</span><h2 id="engine-heading">Conversion engine</h2></div>
            <fieldset disabled={busy}><legend className="sr-only">Conversion engine</legend>{engines.map((item) => (
              <label key={item.id} className={`engine ${selectedEngines.includes(item.id) ? 'selected' : ''}`}>
                <input type="checkbox" name="engine" value={item.id} checked={selectedEngines.includes(item.id)} onChange={() => {
                  setSelectedEngines((current) => current.includes(item.id) ? current.filter((id) => id !== item.id) : [...current, item.id]);
                  clearResults();
                  setError('');
                }} />
                <span><strong>{item.name}</strong><small>{item.description}</small></span>
              </label>
            ))}</fieldset>
            <button className="primary" type="submit" disabled={!file || busy || selectedEngines.length === 0}>{busy ? 'Converting…' : 'Convert to PDF'}<span aria-hidden="true">→</span></button>
            <p className="quiet">View or download your converted PDF below.</p>
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
        <footer><span>Excel in. PDF out.</span><span>Powered by LibreOffice</span></footer>
      </main>
    </div>
  );
}

createRoot(document.getElementById('root')).render(<StrictMode><App /></StrictMode>);
