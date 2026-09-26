import { useEffect, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { ApiError, api, isTerminal, stageLabel } from '../api/client';
import { ApprovalBar } from '../components/ApprovalBar';
import { DiffViewer } from '../components/DiffViewer';
import { PlanPanel } from '../components/PlanPanel';
import { RunTimeline } from '../components/RunTimeline';
import { TestOutput } from '../components/TestOutput';
import { useRunStream } from '../hooks/useRunStream';
import { navigate } from '../navigation';

interface RunDetailProps {
  readonly runId: string;
}

const ACTOR_STORAGE_KEY = 'repopilot.actor';

/**
 * The review view: everything a reviewer needs to decide, on one page.
 *
 * Composition order follows the decision: what the agent intended, what it
 * would change, what the tests said, and only then the controls that act on it
 * (FR-010, SC-004).
 */
export function RunDetail({ runId }: RunDetailProps) {
  const queryClient = useQueryClient();

  const [actor, setActor] = useState(() => localStorage.getItem(ACTOR_STORAGE_KEY) ?? '');

  // Why a cancellation was not attempted, as opposed to why one failed. Held
  // separately from the mutation's error so the two cannot overwrite each other.
  const [cancelBlocked, setCancelBlocked] = useState<string | null>(null);

  function rememberActor(value: string) {
    setActor(value);
    // Convenience only. It is not a credential, and the server treats whatever
    // arrives as attributable rather than authenticated.
    localStorage.setItem(ACTOR_STORAGE_KEY, value);
  }

  // The event stream is the live view (FR-028a). The run itself is still
  // fetched, because a stage is a fact about the run rather than something a
  // client should derive by replaying events — but the stream is what makes it
  // arrive without a refresh, so the poll exists only as a fallback for a
  // browser with no EventSource.
  const stream = useRunStream(runId);

  const run = useQuery({
    queryKey: ['run', runId],
    queryFn: () => api.getRun(runId),
    // Polls while the run is live and stops once it ends. A finished run's
    // state cannot change, so continuing to poll would be pure noise.
    refetchInterval: (query) =>
      query.state.data && isTerminal(query.state.data.stage) ? false : 2000,
  });

  // Refetch on each event rather than on a timer: the stream already knows
  // something changed, and waiting out the poll interval after it would be a
  // delay the system has no reason to have.
  const eventCount = stream.events.length;

  useEffect(() => {
    if (eventCount > 0) {
      void queryClient.invalidateQueries({ queryKey: ['run', runId] });
      void queryClient.invalidateQueries({ queryKey: ['proposal', runId] });
      void queryClient.invalidateQueries({ queryKey: ['tests', runId] });
    }
  }, [eventCount, queryClient, runId]);

  const proposal = useQuery({
    queryKey: ['proposal', runId],
    queryFn: () => api.getProposal(runId),
    // A run without a proposal yet answers 404, which is an expected state
    // rather than an error worth retrying into.
    retry: (_, error) => !(error instanceof ApiError && error.status === 404),
    enabled: run.data !== undefined,
  });

  const tests = useQuery({
    queryKey: ['tests', runId],
    queryFn: () => api.getTests(runId),
    enabled: run.data !== undefined,
  });

  const decide = useMutation({
    mutationFn: ({ decision, diffHash }: { decision: 'approve' | 'reject'; diffHash: string }) =>
      api.decide(runId, proposal.data!.id, decision, diffHash, { value: actor }),
    onSuccess: () => {
      // Refetch rather than patching the cache: the decision moved the run's
      // stage on the server, and guessing which stage it moved to would be the
      // UI deciding a transition the backend owns (Principle IV).
      void queryClient.invalidateQueries({ queryKey: ['run', runId] });
      void queryClient.invalidateQueries({ queryKey: ['proposal', runId] });
    },
  });

  const cancel = useMutation({
    mutationFn: () => api.cancel(runId, { value: actor }),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['run', runId] });
    },
  });

  if (run.isPending) {
    return <p className="run-detail__loading">Loading run…</p>;
  }

  if (run.isError) {
    return (
      <p className="run-detail__error" role="alert">
        {run.error instanceof ApiError ? run.error.message : 'This run could not be loaded.'}
      </p>
    );
  }

  const current = run.data;
  const awaitingDecision = current.stage === 'awaiting_approval';

  return (
    <article className="run-detail">
      <header className="run-detail__header">
        <button
          type="button"
          className="run-detail__back"
          onClick={() => navigate({ view: 'runs', runId: null })}
        >
          ← All runs
        </button>

        <h1>{current.taskDescription}</h1>

        {/* Announced when it changes. The stage arrives over the event stream
            without a page change, so a reviewer who cannot see it move has no
            way to learn that the run is now waiting for them (FR-028a). Polite
            rather than assertive: it should not interrupt someone mid-sentence
            in the diff. */}
        <p
          className="run-detail__stage"
          data-stage={current.stage}
          aria-live="polite"
          aria-atomic="true"
        >
          {stageLabel(current.stage)}
          {current.outcomeReason && (
            <span className="run-detail__reason">
              {' '}
              — {current.outcomeReason.replace(/_/g, ' ')}
            </span>
          )}
        </p>

        {!isTerminal(current.stage) && (
          <button
            type="button"
            className="run-detail__cancel"
            onClick={() => {
              if (actor.trim().length === 0) {
                setCancelBlocked('Enter your name below before cancelling.');
                return;
              }

              setCancelBlocked(null);
              cancel.mutate();
            }}
            // Not disabled on a missing name, for the same reason the approve
            // button is not: a disabled control leaves the tab order, so a
            // keyboard user meets nothing at all rather than meeting something
            // that explains itself. A tooltip is no substitute — it is not
            // announced and cannot be reached without a pointer.
            disabled={cancel.isPending}
            aria-busy={cancel.isPending}
          >
            {cancel.isPending ? 'Cancelling…' : 'Cancel run'}
          </button>
        )}

        <div className="run-detail__cancel-error" role="alert" aria-live="assertive">
          {cancelBlocked && <p className="run-detail__error">{cancelBlocked}</p>}

          {cancel.isError && (
            <p className="run-detail__error">
              {cancel.error instanceof ApiError
                ? cancel.error.message
                : 'The run could not be cancelled.'}
            </p>
          )}
        </div>
      </header>

      <PlanPanel plan={current.plan} />

      {proposal.data ? (
        <>
          <DiffViewer proposal={proposal.data} />

          {awaitingDecision && (
            <ApprovalBar
              proposal={proposal.data}
              actor={actor}
              onActorChange={rememberActor}
              onDecide={(decision, diffHash) => decide.mutateAsync({ decision, diffHash })}
            />
          )}
        </>
      ) : (
        <p className="run-detail__no-proposal">
          {isTerminal(current.stage)
            ? 'This run ended without proposing a change.'
            : 'No change has been proposed yet.'}
        </p>
      )}

      <TestOutput results={tests.data ?? []} />

      <RunTimeline events={stream.events} reconnecting={stream.reconnecting} />
    </article>
  );
}
