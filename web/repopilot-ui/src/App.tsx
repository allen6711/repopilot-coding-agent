import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { RepositoryList } from './pages/RepositoryList';
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
 * Reads the run to display from the URL: `/?run=<uuid>`. Without one, the
 * operator view.
 *
 * Deliberately not a router. This feature has two views and one link between
 * them; a router would be a dependency carried for a navigation model that does
 * not exist yet.
 */
function runIdFromLocation(): string | null {
  return new URLSearchParams(window.location.search).get('run');
}

export default function App() {
  const runId = runIdFromLocation();

  return (
    <QueryClientProvider client={queryClient}>
      <main className="app">{runId ? <RunDetail runId={runId} /> : <RepositoryList />}</main>
    </QueryClientProvider>
  );
}
