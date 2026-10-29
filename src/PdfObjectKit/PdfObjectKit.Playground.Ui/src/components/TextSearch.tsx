import { useEffect, useRef, useState } from 'react';
import { CaseSensitive, WholeWord, ChevronDown, ChevronUp, Search, X } from 'lucide-react';
import type { PageViewport } from 'pdfjs-dist';
import './TextSearch.css';

type Match = { pageNumber: number; start: number; text: string; snippet: string; regions: { corners: { x: number; y: number }[] }[] };
type Results = { matches: Match[]; truncated: boolean; warnings: string[] };
export function useTextSearch(pdf: Blob | null, disabled: boolean) {
  const [open, setOpen] = useState(false);
  const [query, setQuery] = useState('');
  const [matchCase, setMatchCase] = useState(false);
  const [wholeWord, setWholeWord] = useState(false);
  const [index, setIndex] = useState(0);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const [retry, setRetry] = useState(0);
  const [response, setResponse] = useState<{ pdf: Blob; key: string; results: Results } | null>(null);
  const cache = useRef(new Map<string, Results>());
  const key = JSON.stringify([query, matchCase, wholeWord]);
  const results = open && !disabled && response?.pdf === pdf && response.key === key ? response.results : null;
  useEffect(() => {
    cache.current.clear();
    setResponse(null);
    setIndex(0);
  }, [pdf]);
  useEffect(() => {
    function shortcut(event: KeyboardEvent) {
      if (!pdf || disabled || document.querySelector('dialog[open]')) {
        return;
      }
      if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 'f') {
        event.preventDefault();
        setOpen(true);
        document.getElementById('pdf-search-query')?.focus();
      } else if (event.key === 'Escape' && open) {
        setOpen(false);
      }
    }
    window.addEventListener('keydown', shortcut);
    return () => window.removeEventListener('keydown', shortcut);
  }, [pdf, disabled, open]);
  useEffect(() => {
    setError('');
    setIndex(0);
    if (!open || !pdf || disabled || !query.trim()) {
      setBusy(false);
      return;
    }
    const hit = cache.current.get(key);
    if (hit) {
      setResponse({ pdf, key, results: hit });
      setBusy(false);
      return;
    }
    const controller = new AbortController();
    setBusy(true);
    const timer = setTimeout(() => {
      void (async () => {
        try {
          const parameters = new URLSearchParams({ query, matchCase: String(matchCase), wholeWord: String(wholeWord) });
          const res = await fetch('/api/documents/search?' + parameters, { method: 'POST', headers: { 'Content-Type': 'application/pdf' }, body: pdf, signal: controller.signal });
          const data = await res.json();
          if (!res.ok) {
            throw new Error(data.detail ?? 'Search failed.');
          }
          if (!controller.signal.aborted) {
            if (cache.current.size >= 10) {
              cache.current.delete(cache.current.keys().next().value!);
            }
            cache.current.set(key, data);
            setResponse({ pdf, key, results: data });
          }
        } catch (cause) {
          if (!controller.signal.aborted) {
            setError(cause instanceof Error ? cause.message : 'Search failed.');
          }
        } finally {
          if (!controller.signal.aborted) {
            setBusy(false);
          }
        }
      })();
    }, 300);
    return () => {
      clearTimeout(timer);
      controller.abort();
    };
  }, [pdf, open, query, matchCase, wholeWord, disabled, retry]);
  function move(delta: number) {
    if (results?.matches.length) {
      setIndex(value => (value + delta + results.matches.length) % results.matches.length);
    }
  }
  return { open, setOpen, query, setQuery, matchCase, setMatchCase, wholeWord, setWholeWord, index, setIndex, results, active: results?.matches[index], busy, error, move, retry: () => setRetry(value => value + 1) };
}

export function TextSearchBar({ search, disabled }: { search: ReturnType<typeof useTextSearch>; disabled: boolean }) {
  const input = useRef<HTMLInputElement>(null);
  useEffect(() => {
    if (search.open) {
      input.current?.focus();
    }
  }, [search.open]);
  if (!search.open) {
    return null;
  }
  return <section className="pdf-search" aria-label="Find in PDF">
    <div className="pdf-search-controls"><div className="pdf-search-input"><Search size={17} aria-hidden="true" /><input ref={input} id="pdf-search-query" aria-label="Find text" placeholder="Find in document" maxLength={256} value={search.query} disabled={disabled} onChange={event => search.setQuery(event.target.value)} onKeyDown={event => {
      if (event.key === 'Enter') {
        event.preventDefault();
        search.move(event.shiftKey ? -1 : 1);
      }
    }} /></div>
      <div className="pdf-search-navigation" role="group" aria-label="Match navigation">
        <button title="Previous match (Shift+Enter)" aria-label="Previous match" disabled={disabled || search.busy || !search.results?.matches.length} onClick={() => search.move(-1)}><ChevronUp size={18} /></button>
        <button title="Next match (Enter)" aria-label="Next match" disabled={disabled || search.busy || !search.results?.matches.length} onClick={() => search.move(1)}><ChevronDown size={18} /></button>
      </div>
      <button className="pdf-search-close" aria-label="Close search" title="Close search (Escape)" onClick={() => search.setOpen(false)}><X size={18} /></button>
    </div>
    <div className="pdf-search-options">
      <label className={search.matchCase ? 'enabled' : ''} title="Match uppercase and lowercase exactly"><input type="checkbox" checked={search.matchCase} disabled={disabled} onChange={event => search.setMatchCase(event.target.checked)} /><CaseSensitive size={16} aria-hidden="true" />Match case</label>
      <label className={search.wholeWord ? 'enabled' : ''} title="Match complete words only"><input type="checkbox" checked={search.wholeWord} disabled={disabled} onChange={event => search.setWholeWord(event.target.checked)} /><WholeWord size={16} aria-hidden="true" />Whole words</label>
      <span role="status" className={search.results?.matches.length === 0 ? 'no-matches' : ''}>{search.busy ? 'Searching…' : search.results ? search.results.matches.length ? `${search.index + 1} of ${search.results.matches.length}${search.results.truncated ? '+' : ''}` : 'No matches' : 'Type to search'}</span>
    </div>
    {search.results && search.results.matches.length > 0 && <SearchResultList search={search} disabled={disabled} />}
    {search.error && <p role="alert">{search.error} <button onClick={search.retry}>Retry search</button></p>}
    {!!search.results?.warnings.length && <details><summary>Search notes ({search.results.warnings.length})</summary>{search.results.warnings.map((warning, index) => <p key={index}>{warning}</p>)}</details>}
  </section>;
}

function SearchResultList({ search, disabled }: { search: ReturnType<typeof useTextSearch>; disabled: boolean }) {
  const list = useRef<HTMLOListElement>(null);
  useEffect(() => {
    const container = list.current;
    const selected = container?.querySelector<HTMLButtonElement>('[aria-current="true"]');
    if (container && selected) {
      const bounds = container.getBoundingClientRect();
      const row = selected.getBoundingClientRect();
      if (row.top < bounds.top) {
        container.scrollTop += row.top - bounds.top - 4;
      } else if (row.bottom > bounds.bottom) {
        container.scrollTop += row.bottom - bounds.bottom + 4;
      }
    }
  }, [search.index, search.results]);
  return <div className="pdf-search-results">
    <div className="pdf-search-results-heading"><span>Matches</span><span>Click to jump to text</span></div>
    <ol ref={list} aria-label="Search results">{search.results!.matches.map((match, index) => {
      const parts = match.text ? match.snippet.split(match.text) : [match.snippet];
      return <li key={index}><button type="button" disabled={disabled} aria-current={index === search.index ? 'true' : undefined} tabIndex={index === search.index ? 0 : -1} aria-label={`Result ${index + 1}, page ${match.pageNumber}: ${match.snippet}`} onClick={() => search.setIndex(index)} onKeyDown={event => {
        const count = search.results!.matches.length;
        const next = event.key === 'ArrowDown' ? (index + 1) % count : event.key === 'ArrowUp' ? (index + count - 1) % count : event.key === 'Home' ? 0 : event.key === 'End' ? count - 1 : null;
        if (next !== null) {
          event.preventDefault();
          search.setIndex(next);
          list.current?.querySelectorAll<HTMLButtonElement>('button')[next]?.focus({ preventScroll: true });
        }
      }}><span className="pdf-search-result-meta"><span>Page {match.pageNumber}</span><span className="pdf-search-result-number">{index + 1}</span></span><span className="pdf-search-result-snippet">{match.start > 40 && <span aria-hidden="true">…</span>}{parts.map((part, partIndex) => <span key={partIndex}>{partIndex > 0 && <mark>{match.text}</mark>}{part.replace(/[\u0000-\u001f\u007f]/g, ' ')}</span>)}</span></button></li>;
    })}</ol>
    {search.results!.truncated && <p className="pdf-search-result-limit">Showing the first {search.results!.matches.length} matches. Refine your search to see fewer results.</p>}
  </div>;
}
export function SearchHighlights({ search, page, viewport }: { search: ReturnType<typeof useTextSearch>; page: number; viewport: PageViewport }) {
  const active = useRef<SVGPolygonElement>(null);
  useEffect(() => {
    active.current?.scrollIntoView({ block: 'center', inline: 'nearest' });
  }, [search.active, page, viewport]);
  return <svg className="search-highlights" width={viewport.width} height={viewport.height} aria-hidden="true">{search.results?.matches.flatMap((match, index) => match.pageNumber !== page ? [] : match.regions.map((region, part) => <polygon key={`${index}-${part}`} ref={index === search.index && part === 0 ? active : undefined} className={index === search.index ? 'active-search-match' : ''} points={region.corners.map(point => viewport.convertToViewportPoint(point.x, point.y).join(',')).join(' ')} />))}</svg>;
}
