# Phase 0 Research: Governed Agent Run

**Feature**: 001-governed-agent-run | **Date**: 2026-08-10

Every unknown in the plan's Technical Context is resolved below. Each entry states the decision, why
it was chosen, and what was rejected. Where a decision is load-bearing for a constitution principle,
the principle is named.

---

## 1. Runtime and who owns the agent loop

- **Decision**: .NET 10 (LTS) with C# 13. No agent-orchestration framework. `RunOrchestrator` owns
  the loop and calls `IAgentTurnRunner` for one turn at a time; `Microsoft.Extensions.AI` supplies
  the provider-neutral chat and embedding shapes.
- **Rationale**: An agent framework earns its place by owning the tool-call loop. That loop is where
  stage transitions are decided, and Principle IV requires every run lifecycle transition to be
  implemented explicitly in backend code and forbids inferring one from model output. Confined to
  what Principle IV allows, the framework had nothing left to do — it was referenced as a package
  and used by no source file, which made the dependency list imply a compliance the code did not
  have. Constitution v2.0.0 resolved the tension in favour of Principle IV.
- **Revised 2026-08-20.** This decision originally read: "Agent runtime is Microsoft Agent Framework
  (`Microsoft.Agents.AI`), which reached 1.0 GA on 2026-04-02 … The constitution mandates the
  framework", and dismissed hand-rolling the loop as "re-implementing the mandated runtime for no
  measured benefit". That was written before the orchestrator existed. What the implementation
  showed is that the loop is not an incidental convenience but the place the governance lives, so
  the earlier reasoning had the dependency backwards.
- **Alternatives considered**: Adopting the framework and letting it own the loop — rejected, it
  puts stage transitions inside a third-party loop where they cannot be asserted on. Adopting it for
  prompt assembly and tool marshalling only — still permitted by the amended constitution, but it
  buys little over `Microsoft.Extensions.AI` and adds a dependency whose main feature must be left
  unused.

## 2. Chat provider adapter

- **Decision**: `IChatProviderAdapter` in Application, implemented in Infrastructure over
  `Microsoft.Extensions.AI.IChatClient`. Default implementation targets Claude via the official
  Anthropic .NET SDK (`Anthropic` NuGet package) with model id `claude-opus-5`, adaptive thinking,
  and `effort: xhigh` for the proposing stage / `high` elsewhere. Provider types, prompts, and SDK
  calls stay inside `RepoPilot.Infrastructure` and `RepoPilot.Agent`.
- **Rationale**: The constitution requires provider access behind an adapter with no leakage into
  Application or Domain. `Microsoft.Extensions.AI` gives a provider-neutral `IChatClient`, so the
  adapter is thin. Claude
  Opus 5 is the current default for agentic coding work; the id is a fixed string with no date
  suffix.
- **Alternatives considered**: Calling the SDK directly from the orchestrator — rejected, violates
  the layering constraint and makes provider comparison (a stretch goal) impossible without a
  rewrite. Pinning an older model — rejected; no reason to start behind.
- **Note**: token/cost metadata is read from the provider response and attached to the run's OTel
  span where the provider exposes it (Principle IV).

## 3. Embedding provider — separate from the chat provider

- **Decision**: A second adapter, `IEmbeddingProviderAdapter`, over
  `Microsoft.Extensions.AI.IEmbeddingGenerator<string, Embedding<float>>`. The development and
  evaluation default is a **local ONNX sentence-embedding model** loaded in-process (768 or 384
  dimensions, configured once and fixed per index); a hosted embedding provider is a configuration
  swap.
- **Rationale**: Anthropic's API has no embeddings endpoint, so the chat provider cannot supply
  vectors — a plan that assumed one adapter for both would be wrong. A local model additionally
  makes indexing deterministic and offline, which is what makes SC-007 (identical retrieval metrics
  on repeated evaluation) hold without caveats, and removes a per-token cost from the 30-task
  evaluation loop.
- **Alternatives considered**: A hosted embedding API as the default — rejected for the MVP because
  it adds a second vendor credential, a network dependency in CI, and a cost per evaluation run for
  no measured retrieval gain at fixture scale. Keyword-only retrieval with no vectors — rejected;
  FR-005 requires meaning-based search.
- **Constraint recorded**: the embedding model id and dimension are stored on the repository's index
  metadata. Changing either invalidates the index and forces a full rebuild.

## 4. Chunking strategy

- **Decision**: Line-window chunking — ~60 lines per chunk with 15 lines of overlap, never splitting
  across files, with language inferred from the file extension and stored as metadata. Files below
  the window size become a single chunk.
- **Rationale**: Every retrieval result must carry a repository-relative path and start/end lines
  (FR-004); line windows make those exact by construction. At fixture scale the recall gain from
  syntax-aware splitting does not justify a parser dependency per language (Principle III).
- **Alternatives considered**: tree-sitter or Roslyn symbol-boundary chunking — deferred; revisit
  only if measured Recall@5 misses the 80% target in SC-005, which is exactly the evidence
  Principle III asks for before adding complexity. Whole-file chunks — rejected, blows the
  retrieved-context budget and returns useless line ranges.

## 5. Hybrid retrieval and fusion

- **Decision**: One PostgreSQL query per search combining (a) pgvector cosine distance over the chunk
  embedding and (b) PostgreSQL full-text search (`tsvector`) plus `pg_trgm` similarity over the chunk
  content, fused with Reciprocal Rank Fusion at `k = 60`. Exact-identifier lookup routes through the
  lexical arm; meaning-based search through the vector arm; both always run and are fused.
- **Rationale**: FR-005 requires both modes, and the constitution names pgvector as the system of
  record — running both arms inside the same database keeps it that way with no second search
  service. RRF needs no score normalization between two incomparable scales and no tuned weight.
  Published .NET/pgvector hybrid results put fusion meaningfully ahead of pure vector search on
  retrieval precision.
- **Alternatives considered**: Vector-only — rejected, loses exact identifier matches, which is the
  most common real query in a coding agent. Weighted score blending — rejected; requires normalizing
  cosine distance against `ts_rank`, and the weight becomes a tuning knob with no principled default.
  A dedicated search service (Elasticsearch/Meilisearch) — rejected; adds infrastructure the
  constitution's storage constraint does not permit without justification.
- **Indexes**: HNSW on the embedding column, GIN on the `tsvector` column, GIN/`gin_trgm_ops` for
  trigram, plus a plain B-tree on `(repository_id, index_version)`.

## 6. Atomic index rebuild

- **Decision**: Monotonic `index_version` integer per repository. A rebuild writes all chunks at
  `version = N+1`, then a single transaction flips `repositories.active_index_version` to `N+1` and
  deletes rows at version `N`. All searches filter on the active version.
- **Rationale**: FR-003a requires full rebuild with atomic replacement and no duplicated or stale
  entries, and requires in-flight searches to keep serving the previous index until the swap. A
  version column plus one committed `UPDATE` gives exactly that with no table renames, no partition
  juggling, and no window where the index is half-populated.
- **Alternatives considered**: `DELETE` then `INSERT` — rejected, defines a window where searches
  return partial results. Table-swap via `ALTER TABLE ... RENAME` — rejected; heavier locking and it
  complicates foreign keys from `index_entries` to `repositories`. Incremental per-file updates —
  explicitly rejected in clarification; the correctness risk is not worth the saved time at fixture
  scale.

## 7. Patch proposal representation

- **Decision**: A proposal is a set of `{path, operation, newContent}` entries where `operation` is
  `create` or `modify`. The model returns full replacement content for each affected file; the system
  computes the unified diff for display with DiffPlex. Applying a proposal is a validated write of
  `newContent`, not a hunk application.
- **Rationale**: This removes the "diff does not apply cleanly" failure mode almost entirely — the
  edge case in the spec is then reachable only when the working copy changed underneath, which the
  hash check already catches. It also makes the approval hash trivial and exact: the approver sees a
  diff derived deterministically from the same bytes that will be written. Model-authored unified
  hunks are a well-known source of malformed context lines and off-by-one line numbers.
- **Alternatives considered**: Model-authored unified diff applied with a hunk applier — rejected for
  the failure rate and for making FR-020 (refuse to apply content differing from what was shown)
  harder to state precisely. Shelling out to `git apply` — rejected; it moves path validation outside
  the tool boundary, which Principle II forbids.
- **Constraint recorded**: proposals are capped (default 20 files, 256 KB per file, 512 KB total).
  Binary files are out of scope per the spec's assumptions. Large-file edits are a known limitation
  of the full-content approach and are acceptable at fixture scale.

## 8. Approval integrity — the diff hash

- **Decision**: `DiffHash` = SHA-256 over a canonical serialization of the proposal: entries sorted by
  path, each emitted as `path \n operation \n SHA-256(newContent)`, joined with `\n`. Stored on
  `change_proposals.diff_hash` and copied onto `approval_decisions.diff_hash`. `ApplyPatchCapability`
  recomputes the hash from the stored proposal and refuses unless it equals the approval's hash.
- **Rationale**: The constitution requires the audit record to name "the diff hash the decision
  applied to", and FR-020 requires refusing to apply content that differs from what was shown.
  Canonical ordering makes the hash independent of enumeration order; hashing content rather than the
  rendered diff makes it independent of diff formatting.
- **Alternatives considered**: Hashing the rendered unified diff — rejected; a DiffPlex version bump
  or a context-line setting change would invalidate historical approvals. Comparing proposal ids only
  — rejected; an id match does not prove the content is unchanged.

## 9. Working copy lifecycle

- **Decision**: One directory per run at `{workspaceRoot}/runs/{runId}`, created by recursive copy of
  the fixture directory honoring the same exclusion rules as indexing. Created **on entering
  `retrieving`**, before the run's first file access, so that "the workspace" has one meaning for
  the whole run and every read and write resolves against the same root (FR-024a). Destroyed by
  `WorkingCopyManager` when the run reaches any terminal outcome. A startup hosted service sweeps
  `{workspaceRoot}/runs/` and deletes any directory whose run id is absent or terminal (FR-026a).
- **Application is atomic**: entries are written to temporary files inside the working copy and moved
  into place together, so an interruption part-way leaves the pre-apply state rather than a partial
  change (FR-016b).
- **Rationale**: Fixtures are committed subdirectories of this repository, not independent git
  repositories, so a copy is the honest primitive. Naming the directory after the run id makes the
  orphan sweep a set difference against the database rather than a heuristic.
- **Alternatives considered**: `git clone --local` — rejected; fixtures are not separate repositories,
  and cloning this repository per run would copy the whole project. Copy-on-write overlays — rejected
  as platform-specific complexity for no MVP benefit. Retaining copies for debugging — rejected in
  clarification; SC-008 holds because the diff, test output, and events are persisted independently
  (FR-026b), so nothing needs the copy to survive.

## 10. Sandbox execution

- **Decision**: `Docker.DotNet` against the Docker Engine API. Per execution: a container from the
  fixture's declared pre-baked image, bind-mounting only the run's working copy at `/workspace`;
  `NetworkMode = "none"`; `ReadonlyRootfs = true` with a small `tmpfs` at `/tmp`; `User = "1000:1000"`;
  `CapDrop = ["ALL"]`; `SecurityOpt = ["no-new-privileges"]`; memory, NanoCPU, and PID limits; no
  inherited host environment variables. The command is selected by name from the fixture's committed
  allow-list — never a model-produced string. Timeout is enforced by a `CancellationToken` that kills
  and removes the container; the result records `TimedOut = true`.
- **Rationale**: Principle II names every one of these controls. Using the Engine API rather than
  shelling to the `docker` CLI gives structured exit codes, streamed logs, and a kill path that does
  not depend on parsing CLI output or on a shell being present.
- **Alternatives considered**: `Process.Start("docker", ...)` — rejected; command construction from
  strings is exactly the surface Principle II is trying to eliminate, and timeout handling becomes
  process-tree management. gVisor/Firecracker — stronger isolation, rejected as out of scope for the
  MVP and not required by the constitution.
- **Constraint recorded**: because the sandbox has no network, fixture dependencies must be restored
  into the image at build time. `sandbox/Dockerfile.*` bakes them; a fixture whose tests need a
  network fetch is not a valid fixture.

## 11. Concurrency and queueing

- **Decision**: An in-process bounded queue (`System.Threading.Channels`) with a worker pool sized by
  `MaxConcurrentRuns` (default 4). A run occupies a slot for the retrieve→propose segment, **releases
  it** on entering `awaiting approval`, and re-acquires a slot for the apply→test segment after a
  decision. Sandbox containers are additionally capped by the same limit.
- **Rationale**: FR-013a/FR-013b exactly. Releasing the slot across the human wait is what prevents
  one unanswered approval from stalling the system; it is also why the orchestrator is written as
  resumable segments rather than one long method.
- **Alternatives considered**: One long-lived task per run holding a slot throughout — rejected for
  the deadlock. Redis-backed queue — rejected; the constitution permits Redis only with written
  justification and an in-process queue meets the requirement, given that runs are explicitly not
  resumable across restarts.
- **Restart behavior**: on startup, any run in a non-terminal stage is transitioned to `failed` with
  reason `service_restarted`, and its working copy is swept (matches the spec's assumption).

## 12. Live event delivery

- **Decision**: Server-Sent Events at `GET /api/runs/{id}/events`, backed by a per-run in-process
  broadcast channel. Every persisted `RunEvent` is written to the database first, then published to
  subscribers. Each SSE frame carries `id: {sequence}`; a reconnecting client sends `Last-Event-ID`
  and the endpoint replays persisted events from that sequence before attaching to the live stream.
- **Rationale**: FR-028a/SC-013 need push, not polling — a 2-second budget with polling means a 1–2
  second poll interval and wasted load. SSE is one-directional, works through ordinary HTTP
  infrastructure, and needs no client library. Persist-then-publish is what makes replay and SC-008
  (reconstruct from recorded events alone) the same mechanism.
- **Alternatives considered**: SignalR/WebSockets — rejected; bidirectional transport and a hub
  abstraction for a stream that only flows one way. Polling `GET /api/runs/{id}` — rejected against
  the 2-second target.

## 13. Run state machine

- **Decision**: `RunStage` enum plus a static, exhaustive transition table in `RepoPilot.Domain`:
  `RunStateMachine.TryTransition(RunStage from, RunTrigger trigger, out RunStage to)`. The
  orchestrator calls it before every stage change; a rejected transition throws
  `IllegalTransitionException` and the handler records a `StageTransitionRejected` event rather than
  continuing. Terminal outcomes: `Succeeded`, `Failed`, `Rejected`, `Cancelled`, `NoChange`, each with
  a recorded reason.
- **Rationale**: Principle IV requires transitions implemented explicitly in backend code and failing
  loudly. A pure table in the Domain layer is unit-testable exhaustively — every (stage, trigger)
  pair — with no database or container, which is what the mandatory test list asks for.
- **Alternatives considered**: A workflow library (Stateless, MassTransit sagas) — rejected as an
  orchestration layer Principle III would require justifying. Inferring stage from the presence of
  related rows — rejected; makes illegal transitions silent.

## 14. Observability

- **Decision**: A single `ActivitySource("RepoPilot")`. One span per run, child spans per stage, child
  spans per capability invocation carrying tool name, run id, argument summary, and status. Metrics:
  run duration histogram, stage duration, capability call counter by name and outcome, sandbox
  execution duration, queue depth, and provider token/cost counters where exposed. OTLP exporter,
  configured endpoint, console exporter in development.
- **Rationale**: Principle IV names latency, tool calls, errors, and token/cost metadata explicitly.
  Capability spans are created inside `ToolInvoker`, so instrumentation cannot be forgotten when a
  capability is added.
- **Alternatives considered**: Logging only — rejected; the constitution requires traces. A hosted APM
  SDK — rejected; OTLP keeps the backend swappable.

## 15. Evaluation harness

- **Decision**: `RepoPilot.Evals` console CLI reads every task JSON under `evals/tasks/`, executes the
  same orchestrator path used by interactive runs with `ApprovalMode = Programmatic` (which still
  writes a real `ApprovalDecision` row bound to the diff hash and counts toward SC-001), and writes
  `EvaluationResult` rows plus a committed JSON report under `evals/results/`. It runs each task twice
  per evaluation: once `ToolsEnabled = false` (retrieval-only baseline) and once `ToolsEnabled = true`.
- **Rationale**: FR-032–FR-034 and SC-006 require both figures over the same task set from the same
  flow; reusing the orchestrator is the only way the approval-coverage metric measures the real code
  path rather than a parallel one.
- **Reference-patch isolation**: reference patches live in `evals/tasks/_reference/`, outside every
  fixture root. Since only fixture roots are indexable and the path guard confines file access to the
  run's working copy, a reference patch cannot enter model context by any code path — FR-035 is
  satisfied structurally rather than by a filter that could be misconfigured.
- **Determinism**: retrieval metrics are computed against the stored active index; an evaluation run
  never re-indexes. Two runs over unchanged fixtures therefore read identical vectors and produce
  identical Recall@5 (SC-007).
- **Alternatives considered**: A separate lightweight evaluation path that skips the approval gate —
  rejected; it would make SC-001 unmeasurable on the code path that matters.

## 16. Secret handling and indexing exclusions

- **Decision**: Exclusion happens at index time and is layered: (a) directory denylist
  (`.git`, `node_modules`, `bin`, `obj`, `dist`, `build`, `target`, `vendor`, `.venv`); (b) binary
  detection by null-byte sniff of the first 8 KB; (c) size limit (default 256 KB); (d) secret-file
  patterns (`.env*`, `*.pem`, `*.key`, `*.pfx`, `id_rsa*`, `*credentials*`); (e) content scan for
  known token shapes and high-entropy strings, which excludes the whole file. Indexing reports
  included and excluded counts by reason (FR-003).
- **Rationale**: Principle II requires that credentials and secrets never enter model context. Because
  the only content the agent can read is an indexed chunk, a `read_file` of a path inside the
  working copy, or **test output returned during a revision attempt**, all three go through the same
  redaction predicate: exclusion at index time, the same predicate in `read_file`, and a redaction
  pass over sandbox output before it is stored, displayed, or returned to the agent (FR-025a,
  FR-025b). Recorded action arguments pass through the same predicate (FR-027a).
- **Alternatives considered**: Redacting secrets in-place rather than excluding the file — rejected;
  partial redaction is a detection problem with false negatives, and excluding is cheap at fixture
  scale. Excluding only at retrieval time — rejected; the secret would still sit in the database.

---

## Resolved unknowns summary

| Technical Context field | Resolution |
|---|---|
| Language/Version | .NET 10 LTS, C# 13; TypeScript 5.x + React 19 |
| Primary Dependencies | Microsoft.Extensions.AI, Anthropic .NET SDK, EF Core 10 + pgvector-dotnet, DiffPlex, Docker.DotNet, OpenTelemetry |
| Storage | PostgreSQL 17+ with pgvector 0.8+; filesystem workspace root; no Redis |
| Testing | xUnit + Testcontainers; Vitest; one API-level e2e |
| Target Platform | Linux containers; Docker daemon access required |
| Performance Goals | <3 min to reviewable diff at/below concurrency limit; 95% of events within 2 s |
| Constraints | Approval-gated writes, network-isolated sandbox, path guard, context and size limits |
| Scale/Scope | ≤5,000 files per fixture, 4 concurrent runs by default, ≥30 evaluation tasks |

No `NEEDS CLARIFICATION` markers remain.

## Sources

- [NuGet — Microsoft.Agents.AI](https://www.nuget.org/packages/Microsoft.Agents.AI/)
- [Microsoft Agent Framework reaches Release Candidate — Microsoft Foundry Blog](https://devblogs.microsoft.com/foundry/microsoft-agent-framework-reaches-release-candidate/)
- [Microsoft Agent Framework at BUILD 2026](https://devblogs.microsoft.com/agent-framework/microsoft-agent-framework-at-build-2026-announce/)
- [Getting started with pgvector in .NET — Milan Jovanović](https://milanjovanovic.tech/blog/getting-started-with-pgvector-in-dotnet-for-simple-vector-search)
- [Npgsql EF Core — Full Text Search mapping](https://www.npgsql.org/efcore/mapping/full-text-search.html)
- [Building hybrid search for RAG: pgvector + full-text search with RRF](https://dev.to/lpossamai/building-hybrid-search-for-rag-combining-pgvector-and-full-text-search-with-reciprocal-rank-fusion-6nk)
