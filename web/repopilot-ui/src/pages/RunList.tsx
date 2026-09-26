import { useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { api, isTerminal, stageLabel } from '../api/client';
import { navigate } from '../navigation';

/**
 * Every run, most recent first, as the way into the review view (US1/AC5).
 *
 * The review view is where a decision gets made, and until this page existed the
 * only way to reach it was to know a run's id and type it into the URL. That made
 * US1/AC5 — "given any completed run, when the developer opens it" — true of the
 * API and false of the product.
 *
 * Runs awaiting a decision are called out rather than merely listed. They are the
 * ones holding a working copy and waiting on a person (FR-013b), so a reviewer
 * opening this page should be able to see what needs them without reading every
 * row.
 */
export function RunList() {
  const [repositoryId, setRepositoryId] = useState('');

  const repositories = useQuery({
    queryKey: ['repositories'],
    queryFn: api.listRepositories,
  });

  const runs = useQuery({
    queryKey: ['runs', repositoryId || null],
    queryFn: () => api.listRuns(repositoryId || undefined),
    // A run in this list is usually one someone is waiting on. Refetching keeps
    // a stage from going stale on screen without the per-run event stream, which
    // belongs to the review view rather than to a list of thirty runs.
    refetchInterval: 5_000,
  });

  const slugs = new Map((repositories.data ?? []).map((r) => [r.id, r.slug]));

  const waiting = (runs.data ?? []).filter((run) => run.stage === 'awaiting_approval');

  return (
    <section className="run-list">
      <h1>Runs</h1>

      <label className="run-list__filter" htmlFor="run-list-repository">
        Repository
        <select
          id="run-list-repository"
          value={repositoryId}
          onChange={(event) => setRepositoryId(event.target.value)}
        >
          <option value="">All repositories</option>
          {(repositories.data ?? []).map((repository) => (
            <option key={repository.id} value={repository.id}>
              {repository.slug}
            </option>
          ))}
        </select>
      </label>

      {waiting.length > 0 && (
        <p className="run-list__waiting">
          {waiting.length === 1
            ? '1 run is awaiting your decision.'
            : `${waiting.length} runs are awaiting your decision.`}
        </p>
      )}

      {runs.isPending && <p className="run-list__loading">Loading runs…</p>}

      {runs.isError && (
        <p className="run-list__error" role="alert">
          The runs could not be loaded.
        </p>
      )}

      {runs.data?.length === 0 && (
        <p className="run-list__empty">
          No runs yet. Start one from a repository on the operator view.
        </p>
      )}

      {runs.data && runs.data.length > 0 && (
        <ul className="run-list__runs">
          {runs.data.map((run) => (
            <li
              key={run.id}
              className="run-list__run"
              data-stage={run.stage}
              data-awaiting={run.stage === 'awaiting_approval'}
            >
              <button
                type="button"
                className="run-list__open"
                onClick={() => navigate({ view: 'runs', runId: run.id })}
              >
                {run.taskDescription}
              </button>

              <dl className="run-list__meta">
                <div>
                  <dt>Stage</dt>
                  <dd className="run-list__stage">{stageLabel(run.stage)}</dd>
                </div>
                <div>
                  <dt>Repository</dt>
                  <dd>{slugs.get(run.repositoryId) ?? '—'}</dd>
                </div>
                <div>
                  <dt>Started</dt>
                  <dd>{new Date(run.createdAt).toLocaleString()}</dd>
                </div>
                <div>
                  <dt>Outcome</dt>
                  {/* Blank while a run is live, rather than a dash that reads as
                      "no outcome recorded" for a run that has not finished. */}
                  <dd>{isTerminal(run.stage) ? (run.terminalOutcome ?? '—') : ''}</dd>
                </div>
              </dl>
            </li>
          ))}
        </ul>
      )}
    </section>
  );
}
