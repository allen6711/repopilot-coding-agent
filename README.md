# RepoPilot — Governed Repository Coding Agent

RepoPilot is a full-stack software-engineering agent that accepts a repository task, retrieves relevant code context, uses constrained repository tools, proposes a patch, runs tests inside an isolated container, and requires human approval before any repository write operation.

The goal is not to build a replacement for a commercial coding assistant. The project focuses on the engineering patterns behind a trustworthy agentic application: retrieval, tool use, controlled workflows, evaluation, observability, and human-in-the-loop governance.

> Status: planned / under development. Evaluation values in this README must be replaced with measured results before they are presented as project outcomes.

## Why this project

RepoPilot is designed to demonstrate:

- a real agent that can use tools instead of only answering with RAG context;
- retrieval over source code and repository documentation;
- explicit read/write permission boundaries;
- human approval before code modification;
- sandboxed test execution;
- measurable agent and retrieval evaluation;
- C#/.NET backend development plus a React UI;
- OpenTelemetry-based observability for agent runs and tool calls.

## Architecture

```text
              React Web UI
                   |
                   v
           ASP.NET Core API
                   |
                   v
        Task / Run Coordinator
                   |
                   v
       Microsoft Agent Framework
          single coding agent
                   |
      +------------+-------------+
      |            |             |
      v            v             v
 search_code    read_file    propose_patch
      |            |             |
      +------------+-------------+
                   |
             approval gate
                   |
                   v
              apply_patch
                   |
                   v
           Docker test sandbox
                   |
                   v
        test result + final diff

PostgreSQL/pgvector: repository chunks and run metadata
Redis: optional short-lived run/cache state
OpenTelemetry: run latency, tool calls, errors, token/cost metadata where available
```

## Agent design principle

Use the simplest agent pattern that satisfies the task.

MVP should use **one agent with a small set of well-defined tools**. Do not introduce a planner/coder/reviewer multi-agent architecture unless the MVP has been completed and a measured evaluation shows a clear reason to add it.

## Planned technology stack

| Area | Technology |
| --- | --- |
| Backend | C#, ASP.NET Core |
| Agent runtime | Microsoft Agent Framework |
| Frontend | React, TypeScript |
| Relational/vector store | PostgreSQL + pgvector |
| Cache/state | Redis (only where justified) |
| Sandbox | Docker |
| Observability | OpenTelemetry |
| CI | GitHub Actions |
| Model provider | Configurable; provider-specific code kept behind an adapter |

## Core user flow

1. User registers/selects a repository fixture.
2. User chooses an issue/task from the included evaluation set or enters a task.
3. RepoPilot indexes relevant source/docs if needed.
4. Agent searches repository context and reads selected files.
5. Agent returns a short implementation plan.
6. Agent proposes a patch but cannot apply it yet.
7. UI shows files/diff and asks for approval.
8. After approval, the patch is applied inside a disposable working copy.
9. Tests run in an isolated Docker sandbox.
10. Agent may inspect test failures and propose at most two follow-up patch attempts.
11. Final UI shows status, diff, tests, tool-call trace, and evaluation metadata.

## Required tools

MVP exposes only these tools to the model:

| Tool | Permission | Purpose |
| --- | --- | --- |
| `list_files` | read | List repository files within allowed root |
| `search_code` | read | Lexical/vector-assisted code search |
| `read_file` | read | Read bounded file content |
| `search_docs` | read | Search README/docs content |
| `propose_patch` | no direct write | Return structured diff proposal |
| `apply_patch` | write, approval required | Apply approved diff to disposable working copy |
| `run_tests` | sandbox execution | Execute allow-listed test command |

The model must not receive arbitrary shell access in MVP.

## Retrieval

Repository indexing should include source files and project documentation, excluding binaries, build artifacts, secrets, dependency directories, and files beyond configured limits.

A retrieval result should contain:

- repository-relative path;
- chunk identifier;
- code/text content;
- start/end line;
- retrieval score;
- optional symbol/language metadata.

Use hybrid retrieval if practical: lexical search for exact identifiers plus vector retrieval for semantic context.

## Governance and safety controls

Required controls:

- all repository write operations require explicit user approval;
- all code execution happens inside an isolated Docker container;
- test commands come from a repository-specific allow list/configuration, not arbitrary model-generated shell commands;
- path traversal outside the repository workspace is rejected;
- repository credentials/secrets are never passed to the model;
- file size and retrieved-context limits are enforced;
- every tool call records tool name, timing, status, and run ID;
- every approved/rejected write action is audit logged.

Optional authentication stretch goal: Microsoft Entra ID / OIDC with Viewer, Developer, and Admin roles.

## API summary

| Method | Endpoint | Purpose |
| --- | --- | --- |
| POST | `/api/repositories` | Register/import an allowed repository fixture |
| POST | `/api/repositories/{id}/index` | Build/update retrieval index |
| POST | `/api/runs` | Create coding-agent run |
| GET | `/api/runs/{id}` | Read current state/result |
| GET | `/api/runs/{id}/events` | Stream run/tool events |
| POST | `/api/runs/{id}/approval` | Approve/reject proposed write |
| GET | `/api/runs/{id}/diff` | Read proposed/final diff |
| GET | `/api/runs/{id}/tests` | Read test results |
| POST | `/api/evaluations` | Run evaluation suite |
| GET | `/api/evaluations/{id}` | Read evaluation metrics |

## Run state machine

```text
CREATED
  -> INDEXING (optional)
  -> RETRIEVING
  -> PLANNING
  -> PROPOSING_PATCH
  -> WAITING_FOR_APPROVAL
       -> REJECTED
       -> APPLYING_PATCH
           -> RUNNING_TESTS
               -> SUCCEEDED
               -> NEEDS_REVISION
                    -> PROPOSING_PATCH (max 2 retries)
               -> FAILED
```

State transitions must be explicit in backend code rather than inferred only from natural-language model output.

## Evaluation plan

Create a committed evaluation dataset of at least **30 reproducible tasks** across one or more small repository fixtures.

Recommended task mix:

- 8 bug fixes;
- 6 input-validation changes;
- 6 small API behavior changes;
- 5 refactors with unchanged tests;
- 5 test-generation/fix tasks.

Each task should define ground truth:

- task description;
- relevant files;
- baseline tests;
- expected tests after fix;
- success command;
- optional reference patch kept outside the model context.

### Metrics

1. **Retrieval Recall@5**  
   Fraction of tasks where at least one ground-truth relevant file appears in the first five retrieved files.

2. **Task completion rate**  
   Fraction of tasks whose final working copy passes the task-specific success tests.

3. **Baseline vs tool-enabled completion**  
   Compare a retrieval-only response baseline with the full tool-enabled agent.

4. **Tool success rate**  
   Successful tool invocations / total tool invocations.

5. **Average tool calls per completed task**.

6. **End-to-end latency**.

7. **Write approval coverage**  
   Must be 100% for repository write operations.

Recommended development targets, not guaranteed outcomes:

- evaluation tasks: 30;
- Retrieval Recall@5: 80–90%;
- retrieval-only completion baseline: approximately 35–50%;
- tool-enabled completion: approximately 60–75%;
- every write operation gated by approval: 100%.

## Testing

Required automated tests:

- path-allow-list validation;
- file-size/context-size limits;
- run-state transitions;
- approval required before `apply_patch`;
- rejected approval never applies a patch;
- tool-call audit records;
- retrieval filtering/exclusions;
- diff parser/validator;
- allowed test-command enforcement;
- Docker sandbox timeout behavior;
- evaluation metric calculations.

Integration tests should cover PostgreSQL/pgvector and the sandbox runner.

An end-to-end test must execute at least one seeded issue from task creation through approval, patch application, test execution, and final result.

## MVP acceptance criteria

The MVP is complete only when:

- a repository fixture can be indexed and searched;
- the agent can call `search_code`, `read_file`, and `search_docs`;
- the agent can produce a structured patch proposal;
- no code change can be applied without a recorded approval;
- approved patches apply only to a disposable workspace;
- tests run inside Docker with a timeout and allow-listed command;
- final diff and test output are visible in the React UI;
- all tool calls are traceable by run ID;
- a 30-task evaluation suite produces Retrieval Recall@5 and task-completion metrics;
- a retrieval-only baseline and tool-enabled result can be compared reproducibly.

## Stretch goals

Only after MVP:

- Microsoft Entra ID / OIDC authentication;
- Viewer / Developer / Admin RBAC;
- GitHub App integration and pull-request creation;
- checkpoint/resume for interrupted runs;
- second reviewer agent, only if evaluation justifies it;
- MCP tool exposure;
- model-provider comparison on the same evaluation set.

## Explicit non-goals

- replacing GitHub Copilot/Claude Code or building a general-purpose IDE;
- unrestricted shell access;
- autonomous merge/deploy to production;
- five-agent orchestration for presentation value;
- training/fine-tuning an LLM;
- supporting every programming language/repository type;
- claiming agent correctness without an evaluation dataset.

## Resume-ready measurements

Do not publish target values as actual outcomes. After evaluation, the project should support evidence such as:

> Built a full-stack repository coding agent in C#/.NET and React that retrieved code context, called constrained repository tools, proposed patches, and executed tests in isolated Docker workspaces with human approval before every write operation.

> Evaluated **[N]** seeded repository tasks, achieving **[measured Recall@5]** relevant-file Recall@5 and improving task completion from **[baseline %]** to **[tool-enabled %]** with tool use and test execution.

## Repository layout

```text
repopilot/
├── src/
│   ├── RepoPilot.Api/
│   ├── RepoPilot.Application/
│   ├── RepoPilot.Domain/
│   ├── RepoPilot.Infrastructure/
│   └── RepoPilot.Agent/
├── web/
│   └── repopilot-ui/
├── sandbox/
├── evals/
│   ├── fixtures/
│   ├── tasks/
│   └── results/
├── tests/
│   ├── unit/
│   ├── integration/
│   └── e2e/
├── deployments/
├── docker-compose.yml
└── README.md
```
