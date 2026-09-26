import type { RunEvent } from '../api/client';

interface RunTimelineProps {
  readonly events: readonly RunEvent[];
  readonly reconnecting: boolean;
}

/**
 * Every recorded action, in the order it happened (FR-027, FR-028a).
 *
 * This is the answer to "what did the agent actually do" — which files it
 * looked at, which actions failed, how long each took. It shows failures as
 * prominently as successes: a refused path access or an exhausted context
 * budget is the most interesting thing in a run, and a timeline that only
 * listed what worked would hide exactly the entries worth reading.
 */
export function RunTimeline({ events, reconnecting }: RunTimelineProps) {
  if (events.length === 0) {
    return (
      <section className="run-timeline" aria-label="Activity">
        <h2>Activity</h2>
        <p className="run-timeline__empty">Nothing has been recorded yet.</p>
      </section>
    );
  }

  return (
    <section className="run-timeline" aria-label="Activity">
      <h2>
        Activity
        {reconnecting && <span className="run-timeline__reconnecting"> — reconnecting…</span>}
      </h2>

      <ol className="run-timeline__entries">
        {events.map((event) => (
          <li
            key={event.sequence}
            className={`run-timeline__entry run-timeline__entry--${event.status}`}
            data-event-type={event.eventType}
          >
            <span className="run-timeline__sequence">{event.sequence}</span>

            <span className="run-timeline__label">
              {event.toolName ?? humanize(event.eventType)}
            </span>

            <span className="run-timeline__detail">{describe(event)}</span>

            {event.durationMs !== null && event.durationMs !== undefined && (
              <span className="run-timeline__duration">{event.durationMs} ms</span>
            )}

            {event.status === 'failed' && <span className="run-timeline__status">failed</span>}

            {/* The error is shown in full rather than behind a disclosure. It
                has already been redacted on the way in, and a reader scanning
                for what went wrong should not have to click. */}
            {event.errorMessage && <p className="run-timeline__error">{event.errorMessage}</p>}
          </li>
        ))}
      </ol>
    </section>
  );
}

/**
 * A one-line account of what the entry was about, read from the summary.
 *
 * The summary is an object, not a string, so this reads named fields rather
 * than pattern-matching text — PostgreSQL normalizes the stored JSON, and
 * anything depending on its formatting would be unreliable.
 */
function describe(event: RunEvent): string {
  const summary = event.argumentsSummary as Record<string, unknown> | null | undefined;

  if (!summary) {
    return '';
  }

  switch (event.eventType) {
    case 'stage_changed':
      return `${summary.fromStage} → ${summary.toStage}`;

    case 'stage_transition_rejected':
      return `${summary.attemptedTrigger} not permitted from ${summary.fromStage}`;

    case 'run_failed':
      return `at ${summary.failureStage}: ${summary.outcomeReason}`;

    case 'run_ended':
      return `${summary.terminalOutcome}`;

    case 'approval_recorded':
      return `${summary.decision} by ${summary.by}`;

    default:
      return firstMeaningfulValue(summary);
  }
}

/**
 * For a tool call, the most identifying field the summary happens to carry —
 * a path, a query, a command name.
 */
function firstMeaningfulValue(summary: Record<string, unknown>): string {
  for (const key of ['path', 'query', 'command', 'command_name', 'directory']) {
    const value = summary[key];

    if (typeof value === 'string' && value.length > 0) {
      return value;
    }
  }

  const paths = summary.affected_paths;

  if (Array.isArray(paths) && paths.length > 0) {
    return paths.join(', ');
  }

  return '';
}

function humanize(eventType: string): string {
  return eventType.replace(/_/g, ' ');
}
