import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { RepositoryList } from './pages/RepositoryList';
import { RunDetail } from './pages/RunDetail';
import { RunList } from './pages/RunList';
import { navigate, useRoute, type View } from './navigation';
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
      <Nav route={route} />

      <main className="app">
        {route.runId ? (
          <RunDetail runId={route.runId} />
        ) : route.view === 'runs' ? (
          <RunList />
        ) : (
          <RepositoryList />
        )}
      </main>
    </QueryClientProvider>
  );
}

/**
 * The two list views, and the way back out of a run.
 *
 * `aria-current` rather than styling alone: which view you are on is the one
 * piece of state this nav carries, and a screen reader has no access to a bolder
 * font weight.
 */
function Nav({ route }: { readonly route: ReturnType<typeof useRoute> }) {
  return (
    <nav className="app-nav" aria-label="Views">
      <NavLink view="repositories" current={route} label="Repositories" />
      <NavLink view="runs" current={route} label="Runs" />
    </nav>
  );
}

function NavLink({
  view,
  current,
  label,
}: {
  readonly view: View;
  readonly current: ReturnType<typeof useRoute>;
  readonly label: string;
}) {
  // A run's review view is reached from the run list, so the list stays marked
  // as the section you are in while you read one.
  const active = current.runId ? view === 'runs' : current.view === view;

  return (
    <button
      type="button"
      className="app-nav__link"
      aria-current={active ? 'page' : undefined}
      onClick={() => navigate({ view, runId: null })}
    >
      {label}
    </button>
  );
}
