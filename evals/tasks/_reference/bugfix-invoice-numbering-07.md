# Reference solution — bugfix-invoice-numbering-07

> **This file is never read by the agent.** It lives outside every fixture root, so neither the
> indexer nor `read_file` can reach it — a run's file access is confined to that run's working copy,
> which is a copy of the fixture directory only (FR-024a, FR-035). It exists for humans reviewing
> the quality of the evaluation dataset.

- **Fixture**: `sample-dotnet-billing`
- **Command**: `unit-invoice-number`
- **Category**: `bug_fix`

## What is wrong

`{_last++:D5}` formats the counter and then increments it, so the first call formats the initial
zero.

## One acceptable change

```csharp
return $"INV-{_year}-{++_last:D5}";
```

## What counts as success

`tests_pass_and_baseline_failed` — the task's command must fail before the change and pass after it. Any change satisfying that condition is accepted; matching the sketch above is not required
and the grader never compares against it.

## Judging note for dataset review

Seeding `_last` to 1 and post-incrementing also produces 00001 first, but leaves the year-rollover
reset setting it to 0. Check that a new year still starts at 00001.
