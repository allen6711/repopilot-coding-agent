import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import { ApprovalBar } from './ApprovalBar';
import { ApiError, type ChangeProposal } from '../api/client';

const HASH = 'a'.repeat(64);

function proposal(overrides: Partial<ChangeProposal> = {}): ChangeProposal {
  return {
    id: '11111111-1111-1111-1111-111111111111',
    runId: '22222222-2222-2222-2222-222222222222',
    affectedPaths: ['src/Service.cs'],
    unifiedDiff: '--- a\n+++ b\n',
    diffHash: HASH,
    decisionStatus: 'pending',
    ...overrides,
  };
}

function renderBar(
  onDecide: (decision: 'approve' | 'reject', hash: string) => Promise<unknown>,
  overrides: Partial<ChangeProposal> = {},
  actor = 'reviewer@example.com',
) {
  return render(
    <ApprovalBar
      proposal={proposal(overrides)}
      actor={actor}
      onActorChange={() => {}}
      onDecide={onDecide}
    />,
  );
}

describe('ApprovalBar', () => {
  it('sends the hash of the proposal it was rendered with', async () => {
    const onDecide = vi.fn().mockResolvedValue({});
    renderBar(onDecide);

    await userEvent.click(screen.getByRole('button', { name: /approve/i }));

    // FR-020a. The hash echoed back is the one belonging to the diff the
    // reviewer was looking at, not one read fresh at click time.
    expect(onDecide).toHaveBeenCalledWith('approve', HASH);
  });

  it('sends the stale hash when the proposal changed underneath', async () => {
    const onDecide = vi.fn().mockResolvedValue({});
    const { rerender } = renderBar(onDecide);

    // A background refresh replaces the proposal. The reviewer is still looking
    // at what was on screen when they decided to click.
    rerender(
      <ApprovalBar
        proposal={proposal({ diffHash: 'b'.repeat(64) })}
        actor="reviewer@example.com"
        onActorChange={() => {}}
        onDecide={onDecide}
      />,
    );

    await userEvent.click(screen.getByRole('button', { name: /approve/i }));

    // Sending the new hash would make the server's check pass while the
    // reviewer approved content they never read. Sending the one they saw lets
    // the server refuse — which is the outcome that protects them.
    expect(onDecide).toHaveBeenCalledWith('approve', 'b'.repeat(64));
  });

  it('refuses to submit without an actor', async () => {
    const onDecide = vi.fn().mockResolvedValue({});
    renderBar(onDecide, {}, '   ');

    const approve = screen.getByRole('button', { name: /approve/i });

    // SC-015: an unattributable decision is not offered in the first place.
    expect(approve).toBeDisabled();
    expect(onDecide).not.toHaveBeenCalled();
  });

  it('shows the server’s reason when a decision is refused', async () => {
    const onDecide = vi.fn().mockRejectedValue(
      new ApiError(422, {
        title: 'Diff hash does not match',
        detail: 'The decision names a different hash from the stored proposal.',
        status: 422,
      }),
    );

    renderBar(onDecide);

    await userEvent.click(screen.getByRole('button', { name: /approve/i }));

    // The specific reason, not a generic failure — a reviewer needs to tell a
    // stale view from a mistake.
    expect(await screen.findByRole('alert')).toHaveTextContent(
      /different hash from the stored proposal/i,
    );
  });

  it('stops offering a retry once the proposal is already decided', async () => {
    const onDecide = vi.fn().mockRejectedValue(
      new ApiError(409, {
        title: 'Proposal already decided',
        detail: 'This proposal already has a decision.',
        status: 409,
      }),
    );

    renderBar(onDecide);

    await userEvent.click(screen.getByRole('button', { name: /approve/i }));
    await screen.findByRole('alert');

    // Trying again cannot succeed, so the buttons stay down rather than
    // inviting a click that will always fail.
    expect(screen.getByRole('button', { name: /approve/i })).toBeDisabled();
    expect(screen.getByRole('button', { name: /reject/i })).toBeDisabled();
  });

  it('offers no controls for a proposal that already has a decision', () => {
    const onDecide = vi.fn();
    renderBar(onDecide, { decisionStatus: 'approved' });

    // FR-018: a second decision is refused by the server. Not presenting the
    // buttons means the reviewer is not invited into a refusal.
    expect(screen.queryByRole('button', { name: /approve/i })).toBeNull();
    expect(screen.getByText(/already approved/i)).toBeInTheDocument();
  });

  it('shows what the approval is bound to', () => {
    renderBar(vi.fn());

    expect(screen.getByText(new RegExp(HASH.slice(0, 12)))).toBeInTheDocument();
  });
});
