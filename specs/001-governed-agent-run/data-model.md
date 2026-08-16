# Phase 1 Data Model: Governed Agent Run

**Feature**: 001-governed-agent-run | **Date**: 2026-08-10 | **Store**: PostgreSQL 17+ with pgvector

All identifiers are UUIDv7 unless stated. All timestamps are `timestamptz` in UTC. Enumerations are
stored as PostgreSQL enum types so an illegal value cannot be persisted.

---

## Entity overview

```text
RepositoryFixture 1───* IndexEntry
        │
        └──* Run 1───* RunEvent
              │  1───* ChangeProposal 1───0..1 ApprovalDecision
              │  1───0..1 WorkingCopy
              │  1───* TestResult
              │
EvaluationRun 1───* EvaluationTaskResult *───1 EvaluationTask (file-backed)
              └──* Run
```

---

## 1. RepositoryFixture

A registered code repository the system is permitted to work against.

| Field | Type | Notes |
|---|---|---|
| `id` | uuid PK | |
| `slug` | text UNIQUE NOT NULL | Stable key from the allowed set, e.g. `sample-dotnet-api` |
| `display_name` | text NOT NULL | |
| `root_path` | text NOT NULL | Repository-relative path under `evals/fixtures/`; resolved and validated at registration |
| `indexing_status` | enum NOT NULL | `never_indexed` \| `indexing` \| `indexed` \| `failed` |
| `active_index_version` | int NULL | `NULL` until the first successful swap |
| `embedding_model_id` | text NULL | Pinned at first index; changing it forces a rebuild |
| `embedding_dimensions` | int NULL | Must match the `vector(n)` column configuration |
| `last_indexed_at` | timestamptz NULL | |
| `included_file_count` | int NOT NULL DEFAULT 0 | Reported by FR-003 |
| `excluded_file_count` | int NOT NULL DEFAULT 0 | |
| `exclusion_breakdown` | jsonb NOT NULL DEFAULT '{}' | Counts keyed by reason: `binary`, `size`, `denylisted_dir`, `secret_pattern`, `secret_content` |
| `test_config` | jsonb NOT NULL | Validated against `contracts/fixture-config.schema.json` |
| `created_at` | timestamptz NOT NULL | |

**Validation rules**

- `slug` must appear in the configured allowed set; registration is refused otherwise (FR-001).
- `root_path` must canonicalize to a location inside the configured fixtures root (Principle II).
- `test_config.commands` must be non-empty; each entry has a `name` and an `argv` array — never a
  shell string.
- A fixture with `included_file_count = 0` may be registered but runs against it are refused
  (edge case: empty fixture).

## 2. IndexEntry

A searchable unit of repository content.

| Field | Type | Notes |
|---|---|---|
| `id` | uuid PK | |
| `repository_id` | uuid FK → RepositoryFixture | |
| `index_version` | int NOT NULL | Rows are visible only when equal to the repository's `active_index_version` |
| `relative_path` | text NOT NULL | Repository-relative, forward slashes |
| `chunk_ordinal` | int NOT NULL | 0-based within the file |
| `content` | text NOT NULL | |
| `start_line` | int NOT NULL | 1-based, inclusive |
| `end_line` | int NOT NULL | 1-based, inclusive |
| `language` | text NULL | Inferred from extension |
| `symbols` | text[] NULL | Best-effort identifier list for lexical boosting |
| `embedding` | vector(n) NOT NULL | `n` = repository `embedding_dimensions` |
| `content_tsv` | tsvector GENERATED | `to_tsvector('simple', content)` |

**Indexes**: HNSW on `embedding` (cosine); GIN on `content_tsv`; GIN `gin_trgm_ops` on `content`;
B-tree on `(repository_id, index_version)`; UNIQUE on `(repository_id, index_version, relative_path,
chunk_ordinal)`.

**Validation rules**

- `end_line >= start_line`.
- No row may be created for a path excluded by the indexing exclusion set (FR-002, Principle II).
- Rebuild writes `index_version = active + 1`, then one transaction flips
  `active_index_version` and deletes the previous version (FR-003a).

## 3. Run

One task execution against one repository.

| Field | Type | Notes |
|---|---|---|
| `id` | uuid PK | |
| `repository_id` | uuid FK → RepositoryFixture | |
| `task_description` | text NOT NULL | Free text or copied from a seeded task |
| `seeded_task_id` | text NULL | Set when started from the committed task set |
| `stage` | enum NOT NULL | See state machine below |
| `terminal_outcome` | enum NULL | `succeeded` \| `failed` \| `rejected` \| `cancelled` \| `no_change` |
| `outcome_reason` | enum NULL | Normative set (FR-008c), stored as a PostgreSQL enum so an undocumented reason cannot be persisted: `revision_limit_reached`, `deliberate_no_op`, `insufficient_context`, `proposal_rejected`, `abandoned_by_user`, `provider_unavailable`, `isolated_env_unavailable`, `isolated_env_not_terminable`, `service_restarted` — named to match the FR-008c vocabulary rather than the runtime that happens to implement isolation |
| `failure_stage` | enum NULL | Stage at which a non-success outcome occurred (FR-030) |
| `revision_attempt` | int NOT NULL DEFAULT 0 | Capped at 2 (FR-012) |
| `tools_enabled` | boolean NOT NULL DEFAULT true | `false` for the retrieval-only baseline |
| `verify_command_name` | varchar(64) NULL | Allow-listed command this run is verified against; null uses the fixture's default. Set from an evaluation task's success command, and stored rather than resolved at test time so the record says which command decided the run and a later task edit cannot rewrite it |
| `approval_mode` | enum NOT NULL | `interactive` \| `programmatic` |
| `evaluation_run_id` | uuid NULL FK → EvaluationRun | |
| `created_at` / `started_at` / `ended_at` | timestamptz | |

**State machine** (implemented as a static transition table in `RepoPilot.Domain`; every transition
is attempted in backend code and persisted before the next action — Principle IV):

```text
created ──▶ retrieving ──▶ planning ──▶ proposing ──▶ awaiting_approval
                                                       │
                                    (reject)───────────┼──────────▶ rejected ●
                                    (abandon)──────────┤
                                                       ▼
                                                   applying ──▶ testing
                                                                  │
                                       (pass)─────────────────────┼──▶ succeeded ●
                                       (fail, attempts < 2)───────┼──▶ proposing
                                       (fail, attempts = 2)───────┴──▶ failed ●

any non-terminal stage ──(cancel)──▶ cancelled ●
proposing ──(no modification proposed)──▶ no_change ●
retrieving ──(insufficient context)──▶ no_change ●
any non-terminal stage ──(error)──▶ failed ●
```

**Validation rules**

- Exactly one terminal outcome per run; `ended_at` is set with it.
- A transition not present in the table raises `IllegalTransitionException`; the handler records a
  `stage_transition_rejected` event and does not continue (FR-009).
- `revision_attempt` may not exceed 2; the third failure transitions to `failed` with
  `outcome_reason = revision_limit_reached` (FR-012).
- Entering `applying` requires a matching `ApprovalDecision` with `decision = approve` whose
  `diff_hash` equals the current proposal's (FR-014, FR-020, Principle I).
- `no_change` runs must have no `ChangeProposal` rows (FR-008b).
- A run holds a concurrency slot only while in `retrieving`/`planning`/`proposing` or
  `applying`/`testing`; `awaiting_approval` holds none (FR-013b).

## 4. RunEvent

A single recorded action or stage change within a run. This table alone must be sufficient to
reconstruct a completed run (FR-029, SC-008).

| Field | Type | Notes |
|---|---|---|
| `id` | uuid PK | |
| `run_id` | uuid FK → Run | |
| `sequence` | bigint NOT NULL | Monotonic per run; also the SSE `id:` value |
| `event_type` | enum NOT NULL | `stage_changed` \| `stage_transition_rejected` \| `tool_call` \| `plan_produced` \| `proposal_created` \| `approval_recorded` \| `patch_applied` \| `tests_completed` \| `run_failed` \| `run_ended` |
| `tool_name` | text NULL | Set for `tool_call` |
| `arguments_summary` | jsonb NULL | Redacted, bounded; never full file contents |
| `status` | enum NOT NULL | `succeeded` \| `failed` |
| `error_message` | text NULL | |
| `started_at` | timestamptz NOT NULL | |
| `ended_at` | timestamptz NULL | |
| `duration_ms` | int NULL | |

**Indexes**: UNIQUE on `(run_id, sequence)`; B-tree on `(run_id, id)`.

**Validation rules**

- Written before the event is published to SSE subscribers, so a replay from `Last-Event-ID` can
  never miss an event that a live subscriber saw.
- `arguments_summary` passes through the same redaction predicate as model context.

## 5. ChangeProposal

A set of file modifications the agent proposes. Immutable once created.

| Field | Type | Notes |
|---|---|---|
| `id` | uuid PK | |
| `run_id` | uuid FK → Run | |
| `revision_attempt` | int NOT NULL | Matches the run's attempt at creation time |
| `entries` | jsonb NOT NULL | Array of `{path, operation, new_content}` |
| `unified_diff` | text NOT NULL | Rendered for display; never the hash input |
| `affected_paths` | text[] NOT NULL | Denormalized for the review list (FR-011, SC-004) |
| `diff_hash` | char(64) NOT NULL | SHA-256 over the canonical serialization (see below) |
| `decision_status` | enum NOT NULL | `pending` \| `approved` \| `rejected` |
| `created_at` | timestamptz NOT NULL | |

**Canonical hash input**: entries sorted by `path`; each rendered as
`path \n operation \n sha256(new_content)`; joined with `\n`; hashed with SHA-256.

**Validation rules**

- `operation ∈ {create, modify}`; binary content is rejected.
- Every `path` must canonicalize inside the run's working copy; a path resolving outside is rejected
  before any file is touched (FR-024, SC-010).
- Caps: ≤20 entries, ≤256 KB per `new_content`, ≤512 KB total.
- `entries` must be non-empty — an empty proposal is a `no_change` outcome, not a proposal (FR-008b).
- Creating a proposal performs no write of any kind (Principle I).

## 6. ApprovalDecision

A human decision on one proposal. Append-only; there is no update path.

| Field | Type | Notes |
|---|---|---|
| `id` | uuid PK | |
| `run_id` | uuid FK → Run | |
| `proposal_id` | uuid FK → ChangeProposal | UNIQUE — enforces "at most one decision" (FR-018) |
| `decision` | enum NOT NULL | `approve` \| `reject` |
| `diff_hash` | char(64) NOT NULL | Copied from the proposal at decision time |
| `decided_by` | text NOT NULL | Identity supplied by the deployment; `evaluation-harness` in programmatic mode |
| `decided_at` | timestamptz NOT NULL | |
| `mode` | enum NOT NULL | `interactive` \| `programmatic` — both count toward SC-001 |

**Validation rules**

- The UNIQUE constraint on `proposal_id` is the enforcement point for FR-018: a second decision — of
  either kind, in either order, by the same or a different actor — fails at the database, not in a
  race-prone service check.
- `diff_hash` must equal the proposal's `diff_hash` at insert time, and the request must carry it
  back explicitly (FR-020a).
- `decided_by` must be non-empty; a decision with no actor identity is refused (FR-015a, SC-015).
  The value is attributable, not verified — the deployment supplies it and this feature does not
  authenticate it.
- `mode = programmatic` is accepted only for runs belonging to an evaluation run (FR-015b).
- Append-only: the table has no update or delete path, and rows outlive nothing shorter than the run
  they belong to (FR-019b).
- A `reject` decision performs no filesystem operation whatsoever (FR-017).

## 7. WorkingCopy

A disposable copy of a fixture created for one run.

| Field | Type | Notes |
|---|---|---|
| `id` | uuid PK | |
| `run_id` | uuid FK → Run | UNIQUE — one working copy per run |
| `absolute_path` | text NOT NULL | `{workspaceRoot}/runs/{runId}` |
| `created_at` | timestamptz NOT NULL | |
| `destroyed_at` | timestamptz NULL | Set when the directory is removed |

**Validation rules**

- Created on entering `retrieving`, before the run's first file access. Every agent file read and
  every applied write resolves against this directory and nothing else, which is what makes "the
  repository workspace" a single unambiguous root for the whole run (FR-024a).
- Destroyed when the run reaches any terminal outcome, including `cancelled` and `rejected`
  (FR-026a).
- Startup sweep deletes any directory under `{workspaceRoot}/runs/` whose run id is unknown or
  terminal (SC-012).
- No completed run may depend on this row or directory for inspection (FR-026b).

## 8. TestResult

Outcome of one sandboxed execution.

| Field | Type | Notes |
|---|---|---|
| `id` | uuid PK | |
| `run_id` | uuid FK → Run | |
| `revision_attempt` | int NOT NULL | |
| `command_name` | text NOT NULL | Name from the fixture allow-list, not a shell string |
| `passed` | boolean NOT NULL | |
| `exit_code` | int NULL | `NULL` when terminated by timeout |
| `output` | text NOT NULL | Combined stdout/stderr, redacted for secrets before storage, display, or re-entry into model context (FR-025b), then truncated to a configured cap with a marker |
| `duration_ms` | int NOT NULL | |
| `timed_out` | boolean NOT NULL DEFAULT false | FR-023, SC-009 |
| `created_at` | timestamptz NOT NULL | |

**Validation rules**

- `command_name` must resolve against the fixture's `test_config.commands`; anything else is refused
  before container creation (FR-022).
- `timed_out = true` implies `passed = false` and a torn-down container.

## 9. EvaluationTask (file-backed, not a table)

A committed task definition under `evals/tasks/`, validated against
`contracts/eval-task.schema.json`.

| Field | Type | Notes |
|---|---|---|
| `id` | string | Stable, e.g. `bugfix-null-guard-01` |
| `repository_slug` | string | Must be a registered fixture |
| `description` | string | Given to the agent verbatim |
| `relevant_files` | string[] | Ground truth for Recall@5 |
| `baseline_test_command` | string | Name from the fixture allow-list |
| `success_test_command` | string | Name from the fixture allow-list |
| `success_condition` | object | Machine-checkable; no human judgement (SC-011) |
| `category` | string | `bug_fix` \| `input_validation` \| `api_behavior` \| `refactor` \| `test_work` |

**Validation rules**

- At least 30 task files must exist for an evaluation to be considered complete (FR-031, SC-011).
- Reference patches live under `evals/tasks/_reference/`, outside every fixture root, so no indexing
  or file-read path can reach them (FR-035).

## 10. EvaluationRun and EvaluationTaskResult

| EvaluationRun field | Type | Notes |
|---|---|---|
| `id` | uuid PK | |
| `started_at` / `ended_at` | timestamptz | |
| `task_count` | int NOT NULL | |
| `recall_at_5` | numeric(5,4) NULL | FR-032, SC-005 |
| `completion_rate_tool_enabled` | numeric(5,4) NULL | FR-032 |
| `completion_rate_baseline` | numeric(5,4) NULL | FR-033, SC-006 |
| `approval_coverage` | numeric(5,4) NULL | FR-034; below 1.0 sets `flagged` |
| `tool_success_rate` | numeric(5,4) NULL | |
| `avg_tool_calls_per_completed_task` | numeric(6,2) NULL | |
| `p50_latency_ms` / `p95_latency_ms` | int NULL | |
| `flagged` | boolean NOT NULL DEFAULT false | `true` when approval coverage < 100% (SC-001) |

| EvaluationTaskResult field | Type | Notes |
|---|---|---|
| `id` | uuid PK | |
| `evaluation_run_id` | uuid FK | |
| `task_id` | text NOT NULL | |
| `mode` | enum NOT NULL | `baseline` \| `tool_enabled` |
| `run_id` | uuid FK → Run | |
| `relevant_file_in_top_5` | boolean NOT NULL | |
| `success_condition_met` | boolean NOT NULL | |
| `tool_call_count` | int NOT NULL | |
| `duration_ms` | int NOT NULL | |

**Validation rules**

- Retrieval metrics are computed against the stored active index and never trigger re-indexing, which
  is what makes repeat runs bit-identical (SC-007).
- Every applied change counted here must have a matching `ApprovalDecision` row; a mismatch sets
  `flagged` and fails the evaluation rather than being reported as a number.

---

## Cross-cutting invariants

These are the invariants the mandatory test list in the constitution targets directly.

1. **No write without approval.** The only code path that writes to a working copy is
   `ApplyPatchCapability`, and it loads an `ApprovalDecision` with `decision = approve` and a matching
   recomputed `diff_hash` before opening any file handle.
2. **No write outside the working copy.** `PathGuard.Resolve(root, candidate)` canonicalizes and
   rejects before I/O; every capability that touches the filesystem calls it.
3. **At most one decision per proposal.** Database UNIQUE constraint, not application logic.
4. **At most one applied change per run per attempt.** `ChangeProposal.decision_status` moves
   `pending → approved` exactly once, guarded by the same UNIQUE constraint.
5. **Every capability invocation is recorded.** `ToolInvoker` writes the `RunEvent` in a `finally`
   block, so a throwing capability still produces a `failed` record.
6. **Reconstructability.** Given `run_events`, `change_proposals`, and `test_results` for a run id,
   the full stage sequence, action history, final diff, and test output are recoverable with no
   reference to the working copy or to live process state.
7. **One workspace root per run.** The working copy exists for the whole run, from `retrieving`
   onward, so there is exactly one meaning of "inside the workspace" at every stage. The registered
   fixture is opened for reading only — by the indexer and by the copy that creates the working
   copy — and by no code path for writing (FR-016a).
8. **Atomic application.** Applying a proposal stages every file and commits them together; an
   interruption part-way leaves the working copy in its pre-apply state, and a restart destroys it
   regardless (FR-016b, FR-030a).
9. **No decision without an actor and a hash.** Both columns are `NOT NULL` and the hash is compared
   against the stored proposal before insert, so SC-015 is a schema property rather than a runtime
   check that could be skipped.
