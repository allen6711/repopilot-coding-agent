import { render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { DiffViewer } from './DiffViewer';
import { PlanPanel } from './PlanPanel';
import { TestOutput } from './TestOutput';
import type { ChangeProposal, TestResult } from '../api/client';

const proposal: ChangeProposal = {
  id: '11111111-1111-1111-1111-111111111111',
  runId: '22222222-2222-2222-2222-222222222222',
  affectedPaths: ['src/Orders/OrderLookupService.cs', 'src/Orders/OrderView.cs'],
  unifiedDiff: [
    '--- a/src/Orders/OrderLookupService.cs',
    '+++ b/src/Orders/OrderLookupService.cs',
    '@@ -20,6 +20,9 @@',
    '     var order = _repository.Find(orderId);',
    '-    return Project(order);',
    '+    if (order is null) return null;',
    '+    return Project(order);',
  ].join('\n'),
  diffHash: 'c'.repeat(64),
  decisionStatus: 'pending',
};

describe('DiffViewer', () => {
  it('lists every affected file', () => {
    render(<DiffViewer proposal={proposal} />);

    // SC-004: all of them, not the first few. A reviewer who cannot see that a
    // second file changed cannot have consented to it changing.
    expect(screen.getByText('src/Orders/OrderLookupService.cs')).toBeInTheDocument();
    expect(screen.getByText('src/Orders/OrderView.cs')).toBeInTheDocument();
    expect(screen.getByText('2 files changed')).toBeInTheDocument();
  });

  it('renders every line of the diff', () => {
    render(<DiffViewer proposal={proposal} />);

    const rendered = screen.getByLabelText('Unified diff').textContent ?? '';

    for (const line of proposal.unifiedDiff.split('\n')) {
      expect(rendered).toContain(line);
    }
  });

  it('distinguishes additions from removals', () => {
    const { container } = render(<DiffViewer proposal={proposal} />);

    expect(container.querySelectorAll('.diff-line--added')).toHaveLength(2);
    expect(container.querySelectorAll('.diff-line--removed')).toHaveLength(1);
  });

  it('shows the hash the approval will be bound to', () => {
    render(<DiffViewer proposal={proposal} />);

    expect(screen.getByText(proposal.diffHash)).toBeInTheDocument();
  });
});

describe('PlanPanel', () => {
  it('shows the plan', () => {
    render(<PlanPanel plan={'Guard the lookup result.\n\nLeave projection alone.'} />);

    expect(screen.getByText('Guard the lookup result.')).toBeInTheDocument();
    expect(screen.getByText('Leave projection alone.')).toBeInTheDocument();
  });

  it('says so when no plan was recorded', () => {
    render(<PlanPanel plan={null} />);

    // A run reaches a proposal only through a plan, so a missing one is worth
    // surfacing rather than rendering as blank space.
    expect(screen.getByText(/no plan was recorded/i)).toBeInTheDocument();
  });
});

function result(overrides: Partial<TestResult> = {}): TestResult {
  return {
    id: '33333333-3333-3333-3333-333333333333',
    runId: '22222222-2222-2222-2222-222222222222',
    commandName: 'unit',
    passed: false,
    exitCode: 1,
    output: '1 failed, 2 passed',
    durationMs: 4200,
    timedOut: false,
    ...overrides,
  };
}

describe('TestOutput', () => {
  it('reads a timeout differently from a failure', () => {
    render(
      <TestOutput
        results={[
          result({ id: 'a', passed: false, timedOut: false }),
          result({ id: 'b', passed: false, timedOut: true, exitCode: null }),
        ]}
      />,
    );

    // SC-009 counts clean finishes and forced terminations separately. If the
    // UI collapsed them, the distinction the backend keeps would be invisible
    // to the person acting on it.
    expect(screen.getByText('Failed')).toBeInTheDocument();
    expect(screen.getByText('Timed out')).toBeInTheDocument();
  });

  it('shows the output a revision would be based on', () => {
    render(<TestOutput results={[result()]} />);

    expect(screen.getByText('1 failed, 2 passed')).toBeInTheDocument();
    expect(screen.getByText('exit 1')).toBeInTheDocument();
  });

  it('names which revision attempt produced a result', () => {
    render(<TestOutput results={[result({ revisionAttempt: 2 })]} />);

    expect(screen.getByText('revision 2')).toBeInTheDocument();
  });

  it('says when nothing has run yet', () => {
    render(<TestOutput results={[]} />);

    expect(screen.getByText(/no tests have run/i)).toBeInTheDocument();
  });
});
