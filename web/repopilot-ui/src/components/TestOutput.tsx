import type { TestResult } from '../api/client';

interface TestOutputProps {
  readonly results: readonly TestResult[];
}

function outcomeLabel(result: TestResult): string {
  // A timeout is not a test failure, and SC-009 counts the two separately. A UI
  // that showed both as "failed" would make the distinction the backend keeps
  // invisible to the person acting on it.
  if (result.timedOut) return 'Timed out';
  return result.passed ? 'Passed' : 'Failed';
}

function outcomeModifier(result: TestResult): string {
  if (result.timedOut) return 'timeout';
  return result.passed ? 'pass' : 'fail';
}

/**
 * Results of the sandboxed test runs (FR-028).
 *
 * The output shown here has already been redacted at the container boundary
 * (FR-025b), so this component renders what it is given rather than filtering
 * again — a second, different filter here would be a second thing to keep
 * correct, and the stored copy would still be the authority.
 */
export function TestOutput({ results }: TestOutputProps) {
  if (results.length === 0) {
    return (
      <section className="test-output test-output--empty" aria-label="Test results">
        <h2>Tests</h2>
        <p>No tests have run for this run yet.</p>
      </section>
    );
  }

  return (
    <section className="test-output" aria-label="Test results">
      <h2>Tests</h2>

      {results.map((result) => (
        <article
          key={result.id}
          className={`test-output__result test-output__result--${outcomeModifier(result)}`}
        >
          <header className="test-output__header">
            <code className="test-output__command">{result.commandName}</code>

            <span className="test-output__outcome">{outcomeLabel(result)}</span>

            {result.exitCode !== null && result.exitCode !== undefined && (
              <span className="test-output__exit">exit {result.exitCode}</span>
            )}

            <span className="test-output__duration">{(result.durationMs / 1000).toFixed(1)}s</span>

            {(result.revisionAttempt ?? 0) > 0 && (
              // Which attempt produced this matters: a reviewer looking at a
              // revision needs to know the failure they are reading is the one
              // that prompted it.
              <span className="test-output__attempt">revision {result.revisionAttempt}</span>
            )}
          </header>

          {result.output && <pre className="test-output__log">{result.output}</pre>}
        </article>
      ))}
    </section>
  );
}
