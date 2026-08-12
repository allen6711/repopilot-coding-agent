---

description: "Task list for Governed Agent Run (MVP End-to-End Flow)"
---

# Tasks: Governed Agent Run (MVP End-to-End Flow)

**Input**: Design documents from `/specs/001-governed-agent-run/`

**Prerequisites**: [plan.md](./plan.md), [spec.md](./spec.md), [research.md](./research.md),
[data-model.md](./data-model.md), [contracts/](./contracts/), [quickstart.md](./quickstart.md)

**Tests**: **Required, not optional.** The constitution's Development Workflow section names eleven
areas that MUST have automated tests before the related feature is complete, requires integration
coverage of PostgreSQL/pgvector and the sandbox runner, and requires at least one end-to-end test
from run creation through approval, patch application, test execution, and final result. Test tasks
below are governance requirements, not a stylistic choice.

**Organization**: Tasks are grouped by user story so each can be implemented and tested
independently.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Can run in parallel (different files, no dependency on incomplete work)
- **[Story]**: Which user story the task belongs to (US1–US4)
- Exact file paths are included in every task
- **Suffixed IDs** (T011a, T052a, …) were added by the `/speckit-analyze` remediation on 2026-08-10.
  They sit in execution order at the point of the suffix and never renumber an existing task, so
  every earlier reference stays valid — the same convention the spec uses for requirement IDs.

## Path Conventions

Web application layout from plan.md: `src/RepoPilot.*/` for backend and agent, `web/repopilot-ui/`
for the frontend, `tests/{unit,integration,e2e}/` for tests, `evals/` for fixtures and the committed
task set, `sandbox/` for pre-baked execution images.

---

## Phase 1: Setup (Shared Infrastructure)

**Purpose**: Project initialization and the scaffolding every later phase depends on.

- [X] T001 Create the solution and six projects (`RepoPilot.Domain`, `RepoPilot.Application`, `RepoPilot.Infrastructure`, `RepoPilot.Agent`, `RepoPilot.Api`, `RepoPilot.Evals`) with project references enforcing the layering in `RepoPilot.sln` and `src/`
- [X] T002 [P] Add package references — Agent Framework, Anthropic SDK, EF Core + Npgsql + pgvector, DiffPlex, Docker.DotNet, OpenTelemetry — to the relevant `src/RepoPilot.*/*.csproj`
- [X] T003 [P] Scaffold the React + TypeScript + Vite app in `web/repopilot-ui/` with TanStack Query and Vitest configured
- [X] T004 [P] Create `docker-compose.yml` with PostgreSQL 17 + pgvector and an OpenTelemetry collector
- [X] T005 [P] Create test projects `tests/unit/`, `tests/integration/` (with Testcontainers), and `tests/e2e/`
- [X] T006 [P] Configure formatting and linting in `.editorconfig`, `web/repopilot-ui/eslint.config.js`, and `web/repopilot-ui/.prettierrc`
- [X] T007 [P] Add the CI workflow in `.github/workflows/ci.yml` that runs unit, integration, and e2e tests and blocks merge on failure
- [X] T007a [P] Add `.github/CODEOWNERS` requiring review on `evals/fixtures/**/repopilot.fixture.json` and `evals/tasks/**`, so a fixture's command allow-list cannot change without the same review as source code (FR-022b)
- [X] T008 [P] Create pre-baked sandbox images with dependencies restored at build time in `sandbox/Dockerfile.dotnet`, `sandbox/Dockerfile.node`, and `sandbox/Dockerfile.python`
- [X] T009 [P] Define strongly-typed configuration options (workspace root, concurrency limit, context budget, indexing limits, allowed repository slugs) in `src/RepoPilot.Application/Configuration/`
- [X] T010 [P] Create the evaluation directory layout `evals/fixtures/`, `evals/tasks/`, `evals/tasks/_reference/`, and `evals/results/`, with `_reference/` deliberately outside every fixture root (FR-035)
- [X] T011 [P] Add the first repository fixture with its committed `repopilot.fixture.json` under `evals/fixtures/sample-dotnet-api/`
- [X] T011a [P] Add one seeded evaluation task (`evals/tasks/bugfix-null-guard-01.json`) against that fixture, so User Story 1's end-to-end test and the `quickstart.md` walkthrough can run before User Story 4 authors the full set. Without this, US1 is not independently testable despite claiming to be
- [X] T012 [P] Add the fixture and evaluation-task JSON schemas to `src/RepoPilot.Application/Schemas/` and wire schema validation helpers

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: The governance primitives, persistence, retrieval pipeline, and cross-cutting
infrastructure that every user story depends on. The indexing pipeline lives here because User
Story 1's independent test requires a pre-indexed fixture.

**⚠️ CRITICAL**: No user story work can begin until this phase is complete.

### Domain primitives

- [X] T013 [P] Define `RunStage`, `RunTrigger`, `TerminalOutcome`, and the normative `OutcomeReason` set (FR-008c) in `src/RepoPilot.Domain/Runs/`
- [X] T014 [P] Implement `RunStateMachine` as a static transition table with `TryTransition` and `IllegalTransitionException` in `src/RepoPilot.Domain/Runs/RunStateMachine.cs`
- [X] T015 [P] Unit test every (stage, trigger) pair exhaustively, asserting legal transitions succeed and illegal ones throw, in `tests/unit/Domain/RunStateMachineTests.cs` (FR-009)
- [X] T016 [P] Implement `PathGuard.Resolve(root, candidate)` with canonicalization and symlink-escape rejection in `src/RepoPilot.Domain/Workspace/PathGuard.cs` (FR-024, FR-024b)
- [X] T017 [P] Unit test `PathGuard` for `..` traversal, absolute paths, URL-encoded traversal, and symlinks present in the fixture, in `tests/unit/Domain/PathGuardTests.cs` (SC-010)
- [X] T017a [P] Extend `PathGuard` with a read-only root concept and register the fixture root as read-only, so a write resolution against it is refused at the guard rather than only being avoided by convention, in `src/RepoPilot.Domain/Workspace/PathGuard.cs` (FR-016a)
- [X] T017b [P] Unit test that every write-intent resolution against the fixture root is refused on all code paths, in `tests/unit/Domain/FixtureIsReadOnlyTests.cs` (FR-016a)
- [X] T018 [P] Implement `DiffHash` canonical serialization (entries sorted by path, `path\noperation\nsha256(content)`, joined and hashed) in `src/RepoPilot.Domain/Proposals/DiffHash.cs` (FR-019a)
- [X] T019 [P] Unit test `DiffHash` for entry-order independence, content sensitivity, and stability across diff-rendering changes, in `tests/unit/Domain/DiffHashTests.cs` (FR-020a)
- [X] T020 [P] Define the four permission classes and the `CapabilityDescriptor` that binds a capability to exactly one class in `src/RepoPilot.Domain/Capabilities/` (FR-026c)
- [X] T021 [P] Implement the shared `SecretRedactor` predicate covering credential/key files, environment files, and known credential content formats in `src/RepoPilot.Domain/Security/SecretRedactor.cs` (FR-025a)
- [X] T022 [P] Unit test `SecretRedactor` against known token shapes and high-entropy strings in `tests/unit/Domain/SecretRedactorTests.cs`
- [X] T023 [P] Implement `IndexingExclusionPolicy` returning one of the five normative reasons in `src/RepoPilot.Domain/Indexing/IndexingExclusionPolicy.cs` (FR-002, FR-003b)
- [X] T024 [P] Unit test the exclusion policy, including that a fixture override may narrow but never widen indexing, in `tests/unit/Domain/IndexingExclusionPolicyTests.cs` (FR-003b)

### Persistence

- [X] T025 Implement `RepoPilotDbContext` with entity configurations for all ten entities from data-model.md in `src/RepoPilot.Infrastructure/Persistence/`
- [X] T026 Create the initial migration enabling pgvector, creating the PostgreSQL enums, HNSW/GIN/trigram indexes, and the UNIQUE constraints on `approval_decisions.proposal_id` and `(run_id, sequence)` in `src/RepoPilot.Infrastructure/Migrations/`
- [X] T027 Integration test that the database itself refuses a second decision on a decided proposal and refuses a decision with a null actor or hash, in `tests/integration/Persistence/ApprovalConstraintTests.cs` (FR-018, SC-015)
- [X] T027a Configure decision records as append-only — no update or delete path in the model configuration, enforced by database grants — and integration test that an update or delete attempt fails and that records outlive their run, in `src/RepoPilot.Infrastructure/Persistence/AuditConfiguration.cs` and `tests/integration/Persistence/AuditImmutabilityTests.cs` (FR-019b)
- [X] T028 [P] Define repository port interfaces in `src/RepoPilot.Application/Ports/` and their EF Core implementations in `src/RepoPilot.Infrastructure/Persistence/Repositories/`

### Provider adapters

- [X] T029 [P] Define `IChatProviderAdapter` in `src/RepoPilot.Application/Ports/IChatProviderAdapter.cs` and implement it over the Anthropic SDK with `claude-opus-5`, adaptive thinking, and per-stage effort in `src/RepoPilot.Infrastructure/Providers/AnthropicChatAdapter.cs`
- [X] T030 [P] Define `IEmbeddingProviderAdapter` and implement the local ONNX generator, pinning model id and dimensions per repository, in `src/RepoPilot.Infrastructure/Providers/OnnxEmbeddingAdapter.cs`
- [X] T031 [P] Architecture test asserting no provider or SDK type is referenced from `RepoPilot.Application` or `RepoPilot.Domain`, in `tests/unit/Architecture/LayeringTests.cs`
- [X] T031a [P] Capture token and cost metadata from provider responses in the adapter and attach it to the run and stage spans and to a cost metric, in `src/RepoPilot.Infrastructure/Providers/AnthropicChatAdapter.cs` and `src/RepoPilot.Infrastructure/Observability/` — the constitution requires traces to cover token and cost metadata where the provider exposes it, and T043 alone does not name it (Principle IV)

### Retrieval pipeline

- [ ] T032 [P] Implement `LineWindowChunker` (~60 lines, 15 overlap, never crossing files) in `src/RepoPilot.Infrastructure/Indexing/LineWindowChunker.cs`
- [ ] T033 [P] Unit test chunk boundaries and that reported start/end lines match the source exactly, in `tests/unit/Infrastructure/LineWindowChunkerTests.cs` (FR-004)
- [ ] T034 Implement `IndexingService` — walk, exclude with reasons, chunk, embed, write at `index_version = active + 1` — in `src/RepoPilot.Infrastructure/Indexing/IndexingService.cs`
- [ ] T035 Implement the atomic index swap as a single transaction flipping `active_index_version` and deleting the prior version, in `src/RepoPilot.Infrastructure/Indexing/IndexSwap.cs` (FR-003a)
- [ ] T036 Integration test against pgvector that excluded content is never indexed, a rebuild leaves no duplicates, and searches during a rebuild still serve the previous index, in `tests/integration/Indexing/IndexRebuildTests.cs`
- [ ] T037 Implement `HybridRetriever` fusing pgvector cosine with tsvector and trigram results via RRF (k=60) in `src/RepoPilot.Infrastructure/Retrieval/HybridRetriever.cs` (FR-005)
- [ ] T038 Integration test that an exact identifier query returns its defining file with a correct line range and score, in `tests/integration/Retrieval/HybridRetrieverTests.cs`

### Cross-cutting infrastructure

- [ ] T039 [P] Implement `ToolInvoker` enforcing permission class, path guard, size and context budgets, and writing the audit record in a `finally` block, in `src/RepoPilot.Agent/Invocation/ToolInvoker.cs` (FR-027)
- [ ] T040 [P] Unit test that a throwing capability still produces a `failed` audit record and that exceeding the context budget refuses rather than truncates, in `tests/unit/Agent/ToolInvokerTests.cs` (FR-006)
- [ ] T040a [P] Unit test the per-file size limit at both enforcement points — indexing exclusion and bounded file read — in `tests/unit/Agent/FileSizeLimitTests.cs`, closing the constitution's "file-size and context-size limits" test area (only the context half was covered by T040)
- [ ] T041 [P] Implement `RunEventStore` with monotonic per-run sequence and strict persist-then-publish ordering in `src/RepoPilot.Infrastructure/Events/RunEventStore.cs`
- [ ] T042 [P] Integration test that sequences are gap-free and ordered under concurrent writes, in `tests/integration/Events/RunEventSequenceTests.cs`
- [ ] T043 [P] Wire OpenTelemetry — `ActivitySource("RepoPilot")`, run/stage/capability spans, metrics, OTLP exporter — in `src/RepoPilot.Infrastructure/Observability/`
- [ ] T044 [P] Create the API host with Minimal APIs, RFC 9457 problem details, and `X-Actor` binding that refuses an absent or empty value, in `src/RepoPilot.Api/Program.cs` and `src/RepoPilot.Api/Middleware/` (FR-015a)
- [ ] T045 [P] Implement the startup recovery hosted service — fail non-terminal runs with `service_restarted`, destroy their working copies, remove orphaned containers — in `src/RepoPilot.Api/Hosting/StartupRecoveryService.cs` (FR-030a, FR-026e)
- [ ] T046 Integration test startup recovery leaves no non-terminal run, no working copy, and no container behind, in `tests/integration/Hosting/StartupRecoveryTests.cs` (SC-012)
- [ ] T047 [P] Create the agent host — single Agent Framework agent, prompt assembly, read-capability registration — in `src/RepoPilot.Agent/RepoPilotAgent.cs`
- [ ] T048 [P] Add a developer seed command that registers and indexes a fixture so User Story 1 can run against a pre-indexed repository, in `src/RepoPilot.Api/Seed/SeedCommand.cs`

**Checkpoint**: Governance primitives, persistence, retrieval, and audit infrastructure exist and are
tested. User story work can begin.

---

## Phase 3: User Story 1 - Approve a proposed change and see it tested (Priority: P1) 🎯 MVP

**Goal**: A reviewer starts a run against a pre-indexed fixture, receives a plan and a reviewable
diff with nothing yet written, approves it, and sees the change applied to a disposable working copy
and tested in isolation.

**Independent Test**: With one fixture pre-indexed (T048), start a run, receive a diff, confirm the
fixture and working copy are unchanged, approve, and observe test results — end to end, with no
other story implemented.

### Read and propose capabilities

- [ ] T049 [P] [US1] Implement the `list_files` capability with its read permission class in `src/RepoPilot.Agent/Capabilities/ListFilesCapability.cs`
- [ ] T050 [P] [US1] Implement the `read_file` capability applying the exclusion predicate and size limit in `src/RepoPilot.Agent/Capabilities/ReadFileCapability.cs`
- [ ] T051 [P] [US1] Implement the `search_code` capability over the hybrid retriever in `src/RepoPilot.Agent/Capabilities/SearchCodeCapability.cs`
- [ ] T052 [P] [US1] Implement the `search_docs` capability restricted to documentation entries in `src/RepoPilot.Agent/Capabilities/SearchDocsCapability.cs`
- [ ] T052a [US1] Implement plan production — the agent emits a short human-readable plan, persisted and published as a `plan_produced` event, before any proposal can be created — in `src/RepoPilot.Agent/PlanStage.cs` and `src/RepoPilot.Application/Runs/RunOrchestrator.cs` (FR-010)
- [ ] T053 [US1] Implement the `propose_patch` capability with no filesystem writer injected, in `src/RepoPilot.Agent/Capabilities/ProposePatchCapability.cs` (FR-014)
- [ ] T054 [US1] Unit test that the working copy is byte-identical before and after `propose_patch`, in `tests/unit/Agent/ProposePatchWritesNothingTests.cs` (Principle I)
- [ ] T055 [US1] Implement proposal validation — size caps, binary rejection, empty-proposal rejection, path guard on every entry — in `src/RepoPilot.Application/Proposals/ProposalValidator.cs` (FR-011a, FR-008b)
- [ ] T056 [US1] Unit test that a proposal exceeding any configured cap is refused at creation, in `tests/unit/Application/ProposalCapTests.cs` (SC-004)
- [ ] T057 [US1] Implement unified-diff rendering with DiffPlex and the affected-path list in `src/RepoPilot.Application/Proposals/DiffRenderer.cs` (FR-011)

### Working copy and approval gate

- [ ] T058 [US1] Implement `WorkingCopyManager` — create on entering `retrieving`, stage-and-commit atomic apply, destroy on any terminal outcome — in `src/RepoPilot.Infrastructure/Workspace/WorkingCopyManager.cs` (FR-024a, FR-016b, FR-026a)
- [ ] T059 [US1] Integration test that an interruption mid-apply leaves the working copy in its pre-apply state, in `tests/integration/Workspace/AtomicApplyTests.cs` (FR-016b)
- [ ] T060 [US1] Implement the `apply_patch` capability enforcing, in order, approval exists → hash matches → paths inside workspace → stage is `applying`, in `src/RepoPilot.Agent/Capabilities/ApplyPatchCapability.cs` (FR-014, FR-020, FR-024)
- [ ] T061 [US1] Integration test that apply without an approval record is refused and no file handle is opened, in `tests/integration/Approval/ApprovalRequiredTests.cs` (Principle I)
- [ ] T062 [US1] Integration test that a rejected proposal never applies a patch and leaves the workspace unchanged, in `tests/integration/Approval/RejectionAppliesNothingTests.cs` (FR-017)
- [ ] T063 [US1] Integration test that a hash mismatch between approval and stored proposal refuses the apply, in `tests/integration/Approval/DiffHashMismatchTests.cs` (FR-020a)
- [ ] T063a [US1] Add a per-run assertion that nothing outside the run's working copy changed — snapshot the fixture directory and the workspace root before and after each run — usable during MVP validation rather than only across the evaluation set, in `tests/integration/Security/PerRunNoOutsideWritesTests.cs` (SC-002)
- [ ] T064 [US1] Implement `DecideProposalUseCase` recording the decision with actor, timestamp, run, hash, and mode, refusing an already-decided proposal, in `src/RepoPilot.Application/UseCases/DecideProposalUseCase.cs` (FR-018, FR-019)

### Sandboxed execution

- [ ] T065 [US1] Implement `DockerSandboxRunner` with no network, read-only root, tmpfs `/tmp`, non-root user, all capabilities dropped, no-new-privileges, memory/CPU/PID limits, and no inherited environment, in `src/RepoPilot.Infrastructure/Sandbox/DockerSandboxRunner.cs` (FR-021, FR-021a, FR-021b)
- [ ] T066 [US1] Integration test asserting each isolation control is actually in effect inside the container, in `tests/integration/Sandbox/IsolationTests.cs`
- [ ] T067 [US1] Implement timeout enforcement by container kill, plus the non-terminable path failing the run, in `src/RepoPilot.Infrastructure/Sandbox/SandboxTimeout.cs` (FR-023, FR-023b)
- [ ] T068 [US1] Integration test timeout behavior, reporting clean finishes and forced terminations separately, in `tests/integration/Sandbox/TimeoutTests.cs` (SC-009)
- [ ] T069 [US1] Implement the `run_tests` capability resolving `command_name` against the fixture allow-list before any container is created, in `src/RepoPilot.Agent/Capabilities/RunTestsCapability.cs` (FR-022)
- [ ] T070 [US1] Integration test that a command absent from the allow-list is refused before container creation, and that argument vectors reject shell operators, in `tests/integration/Sandbox/AllowedCommandTests.cs` (FR-022a)
- [ ] T071 [US1] Apply secret redaction to sandbox output before storage, display, and re-entry into model context, in `src/RepoPilot.Infrastructure/Sandbox/OutputRedaction.cs` (FR-025b)

### Orchestration

- [ ] T072 [US1] Implement `RunOrchestrator` as resumable segments (retrieve → plan → propose, then apply → test) driving every stage transition through `RunStateMachine`, in `src/RepoPilot.Application/Runs/RunOrchestrator.cs` (FR-008, Principle IV)
- [ ] T073 [US1] Implement `RunQueue` with a bounded channel and concurrency limiter that releases the slot on entering `awaiting approval` and re-acquires it at `applying`, in `src/RepoPilot.Application/Runs/RunQueue.cs` (FR-013a, FR-013b)
- [ ] T074 [US1] Integration test that six runs at a limit of four queue rather than fail, and that a run awaiting approval holds no slot, in `tests/integration/Runs/ConcurrencyTests.cs`
- [ ] T074a [US1] Measure start-of-run to first `proposal_created` for a single seeded task at or below the concurrency limit and assert it stays under three minutes, in `tests/integration/Runs/SingleRunLatencyTests.cs` (SC-003)
- [ ] T075 [US1] Implement the revision loop — at most two attempts, each requiring its own approval — in `src/RepoPilot.Application/Runs/RevisionPolicy.cs` (FR-012, FR-013)
- [ ] T076 [US1] Integration test that the third failure ends the run as failed and that no revision applies without its own approval, in `tests/integration/Runs/RevisionLimitTests.cs`
- [ ] T077 [US1] Implement the `no change` terminal outcome for both the deliberate no-op and insufficient-context reasons, ensuring no empty proposal is ever offered, in `src/RepoPilot.Application/Runs/NoChangeOutcome.cs` (FR-008b)
- [ ] T078 [US1] Implement `CancelRunUseCase` requiring an actor and ending the run as cancelled from any non-terminal stage, in `src/RepoPilot.Application/UseCases/CancelRunUseCase.cs` (FR-008a)

### API endpoints

- [ ] T079 [US1] Implement `POST /api/runs` accepting a free-text or seeded task and queueing the run, in `src/RepoPilot.Api/Endpoints/RunEndpoints.cs` (FR-007)
- [ ] T080 [US1] Implement `GET /api/runs/{id}`, `/proposal`, `/diff`, and `/tests` in `src/RepoPilot.Api/Endpoints/RunEndpoints.cs` (FR-028)
- [ ] T081 [US1] Implement `POST /api/runs/{id}/approval` requiring the echoed hash and returning 409 on a decided proposal, 422 on mismatch or missing actor, in `src/RepoPilot.Api/Endpoints/ApprovalEndpoints.cs` (FR-020a, FR-015a)
- [ ] T082 [US1] Implement `POST /api/runs/{id}/cancel` in `src/RepoPilot.Api/Endpoints/RunEndpoints.cs` (FR-008a)
- [ ] T083 [US1] Contract test the run and approval endpoints against `contracts/rest-api.yaml`, in `tests/integration/Contracts/RunApiContractTests.cs`

### Review UI

- [ ] T084 [P] [US1] Generate the typed API client from `contracts/rest-api.yaml` into `web/repopilot-ui/src/api/`
- [ ] T085 [P] [US1] Build the run detail page in `web/repopilot-ui/src/pages/RunDetail.tsx`, composing the plan panel, diff viewer, approval bar, and test output, and loading run state from `GET /api/runs/{id}` and `/proposal`
- [ ] T086 [P] [US1] Build the `DiffViewer` component showing every affected file and the full diff in `web/repopilot-ui/src/components/DiffViewer.tsx` (SC-004)
- [ ] T086a [P] [US1] Build the `PlanPanel` component displaying the run's short plan above the diff in `web/repopilot-ui/src/components/PlanPanel.tsx` (FR-010)
- [ ] T087 [US1] Build the `ApprovalBar` component echoing the proposal hash on approve or reject in `web/repopilot-ui/src/components/ApprovalBar.tsx`
- [ ] T088 [P] [US1] Build the `TestOutput` component in `web/repopilot-ui/src/components/TestOutput.tsx`

### End-to-end

- [ ] T089 [US1] End-to-end test driving a seeded task from creation through approval, apply, test execution, and final result, in `tests/e2e/SeededTaskFlowTests.cs`
- [ ] T090 [US1] End-to-end test of the rejection path asserting the fixture and workspace are unchanged and the decision is recorded, in `tests/e2e/RejectionFlowTests.cs`
- [ ] T090a [US1] Test that a `plan_produced` event always precedes `proposal_created` for a run, and that a proposal created without a preceding plan is refused, in `tests/integration/Runs/PlanPrecedesProposalTests.cs` (FR-010)

**Checkpoint**: User Story 1 is fully functional and independently testable. This is the MVP.

---

## Phase 4: User Story 2 - Register and index a repository fixture (Priority: P2)

**Goal**: An operator registers a fixture from the allowed set, triggers indexing, and sees what was
included, what was excluded and why, and can search the result.

**Independent Test**: Register a fixture, index it, and issue a search returning file paths and line
ranges — with no agent run involved.

- [ ] T091 [P] [US2] Implement `POST /api/repositories` refusing any slug outside the configured allowed set, in `src/RepoPilot.Api/Endpoints/RepositoryEndpoints.cs` (FR-001)
- [ ] T092 [P] [US2] Implement `GET /api/repositories` and `GET /api/repositories/{id}` in `src/RepoPilot.Api/Endpoints/RepositoryEndpoints.cs`
- [ ] T093 [US2] Implement `POST /api/repositories/{id}/index` returning 202, and 409 when a rebuild is already running, in `src/RepoPilot.Api/Endpoints/RepositoryEndpoints.cs`
- [ ] T094 [US2] Implement exclusion-count reporting broken down by reason in `src/RepoPilot.Application/UseCases/IndexRepositoryUseCase.cs` (FR-003, FR-003b)
- [ ] T095 [US2] Integration test that included and excluded counts and the reason breakdown are accurate for a fixture seeded with each exclusion category, in `tests/integration/Indexing/ExclusionReportingTests.cs`
- [ ] T096 [US2] Implement `GET /api/repositories/{id}/search` returning path, content, start line, end line, and score, in `src/RepoPilot.Api/Endpoints/RepositoryEndpoints.cs` (FR-004)
- [ ] T097 [US2] Validate `repopilot.fixture.json` against its schema at registration, rejecting shell operators in any `argv` entry, in `src/RepoPilot.Application/UseCases/RegisterRepositoryUseCase.cs` (FR-022a, FR-022b)
- [ ] T098 [US2] Integration test that an empty fixture registers successfully, reports zero indexed files, and causes run creation to be refused, in `tests/integration/Indexing/EmptyFixtureTests.cs`
- [ ] T099 [P] [US2] Contract test the repository endpoints against `contracts/rest-api.yaml`, in `tests/integration/Contracts/RepositoryApiContractTests.cs`
- [ ] T100 [P] [US2] Build the repository list page with register, index trigger, and status display in `web/repopilot-ui/src/pages/RepositoryList.tsx`
- [ ] T101 [P] [US2] Build the search panel showing path, line range, and score in `web/repopilot-ui/src/components/SearchPanel.tsx`
- [ ] T102 [US2] Acceptance test through the API that re-indexing serves the previous index until the swap completes and leaves no duplicates, in `tests/integration/Indexing/AtomicSwapAcceptanceTests.cs` (FR-003a) — the swap mechanism itself is covered by T036; this asserts only the operator-visible behavior

**Checkpoint**: Stories 1 and 2 both work independently.

---

## Phase 5: User Story 3 - Follow a run as it happens (Priority: P2)

**Goal**: A reviewer watching a run sees the current stage, which files the agent looked at, and
which actions succeeded or failed, as they happen.

**Independent Test**: Start a run and observe stage transitions and action entries appearing in order
before the run completes.

- [ ] T103 [US3] Implement `GET /api/runs/{id}/events` as SSE with the event sequence as the frame id and a 15-second keepalive, in `src/RepoPilot.Api/Endpoints/RunEventEndpoints.cs` (FR-028a)
- [ ] T104 [US3] Implement `Last-Event-ID` replay from persisted events before attaching to the live channel, in `src/RepoPilot.Api/Endpoints/RunEventEndpoints.cs`
- [ ] T105 [US3] Integration test that a dropped and reconnected stream produces no gap and no duplicate, in `tests/integration/Events/ReconnectReplayTests.cs`
- [ ] T106 [US3] Apply the redaction predicate to recorded action argument summaries, never storing full file contents, in `src/RepoPilot.Agent/Invocation/ArgumentSummarizer.cs` (FR-027a)
- [ ] T107 [US3] Integration test that no secret value appears in any recorded argument summary across a full run, in `tests/integration/Events/ArgumentRedactionTests.cs`
- [ ] T108 [US3] Record `stage_transition_rejected` events for illegal transitions rather than continuing, in `src/RepoPilot.Application/Runs/RunOrchestrator.cs` (FR-009)
- [ ] T109 [US3] Surface the failure reason and the stage it occurred at on every non-success outcome, in `src/RepoPilot.Application/Runs/RunOrchestrator.cs` (FR-030)
- [ ] T110 [P] [US3] Implement the `useRunStream` SSE hook with reconnect and `Last-Event-ID` handling in `web/repopilot-ui/src/hooks/useRunStream.ts`
- [ ] T111 [P] [US3] Build the `RunTimeline` component listing every action with name, timing, and status in `web/repopilot-ui/src/components/RunTimeline.tsx` (FR-027)
- [ ] T112 [US3] Integration test measuring that 95% of stage transitions and recorded actions reach a subscriber within 2 seconds, in `tests/integration/Events/EventLatencyTests.cs` (SC-013)
- [ ] T113 [US3] Reconstructability test rebuilding a completed run's stage sequence, action history, diff, and test output from stored data alone, with the service stopped and the working copy deleted, in `tests/integration/Events/ReconstructabilityTests.cs` (FR-029, SC-008)
- [ ] T114 [US3] Implement the `Accept: application/json` variant of the events endpoint returning the persisted list, in `src/RepoPilot.Api/Endpoints/RunEventEndpoints.cs`
- [ ] T114a [P] [US3] Contract test the events endpoint — SSE frame shape, `id` as sequence, and the JSON variant — against `contracts/rest-api.yaml` and `contracts/run-events.md`, in `tests/integration/Contracts/RunEventContractTests.cs`

**Checkpoint**: Stories 1, 2, and 3 all work independently.

---

## Phase 6: User Story 4 - Measure the agent against a fixed task set (Priority: P3)

**Goal**: An evaluator runs the committed task set and receives reproducible metrics, including a
retrieval-only baseline compared against the tool-enabled agent.

**Independent Test**: Execute the evaluation set twice on unchanged fixtures and confirm the reported
metrics are consistent and derived only from committed task definitions.

- [ ] T115 [P] [US4] Implement the evaluation task loader with schema validation in `src/RepoPilot.Evals/Tasks/EvaluationTaskLoader.cs` (FR-031)
- [ ] T116 [US4] Author 8 bug-fix tasks under `evals/tasks/`, each with relevant files, baseline and success commands, and a machine-checkable success condition (FR-031, SC-011)
- [ ] T116a [P] [US4] Author 6 input-validation tasks under `evals/tasks/` to the same standard
- [ ] T116b [P] [US4] Author 6 API-behavior-change tasks under `evals/tasks/` to the same standard
- [ ] T116c [P] [US4] Author 5 refactor tasks with unchanged tests under `evals/tasks/` to the same standard
- [ ] T116d [P] [US4] Author 5 test-generation and test-fix tasks under `evals/tasks/` to the same standard
- [ ] T116e [US4] Assert the committed set holds at least 30 tasks and that every success condition is machine-checkable, in `tests/unit/Evals/TaskSetCompletenessTests.cs` (SC-011)
- [ ] T117 [P] [US4] Add reference patches under `evals/tasks/_reference/`, outside every fixture root, and assert in test that no run resolves a path there (FR-035)
- [ ] T118 [US4] Build the evaluation CLI host reusing `RunOrchestrator` rather than a parallel path, in `src/RepoPilot.Evals/Program.cs` (FR-034)
- [ ] T119 [US4] Implement programmatic approval that still writes a real decision record bound to the diff hash and is available only to evaluation runs, in `src/RepoPilot.Application/Approval/ProgrammaticApproval.cs` (FR-015b)
- [ ] T120 [US4] Integration test that programmatic approval is refused on an interactive run, in `tests/integration/Approval/ProgrammaticModeScopeTests.cs`
- [ ] T121 [US4] Implement the retrieval-only baseline mode that disables tool use for the same task set, in `src/RepoPilot.Evals/Modes/BaselineMode.cs` (FR-033)
- [ ] T122 [US4] Implement metric calculation — Recall@5, completion rate per mode, approval coverage, tool success rate, average tool calls, latency percentiles — in `src/RepoPilot.Evals/Metrics/MetricsCalculator.cs` (FR-032)
- [ ] T123 [P] [US4] Unit test each metric calculation against fixed inputs in `tests/unit/Evals/MetricsCalculatorTests.cs`
- [ ] T124 [US4] Flag and fail an evaluation whose approval coverage is below 100% rather than reporting the number, in `src/RepoPilot.Evals/Metrics/ApprovalCoverageGate.cs` (FR-034, SC-001)
- [ ] T125 [US4] Ensure an evaluation never triggers re-indexing, so repeat runs read identical vectors, in `src/RepoPilot.Evals/Program.cs` (SC-007)
- [ ] T126 [US4] Integration test that two consecutive evaluations over unchanged fixtures produce identical retrieval metrics, in `tests/integration/Evals/DeterminismTests.cs`
- [ ] T127 [US4] Implement `POST /api/evaluations` and `GET /api/evaluations/{id}` delegating to the same library, in `src/RepoPilot.Api/Endpoints/EvaluationEndpoints.cs`
- [ ] T127a [P] [US4] Contract test the evaluation endpoints against `contracts/rest-api.yaml`, in `tests/integration/Contracts/EvaluationApiContractTests.cs`
- [ ] T128 [US4] Write the committed JSON report to `evals/results/` in `src/RepoPilot.Evals/Reporting/ReportWriter.cs`
- [ ] T129 [P] [US4] Add an adversarial fixture whose content attempts to instruct the agent to skip approval or read outside the workspace, under `evals/fixtures/adversarial-content/`
- [ ] T130 [US4] Integration test that no control is bypassed by repository content — no unapproved write, no out-of-workspace access, no command outside the allow-list, no secret reaching model context, in `tests/integration/Security/ContentAsInstructionTests.cs` (FR-026d, SC-014)

**Checkpoint**: All four user stories are independently functional.

---

## Phase 7: Polish & Cross-Cutting Concerns

**Purpose**: Release-gate verification and work that spans stories.

- [ ] T131 [P] Extend the per-run check from T063a into a full-evaluation-set sweep, asserting zero modifications outside any run's working copy across every task in the committed set, in `tests/integration/Security/EvaluationSetNoOutsideWritesTests.cs` (SC-002)
- [ ] T132 [P] Implement the SC-010 refusal report counting every refused out-of-workspace attempt across the evaluation set, in `src/RepoPilot.Evals/Metrics/RefusalReport.cs`
- [ ] T133 [P] Add the SC-015 check asserting every decision record carries an actor and a hash, in `tests/integration/Approval/DecisionRecordCompletenessTests.cs`
- [ ] T134 [P] Extend the single-run measurement from T074a to the full committed task set, reporting the distribution rather than a single sample, in `tests/integration/Runs/LatencyBudgetTests.cs` (SC-003)
- [ ] T135 [P] Accessibility pass over the review view (keyboard operation of approve and reject, focus order, contrast) in `web/repopilot-ui/src/components/`
- [ ] T136 [P] Author the capability contract and fixture authoring guide in `docs/`
- [ ] T137 Replace the target values in `README.md` with measured values from the first complete evaluation, keeping unmeasured figures labelled as targets (Principle V)
- [ ] T138 Run the full `quickstart.md` validation end to end and record any deviation

---

## Dependencies & Execution Order

### Phase Dependencies

- **Setup (Phase 1)**: No dependencies — start immediately.
- **Foundational (Phase 2)**: Depends on Setup. **Blocks every user story.**
- **User Story 1 (Phase 3)**: Depends on Foundational. No dependency on other stories.
- **User Story 2 (Phase 4)**: Depends on Foundational. Independent of US1 — it exposes the operator
  surface over the indexing pipeline that Phase 2 already built.
- **User Story 3 (Phase 5)**: Depends on Foundational. Independently testable, though it is most
  useful once US1 produces runs worth watching.
- **User Story 4 (Phase 6)**: Depends on Foundational and on US1, because evaluation drives the same
  orchestrator that US1 builds.
- **US1 does not depend on US4.** T011a seeds one evaluation task in Phase 1 precisely so that US1's
  end-to-end test and the `quickstart.md` walkthrough can run before the full committed set is
  authored in T116–T116d. Without that seed the claimed independence would be false.
- **Polish (Phase 7)**: Depends on the stories whose criteria it verifies.

### Why the indexing pipeline is foundational rather than part of US2

User Story 1's independent test requires "one repository fixture pre-indexed". The chunker, indexer,
atomic swap, and hybrid retriever therefore have to exist before US1 can run at all, so they sit in
Phase 2 with a seed command (T048). User Story 2 adds the operator-facing surface on top:
registration, allowed-set enforcement, exclusion reporting, the search endpoint, and the UI.

### Within Each User Story

- Domain primitives and validators before the capabilities that use them.
- Capabilities before the orchestrator that invokes them.
- Orchestrator before the endpoints that start runs.
- Endpoints before the UI that calls them.
- Governance tests (T054, T061, T062, T063) should be written before or alongside the code they
  constrain — they are the specification of Principle I in executable form.

### Parallel Opportunities

- Phase 1: T002–T012 are all `[P]`, including T007a and T011a.
- Phase 2: the domain tasks (T013–T024, plus T017a and T017b) are all `[P]` and independent of
  persistence; T029–T031a and T039–T045 are `[P]` once persistence exists.
- Phase 3: the four read capabilities (T049–T052) are `[P]`; the UI components (T084–T086a, T088)
  are `[P]` once the typed client exists.
- Phase 4: T091, T092, T099, T100, T101 are `[P]`.
- Phase 5: T110, T111, and T114a are `[P]`.
- Phase 6: T115, T116a–T116d, T117, T123, T127a, T129 are `[P]` — the four task-authoring batches are
  independent of each other and are the largest parallel opportunity in the phase.
- Once Phase 2 completes, US1, US2, and US3 can be staffed in parallel.

---

## Parallel Example: Phase 2 domain primitives

```bash
# All twelve are independent files with no shared state:
Task: "Define RunStage/RunTrigger/TerminalOutcome/OutcomeReason in src/RepoPilot.Domain/Runs/"
Task: "Implement RunStateMachine in src/RepoPilot.Domain/Runs/RunStateMachine.cs"
Task: "Implement PathGuard in src/RepoPilot.Domain/Workspace/PathGuard.cs"
Task: "Implement DiffHash in src/RepoPilot.Domain/Proposals/DiffHash.cs"
Task: "Implement SecretRedactor in src/RepoPilot.Domain/Security/SecretRedactor.cs"
Task: "Implement IndexingExclusionPolicy in src/RepoPilot.Domain/Indexing/IndexingExclusionPolicy.cs"
```

## Parallel Example: User Story 1 read capabilities

```bash
Task: "Implement list_files in src/RepoPilot.Agent/Capabilities/ListFilesCapability.cs"
Task: "Implement read_file in src/RepoPilot.Agent/Capabilities/ReadFileCapability.cs"
Task: "Implement search_code in src/RepoPilot.Agent/Capabilities/SearchCodeCapability.cs"
Task: "Implement search_docs in src/RepoPilot.Agent/Capabilities/SearchDocsCapability.cs"
```

---

## Implementation Strategy

### MVP First (User Story 1 only)

1. Complete Phase 1: Setup.
2. Complete Phase 2: Foundational — this is the largest phase because the governance guarantees live
   here, and it blocks everything.
3. Complete Phase 3: User Story 1.
4. **Stop and validate**: run Scenarios 2, 3, and 4 from `quickstart.md`. The approval gate must be
   unbypassable before anything else is built on top of it.
5. Demo the MVP.

### Incremental Delivery

1. Setup + Foundational → foundation ready.
2. Add User Story 1 → validate → demo (MVP).
3. Add User Story 2 → operators can register and index fixtures themselves.
4. Add User Story 3 → approval becomes meaningful rather than a rubber stamp.
5. Add User Story 4 → capability claims become evidence-backed, and Principle V's release gate can
   actually be evaluated.

### Parallel Team Strategy

After Phase 2 completes:

- Developer A: User Story 1 (the critical path — everything else is either input to it or
  observation of it).
- Developer B: User Story 2.
- Developer C: User Story 3.
- User Story 4 starts once US1's orchestrator is stable, since it reuses that path deliberately.

---

## Notes

- `[P]` means a different file with no dependency on incomplete work.
- The governance tests are not ordinary coverage: T054, T061, T062, T063, T063a, T066, T070, T017b,
  and T130 are the executable form of Principles I and II. A change that makes any of them fail is a
  release blocker, not a bug to triage.
- **Constitution mandatory-test mapping.** The Development Workflow section names eleven areas; each
  maps to a task here so none can be silently skipped: path allow-list → T017; file-size and
  context-size limits → T040, T040a; run-state transitions → T015; approval required before apply →
  T061; rejected approval never applies → T062; tool-call audit records → T040; retrieval filtering →
  T036; **proposal parsing and validation including the change-content hash → T019, T055, T056**;
  allowed test-command enforcement → T070; sandbox timeout behavior → T068; evaluation metric
  calculation → T123. The emphasised area was reworded by constitution amendment 1.0.1 because this
  design carries full replacement content per file rather than model-authored diff hunks, so no diff
  parser exists to test.
- **SC-005 and SC-006 are reported, not gated.** Metrics tasks compute and publish them; no task
  fails a build on the value. The release gate is SC-001 (approval coverage), which T124 enforces.
- Commit after each task or logical group; stop at any checkpoint to validate a story on its own.
- Do not begin stretch-goal work (identity, RBAC, GitHub App, checkpoint/resume, reviewer agent, MCP
  exposure, provider comparison) until the MVP acceptance criteria in `README.md` are met.
