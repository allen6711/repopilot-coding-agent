import { useEffect, useRef, useState } from 'react';
import type { RunEvent } from '../api/client';

interface RunStream {
  readonly events: readonly RunEvent[];
  /** True once `run_ended` has arrived — the run is over, not merely quiet. */
  readonly ended: boolean;
  /** True while the browser is between connections. */
  readonly reconnecting: boolean;
}

/**
 * Subscribes to a run's event stream.
 *
 * Three things about this are the contract rather than convenience.
 *
 * A fresh connection replays from sequence 1, so this hook has no separate
 * "fetch history then subscribe" path — the events array is complete from the
 * first frame.
 *
 * The cursor is kept in a ref and passed as `lastEventId` on reconnect.
 * `EventSource` sets `Last-Event-ID` itself when it reconnects on its own, but
 * a connection the browser considers closed for good has to be rebuilt by hand,
 * and a rebuilt one cannot carry headers. The query parameter is the same cursor
 * by another route.
 *
 * `run_ended` closes the stream deliberately. A client treats closure *without*
 * it as a transport drop and reconnects, so distinguishing the two is what
 * stops a finished run from being reconnected to forever.
 */
export function useRunStream(runId: string, enabled = true): RunStream {
  const [events, setEvents] = useState<readonly RunEvent[]>([]);
  const [ended, setEnded] = useState(false);
  const [reconnecting, setReconnecting] = useState(false);

  // Refs, not state: the reconnect path reads these inside a closure that must
  // not be re-created on every event, or each frame would tear down the stream.
  const lastEventId = useRef(0);
  const closed = useRef(false);

  useEffect(() => {
    if (!enabled) {
      return;
    }

    closed.current = false;
    let source: EventSource | null = null;
    let retry: ReturnType<typeof setTimeout> | undefined;

    function connect() {
      if (closed.current) {
        return;
      }

      const cursor = lastEventId.current;
      const url =
        cursor > 0
          ? `/api/runs/${runId}/events?lastEventId=${cursor}`
          : `/api/runs/${runId}/events`;

      source = new EventSource(url);

      source.onopen = () => setReconnecting(false);

      source.onmessage = (message: MessageEvent<string>) => {
        const received = JSON.parse(message.data) as RunEvent;

        // The server already skips anything at or below the cursor, but a
        // duplicate here would be worse than a wasted render: the timeline is
        // keyed by sequence, and React would warn and then show the event
        // twice.
        if (received.sequence <= lastEventId.current) {
          return;
        }

        lastEventId.current = received.sequence;
        setEvents((current) => [...current, received]);

        if (received.eventType === 'run_ended') {
          closed.current = true;
          setEnded(true);
          source?.close();
        }
      };

      source.onerror = () => {
        source?.close();

        if (closed.current) {
          return;
        }

        // A drop, not the end. Reconnect from the cursor; the replay fills
        // whatever happened while disconnected.
        setReconnecting(true);
        retry = setTimeout(connect, 1000);
      };
    }

    connect();

    return () => {
      closed.current = true;
      clearTimeout(retry);
      source?.close();
    };
  }, [runId, enabled]);

  return { events, ended, reconnecting };
}
