import { useState } from 'react';
import { useMutation } from '@tanstack/react-query';
import { ApiError, api } from '../api/client';
import { navigate } from '../navigation';

interface StartRunFormProps {
  readonly repositoryId: string;
  /** Whether the fixture has an active index with files in it. */
  readonly indexed: boolean;
}

/**
 * Starts a run against one fixture (FR-007, US1/AC1).
 *
 * The entry point to the P1 story. A reviewer describes a task here and lands on
 * the review view for the run it created — which is the only navigation this
 * form does, because a run that has been started and cannot be found is
 * indistinguishable from one that was never started.
 *
 * A seeded task id is offered alongside the description rather than instead of
 * it: FR-007 names the two as alternatives, and the seeded ids are what the
 * committed evaluation set is addressed by, so an operator reproducing a
 * measured task needs to be able to name one.
 */
export function StartRunForm({ repositoryId, indexed }: StartRunFormProps) {
  const [description, setDescription] = useState('');
  const [seededTaskId, setSeededTaskId] = useState('');

  const start = useMutation({
    mutationFn: () =>
      api.createRun(repositoryId, {
        taskDescription: description,
        seededTaskId,
      }),
    onSuccess: (run) => {
      setDescription('');
      setSeededTaskId('');
      navigate({ view: 'runs', runId: run.id });
    },
  });

  // Either one is enough, which is what the server enforces. Mirrored here so
  // the control is disabled rather than the request refused — a 422 telling the
  // operator what they could have been told before pressing the button.
  const hasTask = description.trim().length > 0 || seededTaskId.trim().length > 0;

  return (
    <form
      className="start-run"
      onSubmit={(event) => {
        event.preventDefault();

        if (hasTask && indexed) {
          start.mutate();
        }
      }}
    >
      <h3>Start a run</h3>

      <label htmlFor={`task-${repositoryId}`}>What should the agent do?</label>
      <textarea
        id={`task-${repositoryId}`}
        value={description}
        rows={3}
        onChange={(event) => setDescription(event.target.value)}
        placeholder="Fix the null reference when an order has no shipping address."
        disabled={!indexed || start.isPending}
      />

      <label htmlFor={`seeded-${repositoryId}`}>
        Or a seeded task id <span className="start-run__optional">(optional)</span>
      </label>
      <input
        id={`seeded-${repositoryId}`}
        value={seededTaskId}
        onChange={(event) => setSeededTaskId(event.target.value)}
        placeholder="bugfix-null-guard-01"
        autoComplete="off"
        disabled={!indexed || start.isPending}
      />

      <button type="submit" disabled={!indexed || !hasTask || start.isPending}>
        {start.isPending ? 'Starting…' : 'Start run'}
      </button>

      {!indexed && (
        <p className="start-run__blocked">
          Build the index first. A run against an empty index cannot retrieve anything, so it would
          end reporting insufficient context.
        </p>
      )}

      {start.isError && (
        <p className="start-run__error" role="alert">
          {start.error instanceof ApiError ? start.error.message : 'The run could not be started.'}
        </p>
      )}
    </form>
  );
}
