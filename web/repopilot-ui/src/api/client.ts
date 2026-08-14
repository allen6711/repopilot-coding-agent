import type { components } from './schema';

/**
 * Types come from `schema.ts`, which is generated from
 * `contracts/rest-api.yaml` by `pnpm generate:api`. Re-exported under short
 * names so components import a domain word rather than a path into the
 * generated tree — and so a contract change surfaces as a type error here,
 * in one file, instead of in every component that touched the shape.
 */
export type Run = components['schemas']['Run'];
export type RunSummary = components['schemas']['RunSummary'];
export type RunStage = components['schemas']['RunStage'];
export type ChangeProposal = components['schemas']['ChangeProposal'];
export type ApprovalDecision = components['schemas']['ApprovalDecision'];
export type TestResult = components['schemas']['TestResult'];
export type RunEvent = components['schemas']['RunEvent'];
export type Problem = components['schemas']['Problem'];

/**
 * An error carrying the server's RFC 9457 problem details.
 *
 * The `detail` field is what the reviewer is shown. A refused approval says
 * exactly why it was refused — a stale hash, a proposal already decided — and
 * replacing that with a generic message would leave someone unable to tell a
 * mistake from a race.
 */
export class ApiError extends Error {
  readonly status: number;
  readonly problem: Problem | null;

  constructor(status: number, problem: Problem | null) {
    super(problem?.detail ?? problem?.title ?? `Request failed with status ${status}`);
    this.name = 'ApiError';
    this.status = status;
    this.problem = problem;
  }

  /** The proposal already has a decision, or the run has already ended. */
  get isConflict(): boolean {
    return this.status === 409;
  }

  /** The request was refused: stale hash, missing actor, or malformed decision. */
  get isRefused(): boolean {
    return this.status === 422;
  }
}

/** Identity recorded on an approval, rejection, or cancellation (FR-015a). */
export interface Actor {
  readonly value: string;
}

const ACTOR_HEADER = 'X-Actor';

async function request<T>(path: string, init: RequestInit = {}, actor?: Actor): Promise<T> {
  const headers = new Headers(init.headers);
  headers.set('Accept', 'application/json');

  if (init.body !== undefined) {
    headers.set('Content-Type', 'application/json');
  }

  if (actor) {
    headers.set(ACTOR_HEADER, actor.value);
  }

  const response = await fetch(path, { ...init, headers });

  if (!response.ok) {
    let problem: Problem | null = null;

    try {
      problem = (await response.json()) as Problem;
    } catch {
      // A response body that is not problem details still has a status, and
      // the status alone is more useful than throwing over the parse.
    }

    throw new ApiError(response.status, problem);
  }

  if (response.status === 204) {
    return undefined as T;
  }

  return (await response.json()) as T;
}

export const api = {
  getRun: (runId: string) => request<Run>(`/api/runs/${runId}`),

  listRuns: (repositoryId?: string) =>
    request<RunSummary[]>(repositoryId ? `/api/runs?repositoryId=${repositoryId}` : '/api/runs'),

  getProposal: (runId: string) => request<ChangeProposal>(`/api/runs/${runId}/proposal`),

  getTests: (runId: string) => request<TestResult[]>(`/api/runs/${runId}/tests`),

  getEvents: (runId: string) => request<RunEvent[]>(`/api/runs/${runId}/events`),

  /**
   * Records a decision.
   *
   * `diffHash` is the hash of the proposal the reviewer was shown, echoed back.
   * It is a required argument rather than something this function reads from
   * cached state, because the whole point is to prove the decision was made
   * against the content that will be applied (FR-020a). Reading it from a cache
   * the UI refreshed in the background would defeat the check entirely.
   */
  decide: (
    runId: string,
    proposalId: string,
    decision: 'approve' | 'reject',
    diffHash: string,
    actor: Actor,
  ) =>
    request<ApprovalDecision>(
      `/api/runs/${runId}/approval`,
      { method: 'POST', body: JSON.stringify({ proposalId, decision, diffHash }) },
      actor,
    ),

  cancel: (runId: string, actor: Actor) =>
    request<Run>(`/api/runs/${runId}/cancel`, { method: 'POST' }, actor),

  createRun: (repositoryId: string, taskDescription: string, toolsEnabled = true) =>
    request<Run>('/api/runs', {
      method: 'POST',
      body: JSON.stringify({ repositoryId, taskDescription, toolsEnabled }),
    }),
};

/** Stages from which no further transition is possible. */
const TERMINAL_STAGES: ReadonlySet<RunStage> = new Set<RunStage>([
  'succeeded',
  'failed',
  'rejected',
  'cancelled',
  'no_change',
]);

export function isTerminal(stage: RunStage): boolean {
  return TERMINAL_STAGES.has(stage);
}

/** Human-readable stage labels. */
export function stageLabel(stage: RunStage): string {
  switch (stage) {
    case 'created':
      return 'Queued';
    case 'retrieving':
      return 'Retrieving context';
    case 'planning':
      return 'Planning';
    case 'proposing':
      return 'Preparing a change';
    case 'awaiting_approval':
      return 'Awaiting your decision';
    case 'applying':
      return 'Applying';
    case 'testing':
      return 'Running tests';
    case 'succeeded':
      return 'Succeeded';
    case 'failed':
      return 'Failed';
    case 'rejected':
      return 'Rejected';
    case 'cancelled':
      return 'Cancelled';
    case 'no_change':
      return 'No change needed';
  }
}
