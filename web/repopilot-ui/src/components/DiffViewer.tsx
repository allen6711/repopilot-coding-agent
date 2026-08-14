import type { ChangeProposal } from '../api/client';

interface DiffViewerProps {
  readonly proposal: ChangeProposal;
}

type LineKind = 'added' | 'removed' | 'meta' | 'hunk' | 'context';

function classify(line: string): LineKind {
  if (line.startsWith('+++') || line.startsWith('---')) return 'meta';
  if (line.startsWith('@@')) return 'hunk';
  if (line.startsWith('+')) return 'added';
  if (line.startsWith('-')) return 'removed';
  return 'context';
}

/**
 * The proposed change, in full (SC-004, FR-011).
 *
 * Every affected file is listed and the entire diff is rendered. There is no
 * collapsing, no "show more", and no truncation: a reviewer who approves having
 * seen part of a change has approved something they did not read, and FR-020a's
 * "what was shown" stops being a single definite thing. Proposals are capped at
 * creation precisely so that showing all of one is always possible (FR-011a).
 */
export function DiffViewer({ proposal }: DiffViewerProps) {
  const lines = proposal.unifiedDiff.split('\n');

  return (
    <section className="diff-viewer" aria-label="Proposed change">
      <header className="diff-viewer__header">
        <h2>
          {proposal.affectedPaths.length === 1
            ? '1 file changed'
            : `${proposal.affectedPaths.length} files changed`}
        </h2>

        <ul className="diff-viewer__paths">
          {proposal.affectedPaths.map((path) => (
            <li key={path}>
              <code>{path}</code>
            </li>
          ))}
        </ul>
      </header>

      <pre className="diff-viewer__diff" aria-label="Unified diff">
        {lines.map((line, index) => (
          <span
            // Diff lines repeat freely, and their meaning is positional — two
            // identical "}" lines are different lines. The index is the identity.
            key={index}
            className={`diff-line diff-line--${classify(line)}`}
          >
            {line === '' ? ' ' : line}
            {'\n'}
          </span>
        ))}
      </pre>

      <footer className="diff-viewer__footer">
        <span className="diff-viewer__hash-label">Change hash</span>
        {/* Shown because it is what the approval is bound to. A reviewer who
            sees a different hash on the confirmation than on the diff has
            grounds to stop. */}
        <code className="diff-viewer__hash">{proposal.diffHash}</code>
      </footer>
    </section>
  );
}
