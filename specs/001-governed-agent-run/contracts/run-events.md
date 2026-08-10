# Contract: Run Event Stream

**Feature**: 001-governed-agent-run | **Date**: 2026-08-10

`GET /api/runs/{runId}/events` with `Accept: text/event-stream` returns a Server-Sent Events stream.
The same endpoint with `Accept: application/json` returns the persisted event list.

This contract carries three requirements at once: live visibility within 2 seconds (FR-028a,
SC-013), reconstructability of a completed run from recorded events alone (FR-029, SC-008), and a
recorded rejection for every illegal stage transition (FR-009).

## Frame format

```text
id: 42
event: stage_changed
data: {"sequence":42,"eventType":"stage_changed","status":"succeeded",...}

```

- `id` is the event's per-run `sequence` — monotonic, gap-free, assigned at persist time.
- `event` is the `eventType`.
- `data` is the JSON `RunEvent` object from `rest-api.yaml`.
- A comment frame (`: keepalive`) is sent every 15 seconds so intermediaries do not close an idle
  stream.

## Ordering and durability guarantee

Every event is **persisted before it is published**. A live subscriber therefore never sees an event
that a later replay would miss, and the reverse is impossible by construction. This single ordering
rule is what lets the same table serve both the live stream and SC-008 reconstruction.

## Reconnection

A client that drops sends `Last-Event-ID: <sequence>` on reconnect. The endpoint:

1. Reads persisted events with `sequence > Last-Event-ID` in order and writes them to the stream.
2. Attaches to the live broadcast channel, skipping any event already replayed.
3. If the run has reached a terminal outcome, writes the remaining events and closes the stream.

Without the header, the stream starts from sequence 1 — a fresh reader always gets the full history,
so the UI needs no separate "fetch history then subscribe" path.

## Event catalogue

| `eventType` | Emitted when | Notable `data` fields |
|---|---|---|
| `stage_changed` | Any legal stage transition is persisted | `fromStage`, `toStage`, `trigger` |
| `stage_transition_rejected` | A transition not permitted from the current stage is attempted (FR-009) | `fromStage`, `attemptedTrigger`, `status: "failed"` |
| `tool_call` | Any capability invocation completes or throws | `toolName`, `argumentsSummary`, `status`, `durationMs` |
| `plan_produced` | The agent returns its short human-readable plan (FR-010) | `plan` |
| `proposal_created` | A change proposal is stored | `proposalId`, `affectedPaths`, `diffHash` |
| `approval_recorded` | An approval or rejection is persisted (FR-019) | `proposalId`, `decision`, `decidedBy`, `diffHash` |
| `patch_applied` | An approved proposal is written to the working copy | `proposalId`, `appliedPaths` |
| `tests_completed` | A sandbox execution finishes or times out | `commandName`, `passed`, `timedOut`, `durationMs` |
| `run_failed` | The run enters a non-success terminal outcome | `failureStage`, `outcomeReason` (FR-030) |
| `run_ended` | Any terminal outcome is reached; always the last event | `terminalOutcome`, `outcomeReason` |

## Redaction

`argumentsSummary` passes through the same redaction predicate as model context: file contents are
replaced by `{path, byteCount}`, query strings are kept, and anything matching a secret pattern is
replaced by `[redacted]`. No event ever carries a credential (Principle II).

## Latency budget

The publish step is an in-process channel write immediately after the database commit. The
measurable target is SC-013: 95% of stage transitions and recorded actions visible to a watching
reviewer within 2 seconds, with no manual refresh. The integration test asserts this by timestamping
the persist and the client-side receive for every event in a seeded run.

## Client contract for `run_ended`

`run_ended` is always the final frame; the server closes the stream after writing it. A client
should treat stream closure without `run_ended` as a transport drop and reconnect with
`Last-Event-ID`, not as run completion.
