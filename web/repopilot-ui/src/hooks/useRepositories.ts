import { useQuery, type UseQueryResult } from '@tanstack/react-query';
import { api, type Repository } from '../api/client';

/**
 * The registered fixtures.
 *
 * Shared by the operator view, which acts on them, and the run list, which only
 * needs their slugs to name the repository a run belongs to. One hook so the two
 * views read one cache entry: the alternative is two components declaring the
 * same query key with drifting options, where the one that mounts first silently
 * decides the settings for both.
 */
export function useRepositories(): UseQueryResult<Repository[]> {
  return useQuery({
    queryKey: ['repositories'],
    queryFn: api.listRepositories,
  });
}
