import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { RepositoryList } from './pages/RepositoryList';
import { RunDetail } from './pages/RunDetail';
import { useRoute } from './navigation';
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
 * Which view the URL asks for. See `navigation.ts` for the route shape and for
 * why this is a query string rather than a router.
 */
export default function App() {
  const route = useRoute();

  return (
    <QueryClientProvider client={queryClient}>
      <main className="app">
        {route.runId ? <RunDetail runId={route.runId} /> : <RepositoryList />}
      </main>
    </QueryClientProvider>
  );
}
