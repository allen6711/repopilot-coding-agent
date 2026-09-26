import { useEffect, useState } from 'react';

/**
 * Which of the two list views is showing. A run id, when present, wins over
 * both — the review view is the destination, and the list is how you reach it.
 */
export type View = 'repositories' | 'runs';

export interface Route {
  readonly view: View;
  readonly runId: string | null;
}

/**
 * Still deliberately not a router.
 *
 * The app has three views and the links between them all point at one of the
 * three, so the whole navigation model fits in a query string: `?run=<uuid>`
 * for the review view, `?view=runs` for the run list, and neither for the
 * operator view. What a router would add here is a dependency, a nested
 * component tree, and a second place for the URL contract to live.
 *
 * What it would not add is history, because `pushState` already gives us that —
 * and history matters: a reviewer who follows a run from the list and presses
 * Back expects the list, not the page they were on before the app loaded.
 */
const NAVIGATION_EVENT = 'repopilot:navigate';

export function readRoute(search: string = window.location.search): Route {
  const params = new URLSearchParams(search);
  const runId = params.get('run');

  return {
    runId,
    view: params.get('view') === 'runs' ? 'runs' : 'repositories',
  };
}

export function toSearch(route: Route): string {
  if (route.runId) {
    return `?run=${encodeURIComponent(route.runId)}`;
  }

  return route.view === 'runs' ? '?view=runs' : '/';
}

/**
 * Moves to a route and tells the app about it.
 *
 * `pushState` does not fire `popstate` — that event is the browser reporting a
 * user's own back or forward, not an application's push — so the custom event is
 * what closes the loop. Without it the URL would change and the view would not.
 */
export function navigate(route: Route): void {
  window.history.pushState(null, '', toSearch(route));
  window.dispatchEvent(new Event(NAVIGATION_EVENT));
}

/** The current route, re-read on both our own pushes and the browser's. */
export function useRoute(): Route {
  const [route, setRoute] = useState<Route>(() => readRoute());

  useEffect(() => {
    function sync() {
      setRoute(readRoute());
    }

    window.addEventListener('popstate', sync);
    window.addEventListener(NAVIGATION_EVENT, sync);

    return () => {
      window.removeEventListener('popstate', sync);
      window.removeEventListener(NAVIGATION_EVENT, sync);
    };
  }, []);

  return route;
}
