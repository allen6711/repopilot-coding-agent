import type { ReactElement } from 'react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render } from '@testing-library/react';
import { vi } from 'vitest';

/**
 * Renders a component that talks to the API.
 *
 * Retries are off. A component under test that hits a stubbed failure should
 * show its error state on the first attempt rather than after three, and a test
 * waiting out react-query's backoff is a test that times out for a reason
 * unrelated to what it is checking.
 */
export function renderWithQuery(ui: ReactElement) {
  const client = new QueryClient({
    defaultOptions: {
      queries: { retry: false, gcTime: 0 },
      mutations: { retry: false },
    },
  });

  return render(<QueryClientProvider client={client}>{ui}</QueryClientProvider>);
}

/** A `fetch` stub that answers by path. Anything unmatched fails the test. */
export function stubFetch(
  routes: ReadonlyArray<{
    readonly match: (url: string, init?: RequestInit) => boolean;
    readonly status?: number;
    readonly body: unknown;
  }>,
): ReturnType<typeof vi.fn> {
  const calls = vi.fn(async (url: string | URL, init?: RequestInit) => {
    const path = typeof url === 'string' ? url : url.toString();
    const route = routes.find((r) => r.match(path, init));

    if (!route) {
      throw new Error(`No stubbed route for ${init?.method ?? 'GET'} ${path}`);
    }

    const status = route.status ?? 200;

    return {
      ok: status < 400,
      status,
      json: async () => route.body,
    } as Response;
  });

  vi.stubGlobal('fetch', calls);

  return calls;
}
