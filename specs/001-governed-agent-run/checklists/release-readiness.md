# Release Readiness Checklist: Governed Agent Run

**Purpose**: Everything still standing between this feature and a release, in the order a person
would do it. Unlike the other two checklists in this directory, this is **not** a
requirements-quality audit — every item here asks whether something has been *observed*, not whether
it is *specified well*.

**Created**: 2026-09-26

**Feature**: [spec.md](../spec.md)

**Depth**: Verification gate. An unchecked item is unfinished work or an unmeasured claim, not a
wording problem.

**Why it exists**: the remaining work is not spread across the codebase — it is one missing input.
Every item in the first section needs a model-provider credential and nothing else, and the
constitution makes the whole list a gate: no stretch-goal work may begin until the MVP acceptance
criteria in `README.md` are met, and two of those ten rows are what this checklist closes.

**Sources**: [spec.md](../spec.md), [plan.md](../plan.md), [quickstart.md](../quickstart.md),
`README.md` (MVP acceptance criteria, Measured, Not yet measured), `docs/quickstart-validation.md`,
`.specify/memory/constitution.md` (Principle V, Development Workflow)

---

## Blocked on a model-provider credential

Set `REPOPILOT__ChatProvider__ApiKey`, or let the SDK read it from the environment. Nothing below
needs a code change.

- [ ] CHK001 Has the full evaluation been run to completion — `dotnet run --project src/RepoPilot.Evals` — against both indexed fixtures, writing a report to `evals/results/`? [MVP criterion 9, T137]
- [ ] CHK002 Does that report carry a **retrieval-only baseline** and a **tool-enabled** completion rate for the same 30 tasks, reported separately? [SC-006, FR-033, MVP criterion 10]
- [ ] CHK003 Is the tool-enabled completion rate **measurably higher** than the baseline, and is the comparison stated as measured rather than expected? [SC-006, Principle V]
- [ ] CHK004 Did `ApprovalCoverageGate` report **100%** approval coverage, and did the CLI exit zero? Any value below 100% is a release blocker rather than a metric to report. [SC-001, Principle V]
- [ ] CHK005 Are programmatic and interactive approvals reported as **separate counts**, with no combined "approved" figure anywhere in the report or the README? [SC-001, FR-015b]
- [ ] CHK006 Is start-of-run-to-reviewable-diff **p95 under 3 minutes** at or below the concurrency limit? [SC-003]
- [ ] CHK007 Do `LatencyBudgetTests` and `SingleRunLatencyTests` now **run rather than skip**? They are the only skips `.github/scripts/check_skips.py` allows, and the allowance exists solely because CI has no credential. [SC-003]
- [ ] CHK008 Have the three remaining `README.md` target rows — retrieval-only completion baseline, tool-enabled completion, start-of-run-to-diff p95 — been replaced with measured values citing the report they came from? [T137, Principle V]
- [ ] CHK009 Have MVP acceptance criteria rows 9 and 10 in `README.md` been marked **Met**, with their evidence updated from "needs a credential" to the report? [Constitution, Development Workflow]

## Quickstart scenarios never verified end to end

`docs/quickstart-validation.md` records a real run of [quickstart.md](../quickstart.md) in which
these four could not be reached. Re-run that record with a credential and update it.

- [ ] CHK010 Scenario 2 — approve a change and see it tested, end to end, with the fixture and working copy both unchanged before approval. [US1/AC1, US1/AC2]
- [ ] CHK011 Scenario 3 — a rejected proposal writes nothing anywhere and the decision is recorded. [US1/AC3, FR-017]
- [ ] CHK012 Scenario 6 — six concurrent runs: four execute, two queue, none are refused, and a run left awaiting approval holds no slot. [FR-013a, FR-013b]
- [ ] CHK013 Scenario 8 — the evaluation runs to completion rather than stopping at the baseline probe. [US4]
- [ ] CHK014 Has `docs/quickstart-validation.md` been re-dated and its "Still open" section reduced to what is genuinely still open? [T138]

## Reachable without a credential — confirm before release

- [ ] CHK015 Do all four suites pass on the release commit? Last observed: 275 unit, 211 integration (2 allowed skips), 5 e2e, 43 UI.
- [ ] CHK016 Is the committed Recall@5 report reproducible on the release commit — a second `--retrieval-only` pass producing a byte-identical report apart from its timestamp? [SC-007]
- [ ] CHK017 Does `README.md` still state Recall@5 as measured, with SC-005's threshold and whether it is met? Last observed 83.3% (25/30), met. [SC-005, Principle V]
- [ ] CHK018 Has the force-added `.env` in `evals/fixtures/adversarial-content/config/` been checked against whatever secret scanner runs on the release? It is test data with a fake token and the exclusion test needs it committed, but it will look like a leaked credential to a scanner that does not read the surrounding comment. [FR-025a]
- [ ] CHK019 Has every security-relevant change on the branch been reviewed against Principles I and II explicitly, naming which tests cover it? The constitution requires the review to state its coverage, not merely to happen. [Constitution, Development Workflow]
- [ ] CHK020 Is the pull request's compliance review recorded, with any deviation carrying an expiry condition? Undocumented deviations are defects. [Constitution, Governance]

## After this list is complete

- [ ] CHK021 With every row above checked, are the MVP acceptance criteria met — and therefore is stretch-goal work unblocked for the first time? The nearest candidate is a semantic embedding model in place of `DeterministicEmbeddingAdapter`, which is a model-provider comparison and gated until now. It would trade SC-007's byte-identical reproducibility for retrieval quality on the five tasks still missed, so it is a decision with a cost rather than a free improvement. [Constitution, Development Workflow; README stretch goals]

## Notes

- Items are observations, so an unchecked box means "not yet seen", never "not yet written down".
- CHK001 through CHK009 are one sitting's work once a credential exists: the harness drives the same
  `RunOrchestrator` an interactive run uses, so there is no separate evaluation path to prepare.
- CHK004 is the only item on this list that can fail rather than merely be unmeasured. If approval
  coverage is below 100%, stop: Principle V makes that a release blocker.
