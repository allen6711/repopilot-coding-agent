<!--
Sync Impact Report
- Version change: (uninitialized template) → 1.0.0
- Bump rationale: Initial ratification. Every placeholder replaced with concrete,
  project-specific governance derived from README.md.
- Modified principles:
  - [PRINCIPLE_1_NAME] → I. Human-Approved Writes (NON-NEGOTIABLE)
  - [PRINCIPLE_2_NAME] → II. Sandboxed, Allow-Listed Execution
  - [PRINCIPLE_3_NAME] → III. Minimal Agent, Constrained Tool Surface
  - [PRINCIPLE_4_NAME] → IV. Explicit State, Full Traceability
  - [PRINCIPLE_5_NAME] → V. Evidence Before Claims
- Added sections:
  - [SECTION_2_NAME] → Technology and Architecture Constraints
  - [SECTION_3_NAME] → Development Workflow and Quality Gates
- Removed sections: none
- Deferred TODOs: none
-->

# RepoPilot Constitution

## Core Principles

### I. Human-Approved Writes (NON-NEGOTIABLE)

No repository write operation may occur without an explicit, recorded human approval.
`propose_patch` MUST NOT mutate any file. `apply_patch` MUST reject any invocation whose run has
no stored approval record, and MUST apply changes only to a disposable working copy, never to a
source repository or a shared branch. A rejected approval MUST leave the workspace byte-identical
to its pre-proposal state. Every approval and rejection MUST be audit logged with run ID, actor,
decision, timestamp, and the diff hash the decision applied to.

Rationale: The project's value claim is trustworthiness, not autonomy. An unapproved write, even a
correct one, invalidates the entire governance premise.

### II. Sandboxed, Allow-Listed Execution

All code execution triggered by an agent run MUST happen inside an isolated Docker container with
an enforced timeout and no credential access. Test commands MUST come from repository-specific
configuration, never from model-generated strings. The model MUST NOT be given arbitrary shell
access. File access MUST be confined to the configured workspace root: path traversal outside that
root MUST be rejected before any I/O, and file-size and retrieved-context limits MUST be enforced
at the tool boundary. Repository credentials and secrets MUST NEVER enter model context.

Rationale: An agent that can run arbitrary commands is an unbounded remote-execution surface; the
sandbox and the allow list are what make the tool surface reasonable to reason about.

### III. Minimal Agent, Constrained Tool Surface

The system MUST use the simplest agent pattern that satisfies the task. MVP MUST be one agent with
the defined tool set: `list_files`, `search_code`, `read_file`, `search_docs`, `propose_patch`,
`apply_patch`, `run_tests`. Adding a tool, an agent, or an orchestration layer requires a written
justification citing measured evaluation evidence that the current design fails the task. A
planner/coder/reviewer multi-agent architecture MUST NOT be introduced before MVP acceptance
criteria are met and evaluation data supports it. Each tool MUST declare its permission class
(read, no-direct-write, write-with-approval, sandbox-execution) and MUST be enforced at the call
site, not by prompt instruction.

Rationale: Agent complexity is easy to add and hard to evaluate. Constraining the surface keeps
failures attributable and keeps prompt-level "rules" from substituting for real enforcement.

### IV. Explicit State, Full Traceability

Run lifecycle transitions MUST be implemented explicitly in backend code and MUST NOT be inferred
from natural-language model output. Every state change MUST be persisted and legal per the run
state machine; illegal transitions MUST fail loudly rather than degrade. Every tool call MUST
record tool name, run ID, arguments summary, start/end timing, and status. Runs MUST emit
OpenTelemetry traces covering latency, tool calls, errors, and token/cost metadata where the
provider exposes it. A completed run MUST be reconstructable from its stored events alone.

Rationale: If the state lives only in the model's text, the system cannot be debugged, audited, or
evaluated — and cannot be trusted with a write gate.

### V. Evidence Before Claims

Performance and capability numbers MUST be measured before they are published anywhere, including
README, UI, and project write-ups. Target values MUST be labelled as targets and MUST NOT be
presented as outcomes. Correctness claims about the agent MUST be backed by the committed
evaluation dataset of at least 30 reproducible tasks with defined ground truth. Metrics MUST be
reproducible from committed fixtures and task definitions, and reference patches MUST be kept out
of model context. Write approval coverage MUST measure 100%; any value below 100% is a release
blocker, not a metric to report.

Rationale: An unevaluated agent claim is a guess. The evaluation set is the only thing separating
this project from a demo.

## Technology and Architecture Constraints

- Backend MUST be C# / ASP.NET Core; agent runtime MUST be Microsoft Agent Framework; frontend
  MUST be React with TypeScript.
- PostgreSQL with pgvector is the system of record for repository chunks and run metadata. Redis
  MAY be used only for short-lived run or cache state, and only where a written justification
  exists.
- Model-provider access MUST sit behind an adapter interface. Provider-specific types, prompts,
  and SDK calls MUST NOT leak into Application or Domain layers.
- Retrieval indexing MUST exclude binaries, build artifacts, dependency directories, secrets, and
  files beyond configured size limits. A retrieval result MUST carry repository-relative path,
  chunk identifier, content, start/end line, and score.
- All source code, identifiers, comments, commit messages, documentation, specs, and generated
  artifacts MUST be written in English, regardless of the language used in conversation with
  contributors or agents.

## Development Workflow and Quality Gates

- The following MUST have automated tests before the related feature is considered complete: path
  allow-list validation, file-size and context-size limits, run-state transitions, approval
  required before `apply_patch`, rejected approval never applying a patch, tool-call audit
  records, retrieval filtering, diff parsing and validation, allowed test-command enforcement,
  Docker sandbox timeout behavior, and evaluation metric calculation.
- Integration tests MUST cover PostgreSQL/pgvector and the sandbox runner. At least one end-to-end
  test MUST run a seeded task from creation through approval, patch application, test execution,
  and final result.
- CI MUST run on GitHub Actions and MUST block merge on failing tests.
- Security-relevant changes — tool permissions, path validation, approval flow, sandbox
  configuration, secret handling — MUST be reviewed against Principles I and II explicitly, and
  the review MUST state which tests cover the change.
- Work MUST NOT begin on any stretch goal (Entra ID/OIDC, RBAC, GitHub App, checkpoint/resume,
  reviewer agent, MCP exposure, provider comparison) until the MVP acceptance criteria in README
  are met.
- Explicit non-goals in README MUST be treated as out of scope; proposing work against them
  requires a constitution amendment, not a pull-request argument.

## Governance

This constitution supersedes other practices, conventions, and preferences in this repository.
Where a template, prompt, or command conflicts with it, this document wins and the conflicting
artifact MUST be corrected.

Amendment procedure: an amendment MUST be proposed as a pull request that changes this file,
states the rationale, and lists every artifact requiring follow-up (templates, commands, tests,
README). Amendments touching Principles I, II, or V additionally require a stated migration plan
covering runs and data created under the prior rules. Amendments take effect on merge.

Versioning policy follows semantic versioning of governance impact:

- MAJOR — a principle is removed or redefined in a backward-incompatible way, or a governance rule
  is relaxed.
- MINOR — a principle or section is added, or existing guidance is materially expanded.
- PATCH — clarifications, wording, and non-semantic refinement.

Compliance review: every pull request MUST verify compliance with these principles before merge.
Added complexity MUST be justified in the pull request against Principle III. Any deviation MUST
be recorded in the pull request with an expiry condition — undocumented deviations are defects.
Runtime development guidance for agents lives in the Spec Kit templates under `.specify/`; those
templates read this constitution and MUST NOT contradict it.

**Version**: 1.0.0 | **Ratified**: 2026-08-09 | **Last Amended**: 2026-08-09
