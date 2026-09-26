# Quickstart validation record

A run of [`quickstart.md`](../specs/001-governed-agent-run/quickstart.md) end to end, and what
deviated from it (T138).

**Date**: 2026-08-17
**Environment**: macOS (darwin 24.0.0), .NET 10, Docker 29.3.1, PostgreSQL 17 + pgvector
**Model provider credential**: not available

The absence of a credential is the single most important fact about this record. Every scenario that
needs the agent to produce a proposal is **unverified**, not passed. Nothing below is reported as
having held unless it was observed.

---

## What was run and what happened

| Step | Result |
|---|---|
| `docker compose up -d` | **Deviated** — see D1. Worked after the fix. |
| `dotnet ef database update` | **Deviated** — see D2. Both migrations applied. |
| `docker build -f sandbox/Dockerfile.dotnet` | Passed (image already built and used by the e2e suite) |
| `dotnet run --project src/RepoPilot.Api` | **Deviated** — see D3 and D4 |
| Scenario 1 — register and index | **Passed** after D4 was fixed |
| Scenario 2 — approve a change and see it tested | **Unverified** — needs a provider credential |
| Scenario 3 — rejection writes nothing | **Unverified** — needs a proposal to reject |
| Scenario 4 — the approval gate cannot be bypassed | **Partly verified** — the two rows reachable without a proposal passed; the rest are covered by the integration suite |
| Scenario 5 — failure and revision paths | **One row verified for real** — see below |
| Scenario 6 — concurrency | **Unverified** |
| Scenario 7 — live visibility | **Partly verified** — event ordering and the `run_ended` frame observed |
| Scenario 8 — evaluation | **Started, not completed** — see below |
| Test suite | Passed — 239 unit, 197 integration (1 skipped), 5 e2e, 33 UI |

### Scenario 1, in detail

```
sample-dotnet-api      201  indexed  v1  included=37  excluded=131 (ExcludedDirectory)
sample-dotnet-billing  201  indexed  v1  included=35  excluded=131 (ExcludedDirectory)
adversarial-content    422  not in the deployment's allowed set — correct (FR-001)
```

The 422 is the allow-list working, not a failure. The adversarial fixture is registered directly by
the tests that use it and is deliberately absent from the shipped allowed set.

Exact identifier lookup for `OrderLookupService` returned the defining file at rank 2 with a line
range and a score, behind its own test class — expected for a hybrid retriever, and within the top
five that SC-005 measures.

Excluded content is genuinely absent rather than merely unranked: `select count(*) from
index_entries where "RelativePath" like '%/bin/%' or like '%/obj/%'` returned **0**, and no query
returned a path under either.

Re-indexing produced the same top five in the same order (FR-003a).

### Scenario 5, verified for real

Starting a run without a credential exercised the row *"Model provider unavailable mid-run"*:

```
stage=failed  terminalOutcome=failed  outcomeReason=provider_unavailable  failureStage=retrieving
```

The recorded events were dense and monotonic (`1..4`), the failure reason and the stage it occurred
at were both stored (FR-030), and `run_ended` was emitted last. The fixture directory was untouched
(`git status --porcelain evals/fixtures/` empty) and no working copy survived
(`select count(*) from working_copies where "DestroyedAt" is null` returned 0).

### Scenario 4, what was reachable

| Attempt | Observed |
|---|---|
| `POST /approval` with no `X-Actor` | 422, no decision record written |
| `POST /cancel` with no `X-Actor` | 422, no decision record written |

`select count(*) from approval_decisions` remained 0 throughout. The remaining rows need a proposal,
so they are covered by `ApprovalGateTests`, `ProgrammaticModeScopeTests`,
`DecisionRecordCompletenessTests`, and `ContentAsInstructionTests` rather than by this run.

### Scenario 8, how far it got

`POST /api/evaluations` returned 202 with `taskCount: 30`, so the loader found and schema-validated
the committed set. The harness then began the per-fixture baseline probe, which really does start
`repopilot/fixture-dotnet:1` containers and copy fixtures into `.workspace/baselines/`. Without a
credential every run would fail at `retrieving`, so the evaluation was not run to completion and
**no report was produced**. No figure from Scenario 8 is available.

---

## Deviations

### D1 — `docker compose up` cannot start when the host already uses port 5432

`docker-compose.yml` bound `5432:5432` with no override, so a machine with anything else on that
port gets `bind: address already in use` and the quickstart stops at step 1.

**Fixed.** The mapping is now `${REPOPILOT_POSTGRES_PORT:-5432}:5432`. The default is unchanged; a
developer with a conflict sets the variable.

### D2 — `dotnet ef database update` ignores the obvious connection-string variable

Migrations are applied through `RepoPilotDbContextFactory`, not through the API host, and it read
only `REPOPILOT_CONNECTION_STRING` — a name the quickstart never mentions. Setting
`ConnectionStrings__RepoPilot`, which is what the host reads, had no effect, and the command
silently used the hardcoded default. The symptom is `28P01: password authentication failed`, which
sends you looking at credentials rather than at which database you reached.

**Fixed.** The factory now reads `ConnectionStrings__RepoPilot` as well.

### D3 — the API listened on 5186, not 8080

Every URL in the quickstart is `localhost:8080`. `Properties/launchSettings.json` carried the
scaffold's default port, and it wins over `ASPNETCORE_URLS` under `dotnet run`.

**Fixed.** The launch profiles now use 8080.

### D4 — fixture and task paths resolved against the project directory

`WorkspaceOptions.FixturesRoot` defaults to `./evals/fixtures`, resolved from the process working
directory — which `dotnet run --project src/RepoPilot.Api` sets to the project folder. Registration
failed with:

```
No fixture directory at '…/src/RepoPilot.Api/evals/fixtures/sample-dotnet-api'
```

which reads as a missing fixture rather than as a path resolved from an unexpected root.

**Fixed.** `appsettings.Development.json` now sets the workspace, fixtures, task and results paths
relative to the project directory, so the quickstart's commands work as written. Development only —
a deployment sets absolute paths and has no reason to inherit this repository's layout.

### D5 — `--output` was documented but not implemented

Scenario 8 runs `dotnet run --project src/RepoPilot.Evals -- --output evals/results/….json`. The CLI
took no arguments and always generated its own filename.

**Fixed.** `--output <path>` writes that exact file; omitting it keeps the generated name under the
configured results directory.

### D6 — the CI workflow had never run, and its test commands did not work

Discovered while preparing the first pull request. `main` has two commits and no
`specs/`, so no pull request had ever opened and `.github/workflows/ci.yml` had never
executed. All three of its test steps used `dotnet test <directory>`, which is not a
supported form — it looks for a project in the working directory and fails with
`MSB1003`. The workflow the constitution relies on to block merge would itself have
failed on its first run.

Two further gaps in the same file: the end-to-end tests need the pre-baked sandbox
image, which CI never built, so they would have skipped wholesale; and the
"assert nothing was skipped" step failed on *any* skip, which would have fired on
the two latency tests that legitimately need a model-provider credential CI has no
way to supply.

**Fixed.** Explicit project paths; the sandbox image is built before the end-to-end
step; `REPOPILOT_REQUIRE_DOCKER=1` makes the daemon-gated tests incapable of
skipping; and the skip assertion now reads the reason — a skip that names Docker or
the sandbox image fails the build, one that names a missing credential is reported
and allowed. It also fails a suite that executed nothing, because an empty run
passes every assertion it never made. The whole sequence was replayed locally
against the same commands CI runs.

### D7 — quickstart's own test commands had the same defect

`dotnet test` with no argument, and `dotnet test tests/e2e`. Both fail with
`MSB1003`.

**Fixed.** `dotnet test RepoPilot.slnx` for everything, with explicit project paths
for running one suite at a time.

### D8 — the sandbox could not write to its own working copy on Linux

CI's first green-lit run failed one test: `touch: scratch.txt: Permission denied`
inside `/workspace`.

`DockerSandboxRunner` hardcoded `User = "1000:1000"`, but the bind-mounted working
copy is owned by whoever runs the service. Docker Desktop's bind mounts translate
ownership, so every local run passed; a Linux bind mount does not, and the GitHub
runner is uid 1001. On any Linux host not running the service as uid 1000,
`apply_patch` would succeed and the `run_tests` that follows would fail on a
permission error that reads as a broken fixture.

This is a portability defect in the product, not a test artefact — and local
testing could not have found it. It took CI actually running.

**Fixed.** The container runs as the identity that owns the working copy
(`geteuid`/`getegid`), falling back to 1000 when the service is root, since
matching the host identity there would hand the sandbox root and Principle II
says it is non-root regardless. The image tolerates any uid: its root filesystem
is read-only and every writable path a toolchain needs is already redirected to
the tmpfs at `/tmp`.

`ProcessDoesNotRunAsRoot` asserted `id -u == "1000"`, which pinned an
implementation detail rather than the guarantee. It now asserts the uid parses and
is not zero — which is what the principle actually says, and what stays true when
the service runs as a different user.

---

## Still open

- **Scenarios 2, 3, 6, and 8 are unverified.** They need a model-provider credential. Re-run this
  record with one before treating the quickstart as validated.
- **No full evaluation report exists.** The completion, baseline, and latency figures stay labelled
  as targets until one does (Principle V, T137).
- **Retrieval is measured and SC-005 is met.** `--retrieval-only` needs no credential, so Recall@5 is
  a measured 83.3% (25/30) in `evals/results/`, reproducible byte-for-byte across passes. The first
  measurement read 70.0% and is kept beside it: measuring is what found the lexical arm ANDing every
  term of a query, which left hybrid search fusing one arm. Five tasks still miss, with their
  retrieved paths in the report.
- **The `.env` in `evals/fixtures/adversarial-content/config/` is force-added** past `.gitignore`. It
  is test data with a fake token, and the exclusion test needs it committed — but it will look like a
  leaked credential to any scanner that does not read the surrounding comment.
