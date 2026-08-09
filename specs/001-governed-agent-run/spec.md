# Feature Specification: Governed Agent Run (MVP End-to-End Flow)

**Feature Branch**: `001-governed-agent-run`

**Created**: 2026-08-09

**Status**: Draft

**Input**: User description: "Complete MVP end-to-end flow — register a repository fixture,
index it, retrieve context, plan, propose a patch, require human approval, apply to a disposable
workspace, run tests in an isolated sandbox, and present the result."

## Clarifications

### Session 2026-08-10

- Q: When a run ends without producing an approved-and-tested change — abandoned by the reviewer,
  no modification proposed, or the task too vague to locate code — which terminal outcome applies?
  → A: Add two terminal outcomes, `cancelled` (human explicitly abandons the run) and `no change`
  (agent proposes no modification, including the insufficient-context case, distinguished by a
  recorded reason); five terminal outcomes total.
- Q: How many runs may execute at the same time? → A: Multiple runs execute concurrently under a
  configured global limit on concurrently executing runs and isolated execution environments; runs
  awaiting approval do not count toward that limit, and runs beyond it queue. No per-fixture
  restriction.
- Q: When is a run's disposable working copy destroyed, and what survives it? → A: Destroyed as
  soon as the run reaches any terminal outcome, including cancelled and rejected; the final diff,
  test output, and run events persist in durable storage independent of the working copy. Copies
  orphaned by a service restart are removed at startup.
- Q: How does re-indexing an already-indexed repository treat existing index data? → A: Full
  rebuild with atomic swap — the new index is built in full, then swapped in; queries continue to
  serve the previous index until the swap completes. No incremental per-file updates.
- Q: How quickly must in-progress stage and action updates reach a reviewer watching a run? → A:
  Within 2 seconds of the change occurring, without the reviewer refreshing the view.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Approve a proposed change and see it tested (Priority: P1)

A developer selects an already-registered repository fixture, describes a task (or picks a seeded
task), and starts a run. The system finds the relevant code, returns a short plan, and presents a
proposed change as a reviewable diff. Nothing has been written yet. The developer reviews the
affected files and the diff, then approves. Only then is the change applied to a throwaway copy of
the repository, the project's tests run in isolation, and the developer sees the final diff
alongside pass/fail test output.

**Why this priority**: This is the product. It is the only story that delivers the core promise —
an agent that changes code without ever writing without permission. Every other story is either an
input to this one or an observability layer on top of it.

**Independent Test**: With one repository fixture pre-indexed, a reviewer can start a run, receive
a diff, approve it, and observe test results — end to end, without any other story implemented.
Value delivered: a reviewed, tested code change produced from a natural-language task.

**Acceptance Scenarios**:

1. **Given** an indexed repository fixture and a task description, **When** the developer starts a
   run, **Then** the system returns a short plan and a proposed diff, and the repository fixture
   and the working copy are both unchanged.
2. **Given** a run awaiting approval, **When** the developer approves it, **Then** the change is
   applied to a disposable working copy, tests execute in isolation, and the outcome is reported
   as passed or failed.
3. **Given** a run awaiting approval, **When** the developer rejects it, **Then** no file anywhere
   is modified, the run ends in a rejected state, and the decision is recorded.
4. **Given** a run whose tests fail after approval, **When** the agent revises, **Then** a new
   proposal is presented for approval, and no revision is applied without its own approval.
5. **Given** any completed run, **When** the developer opens it, **Then** the final diff, the test
   output, and the sequence of actions the agent took are all visible.

---

### User Story 2 - Register and index a repository fixture (Priority: P2)

An operator registers a repository fixture from the allowed set and triggers indexing so the
system can search its source and documentation. The operator sees when indexing finishes and how
much of the repository was included, along with what was excluded and why.

**Why this priority**: Story 1 cannot run against an unindexed repository, but a fixture can be
pre-seeded for a first demo. It is a prerequisite in practice, not in demo sequence.

**Independent Test**: An operator registers a fixture, runs indexing, and issues a search query
that returns file paths and line ranges — testable with no agent run involved.

**Acceptance Scenarios**:

1. **Given** a repository fixture from the allowed set, **When** the operator registers and
   indexes it, **Then** indexing completes and reports the number of files included and excluded.
2. **Given** a repository containing binaries, build output, dependency directories, and files
   over the size limit, **When** indexing runs, **Then** none of those appear in search results.
3. **Given** an indexed repository, **When** a search is issued for an exact identifier, **Then**
   results include that identifier's defining file with its line range.
4. **Given** a repository that has already been indexed, **When** indexing is re-run, **Then**
   search results reflect the current content without duplicated entries.

---

### User Story 3 - Follow a run as it happens (Priority: P2)

A reviewer watches a run in progress and sees which stage it is in, which files the agent looked
at, and which actions succeeded or failed — rather than waiting for a final answer with no
visibility into how it was reached.

**Why this priority**: Approval is only meaningful if the reviewer can see what the agent did to
arrive at the proposal. Without it, the approval gate becomes a rubber stamp.

**Independent Test**: Start a run and observe stage transitions and action entries appearing in
order before the run completes.

**Acceptance Scenarios**:

1. **Given** a run in progress, **When** the reviewer opens it, **Then** the current stage is
   shown and updates within 2 seconds of each advance, without the reviewer refreshing the view.
2. **Given** a completed run, **When** the reviewer inspects its history, **Then** every action
   the agent took is listed with its name, timing, and success or failure status.
3. **Given** a run that failed, **When** the reviewer inspects it, **Then** the failure reason and
   the stage at which it occurred are stated.

---

### User Story 4 - Measure the agent against a fixed task set (Priority: P3)

An evaluator runs the committed task set and receives reproducible metrics: how often the relevant
files were retrieved, how often the task was actually completed, and how a retrieval-only baseline
compares to the full tool-using agent.

**Why this priority**: Required before any capability claim can be made, but not required for the
flow to work. It measures the product rather than delivering it.

**Independent Test**: Execute the evaluation set twice on unchanged fixtures and confirm the
reported metrics are consistent and derived only from committed task definitions.

**Acceptance Scenarios**:

1. **Given** the committed task set, **When** an evaluation runs, **Then** it reports
   relevant-file retrieval rate within the top five results and task completion rate.
2. **Given** the same evaluation run twice with no code or fixture changes, **When** results are
   compared, **Then** retrieval metrics are identical.
3. **Given** a completed evaluation, **When** the evaluator reads the results, **Then** a
   retrieval-only baseline and a tool-enabled result are reported separately for the same tasks.
4. **Given** any evaluation run, **When** approval coverage is computed, **Then** it reports 100%
   of applied changes as human-approved, or the evaluation is flagged as failed.

---

### Edge Cases

- A proposed change references a file outside the repository workspace → the proposal is rejected
  before any file is touched, and the run reports the violation rather than silently skipping it.
- A proposed change does not apply cleanly to the working copy → the run reports the conflict and
  does not leave a partially applied change.
- Tests hang or exceed the time limit → execution is terminated at the limit, the run reports a
  timeout, and the isolated environment is torn down.
- The task requires no change, or the agent concludes nothing should change → the run ends as no
  change with a deliberate-no-op reason, not an empty diff presented for approval.
- The reviewer never responds to an approval request → the run stays awaiting approval and holds
  its resources without applying anything; a human can explicitly abandon it, ending the run as
  cancelled.
- The same run is approved twice, or approved after rejection → the second decision is refused and
  the change is applied at most once.
- Test failures persist after the allowed revision attempts → the run ends as failed with the last
  diff and last test output retained for inspection.
- A task description is too vague to locate relevant code → the run ends as no change with an
  insufficient-context reason, rather than proposing a speculative change.
- The repository fixture is empty or contains no indexable files → registration succeeds but
  indexing reports zero indexed files and runs against it are refused.
- The model provider is unavailable mid-run → the run fails at its current stage with the reason
  recorded; no partial change is applied.

## Requirements *(mandatory)*

### Functional Requirements

**Repository and retrieval**

- **FR-001**: System MUST allow an operator to register a repository fixture from a configured
  allowed set, and MUST refuse any repository outside that set.
- **FR-002**: System MUST index registered repository source files and documentation, and MUST
  exclude binaries, build artifacts, dependency directories, secrets, and files exceeding
  configured size limits.
- **FR-003**: System MUST report, after indexing, how many files were included and how many were
  excluded.
- **FR-003a**: Re-indexing a repository MUST rebuild its index in full and replace the previous
  index atomically, leaving no duplicated or stale entries. Searches issued during a rebuild MUST
  continue to return results from the previous index until the replacement completes.
- **FR-004**: System MUST return search results that each identify a repository-relative path, the
  matching content, its start and end line, and a relevance score.
- **FR-005**: System MUST support both exact-identifier lookup and meaning-based search over
  indexed content.
- **FR-006**: System MUST enforce a limit on how much retrieved content is supplied to the agent
  in a single run.

**Run lifecycle**

- **FR-007**: Users MUST be able to start a run against a registered repository with either a
  free-text task or a seeded task from the committed task set.
- **FR-008**: System MUST progress each run through explicit, recorded stages: created,
  retrieving, planning, proposing, awaiting approval, applying, testing, and exactly one terminal
  outcome drawn from: succeeded, failed, rejected, cancelled, or no change.
- **FR-008a**: System MUST end a run as cancelled when a human explicitly abandons it from any
  non-terminal stage, including while awaiting approval, and MUST record who abandoned it and when.
- **FR-008b**: System MUST end a run as no change when the agent proposes no modification, and MUST
  record a reason distinguishing a deliberate no-op from insufficient retrieved context. A
  no-change run MUST NOT present an empty diff for approval.
- **FR-009**: System MUST reject any stage transition that is not permitted from the run's current
  stage, and MUST record the rejection rather than continuing.
- **FR-010**: System MUST produce a short human-readable plan before proposing any change.
- **FR-011**: System MUST present proposed changes as a reviewable diff listing every affected
  file.
- **FR-012**: System MUST allow at most two revision attempts after a failed test run, and MUST
  end the run as failed once that limit is reached.
- **FR-013**: Each revision attempt MUST require its own separate approval before being applied.
- **FR-013a**: System MUST support concurrent runs across and within repository fixtures, bounded
  by a configured maximum number of concurrently executing runs and isolated execution
  environments. Runs beyond that maximum MUST queue rather than be refused.
- **FR-013b**: A run awaiting approval MUST NOT count toward the concurrent-execution limit, so
  that an unanswered approval request cannot block other runs from starting.

**Approval gate**

- **FR-014**: System MUST NOT modify any file until a human approval decision for that specific
  proposed change has been recorded.
- **FR-015**: Users MUST be able to approve or reject a proposed change, and MUST be able to see
  the full diff before deciding.
- **FR-016**: System MUST apply approved changes only to a disposable working copy, never to the
  registered repository fixture.
- **FR-017**: System MUST leave the workspace unchanged when a proposal is rejected.
- **FR-018**: System MUST refuse a second decision on a proposal that has already been decided.
- **FR-019**: System MUST record every approval and rejection with the deciding user, timestamp,
  the run it belongs to, and an identifier of the exact change decided upon.
- **FR-020**: System MUST refuse to apply a change whose content differs from what was shown to
  the approver.

**Execution and safety**

- **FR-021**: System MUST execute all tests in an isolated environment with no access to
  credentials, secrets, or the host filesystem outside the working copy.
- **FR-022**: System MUST run only test commands drawn from repository configuration, and MUST
  refuse any command not present in that configuration.
- **FR-023**: System MUST terminate test execution at a configured time limit and report the run
  as timed out.
- **FR-024**: System MUST reject any file access resolving outside the repository workspace before
  performing the access.
- **FR-025**: System MUST NOT expose repository credentials or secrets to the model.
- **FR-026**: System MUST NOT permit the agent to execute arbitrary commands; only the defined
  capability set is available.
- **FR-026a**: System MUST destroy a run's disposable working copy as soon as that run reaches any
  terminal outcome, including cancelled and rejected, and MUST remove working copies left orphaned
  by a service restart.
- **FR-026b**: System MUST persist a run's final diff, test output, and events in storage
  independent of the working copy, so that no completed run depends on the working copy still
  existing in order to be inspected or reconstructed.

**Visibility**

- **FR-027**: System MUST record every agent action with its name, the run it belongs to, start
  and end time, and success or failure status.
- **FR-028**: Users MUST be able to view a run's current stage, action history, proposed and final
  diff, and test output.
- **FR-028a**: For a run in progress, stage transitions and newly recorded actions MUST become
  visible to a reviewer already watching that run within 2 seconds of being recorded, without the
  reviewer taking any action to refresh.
- **FR-029**: System MUST make a completed run reconstructable from its recorded events alone.
- **FR-030**: System MUST report the reason and stage of failure for any run that does not succeed.

**Evaluation**

- **FR-031**: System MUST include a committed set of at least 30 reproducible tasks with defined
  relevant files, baseline tests, and a success condition for each.
- **FR-032**: System MUST report, for an evaluation run, the rate at which a relevant file appears
  in the top five retrieved results and the rate of tasks meeting their success condition.
- **FR-033**: System MUST report a retrieval-only baseline result and a tool-enabled result for
  the same task set.
- **FR-034**: System MUST report the share of applied changes that were human-approved, and MUST
  flag any evaluation where that share is below 100%.
- **FR-035**: System MUST keep reference solutions out of the agent's available context during
  evaluation.

### Key Entities

- **Repository Fixture**: A registered code repository the system is permitted to work against.
  Holds its identity, allowed-set membership, indexing status, and test configuration.
- **Index Entry**: A searchable unit of repository content. Holds repository-relative path, the
  content, start and end line, and optional language or symbol information.
- **Run**: One task execution against one repository. Holds the task description, current stage,
  revision attempt count, terminal outcome, the reason recorded for that outcome, and timestamps.
- **Run Event**: A single recorded action or stage change within a run. Holds action name, timing,
  status, and ordering.
- **Change Proposal**: A set of file modifications the agent proposes. Holds affected files, the
  diff, the run it belongs to, and its decision status.
- **Approval Decision**: A human decision on one proposal. Holds decider, decision, timestamp, and
  the identifier of the exact change decided upon.
- **Working Copy**: A disposable copy of a fixture created for one run. Holds the run that owns it
  and its lifetime, which ends when that run reaches a terminal outcome.
- **Test Result**: Outcome of one execution. Holds pass/fail status, output, duration, and whether
  it was terminated by the time limit.
- **Evaluation Task**: A committed task definition. Holds description, relevant files, baseline
  tests, expected post-fix tests, and success condition.
- **Evaluation Result**: Metrics from one evaluation run over the task set.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: 100% of applied changes across all runs and evaluations have a recorded human
  approval matching the exact change applied. Any value below 100% blocks release.
- **SC-002**: Zero file modifications occur outside a disposable working copy, verified across the
  full evaluation set.
- **SC-003**: A reviewer can go from starting a run to seeing a reviewable diff in under 3 minutes
  for a task in the committed set, measured while the system is at or below its configured
  concurrent-execution limit.
- **SC-004**: A reviewer can decide on a proposal without leaving the review view — the affected
  file list and full diff are visible before deciding, in 100% of proposals.
- **SC-005**: A relevant file appears in the top five retrieved results for at least 80% of tasks
  in the committed set.
- **SC-006**: The tool-enabled agent completes a measurably higher share of tasks than the
  retrieval-only baseline on the same task set, with both figures reported.
- **SC-007**: Repeating an evaluation on unchanged fixtures reproduces identical retrieval metrics.
- **SC-008**: 100% of completed runs can be reconstructed — stage sequence, actions taken, diff,
  and test output — from recorded data alone, with no reliance on live state.
- **SC-009**: 100% of test executions terminate within the configured time limit, either by
  finishing or by being stopped and reported as timed out.
- **SC-010**: Every attempt to access a path outside the repository workspace is refused, with
  zero successful escapes across the evaluation set.
- **SC-011**: The committed task set contains at least 30 tasks, each with a defined success
  condition that can be checked without human judgment.
- **SC-012**: Zero working copies remain in storage for runs that have reached a terminal outcome,
  verified across the full evaluation set and after a service restart.
- **SC-013**: For a run in progress, 95% of stage transitions and recorded actions become visible
  to a watching reviewer within 2 seconds, with no manual refresh.

## Assumptions

- Repository fixtures are small, self-contained, and come from a pre-approved set committed to the
  project; arbitrary user-supplied repositories are out of scope for this feature.
- A single reviewer decides each proposal. Multi-reviewer approval, delegation, and approval
  policies are out of scope.
- Authentication and role-based access control are out of scope for this feature; the reviewer
  identity recorded on an approval comes from whatever identity the deployment supplies. Formal
  identity is a stretch goal in the project README.
- Each repository fixture ships with its own test configuration, including which commands are
  permitted and the time limit for execution.
- Runs are single-repository and single-task; cross-repository changes and batched tasks are out
  of scope.
- Changes are limited to modifications of existing text files and creation of new text files
  within the workspace. Binary file changes are out of scope.
- Runs are not resumable across service restarts; an interrupted run ends as failed and its working
  copy is discarded. Checkpoint/resume is a stretch goal in the project README.
- No change is ever pushed to a remote, branched, or merged. The disposable working copy is the
  final destination for this feature.
- Evaluation runs execute the same flow as interactive runs, with approval satisfied
  programmatically for tasks in the committed set; that programmatic approval is still recorded as
  an approval decision and counted in SC-001.
- Retrieval quality targets in the project README are development targets, not guarantees, and are
  reported as measured values.
