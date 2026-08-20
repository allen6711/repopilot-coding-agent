# Implementation Plan: Governed Agent Run (MVP End-to-End Flow)

**Branch**: `001-governed-agent-run` | **Date**: 2026-08-10 | **Spec**: [spec.md](./spec.md)

**Input**: Feature specification from `/specs/001-governed-agent-run/spec.md`

## Summary

Deliver the full MVP flow: register and index a repository fixture, retrieve context with hybrid
search, drive a single agent over a constrained read-only tool surface from an orchestrator that
owns the loop,
produce a plan and a structured patch proposal, gate every write behind a recorded human approval
bound to a diff hash, apply the approved change only to a disposable working copy, execute
allow-listed test commands inside a network-isolated Docker container with a timeout, and stream
every stage and tool call to the reviewer within 2 seconds.

The technical approach keeps the run state machine entirely in backend C# code — the model never
drives a stage transition. The agent's exposed tool surface is read-only plus `propose_patch`;
`apply_patch` and `run_tests` are the same enforced capabilities but are invoked by the orchestrator
after an approval record exists, so an unapproved write is impossible by construction rather than by
prompt instruction. Retrieval uses PostgreSQL as the single system of record: pgvector for
meaning-based search and tsvector/pg_trgm for exact identifier lookup, fused with Reciprocal Rank
Fusion. Patch proposals carry full replacement content per file rather than model-authored hunks,
which removes an entire class of "diff does not apply" failures and makes the approval hash trivial
to compute and verify.

## Technical Context

**Language/Version**: C# 13 on .NET 10 (LTS) for backend, agent runtime, and evaluation CLI;
TypeScript 5.x with React 19 for the web UI.

**Primary Dependencies**: ASP.NET Core Minimal APIs; `Microsoft.Extensions.AI` abstractions for
provider-neutral chat and embedding shapes — no agent-orchestration framework, because the loop it
would own is where stage transitions are decided and Principle IV puts those in backend code
(constitution v2.0.0); Anthropic
.NET SDK (`Anthropic` package) as the default chat-provider adapter implementation
(`claude-opus-5`); Npgsql + EF Core 10 with `pgvector-dotnet`; DiffPlex for unified-diff rendering;
Docker.DotNet for sandbox orchestration; OpenTelemetry .NET SDK with OTLP exporter; xUnit +
Testcontainers for integration tests; Vite + TanStack Query + Vitest on the frontend.

**Storage**: PostgreSQL 17+ with pgvector 0.8+ (repository chunks, runs, events, proposals,
approvals, test results, evaluation results). Local filesystem under a configured workspace root for
disposable working copies. No Redis in the MVP — the concurrency limiter and event fan-out are
in-process, so the constitution's "written justification" bar for Redis is not met and it is omitted.

**Testing**: xUnit for unit and integration tests; Testcontainers for PostgreSQL/pgvector and for the
Docker sandbox runner; Vitest + React Testing Library for UI units; one API-level end-to-end test
driving a seeded task from creation through approval, apply, test execution, and final result.

**Target Platform**: Linux containers (x64/arm64), orchestrated locally with Docker Compose; the API
requires access to a Docker daemon socket to create sandbox containers.

**Project Type**: Web application (ASP.NET Core backend + React frontend) plus a headless evaluation
CLI.

**Performance Goals**: SC-003 — start-of-run to reviewable diff under 3 minutes at or below the
configured concurrency limit. SC-013 — 95% of stage transitions and recorded actions visible to a
watching reviewer within 2 seconds (server-push, no polling).

**Constraints**: No file modification without a recorded approval whose diff hash matches the applied
change (Principle I). Test execution in a container with `--network none`, read-only root filesystem,
dropped capabilities, non-root user, and a hard timeout (Principle II). All file access resolved and
validated against the workspace root before any I/O. Repository credentials and secrets never enter
model context. Retrieved-context and file-size limits enforced at the tool boundary.

**Scale/Scope**: Repository fixtures under ~5,000 indexable files and ~50 MB of text; default
per-file size limit 256 KB; default retrieved-context budget 60,000 characters per run; default
maximum 4 concurrently executing runs; committed evaluation set of at least 30 tasks.

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

Gates derived from `.specify/memory/constitution.md` v1.0.0.

| # | Gate (Principle) | Design satisfies it by | Status |
|---|---|---|---|
| I | Human-Approved Writes (NON-NEGOTIABLE) | `propose_patch` returns a value object and touches no file. `ApplyPatchCapability` loads the `ApprovalDecision` for the proposal, recomputes the proposal's SHA-256 diff hash, and refuses when absent or mismatched. Apply targets only `{workspaceRoot}/runs/{runId}`, never the fixture. Rejection performs no write. Every decision is audit-logged with run id, actor, decision, timestamp, and diff hash. | PASS |
| II | Sandboxed, Allow-Listed Execution | `run_tests` executes only a command drawn from the fixture's committed `repopilot.fixture.json` allow-list, inside a Docker container with `NetworkMode=none`, `ReadonlyRootfs=true`, `CapDrop=[ALL]`, `no-new-privileges`, non-root user, memory/CPU/PID limits, no inherited environment, and a hard timeout enforced by container kill. `PathGuard` canonicalizes and validates every path before I/O. File-size and context-size limits are enforced inside the tool invoker. Secrets are excluded at index time and never assembled into model context. | PASS |
| III | Minimal Agent, Constrained Tool Surface | Exactly one agent. Exactly the seven defined capabilities — no additions. Each declares its permission class (`Read`, `NoDirectWrite`, `WriteWithApproval`, `SandboxExecution`) and is enforced in `ToolInvoker` at the call site, not by prompt text. No planner/coder/reviewer split. | PASS (see narrowing note below) |
| IV | Explicit State, Full Traceability | `RunStateMachine` is a static transition table in the Domain layer; every transition is attempted in backend code and persisted before the next action. Illegal transitions throw and are recorded as `StageTransitionRejected` events. Every capability invocation records name, run id, argument summary, start/end, and status. OTel spans wrap run, stage, and tool call. A completed run is replayable from `run_events` alone. | PASS |
| V | Evidence Before Claims | `RepoPilot.Evals` computes Recall@5, completion rate, retrieval-only baseline vs tool-enabled, approval coverage, tool success rate, and latency from the committed task set. Reference patches live outside every indexed fixture directory, so they cannot enter model context. README numbers stay labelled as targets until this harness produces measured values. Approval coverage below 100% fails the evaluation. | PASS |
| — | Technology and Architecture Constraints | C#/ASP.NET Core, React + TypeScript, PostgreSQL + pgvector as system of record, no Redis, provider access behind `IChatProviderAdapter` / `IEmbeddingProviderAdapter` with no provider types in Application or Domain, indexing exclusions enforced, retrieval results carry path/chunk id/content/start line/end line/score. All artifacts in English. The agent loop is owned by `RunOrchestrator`; no orchestration framework is referenced or used (see the v2.0.0 note below). | PASS |
| — | Development Workflow and Quality Gates | Every listed mandatory test area has a named test project and target in this plan; integration tests cover pgvector and the sandbox runner; one end-to-end test covers the seeded-task flow; GitHub Actions blocks merge on failure; no stretch-goal work is planned. | PASS |

**Narrowing note on Principle III.** The constitution requires the seven-tool set and forbids
*adding* tools, agents, or orchestration layers. This design narrows rather than adds: the agent's
prompt-visible tool surface during the retrieving/planning/proposing stages is the five read
capabilities plus `propose_patch`, while `apply_patch` and `run_tests` — identical implementations
behind identical call-site guards — are invoked by the orchestrator during the applying and testing
stages. The reason is Principle IV: if the model could call `apply_patch` mid-turn, the stage
transition into `applying` would be inferred from model output rather than implemented in backend
code. No capability is added, removed, or weakened; only the point of invocation moves inside the
backend. This is recorded here so a reviewer can check it against Principles III and IV explicitly.

**Post-Phase 1 re-check**: re-evaluated after `data-model.md`, `contracts/`, and `quickstart.md` were
written. No gate regressed. The data model adds no entity that stores a secret; the REST contract
exposes no endpoint that writes without an approval; the tool contract pins each permission class to
a documented enforcement point. **PASS.**

**Constitution v2.0.0 re-check (2026-08-20).** The gate above previously read PASS while naming
Microsoft Agent Framework, which was referenced as a package and used by no source file. The
constraint and the design were in genuine tension: an agent framework earns its place by owning the
tool-call loop, and that loop is where stage transitions are decided — which Principle IV requires
to be explicit backend code. `RunOrchestrator` therefore drives the loop and calls
`IAgentTurnRunner` for one turn at a time, leaving the framework nothing to do. The constitution was
amended rather than the design: the rule is now that the loop MUST be owned by backend code and a
framework MAY only assist with prompt assembly, tool-definition marshalling, or provider transport.
The unused package reference was removed in the same change, so the dependency list no longer
implies a compliance the code did not have.

**Post-checklist re-check (2026-08-10)**: after the governance checklist remediation added 30
requirements and 2 success criteria, every gate was re-evaluated. Three controls that had existed
only as design decisions in this plan are now traceable to requirements — network isolation
(FR-021a), resource limits (FR-021b), and the seven-capability set with its permission classes
(FR-026c) — which strengthens Gates II and III rather than changing them. One conflict was resolved:
the working copy is now created on entering `retrieving`, giving a single unambiguous workspace root
for the whole run (FR-024a). **PASS.**

## Project Structure

### Documentation (this feature)

```text
specs/001-governed-agent-run/
├── plan.md                    # This file (/speckit-plan command output)
├── spec.md                    # Feature specification (clarified 2026-08-10)
├── research.md                # Phase 0 output
├── data-model.md              # Phase 1 output
├── quickstart.md              # Phase 1 output
├── contracts/                 # Phase 1 output
│   ├── rest-api.yaml          # OpenAPI 3.1 for the HTTP surface
│   ├── agent-tools.md         # The seven capabilities: schemas + permission classes
│   ├── run-events.md          # SSE envelope and event catalogue
│   ├── fixture-config.schema.json
│   └── eval-task.schema.json
├── checklists/
│   └── requirements.md        # Spec quality checklist
└── tasks.md                   # Phase 2 output (/speckit-tasks — NOT created by /speckit-plan)
```

### Source Code (repository root)

```text
src/
├── RepoPilot.Domain/              # Entities, value objects, RunStateMachine, PathGuard,
│                                  # DiffHash, permission classes. No I/O, no provider types.
├── RepoPilot.Application/         # Use cases (RegisterRepository, IndexRepository, StartRun,
│                                  # DecideProposal, CancelRun), capability interfaces,
│                                  # RunOrchestrator, RunQueue, port interfaces.
├── RepoPilot.Infrastructure/      # EF Core + pgvector persistence, chunker, hybrid retriever,
│                                  # working-copy manager, Docker sandbox runner,
│                                  # provider adapters, OpenTelemetry wiring.
├── RepoPilot.Agent/               # Prompt assembly, one-turn provider call, ToolInvoker,
│                                  # tool schema registration. Not the loop — that is
│                                  # RunOrchestrator's, in Application.
├── RepoPilot.Api/                 # Minimal API endpoints, SSE stream, DI composition root,
│                                  # startup recovery: fail non-terminal runs, sweep orphaned
│                                  # working copies and containers (FR-030a).
└── RepoPilot.Evals/               # Console CLI: runs the committed task set, computes metrics,
                                   # writes results to evals/results/ and to the database.

web/
└── repopilot-ui/
    ├── src/
    │   ├── components/            # DiffViewer, ApprovalBar, RunTimeline, TestOutput
    │   ├── pages/                 # RepositoryList, RunList, RunDetail
    │   ├── hooks/                 # useRunStream (SSE), useRun, useRepositories
    │   └── api/                   # Generated/typed client for rest-api.yaml
    └── tests/

sandbox/
├── Dockerfile.dotnet              # Pre-baked fixture runtime images with dependencies
├── Dockerfile.node                # restored at build time (tests run with no network)
└── Dockerfile.python

evals/
├── fixtures/                      # Committed repository fixtures (the only indexable roots)
├── tasks/                         # One JSON file per evaluation task (eval-task.schema.json)
│   └── _reference/                # Reference patches — outside every fixture root by design
└── results/                       # Committed measured outputs

tests/
├── unit/                          # RepoPilot.Domain.Tests, RepoPilot.Application.Tests
├── integration/                   # Postgres/pgvector, sandbox runner, retrieval filtering
└── e2e/                           # Seeded task: create → approve → apply → test → result

deployments/
docker-compose.yml
```

**Structure Decision**: Web application with a separate evaluation CLI, matching the layout already
committed in `README.md`. `src/RepoPilot.Evals/` is the one addition to that layout and is justified
in Complexity Tracking below. The Domain layer holds the three things the constitution treats as
non-negotiable — the state machine, the path guard, and the diff hash — precisely so they are unit
testable without a database, a container, or a model provider.

## Complexity Tracking

> **Fill ONLY if Constitution Check has violations that must be justified**

No gate failed. Two additions are recorded here because they expand the shape given in `README.md`
and Principle III requires added complexity to be justified rather than assumed.

| Violation | Why Needed | Simpler Alternative Rejected Because |
|-----------|------------|-------------------------------------|
| Sixth source project `RepoPilot.Evals` (README lists five) | FR-031–FR-035 and SC-005–SC-007, SC-011 require a reproducible harness that runs the committed task set headlessly and writes metrics. Principle V makes this a release gate, not an optional extra. | Folding evaluation into `RepoPilot.Api` was rejected: the harness must run in CI without an HTTP host or a browser, and mixing an operator-triggered batch job into the request-serving process makes the concurrency limit and the OTel run traces harder to reason about. A `POST /api/evaluations` endpoint still exists and delegates to the same library. |
| Two-segment run execution — a run releases its concurrency slot at `awaiting approval` and re-acquires one at `applying` | FR-013b requires that an unanswered approval cannot block other runs. A single long-lived worker holding a slot across an indefinite human wait would deadlock the system at the configured limit. | Holding the slot for the whole run was rejected for that deadlock. Unbounded concurrency (no limit at all) was rejected because SC-003's latency target then has no defined load condition and the Docker host has finite memory. |

## Phase Artifacts

- **Phase 0** — [research.md](./research.md): 16 decisions with rationale and rejected alternatives.
  All Technical Context unknowns are resolved; no `NEEDS CLARIFICATION` markers remain.
- **Phase 1** — [data-model.md](./data-model.md), [contracts/](./contracts/),
  [quickstart.md](./quickstart.md).
- **Phase 2** — `tasks.md`, produced by `/speckit-tasks`. Not created by this command.
