import { render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { RunTimeline } from './RunTimeline';
import type { RunEvent } from '../api/client';

function event(overrides: Partial<RunEvent> & { sequence: number }): RunEvent {
  return {
    id: `00000000-0000-0000-0000-${String(overrides.sequence).padStart(12, '0')}`,
    eventType: 'tool_call',
    status: 'succeeded',
    startedAt: '2026-08-16T10:00:00Z',
    ...overrides,
  } as RunEvent;
}

describe('RunTimeline', () => {
  it('lists every action with its name and timing', () => {
    render(
      <RunTimeline
        reconnecting={false}
        events={[
          event({
            sequence: 1,
            toolName: 'search_code',
            durationMs: 120,
            argumentsSummary: { query: 'order lookup' } as never,
          }),
          event({
            sequence: 2,
            toolName: 'read_file',
            durationMs: 8,
            argumentsSummary: { path: 'src/Orders/OrderLookupService.cs' } as never,
          }),
        ]}
      />,
    );

    // FR-027: what the agent did, not just that it did something.
    expect(screen.getByText('search_code')).toBeInTheDocument();
    expect(screen.getByText('order lookup')).toBeInTheDocument();
    expect(screen.getByText('src/Orders/OrderLookupService.cs')).toBeInTheDocument();
    expect(screen.getByText('120 ms')).toBeInTheDocument();
  });

  it('shows failures as prominently as successes', () => {
    const { container } = render(
      <RunTimeline
        reconnecting={false}
        events={[
          event({ sequence: 1, toolName: 'read_file' }),
          event({
            sequence: 2,
            toolName: 'read_file',
            status: 'failed',
            errorMessage: 'Path resolves outside the workspace.',
          }),
        ]}
      />,
    );

    // A refused path access is the most interesting entry in a run. A timeline
    // that only listed what worked would hide exactly what is worth reading.
    expect(screen.getByText('failed')).toBeInTheDocument();
    expect(screen.getByText('Path resolves outside the workspace.')).toBeInTheDocument();
    expect(container.querySelectorAll('.run-timeline__entry--failed')).toHaveLength(1);
  });

  it('reads stage changes from the summary fields', () => {
    render(
      <RunTimeline
        reconnecting={false}
        events={[
          event({
            sequence: 1,
            eventType: 'stage_changed',
            toolName: null,
            argumentsSummary: {
              fromStage: 'Planning',
              trigger: 'PlanProduced',
              toStage: 'Proposing',
            } as never,
          }),
        ]}
      />,
    );

    // Named fields, not pattern-matched text: PostgreSQL normalizes the stored
    // JSON, so anything depending on its formatting would be unreliable.
    expect(screen.getByText('Planning → Proposing')).toBeInTheDocument();
  });

  it('names the stage a run failed at', () => {
    render(
      <RunTimeline
        reconnecting={false}
        events={[
          event({
            sequence: 1,
            eventType: 'run_failed',
            toolName: null,
            status: 'failed',
            argumentsSummary: {
              failureStage: 'Testing',
              outcomeReason: 'RevisionLimitReached',
            } as never,
          }),
        ]}
      />,
    );

    // FR-030: which stage, not just that it failed.
    expect(screen.getByText('at Testing: RevisionLimitReached')).toBeInTheDocument();
  });

  it('says when it is between connections', () => {
    render(<RunTimeline reconnecting events={[event({ sequence: 1 })]} />);

    // A silent stream and a dropped one look identical otherwise.
    expect(screen.getByText(/reconnecting/i)).toBeInTheDocument();
  });

  it('says so when nothing has been recorded', () => {
    render(<RunTimeline reconnecting={false} events={[]} />);

    expect(screen.getByText(/nothing has been recorded/i)).toBeInTheDocument();
  });
});
