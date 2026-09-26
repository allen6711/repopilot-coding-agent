import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { RunList } from './RunList';
import { renderWithQuery, stubFetch } from '../test-utils';

const REPOSITORY = '44444444-4444-4444-4444-444444444444';
const AWAITING = '55555555-5555-5555-5555-555555555555';
const DONE = '66666666-6666-6666-6666-666666666666';

const repositories = [
  { id: REPOSITORY, slug: 'sample-dotnet-api', indexingStatus: 'indexed' },
];

const runs = [
  {
    id: AWAITING,
    repositoryId: REPOSITORY,
    taskDescription: 'Guard the null shipping address.',
    stage: 'awaiting_approval',
    terminalOutcome: null,
    createdAt: '2026-09-25T10:00:00Z',
  },
  {
    id: DONE,
    repositoryId: REPOSITORY,
    taskDescription: 'Cap the page size.',
    stage: 'succeeded',
    terminalOutcome: 'tests_passed',
    createdAt: '2026-09-24T09:00:00Z',
  },
];

function stubList(runRows: unknown = runs) {
  return stubFetch([
    { match: (url) => url === '/api/repositories', body: repositories },
    { match: (url) => url.startsWith('/api/runs'), body: runRows },
  ]);
}

afterEach(() => {
  vi.unstubAllGlobals();
  window.history.pushState(null, '', '/');
});

/** US1/AC5: any run is findable and openable without knowing its id. */
describe('RunList', () => {
  it('lists each run with its stage and repository', async () => {
    stubList();
    renderWithQuery(<RunList />);

    expect(await screen.findByText('Guard the null shipping address.')).toBeInTheDocument();
    expect(screen.getByText('Awaiting your decision')).toBeInTheDocument();
    expect(screen.getAllByText('sample-dotnet-api').length).toBeGreaterThan(0);
    expect(screen.getByText('tests_passed')).toBeInTheDocument();
  });

  it('opens the review view for the run that was clicked', async () => {
    stubList();
    renderWithQuery(<RunList />);

    await userEvent.click(await screen.findByRole('button', { name: /cap the page size/i }));

    await waitFor(() => expect(window.location.search).toBe(`?run=${DONE}`));
  });

  it('counts the runs waiting on a person', async () => {
    stubList();
    renderWithQuery(<RunList />);

    // FR-013b: a run awaiting approval holds a working copy and blocks nobody
    // else, so it will sit there indefinitely unless a reviewer is told.
    expect(await screen.findByText('1 run is awaiting your decision.')).toBeInTheDocument();
  });

  it('says when there is nothing yet, and where runs come from', async () => {
    stubList([]);
    renderWithQuery(<RunList />);

    expect(await screen.findByText(/no runs yet/i)).toBeInTheDocument();
  });

  it('filters by repository', async () => {
    const fetched = stubList();
    renderWithQuery(<RunList />);

    await screen.findByText('Guard the null shipping address.');
    await userEvent.selectOptions(screen.getByLabelText(/repository/i), REPOSITORY);

    await waitFor(() =>
      expect(
        fetched.mock.calls.some(([url]) => String(url) === `/api/runs?repositoryId=${REPOSITORY}`),
      ).toBe(true),
    );
  });
});
