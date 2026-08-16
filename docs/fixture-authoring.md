# Authoring a fixture and its evaluation tasks

How to add a repository fixture the agent can be evaluated against, and how to write tasks whose
results mean something.

The normative schemas are
[`fixture-config.schema.json`](../specs/001-governed-agent-run/contracts/fixture-config.schema.json)
and [`eval-task.schema.json`](../specs/001-governed-agent-run/contracts/eval-task.schema.json). Both
are enforced at registration and at load; this page is about the decisions the schemas cannot make
for you.

## A fixture is a governance artefact

`repopilot.fixture.json` is the sole source of commands the sandbox may ever execute. Nothing else
can introduce one. That is why `.github/CODEOWNERS` requires the same review on it as on source: a
change to the allow-list is a change to what the system can run.

Minimum viable fixture:

```
evals/fixtures/<slug>/
  repopilot.fixture.json     # identity, sandbox limits, command allow-list
  README.md                  # conventions the agent is expected to follow
  .editorconfig              # see "a fixture is not part of this repository" below
  src/...                    # the code
  tests/...                  # the tests each task is graded by
```

The slug must also appear in the deployment's `RepoPilot:AllowedRepositories:Slugs`. Registration is
refused otherwise (FR-001), which is the check, not a formality.

### A fixture is not part of this repository

It simulates an independent one. Give it `root = true` in its own `.editorconfig` so RepoPilot's
analyzer strictness does not leak in — a deliberate null-dereference defect has to fail at run time
as a test failure, not at compile time as a suppressed warning.

Do not commit `bin/` or `obj/`. The working-copy builder skips them anyway, so a committed one is
dead weight that the indexer then has to exclude.

### Sandbox settings

The image must have every dependency already restored. The sandbox runs with no network, a read-only
root filesystem, all capabilities dropped, and a non-root user — a fixture whose tests need to fetch
something is not a valid fixture. Add its packages to `sandbox/nuget-warmup/` (or the language
equivalent) so the image carries them.

Keep `timeoutSeconds` tight. It is a hard limit; on expiry the container is killed and the run
reports a timeout, which SC-009 counts separately from a test failure.

### One command per task, not one command per fixture

This is the decision most likely to be got wrong.

A fixture that carries several deliberate defects at once is never green as a whole. If every task
shares a single `unit` command, then fixing task 3 still leaves tasks 1, 2 and 4 failing, and no task
can ever be scored as complete. Give each task its own command, scoped to that task's test class:

```json
{
  "name": "unit-order-totals",
  "argv": [
    "dotnet", "test", "tests/Orders.Tests/Orders.Tests.csproj",
    "--filter", "FullyQualifiedName~OrderTotalsTests"
  ],
  "purpose": "verify"
}
```

The schema caps the allow-list at 20 commands, so a fixture holds at most ~19 tasks. Past that, add a
second fixture rather than sharing a command.

The first command with `purpose: "verify"` is what an interactive run against the fixture is verified
by, when the run does not name its own. Put a sensible default first.

## Writing a task

### The description is the whole of what the agent gets

It is passed verbatim. Write it as a bug report or a change request from someone who does not know
where the code lives:

> Order totals are coming out short by the value of whatever the last line on the order happens to
> be, and an order with a single line totals nothing at all.

Do **not** name the file to change. `relevantFiles` is the ground truth Recall@5 is measured against,
and a description that names the file measures the agent's ability to read the description.

Do not add encouragement ("be careful", "the tests matter"). It makes the measured figure a property
of the harness's prompt rather than of the committed task set.

### The success condition must be decidable without a person

Four forms exist, and the choice is not cosmetic:

| Condition | Use when | Watch out for |
|---|---|---|
| `tests_pass_and_baseline_failed` | The default for a defect or a missing check | Nothing — this is the strong form |
| `tests_pass` | Refactors, where the tests are green before and after | An agent that changes nothing satisfies it |
| `output_matches` | Test-writing tasks, where success means *more* passing tests | Ties the task to a runner's output format |
| `output_contains` | A specific string must appear | Same |

A task whose baseline already passes and whose condition is `tests_pass` is completed by an agent
that proposes nothing. `TaskSetCompletenessTests` fails the build for any non-refactor task in that
shape.

For a test-writing task, `output_matches` with a pattern like
`Failed:\s+0,\s+Passed:\s+(?:[3-9]|\d{2,}),` requires zero failures and at least three passing tests,
so adding a test is the only way through. Note what this buys and what it does not: it cannot tell a
real boundary test from a trivially true one. That is what the reference note is for.

### Every test must compile against the current source

A fixture carries many outstanding defects simultaneously, and they all have to build. A task that
requires a method that does not exist yet breaks compilation of the whole test project, which makes
*every* command in that fixture fail. Write api-behaviour tasks against existing signatures.

### Verify the baseline before committing

Run the fixture's whole suite and check that exactly the classes you expect are red:

```bash
cd evals/fixtures/<slug>
dotnet test tests/<Project>.Tests/<Project>.Tests.csproj
```

Then confirm each task's own command in isolation. A task whose baseline passes when you thought it
failed is a task that will report completions it did not earn.

## Reference solutions

Put them in `evals/tasks/_reference/`, referenced as `_reference/<task-id>.md`.

That directory sits outside every fixture root, which is what keeps it out of the agent's reach
(FR-035): a run's file access is confined to its working copy, and the working copy is a copy of the
fixture directory only. `ReferenceSolutionsAreUnreachableTests` checks both halves — the layout, and
that no path resolves there from inside a fixture.

Write them for the human reviewing the dataset, not for a grader. The grader never reads them. What
is worth writing down:

- What is actually wrong, precisely enough to tell a real fix from a coincidence.
- One acceptable change — a sketch, explicitly not the only answer.
- A judging note: the over-scoped fix, the fix that games the condition, the boundary a careless
  change would break. These are what tell you the dataset is weakening before the numbers do.

## Checklist

- [ ] Slug added to the deployment's allowed set
- [ ] Sandbox image carries every dependency; nothing is fetched at test time
- [ ] One allow-listed command per task, scoped to that task's tests
- [ ] Fixture has its own `root = true` `.editorconfig`
- [ ] `README.md` states the conventions a task description can rely on
- [ ] Every task description avoids naming the file to change
- [ ] Every non-refactor task's baseline actually fails
- [ ] Whole fixture suite builds with every defect present
- [ ] Reference note per task, under `_reference/`
- [ ] `dotnet test tests/unit` passes — it checks the committed set for all of the above it can see
