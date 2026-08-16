import { useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { ApiError, api, type Repository } from '../api/client';
import { SearchPanel } from '../components/SearchPanel';

/**
 * The operator view: register a fixture, build its index, and see what the
 * index contains and what it left out (FR-001, FR-003, FR-003b).
 */
export function RepositoryList() {
  const queryClient = useQueryClient();
  const [slug, setSlug] = useState('');

  const repositories = useQuery({
    queryKey: ['repositories'],
    queryFn: api.listRepositories,
  });

  const register = useMutation({
    mutationFn: (value: string) => api.registerRepository(value),
    onSuccess: () => {
      setSlug('');
      void queryClient.invalidateQueries({ queryKey: ['repositories'] });
    },
  });

  return (
    <section className="repository-list">
      <h1>Repositories</h1>

      <form
        className="repository-list__register"
        onSubmit={(event) => {
          event.preventDefault();
          if (slug.trim().length > 0) {
            register.mutate(slug.trim());
          }
        }}
      >
        <label htmlFor="repository-slug">Fixture slug</label>
        <input
          id="repository-slug"
          value={slug}
          onChange={(event) => setSlug(event.target.value)}
          placeholder="sample-dotnet-api"
          autoComplete="off"
        />
        <button type="submit" disabled={register.isPending || slug.trim().length === 0}>
          {register.isPending ? 'Registering…' : 'Register'}
        </button>

        <p className="repository-list__hint">
          Only fixtures in the deployment&rsquo;s allowed set can be registered.
        </p>
      </form>

      {register.isError && (
        <p className="repository-list__error" role="alert">
          {register.error instanceof ApiError
            ? register.error.message
            : 'The fixture could not be registered.'}
        </p>
      )}

      {repositories.isPending && <p>Loading repositories…</p>}

      {repositories.data?.length === 0 && (
        <p className="repository-list__empty">No repositories are registered yet.</p>
      )}

      {repositories.data?.map((repository) => (
        <RepositoryCard key={repository.id} repository={repository} />
      ))}
    </section>
  );
}

function RepositoryCard({ repository }: { readonly repository: Repository }) {
  const queryClient = useQueryClient();

  const index = useMutation({
    mutationFn: () => api.indexRepository(repository.id),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['repositories'] });
    },
  });

  const indexed =
    repository.indexingStatus === 'indexed' && (repository.includedFileCount ?? 0) > 0;

  return (
    <article className="repository-card">
      <header className="repository-card__header">
        <div>
          <h2>{repository.displayName ?? repository.slug}</h2>
          <code className="repository-card__slug">{repository.slug}</code>
        </div>

        <span className="repository-card__status" data-status={repository.indexingStatus}>
          {repository.indexingStatus.replace(/_/g, ' ')}
        </span>

        <button
          type="button"
          onClick={() => index.mutate()}
          disabled={index.isPending || repository.indexingStatus === 'indexing'}
        >
          {index.isPending
            ? 'Indexing…'
            : repository.activeIndexVersion
              ? 'Rebuild index'
              : 'Build index'}
        </button>
      </header>

      {index.isError && (
        <p className="repository-card__error" role="alert">
          {index.error instanceof ApiError ? index.error.message : 'The index could not be built.'}
        </p>
      )}

      <dl className="repository-card__counts">
        <div>
          <dt>Indexed files</dt>
          <dd>{repository.includedFileCount ?? 0}</dd>
        </div>
        <div>
          <dt>Excluded files</dt>
          <dd>{repository.excludedFileCount ?? 0}</dd>
        </div>
        <div>
          <dt>Index version</dt>
          <dd>{repository.activeIndexVersion ?? '—'}</dd>
        </div>
        <div>
          <dt>Last indexed</dt>
          <dd>
            {repository.lastIndexedAt ? new Date(repository.lastIndexedAt).toLocaleString() : '—'}
          </dd>
        </div>
      </dl>

      <ExclusionBreakdown breakdown={repository.exclusionBreakdown} />

      {repository.testCommands && repository.testCommands.length > 0 && (
        <p className="repository-card__commands">
          Test commands:{' '}
          {repository.testCommands.map((command) => (
            <code key={command}>{command}</code>
          ))}
        </p>
      )}

      <SearchPanel repositoryId={repository.id} indexed={indexed} />
    </article>
  );
}

/**
 * Why files were left out (FR-003b).
 *
 * Every reason is listed, including the ones that did not occur. A count of zero
 * is information: it says the check ran and found nothing, which is a different
 * claim from a missing row, and the difference matters most for the categories
 * an operator would want to know about — secrets, above all.
 */
function ExclusionBreakdown({
  breakdown,
}: {
  readonly breakdown: Repository['exclusionBreakdown'];
}) {
  const entries = Object.entries(breakdown ?? {});

  if (entries.length === 0) {
    return null;
  }

  return (
    <details className="repository-card__exclusions">
      <summary>Why files were excluded</summary>
      <ul>
        {entries
          .sort(([a], [b]) => a.localeCompare(b))
          .map(([reason, count]) => (
            <li key={reason} data-zero={count === 0}>
              <span className="repository-card__reason">{humanize(reason)}</span>
              <span className="repository-card__reason-count">{count}</span>
            </li>
          ))}
      </ul>
    </details>
  );
}

function humanize(reason: string): string {
  switch (reason) {
    case 'Binary':
      return 'Binary content';
    case 'Size':
      return 'Larger than the size limit';
    case 'ExcludedDirectory':
      return 'Build output, dependencies, or version control';
    case 'SecretFilename':
      return 'Secret-bearing filename';
    case 'SecretContent':
      return 'Secret-bearing content';
    case 'Empty':
      return 'No indexable content';
    default:
      return reason;
  }
}
