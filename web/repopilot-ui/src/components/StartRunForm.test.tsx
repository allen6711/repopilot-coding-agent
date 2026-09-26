import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { StartRunForm } from './StartRunForm';
import { renderWithQuery, stubFetch } from '../test-utils';

const REPOSITORY = '44444444-4444-4444-4444-444444444444';
const RUN = '55555555-5555-5555-5555-555555555555';

function createdRun() {
  return {
    id: RUN,
    repositoryId: REPOSITORY,
    taskDescription: 'Guard the null shipping address.',
    stage: 'created',
    createdAt: '2026-09-25T10:00:00Z',
    revisionAttempt: 0,
    toolsEnabled: true,
  };
}

afterEach(() => {
  vi.unstubAllGlobals();
  window.history.pushState(null, '', '/');
});

/** FR-007, US1/AC1: the entry point to the P1 story. */
describe('StartRunForm', () => {
  it('starts a run from a description and opens its review view', async () => {
    const fetched = stubFetch([
      { match: (url, init) => url === '/api/runs' && init?.method === 'POST', body: createdRun() },
    ]);

    renderWithQuery(<StartRunForm repositoryId={REPOSITORY} indexed />);

    await userEvent.type(
      screen.getByLabelText(/what should the agent do/i),
      'Guard the null shipping address.',
    );
    await userEvent.click(screen.getByRole('button', { name: /start run/i }));

    // The run the operator started is the run they land on. A started run they
    // cannot find is indistinguishable from one that never started.
    await waitFor(() => expect(window.location.search).toBe(`?run=${RUN}`));

    const body = JSON.parse(String(fetched.mock.calls[0][1].body));
    expect(body).toMatchObject({
      repositoryId: REPOSITORY,
      taskDescription: 'Guard the null shipping address.',
      toolsEnabled: true,
    });
  });

  it('accepts a seeded task id instead of a description', async () => {
    const fetched = stubFetch([
      { match: (url, init) => url === '/api/runs' && init?.method === 'POST', body: createdRun() },
    ]);

    renderWithQuery(<StartRunForm repositoryId={REPOSITORY} indexed />);

    await userEvent.type(screen.getByLabelText(/seeded task id/i), 'bugfix-null-guard-01');
    await userEvent.click(screen.getByRole('button', { name: /start run/i }));

    await waitFor(() => expect(fetched).toHaveBeenCalled());

    // FR-007 offers the two as alternatives, so an omitted description is
    // omitted rather than sent as an empty string the server would refuse.
    const body = JSON.parse(String(fetched.mock.calls[0][1].body));
    expect(body.seededTaskId).toBe('bugfix-null-guard-01');
    expect(body.taskDescription).toBeUndefined();
  });

  it('refuses to submit with neither a description nor a seeded id', () => {
    stubFetch([]);
    renderWithQuery(<StartRunForm repositoryId={REPOSITORY} indexed />);

    expect(screen.getByRole('button', { name: /start run/i })).toBeDisabled();
  });

  it('is inert until the fixture has an index, and says why', async () => {
    stubFetch([]);
    renderWithQuery(<StartRunForm repositoryId={REPOSITORY} indexed={false} />);

    expect(screen.getByLabelText(/what should the agent do/i)).toBeDisabled();
    expect(screen.getByRole('button', { name: /start run/i })).toBeDisabled();

    // The reason is on screen before the attempt, not after a 409.
    expect(screen.getByText(/build the index first/i)).toBeInTheDocument();
  });

  it("shows the server's reason when a run is refused", async () => {
    stubFetch([
      {
        match: (url, init) => url === '/api/runs' && init?.method === 'POST',
        status: 409,
        body: {
          title: 'Repository is not indexed',
          detail: "'sample-dotnet-api' has no active index with indexed files.",
        },
      },
    ]);

    renderWithQuery(<StartRunForm repositoryId={REPOSITORY} indexed />);

    await userEvent.type(screen.getByLabelText(/what should the agent do/i), 'Anything.');
    await userEvent.click(screen.getByRole('button', { name: /start run/i }));

    expect(await screen.findByRole('alert')).toHaveTextContent('has no active index');
    expect(window.location.search).toBe('');
  });
});
