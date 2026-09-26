import { useQuery, type UseQueryResult } from '@tanstack/react-query';
import { api, isTerminal, type Run } from '../api/client';

/**
 * One run's own state.
 *
 * Polls while the run is live and stops once it ends. A finished run's stage
 * cannot change, so continuing to poll would be pure noise — which is also why
 * the interval is a function of the data rather than a constant.
 *
 * The event stream is what makes a live run's stage arrive without a refresh
 * (FR-028a); this query is the fact underneath it. A stage is a property of the
 * run, not something a client should derive by replaying events, so the two are
 * kept separate and the poll survives as the fallback for a browser without
 * EventSource.
 */
export function useRun(runId: string): UseQueryResult<Run> {
  return useQuery({
    queryKey: ['run', runId],
    queryFn: () => api.getRun(runId),
    refetchInterval: (query) =>
      query.state.data && isTerminal(query.state.data.stage) ? false : 2000,
  });
}
