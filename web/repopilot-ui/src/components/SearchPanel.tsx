import { useState } from 'react';
import { useMutation } from '@tanstack/react-query';
import { ApiError, api, type RetrievalResult } from '../api/client';

interface SearchPanelProps {
  readonly repositoryId: string;
  readonly indexed: boolean;
}

/**
 * Searches a repository's active index (FR-004).
 *
 * This is the view that makes retrieval inspectable. It runs the same query path
 * the agent's `search_code` capability uses, so what an operator sees here is
 * what the agent would get — a separate query path would let the two diverge and
 * make this misleading exactly when someone is using it to work out why a run
 * found nothing.
 */
export function SearchPanel({ repositoryId, indexed }: SearchPanelProps) {
  const [query, setQuery] = useState('');

  const search = useMutation({
    mutationFn: (q: string) => api.search(repositoryId, q),
  });

  function submit(event: React.FormEvent) {
    event.preventDefault();

    if (query.trim().length > 0) {
      search.mutate(query.trim());
    }
  }

  if (!indexed) {
    return (
      <section className="search-panel search-panel--unavailable" aria-label="Search">
        <h3>Search</h3>
        <p className="search-panel__hint">
          Build the index before searching. Nothing is indexed yet.
        </p>
      </section>
    );
  }

  return (
    <section className="search-panel" aria-label="Search">
      <h3>Search</h3>

      <form onSubmit={submit} className="search-panel__form">
        <label htmlFor={`search-${repositoryId}`} className="search-panel__label">
          Query
        </label>
        <input
          id={`search-${repositoryId}`}
          type="search"
          value={query}
          onChange={(event) => setQuery(event.target.value)}
          placeholder="An identifier, or a description of what you are looking for"
        />
        <button type="submit" disabled={search.isPending || query.trim().length === 0}>
          {search.isPending ? 'Searching…' : 'Search'}
        </button>
      </form>

      {search.isError && (
        <p className="search-panel__error" role="alert">
          {search.error instanceof ApiError ? search.error.message : 'The search could not be run.'}
        </p>
      )}

      {search.isSuccess && <Results results={search.data} />}
    </section>
  );
}

function Results({ results }: { readonly results: readonly RetrievalResult[] }) {
  if (results.length === 0) {
    return (
      <p className="search-panel__empty">
        No matches. The index is built; nothing in it scored against this query.
      </p>
    );
  }

  return (
    <ol className="search-panel__results">
      {results.map((result) => (
        <li key={result.chunkId} className="search-panel__result">
          <header className="search-panel__result-header">
            <code className="search-panel__path">{result.relativePath}</code>

            {/* The line range is what makes a hit actionable rather than merely
                suggestive: it says where to look, not just which file. */}
            <span className="search-panel__lines">
              lines {result.startLine}–{result.endLine}
            </span>

            <span className="search-panel__score" title="Reciprocal rank fusion score">
              {result.score.toFixed(4)}
            </span>

            {result.language && <span className="search-panel__language">{result.language}</span>}
          </header>

          <pre className="search-panel__snippet">{result.content}</pre>
        </li>
      ))}
    </ol>
  );
}
