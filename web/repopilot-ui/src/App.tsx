import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { RunDetail } from './pages/RunDetail';
import './App.css';

const queryClient = new QueryClient({
  defaultOptions: {
    queries: {
      // The run's own query sets a poll interval and stops once it ends. A
      // global stale time on top of that would fight it.
      staleTime: 0,
      refetchOnWindowFocus: true,
    },
  },
});

/**
 * Reads the run to display from the URL: `/?run=<uuid>`.
 *
 * Deliberately not a router. This feature has one view worth routing to, and a
 * router would be a dependency carried for a navigation model that does not
 * exist yet.
 */
function runIdFromLocation(): string | null {
  return new URLSearchParams(window.location.search).get('run');
}

export default function App() {
  const runId = runIdFromLocation();

  return (
    <QueryClientProvider client={queryClient}>
      <main className="app">
        {runId ? (
          <RunDetail runId={runId} />
        ) : (
          <p className="app__empty">
            Open a run with <code>?run=&lt;run id&gt;</code>.
          </p>
        )}
      </main>
    </QueryClientProvider>
  );
}
