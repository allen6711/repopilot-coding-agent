# Governance & Safety Requirements Checklist: Governed Agent Run

**Purpose**: Validate that the approval-gate, sandboxed-execution, path-confinement, and
secret-isolation requirements are complete, unambiguous, consistent, and measurable before task
breakdown begins. This is a requirements-quality audit, not a verification pass — every item asks
whether something is *specified well*, not whether the system *behaves correctly*.

**Created**: 2026-08-10

**Feature**: [spec.md](../spec.md)

**Depth**: Formal pre-implementation gate. Intended for a reviewer to work through before
`/speckit-tasks`. An unchecked item should be resolved by amending the source document, not deferred
to implementation.

**Sources audited**: [spec.md](../spec.md), [plan.md](../plan.md),
[data-model.md](../data-model.md), [contracts/](../contracts/)

**Constitution alignment**: Principle I (Human-Approved Writes, NON-NEGOTIABLE), Principle II
(Sandboxed, Allow-Listed Execution). Per the constitution's compliance rules, security-relevant work
must be reviewed against these two principles explicitly — this checklist is that review's input.

---

## Approval Gate — Requirement Completeness

- [x] CHK001 Are requirements defined for **who is permitted to approve** a proposal, as distinct from who is recorded as having approved it? [Gap, Spec §FR-015, §FR-019]
- [x] CHK002 Is it specified whether the person who started a run may approve that run's own proposal, or whether separation of duties is required? [Gap, Spec Assumptions]
- [x] CHK003 Are requirements defined for **who is permitted to cancel** a run, given that cancellation ends a run and destroys its working copy? [Gap, Spec §FR-008a]
- [x] CHK004 Are requirements defined for the validity lifetime of an approval — may a proposal approved after an arbitrarily long wait still be applied? [Gap, Spec §FR-014, Edge Cases]
- [x] CHK005 Is the required content of an approval record complete and consistent between the spec and the constitution — specifically, does §FR-019's "identifier of the exact change decided upon" mean the same thing as the constitution's "diff hash"? [Consistency, Spec §FR-019]
- [x] CHK006 Are requirements defined for the immutability of approval and rejection records once written (append-only, no update path)? [Gap, Spec §FR-019]
- [x] CHK007 Are retention requirements specified for approval records and run events, given that SC-008 depends on them surviving indefinitely? [Gap, Spec §SC-008]
- [x] CHK008 Are requirements defined governing when programmatic approval mode may be used — specifically, is it required to be unavailable to interactive runs? [Gap, Spec Assumptions, Data Model §6]

## Approval Gate — Clarity & Measurability

- [x] CHK009 Is "content differs from what was shown to the approver" defined precisely enough to be verified without reading the plan — does it mean the rendered diff, the underlying file content, or both? [Ambiguity, Spec §FR-020]
- [x] CHK010 Is the change identifier required to be collision-resistant, or is any identifier sufficient? [Clarity, Spec §FR-019, §FR-020]
- [x] CHK011 Can SC-001's "recorded human approval" be objectively evaluated given that the Assumptions section counts programmatic evaluation approvals as human approvals? [Ambiguity, Spec §SC-001, Assumptions]
- [x] CHK012 Is SC-004's "the full diff is visible before deciding" reconcilable with the 512 KB total proposal cap and any display truncation, or do the two conflict for large proposals? [Conflict, Spec §SC-004, Plan §Research 7]
- [x] CHK013 Are the proposal size caps (20 entries, 256 KB per file, 512 KB total) stated as requirements anywhere, or do they exist only as design decisions in plan and contracts? [Traceability, Plan §Research 7, contracts/agent-tools.md]
- [x] CHK014 Is the behavior on exceeding the retrieved-context limit specified — refuse the call, or silently truncate? [Gap, Spec §FR-006]

## Sandboxed Execution — Requirement Completeness

- [x] CHK015 Is **network isolation** stated as a requirement at the spec level? §FR-021 names credentials, secrets, and host filesystem but not network access. [Gap, Spec §FR-021]
- [x] CHK016 Are resource-limit requirements (memory, CPU, process count) specified, or is resource exhaustion inside the sandbox an unaddressed failure mode? [Gap, Spec §FR-021]
- [x] CHK017 Are requirements defined for tearing down sandbox containers orphaned by a service crash or restart, comparable to the working-copy sweep in §FR-026a? [Gap, Spec §FR-026a]
- [x] CHK018 Are requirements defined for the case where the time limit expires but the execution environment cannot be terminated? [Edge Case, Gap, Spec §FR-023, §SC-009]
- [x] CHK019 Is it required that the allowed test commands be expressed in a form that cannot embed a shell escape (argument vector rather than a shell string), or is that only a contract-level choice? [Traceability, Spec §FR-022, contracts/fixture-config.schema.json]
- [x] CHK020 Are bounds specified for the configured time limit itself, or may it be set to any value including one long enough to hold a slot indefinitely? [Clarity, Spec §FR-023]
- [x] CHK021 Is it specified who may author or amend a fixture's test configuration, given that it is the sole source of executable commands? [Gap, Spec §FR-022]

## Path Confinement & Workspace Integrity

- [x] CHK022 Is "the repository workspace" defined unambiguously — does it mean the registered fixture directory, the run's disposable working copy, or both, and does the answer change by stage? [Ambiguity, Spec §FR-024, §FR-016]
- [x] CHK023 Do the read capabilities' stated source and the working copy's lifecycle agree? Contracts describe `list_files` and `read_file` as reading "the repository working copy", while the data model creates the working copy only on entering `applying`. [Conflict, contracts/agent-tools.md, Data Model §7]
- [x] CHK024 Are requirements defined for symbolic links inside a fixture that resolve outside the workspace root? [Edge Case, Gap, Spec §FR-024]
- [x] CHK025 Is there a *preventive* requirement that the registered fixture cannot be modified, or does the spec rely only on the *detective* measurement in SC-002? [Gap, Spec §FR-016, §SC-002]
- [x] CHK026 Are §FR-024 ("reject before performing the access") and the Edge Cases entry ("the run reports the violation rather than silently skipping it") consistent about whether a refusal must also be recorded and surfaced? [Consistency, Spec §FR-024, Edge Cases]
- [x] CHK027 Are requirements defined for what happens to a partially written change if a run is cancelled or the service restarts during the applying stage? [Gap, Spec §FR-008a, Edge Cases, Assumptions]

## Secret & Credential Isolation

- [x] CHK028 Is §FR-025 ("MUST NOT expose repository credentials or secrets to the model") specified with enough precision to be verifiable, or does it depend on an undefined notion of what constitutes a secret? [Clarity, Spec §FR-025]
- [x] CHK029 Are requirements defined for redacting **test output** before it re-enters model context during a revision attempt? Test output is model-visible and may contain environment dumps. [Gap, Spec §FR-012, §FR-025]
- [x] CHK030 Are requirements defined for redacting recorded action arguments, which are persisted and displayed to reviewers? [Gap, Spec §FR-027, contracts/run-events.md]
- [x] CHK031 Is the indexing exclusion list in §FR-002 stated as a normative minimum, and is it consistent with the categories the plan enumerates (binary, size, denylisted directory, secret pattern, secret content)? [Consistency, Spec §FR-002, §FR-003, Plan §Research 16]
- [x] CHK032 Are requirements defined addressing untrusted repository content that attempts to influence the agent's behavior — for example, text in an indexed file instructing the agent to propose a change or claim approval? [Gap, Spec §FR-025, §FR-026]

## Requirement Consistency Across Documents

- [x] CHK033 Do the terminal outcomes enumerated in §FR-008 match those used in the data model and the API contract, with no outcome present in one and absent from another? [Consistency, Spec §FR-008, Data Model §3, contracts/rest-api.yaml]
- [x] CHK034 Is the outcome-reason vocabulary normative anywhere, or is it introduced only as illustrative examples in the data model? §FR-008b requires a reason that distinguishes two specific cases. [Clarity, Spec §FR-008b, Data Model §3]
- [x] CHK035 Is the seven-capability tool set traceable from a spec requirement, or does §FR-026 ("only the defined capability set") reference a set defined solely in the constitution and contracts? [Traceability, Spec §FR-026]
- [x] CHK036 Are the four permission classes defined normatively in a requirements document, or only in the contracts? [Traceability, contracts/agent-tools.md]
- [x] CHK037 Is the plan's narrowing of the model-visible tool surface (orchestrator-invoked apply and test) reflected in or contradicted by any spec requirement? [Consistency, Plan §Constitution Check, Spec §FR-026]
- [x] CHK038 Is the suffixed requirement-ID scheme (FR-003a, FR-008a, FR-013b, FR-026a) documented so that later amendments extend it predictably? [Traceability, Spec §Requirements]

## Scenario Coverage — Governance Paths

- [x] CHK039 Are requirements complete for the primary approval path: propose, review, approve, apply, test, report? [Coverage, Spec §User Story 1]
- [x] CHK040 Are requirements complete for the rejection path, including the guarantee that no filesystem operation of any kind occurs? [Coverage, Spec §FR-017]
- [x] CHK041 Are requirements complete for the revision path, including that each attempt requires its own approval and that the attempt limit is enforced? [Coverage, Spec §FR-012, §FR-013]
- [x] CHK042 Are requirements defined for every non-success governance ending — rejected, cancelled, no change, and failure at each stage — with a stated reason for each? [Coverage, Spec §FR-008, §FR-030]
- [x] CHK043 Are recovery requirements defined for a run interrupted mid-apply or mid-test, beyond the Assumptions statement that it ends as failed? [Coverage, Gap, Spec Assumptions]

## Dependencies & Assumptions

- [x] CHK044 Is the assumption that reviewer identity "comes from whatever identity the deployment supplies" accompanied by a stated consequence — specifically, that an unauthenticated deployment makes the approval record's actor unverifiable? [Assumption, Spec §Assumptions]
- [x] CHK045 Is the dependency on a container runtime documented as a requirement-level assumption, including the consequence when it is unavailable? [Dependency, Gap, Spec §FR-021]
- [x] CHK046 Is the assumption that fixtures ship with pre-restored dependencies (because the sandbox has no network) recorded in the spec, or only in the plan? [Assumption, Traceability, Plan §Research 10]
- [x] CHK047 Is the single-reviewer assumption paired with a stated boundary — what the system must do if a second reviewer attempts a decision? [Assumption, Spec §Assumptions, §FR-018]

## Measurability of Governance Success Criteria

- [x] CHK048 Can SC-002 ("zero file modifications outside a disposable working copy") be objectively measured, and is the measurement scope defined? [Measurability, Spec §SC-002]
- [x] CHK049 Can SC-010 ("every attempt to access a path outside the workspace is refused") be measured, and is it defined how an attempt is counted when it never reaches I/O? [Measurability, Spec §SC-010]
- [x] CHK050 Is SC-009's "100% of test executions terminate within the configured time limit" stated in a way that distinguishes a clean finish from a forced termination for measurement purposes? [Clarity, Spec §SC-009]
- [x] CHK051 Is SC-012 ("zero working copies remain") specified with a defined observation point, given that destruction is asynchronous relative to reaching a terminal outcome? [Measurability, Spec §SC-012]

---

## Resolution log — 2026-08-10

All 51 items were resolved by amending the source documents. Nothing was deferred to implementation
and nothing was closed by re-interpretation. Summary of what changed:

| Items | Resolution |
|---|---|
| CHK001–CHK003 | FR-015a requires an actor identity on every approval, rejection, and cancellation and refuses the decision without one; FR-008a extends the rule to cancellation. Assumptions now state the consequence — the actor is attributable but not verified while authentication is out of scope. |
| CHK002 | Assumptions now state plainly that self-approval is permitted in this feature and that the audit record makes it visible rather than preventing it; enforcement waits on the identity stretch goal. |
| CHK004 | FR-014a — approvals do not expire, because proposals are immutable and a working copy is owned exclusively by its run. |
| CHK005, CHK010 | FR-019a — the recorded identifier must be a collision-resistant hash of the change content, aligning §FR-019 with the constitution's "diff hash". |
| CHK006, CHK007 | FR-019b — decision records are append-only and retained at least as long as their run. |
| CHK008 | FR-015b — programmatic approval is available only to evaluation runs. |
| CHK009 | FR-020a — defines "what was shown to the approver" as the content named by the hash presented with the diff, and requires the decision request to carry it back. |
| CHK011 | SC-001 rewritten: it now measures recorded decisions with matching hashes, and requires interactive and programmatic approvals to be reported separately so a programmatic decision is never presented as a human one. |
| CHK012, CHK013 | FR-011a — proposal size caps become requirements and are enforced at creation, so every proposal reaching a reviewer is fully displayable. The SC-004 conflict disappears rather than being documented as an exception. |
| CHK014 | FR-006 — exceeding the context limit refuses the retrieval instead of truncating silently. |
| CHK015–CHK018, CHK020 | FR-021a (no network), FR-021b (memory, processor, process-count limits), FR-023a (bounded time limit), FR-023b (non-terminable environment fails the run), FR-026e (orphaned environments removed). |
| CHK019, CHK021 | FR-022a (argument vectors, never shell strings) and FR-022b (test configuration committed with the fixture, changed through code review). |
| CHK022, CHK023 | **Conflict resolved.** FR-024a defines the workspace as the run's own working copy for all agent file access, and the working copy is now created on entering `retrieving` rather than at `applying`. Data model, contracts, research, and quickstart updated to match. |
| CHK024–CHK026 | FR-024b (symlink escapes rejected), FR-024c (refusals recorded as countable failed actions), FR-016a (the fixture is never opened for writing on any path). |
| CHK027, CHK043 | FR-016b (atomic application) and FR-030a (restart ends non-terminal runs, destroys copies, removes containers). |
| CHK028–CHK030 | FR-025a (normative secret definition applied identically at index and read time), FR-025b (sandbox output redacted before storage, display, and re-entry into model context), FR-027a (recorded arguments redacted). |
| CHK031 | FR-003b — exclusion reasons are normative, and FR-003 requires the breakdown to be reported. |
| CHK032 | FR-026d — repository content is never instruction; SC-014 measures it against fixtures deliberately seeded with content that tries to instruct the agent. |
| CHK033, CHK034 | Terminal outcomes verified consistent across spec, data model, and API contract; FR-008c makes the outcome-reason set normative and the data model stores it as an enum so an undocumented reason cannot be persisted. |
| CHK035–CHK037 | FR-026c — the seven capabilities and the four permission classes are stated as requirements, with enforcement required at the point of invocation. |
| CHK038 | The Requirements section now documents the suffix ID scheme. |
| CHK039–CHK042 | Coverage confirmed; FR-008c closes the reason gap for every non-success ending. |
| CHK044–CHK047 | Assumptions expanded with stated consequences: unverified identity, execution-runtime dependency with no unisolated fallback, pre-restored fixture dependencies, and the second-reviewer case enforced by FR-018. |
| CHK048–CHK051 | SC-002, SC-009, SC-010, and SC-012 given explicit measurement scope, counting rules, and observation points. SC-015 added for decision-record completeness. |

**Net effect on the spec**: 30 requirements added (FR-003b, FR-008c, FR-011a, FR-014a, FR-015a/b,
FR-016a/b, FR-019a/b, FR-020a, FR-021a/b, FR-022a/b, FR-023a/b, FR-024a/b/c, FR-025a/b, FR-026c/d/e,
FR-027a, FR-030a), 6 requirements tightened, 2 success criteria added (SC-014, SC-015), 4 success
criteria made measurable, and 5 assumptions given stated consequences.

## Notes

- Items marked `[Gap]` indicate the audit found no requirement covering the case in any of the four
  audited documents. Resolving one means adding or amending a requirement, not writing a task.
- Items marked `[Conflict]` indicate two documents that cannot both be satisfied as written. These
  should be resolved before task breakdown, since each conflict otherwise propagates into multiple
  tasks.
- Items marked `[Traceability]` indicate a rule that is real and enforced in design but has no
  requirement to trace back to. These are the items most likely to be silently dropped during
  implementation.
- Passing this checklist is not evidence that the governance controls work. It is evidence that the
  requirements describing them are good enough to build and test against.
