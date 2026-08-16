import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import { ApprovalBar } from './ApprovalBar';
import { DiffViewer } from './DiffViewer';
import { TestOutput } from './TestOutput';
import type { ChangeProposal, TestResult } from '../api/client';

const HASH = 'a'.repeat(64);

function proposal(overrides: Partial<ChangeProposal> = {}): ChangeProposal {
  return {
    id: '11111111-1111-1111-1111-111111111111',
    runId: '22222222-2222-2222-2222-222222222222',
    affectedPaths: ['src/Orders/OrderLookupService.cs'],
    unifiedDiff:
      '--- a/src/Orders/OrderLookupService.cs\n' +
      '+++ b/src/Orders/OrderLookupService.cs\n' +
      '@@ -20,7 +20,7 @@\n' +
      '-            order.ShippingAddress.City,\n' +
      '+            order.ShippingAddress?.City,\n',
    diffHash: HASH,
    decisionStatus: 'pending',
    ...overrides,
  };
}

function testResult(overrides: Partial<TestResult> = {}): TestResult {
  return {
    id: '33333333-3333-3333-3333-333333333333',
    runId: '22222222-2222-2222-2222-222222222222',
    commandName: 'unit',
    passed: true,
    exitCode: 0,
    output: 'Passed!  - Failed: 0, Passed: 3\n',
    durationMs: 4200,
    timedOut: false,
    revisionAttempt: 0,
    ...overrides,
  } as TestResult;
}

/**
 * The review view is operable without a pointer.
 *
 * SC-004 says a reviewer can decide without leaving the view, and nothing in it
 * says "a reviewer who can use a mouse". These are the properties that make the
 * claim hold for everyone: the decision controls are reachable and operable by
 * keyboard, the content the decision is about can be scrolled to, and anything
 * that stops a decision says so somewhere a screen reader will read it.
 *
 * Written as behaviour rather than as a checklist of attributes, because an
 * attribute assertion passes just as happily when the attribute has been moved
 * to an element that does nothing.
 */
describe('review view accessibility', () => {
  it('reaches the decision controls by keyboard, in the order they are used', async () => {
    render(
      <ApprovalBar
        proposal={proposal()}
        actor="reviewer@example.com"
        onActorChange={() => {}}
        onDecide={vi.fn().mockResolvedValue({})}
      />,
    );

    // Who is deciding, then what they decided. Tabbing into the buttons before
    // the name field would invite a decision before its attribution.
    await userEvent.tab();
    expect(screen.getByLabelText(/your name or email/i)).toHaveFocus();

    await userEvent.tab();
    expect(screen.getByRole('button', { name: /approve/i })).toHaveFocus();

    await userEvent.tab();
    expect(screen.getByRole('button', { name: /reject/i })).toHaveFocus();
  });

  it('approves from the keyboard alone', async () => {
    const onDecide = vi.fn().mockResolvedValue({});

    render(
      <ApprovalBar
        proposal={proposal()}
        actor="reviewer@example.com"
        onActorChange={() => {}}
        onDecide={onDecide}
      />,
    );

    const approve = screen.getByRole('button', { name: /approve/i });
    approve.focus();

    await userEvent.keyboard('{Enter}');

    expect(onDecide).toHaveBeenCalledWith('approve', HASH);
  });

  it('rejects from the keyboard alone', async () => {
    const onDecide = vi.fn().mockResolvedValue({});

    render(
      <ApprovalBar
        proposal={proposal()}
        actor="reviewer@example.com"
        onActorChange={() => {}}
        onDecide={onDecide}
      />,
    );

    const reject = screen.getByRole('button', { name: /reject/i });
    reject.focus();

    // Space, not Enter. Both activate a button and a keyboard user may use
    // either; a control that only answers one of them is half-operable.
    await userEvent.keyboard(' ');

    expect(onDecide).toHaveBeenCalledWith('reject', HASH);
  });

  it('names the decision region with a heading rather than only a label', () => {
    render(
      <ApprovalBar
        proposal={proposal()}
        actor="reviewer@example.com"
        onActorChange={() => {}}
        onDecide={vi.fn().mockResolvedValue({})}
      />,
    );

    // Heading navigation is how a screen-reader user moves through a long page,
    // and the decision point is the one place they must be able to land on.
    expect(
      screen.getByRole('heading', { name: /approve or reject this change/i }),
    ).toBeInTheDocument();
  });

  it('binds the approve button to the statement of what it applies', () => {
    render(
      <ApprovalBar
        proposal={proposal()}
        actor="reviewer@example.com"
        onActorChange={() => {}}
        onDecide={vi.fn().mockResolvedValue({})}
      />,
    );

    const approve = screen.getByRole('button', { name: /approve/i });
    const describedBy = approve.getAttribute('aria-describedby');

    expect(describedBy).toBeTruthy();
    expect(document.getElementById(describedBy!)).toHaveTextContent(
      /applies exactly the change shown above/i,
    );
  });

  it('marks the button busy while a decision is in flight', async () => {
    let release: (value: unknown) => void = () => {};
    const onDecide = vi.fn().mockReturnValue(new Promise((resolve) => (release = resolve)));

    render(
      <ApprovalBar
        proposal={proposal()}
        actor="reviewer@example.com"
        onActorChange={() => {}}
        onDecide={onDecide}
      />,
    );

    const approve = screen.getByRole('button', { name: /approve/i });
    await userEvent.click(approve);

    // The label already changes to "Approving…". aria-busy says the same thing
    // in the channel a screen reader listens to for it.
    expect(screen.getByRole('button', { name: /approving/i })).toHaveAttribute(
      'aria-busy',
      'true',
    );

    release({});
  });

  it('makes the diff reachable by keyboard, since it scrolls', () => {
    render(<DiffViewer proposal={proposal()} />);

    const diff = screen.getByRole('region', { name: /unified diff/i });

    // A horizontally scrolling region that cannot take focus is content a
    // keyboard user cannot read to the end of — and SC-004 wants the whole diff
    // seen before a decision.
    expect(diff).toHaveAttribute('tabindex', '0');
  });

  it('lists every affected path as text, not only as colour in the diff', () => {
    render(<DiffViewer proposal={proposal()} />);

    // Added and removed lines are distinguished by colour and by their leading
    // + and -. The file list is the non-colour answer to "what does this touch".
    expect(screen.getByText('src/Orders/OrderLookupService.cs')).toBeInTheDocument();
    expect(screen.getByRole('heading', { name: /1 file changed/i })).toBeInTheDocument();
  });

  it('names each test log by the command that produced it', () => {
    render(<TestOutput results={[testResult(), testResult({ id: 'other', commandName: 'build' })]} />);

    // Two logs on one page need distinguishable names, or a screen-reader user
    // moving between regions cannot tell which output they are in.
    expect(screen.getByRole('region', { name: /output of unit/i })).toBeInTheDocument();
    expect(screen.getByRole('region', { name: /output of build/i })).toBeInTheDocument();
  });

  it('distinguishes a timeout from a failure in text', () => {
    render(<TestOutput results={[testResult({ passed: false, timedOut: true, exitCode: null })]} />);

    // SC-009 counts the two separately, and a reviewer acting on the result
    // needs the same distinction. Conveyed as words, not only as a border colour.
    expect(screen.getByText(/timed out/i)).toBeInTheDocument();
  });
});
