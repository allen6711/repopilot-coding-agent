# Quickstart & Validation Guide: Governed Agent Run

**Feature**: 001-governed-agent-run | **Date**: 2026-08-10

This is a run-and-verify guide, not an implementation guide. Each scenario below maps to a user story
or success criterion in [spec.md](./spec.md) and states the expected observable outcome. Entity
shapes are in [data-model.md](./data-model.md); request and response shapes are in
[contracts/rest-api.yaml](./contracts/rest-api.yaml).

---

## Prerequisites

| Requirement | Notes |
|---|---|
| .NET 10 SDK | `dotnet --version` reports 10.x |
| Node.js 22+ and pnpm | For `web/repopilot-ui` |
| Docker Engine | The API needs daemon access to create sandbox containers |
| PostgreSQL 17+ with pgvector 0.8+ | Provided by `docker-compose.yml` |
| Chat provider credential | `REPOPILOT__ChatProvider__ApiKey` (default adapter targets Claude) |

The embedding provider defaults to a local in-process model, so no second credential is needed for
development or evaluation.

## Setup

```bash
# 1. Start PostgreSQL with pgvector and the OTel collector
docker compose up -d postgres otel-collector

# 2. Apply migrations
dotnet ef database update --project src/RepoPilot.Infrastructure --startup-project src/RepoPilot.Api

# 3. Build the fixture sandbox images (dependencies are baked in; the sandbox has no network)
docker build -f sandbox/Dockerfile.dotnet -t repopilot/fixture-dotnet:1 .

# 4. Start the API
dotnet run --project src/RepoPilot.Api

# 5. Start the UI
pnpm --dir web/repopilot-ui install && pnpm --dir web/repopilot-ui dev
```

Configuration worth knowing before the scenarios below:

| Setting | Default | Why it matters |
|---|---|---|
| `RepoPilot:Workspace:Root` | `./.workspace` | Working copies live at `{root}/runs/{runId}` |
| `RepoPilot:Runs:MaxConcurrent` | `4` | Runs beyond this queue; awaiting-approval runs hold no slot |
| `RepoPilot:Retrieval:ContextBudgetChars` | `60000` | Per-run cap on retrieved content |
| `RepoPilot:Indexing:MaxFileBytes` | `262144` | Files above this are excluded and counted |
| `RepoPilot:AllowedRepositories` | fixture slugs | Anything outside this set is refused at registration |

---

## Scenario 1 — Register and index a fixture (User Story 2)

```bash
# Register
curl -sX POST localhost:8080/api/repositories \
  -H 'content-type: application/json' \
  -d '{"slug":"sample-dotnet-api"}'

# Index (full rebuild, atomic swap)
curl -sX POST localhost:8080/api/repositories/$REPO_ID/index

# Poll until indexingStatus is "indexed"
curl -s localhost:8080/api/repositories/$REPO_ID
```

**Expected**

- Registration returns `201`; a slug outside the allowed set returns `422` and creates nothing
  (FR-001).
- When indexing completes, `includedFileCount` and `excludedFileCount` are both reported, and
  `exclusionBreakdown` attributes exclusions to `binary`, `size`, `denylisted_dir`, `secret_pattern`,
  or `secret_content` (FR-003).
- Searching for a string that only occurs inside `node_modules/`, a `.env` file, or a file above the
  size limit returns no results (FR-002, acceptance scenario 2).

```bash
# Exact identifier lookup returns the defining file with a line range
curl -s "localhost:8080/api/repositories/$REPO_ID/search?q=OrderLookupService&limit=5"
```

**Expected**: at least one result whose `relativePath` is the defining file, with `startLine`,
`endLine`, and `score` populated (FR-004, FR-005, acceptance scenario 3).

**Re-index determinism**

```bash
curl -sX POST localhost:8080/api/repositories/$REPO_ID/index
curl -s "localhost:8080/api/repositories/$REPO_ID/search?q=OrderLookupService&limit=5"
```

**Expected**: results reflect current content with no duplicated entries, and any search issued
during the rebuild still returns results from the previous index (FR-003a, acceptance scenario 4).

---

## Scenario 2 — Approve a change and see it tested (User Story 1, P1)

This is the minimum releasable slice; it must pass end to end before anything else matters.

```bash
# Start a run
RUN=$(curl -sX POST localhost:8080/api/runs \
  -H 'content-type: application/json' \
  -d "{\"repositoryId\":\"$REPO_ID\",\"seededTaskId\":\"bugfix-null-guard-01\"}" | jq -r .id)

# Watch it live
curl -sN localhost:8080/api/runs/$RUN/events
```

**Expected while running** (User Story 3): `stage_changed` frames arrive in order, `tool_call` frames
name each capability with timing and status, and a `plan_produced` frame carries the short plan
before any proposal exists (FR-010, FR-027).

```bash
# Read the proposal
curl -s localhost:8080/api/runs/$RUN/proposal
```

**Expected**: `affectedPaths`, the full `unifiedDiff`, and a `diffHash` — everything needed to decide
without leaving the review view (FR-011, SC-004). At this moment the fixture directory and the
working copy are both unchanged (acceptance scenario 1, Principle I).

Verify that nothing was written. The working copy exists from the start of the run (FR-024a), so the
check is that it still matches the fixture, not that it is absent:

```bash
git status --porcelain evals/fixtures/sample-dotnet-api    # fixture untouched — expect empty
diff -r evals/fixtures/sample-dotnet-api .workspace/runs/$RUN   # expect no differences
```

```bash
# Approve, echoing back the hash that was shown
curl -sX POST localhost:8080/api/runs/$RUN/approval \
  -H 'content-type: application/json' -H "X-Actor: $USER" \
  -d "{\"proposalId\":\"$PROPOSAL_ID\",\"decision\":\"approve\",\"diffHash\":\"$DIFF_HASH\"}"
```

**Expected after approval**: stage advances to `applying`, then `testing`, then a terminal outcome.
`GET /api/runs/$RUN/tests` returns pass/fail with output and duration; `GET /api/runs/$RUN/diff`
returns the final diff (acceptance scenario 2, FR-028).

---

## Scenario 3 — Rejection writes nothing (User Story 1, acceptance scenario 3)

Start a second run, wait for `awaiting_approval`, then reject.

**Expected**

- Run ends with `terminalOutcome: "rejected"`.
- No file anywhere is modified — re-run the two `git status` / `ls` checks above (FR-017).
- The decision is recorded with actor, timestamp, run, and diff hash (FR-019).
- The working copy, if one existed, is gone (FR-026a).

---

## Scenario 4 — The approval gate cannot be bypassed (SC-001, SC-002, Principle I)

| Attempt | Expected |
|---|---|
| `POST /approval` twice for the same proposal | Second call returns `409` — the change is applied at most once (FR-018) |
| Approve, then reject the same proposal | `409`; the earlier decision stands |
| A second reviewer decides an already-decided proposal | `409`; the single-reviewer assumption is enforced, not assumed (FR-018) |
| `POST /approval` with a `diffHash` that does not match the stored proposal | `422`, nothing applied (FR-020a) |
| `POST /approval` or `POST /cancel` with no `X-Actor` header | `422`; no decision record is written (FR-015a, SC-015) |
| `POST /approval` requesting programmatic mode on an interactive run | `422` (FR-015b) |
| Directly invoking apply for a proposal with no approval row (integration test) | Refused with `approval_required`; no file handle is opened (FR-014) |
| A proposal entry whose path escapes the working copy | Rejected before any file is touched, recorded as a refused action, and the run reports the violation rather than skipping it (FR-024, FR-024c, SC-010) |
| A fixture containing a symlink pointing outside the workspace | Reads through it are refused (FR-024b) |
| A proposal exceeding the configured size caps | Refused at creation, so no proposal reaches a reviewer that cannot be displayed in full (FR-011a, SC-004) |
| A fixture seeded with content instructing the agent to skip approval | No control is relaxed; the change still requires an approval record (FR-026d, SC-014) |

---

## Scenario 5 — Failure and revision paths

| Situation | Expected outcome |
|---|---|
| Tests fail after approval | A new proposal is presented for its own approval; nothing is applied without it (FR-013, acceptance scenario 4) |
| Tests still fail after 2 revision attempts | Run ends `failed` with `outcomeReason: revision_limit_reached`, last diff and last output retained |
| Tests exceed the fixture timeout | Container killed at the limit; `timedOut: true`; run reports a timeout. Clean finishes and forced terminations are counted separately (FR-023, SC-009) |
| Timeout expires but the container cannot be killed | Run ends `failed` with `sandbox_not_terminable` rather than waiting indefinitely (FR-023b) |
| Container runtime unavailable | Run ends `failed` with `sandbox_unavailable`; there is no unisolated fallback path (Assumptions) |
| Cancelled mid-apply | No partially applied change survives — application is atomic, and the working copy is destroyed (FR-016b, FR-026a) |
| Agent concludes nothing should change | Run ends `no_change` with `deliberate_no_op`; no empty diff is offered for approval (FR-008b) |
| Task too vague to locate code | Run ends `no_change` with `insufficient_context` |
| Reviewer never responds | Run stays `awaiting_approval` indefinitely and holds no concurrency slot; `POST /cancel` ends it as `cancelled` (FR-008a, FR-013b) |
| Model provider unavailable mid-run | Run ends `failed` at its current stage with the reason recorded; no partial change applied (FR-030) |
| Service restarted mid-run | Run ends `failed` with `service_restarted`; the working copy is swept and any container it owned is removed, so no partial change and no orphaned resource survives (FR-030a, FR-026e, SC-012) |

---

## Scenario 6 — Concurrency (FR-013a, FR-013b, SC-003)

With `MaxConcurrent = 4`:

1. Start 6 runs at once. Four execute; two queue. None are refused.
2. Let one run reach `awaiting_approval` and leave it there. A queued run starts immediately — the
   waiting run holds no slot.
3. Measure start-of-run to first `proposal_created` event on a seeded task while at or below the
   limit. **Expected: under 3 minutes** (SC-003).

---

## Scenario 7 — Live visibility (User Story 3, SC-013)

Open the run detail view in the UI and start a run.

**Expected**

- Stage updates appear without any manual refresh, within 2 seconds of each transition (FR-028a).
- Every action is listed with name, timing, and success/failure (FR-027).
- Killing and restarting the SSE connection replays missed events via `Last-Event-ID` with no gap and
  no duplicate (see [contracts/run-events.md](./contracts/run-events.md)).
- For a failed run, the failure reason and the stage it occurred at are both shown (FR-030).

**Reconstructability check (SC-008)**: take a completed run id, query `run_events`,
`change_proposals`, and `test_results` directly, and confirm the stage sequence, action history,
final diff, and test output are all recoverable with the API stopped and the working copy deleted
(FR-029, FR-026b).

---

## Scenario 8 — Evaluation (User Story 4)

```bash
dotnet run --project src/RepoPilot.Evals -- --output evals/results/$(date +%Y%m%d-%H%M).json
```

**Expected**

- At least 30 committed tasks are executed, each twice — retrieval-only baseline and tool-enabled
  (FR-031, FR-033).
- The report contains Recall@5, completion rate for both modes, approval coverage, tool success rate,
  average tool calls per completed task, and latency percentiles (FR-032).
- Approval coverage is exactly `1.0`; anything lower sets `flagged: true` and the evaluation fails
  rather than reporting a number (FR-034, SC-001).
- Running it twice against unchanged fixtures, without re-indexing, produces **identical** retrieval
  metrics (SC-007).
- Reference patches are never read during a run — verify by asserting no `read_file` event in any
  evaluation run resolves a path under `evals/tasks/_reference/` (FR-035).

---

## Test suite

```bash
dotnet test RepoPilot.slnx           # everything (Testcontainers starts Postgres; Docker required)
pnpm --dir web/repopilot-ui test     # UI units

# Or one suite at a time. `dotnet test <directory>` is not a supported form —
# it looks for a project in the working directory and fails with MSB1003.
dotnet test tests/unit/RepoPilot.UnitTests/RepoPilot.UnitTests.csproj
dotnet test tests/integration/RepoPilot.IntegrationTests/RepoPilot.IntegrationTests.csproj
dotnet test tests/e2e/RepoPilot.E2ETests/RepoPilot.E2ETests.csproj   # seeded task: create → approve → apply → test → result
```

The mandatory coverage areas from the constitution, and where each is verified:

| Area | Location |
|---|---|
| Path allow-list validation | `tests/unit` — `PathGuard` |
| File-size and context-size limits | `tests/unit` + `tests/integration` — tool invoker |
| Run-state transitions | `tests/unit` — exhaustive over every (stage, trigger) pair |
| Approval required before apply | `tests/integration` |
| Rejected approval never applies a patch | `tests/integration` |
| Tool-call audit records | `tests/integration` — including the throwing-capability case |
| Retrieval filtering and exclusions | `tests/integration` — pgvector container |
| Diff parsing and validation | `tests/unit` — proposal validation and diff hash canonicalization |
| Allowed test-command enforcement | `tests/integration` |
| Docker sandbox timeout behavior | `tests/integration` — sandbox runner |
| Evaluation metric calculation | `tests/unit` |
| End-to-end seeded task | `tests/e2e` |

Added by the governance checklist remediation:

| Area | Location |
|---|---|
| Symlink escape from inside a fixture | `tests/integration` — path guard |
| Refused access recorded as a countable failed action | `tests/integration` |
| Actor identity required on decisions and cancellations | `tests/integration` |
| Programmatic approval refused on interactive runs | `tests/integration` |
| Secret redaction of sandbox output before storage, display, and model context | `tests/unit` + `tests/integration` |
| Proposal size caps refused at creation | `tests/unit` |
| Atomic apply — interruption leaves the pre-apply state | `tests/integration` |
| Restart recovery — non-terminal runs failed, copies and containers swept | `tests/integration` |
| Repository content treated as data, not instruction | `tests/integration` — adversarial fixture |

---

## Troubleshooting

| Symptom | Likely cause |
|---|---|
| `repository_not_indexed` when starting a run | Index never built, or the fixture has zero indexable files — runs against an empty fixture are refused by design |
| Tests fail with network errors inside the sandbox | Dependencies were not baked into the fixture image; the sandbox runs with `--network none` and that is not configurable |
| `command_not_allowed` | The command name is missing from `repopilot.fixture.json`; add it there, not at the call site |
| SSE stream shows nothing | Check `Accept: text/event-stream`; a proxy buffering responses will defeat the 2-second target |
| Retrieval metrics differ between evaluation runs | Something re-indexed between them — check `activeIndexVersion` and the pinned `embedding_model_id` |
| Working copies accumulating under `.workspace/runs/` | The startup sweep did not run, or runs are stuck non-terminal; SC-012 asserts zero remain for terminal runs |
