import { useState } from 'react';
import { ApiError, type ChangeProposal } from '../api/client';

interface ApprovalBarProps {
  readonly proposal: ChangeProposal;
  readonly actor: string;
  readonly onActorChange: (actor: string) => void;
  readonly onDecide: (decision: 'approve' | 'reject', diffHash: string) => Promise<unknown>;
}

/**
 * Where a human approves or rejects a proposed change.
 *
 * Two properties of this component are load-bearing rather than cosmetic.
 *
 * The hash sent with the decision is read from the `proposal` this component
 * was rendered with — the same object the diff above was rendered from. It is
 * never re-fetched at click time. If a background refresh replaced the proposal
 * between render and click, the echoed hash is the one the reviewer actually
 * saw, and the server refuses the decision (FR-020a). Re-reading it fresh would
 * make the check pass while the reviewer approved something they never read.
 *
 * The actor is entered here, alongside the decision, because that is what is
 * recorded as having made it (FR-015a). It is attributable, not authenticated,
 * and the label says so — presenting an unverified name as an identity check
 * would misrepresent what the record means.
 */
export function ApprovalBar({ proposal, actor, onActorChange, onDecide }: ApprovalBarProps) {
  const [pending, setPending] = useState<'approve' | 'reject' | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [conflict, setConflict] = useState(false);

  const alreadyDecided = proposal.decisionStatus !== 'pending';
  const actorMissing = actor.trim().length === 0;
  const disabled = pending !== null || alreadyDecided || conflict;

  async function decide(decision: 'approve' | 'reject') {
    if (actorMissing) {
      setError('Enter who is making this decision before approving or rejecting.');
      return;
    }

    setPending(decision);
    setError(null);

    try {
      // proposal.diffHash, captured at render. See the note above.
      await onDecide(decision, proposal.diffHash);
    } catch (thrown) {
      if (thrown instanceof ApiError) {
        setError(thrown.message);

        // A decided proposal or an ended run will not become decidable by
        // trying again, so the buttons stay down rather than inviting a retry
        // that cannot succeed.
        setConflict(thrown.isConflict);
      } else {
        setError('The decision could not be recorded. Nothing was applied.');
      }
    } finally {
      setPending(null);
    }
  }

  if (alreadyDecided) {
    return (
      <section className="approval-bar approval-bar--decided" aria-label="Decision">
        <p className="approval-bar__decided">This change was already {proposal.decisionStatus}.</p>
      </section>
    );
  }

  return (
    <section className="approval-bar" aria-label="Approve or reject this change">
      <div className="approval-bar__actor">
        <label htmlFor="approval-actor">Your name or email</label>
        <input
          id="approval-actor"
          type="text"
          value={actor}
          onChange={(event) => onActorChange(event.target.value)}
          placeholder="you@example.com"
          autoComplete="off"
          aria-describedby="approval-actor-note"
        />
        <p id="approval-actor-note" className="approval-bar__actor-note">
          Recorded with the decision. Attributable, not verified.
        </p>
      </div>

      <div className="approval-bar__actions">
        <button
          type="button"
          className="approval-bar__approve"
          onClick={() => decide('approve')}
          disabled={disabled || actorMissing}
        >
          {pending === 'approve' ? 'Approving…' : 'Approve and apply'}
        </button>

        <button
          type="button"
          className="approval-bar__reject"
          onClick={() => decide('reject')}
          disabled={disabled || actorMissing}
        >
          {pending === 'reject' ? 'Rejecting…' : 'Reject'}
        </button>
      </div>

      {error && (
        <p className="approval-bar__error" role="alert">
          {error}
        </p>
      )}

      <p className="approval-bar__binding">
        Approving applies exactly the change shown above, identified by{' '}
        <code>{proposal.diffHash.slice(0, 12)}…</code>
      </p>
    </section>
  );
}
