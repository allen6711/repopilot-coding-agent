# Reference solution — bugfix-order-total-04

> **This file is never read by the agent.** It lives outside every fixture root, so neither the
> indexer nor `read_file` can reach it — a run's file access is confined to that run's working copy,
> which is a copy of the fixture directory only (FR-024a, FR-035). It exists for humans reviewing
> the quality of the evaluation dataset.

- **Fixture**: `sample-dotnet-api`
- **Command**: `unit-order-totals`
- **Category**: `bug_fix`

## What is wrong

The loop bound is `i < items.Count - 1`, so the final line is never added. An order with one line
iterates zero times and returns zero.

## One acceptable change

```csharp
for (var i = 0; i < items.Count; i++)
```

## What counts as success

`tests_pass_and_baseline_failed` — the task's command must fail before the change and pass after it. Any change satisfying that condition is accepted; matching the sketch above is not required
and the grader never compares against it.

## Judging note for dataset review

An empty order must still total zero. A fix that changes the bound but drops the empty-list case
is not correct.
