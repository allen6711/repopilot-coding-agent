# Reference solution — bugfix-proration-span-05

> **This file is never read by the agent.** It lives outside every fixture root, so neither the
> indexer nor `read_file` can reach it — a run's file access is confined to that run's working copy,
> which is a copy of the fixture directory only (FR-024a, FR-035). It exists for humans reviewing
> the quality of the evaluation dataset.

- **Fixture**: `sample-dotnet-billing`
- **Command**: `unit-proration`
- **Category**: `bug_fix`

## What is wrong

`activeTo.DayNumber - activeFrom.DayNumber` counts the gap between two dates, not the days covered
by an inclusive span. A one-day span counts zero.

## One acceptable change

```csharp
var activeDays = activeTo.DayNumber - activeFrom.DayNumber + 1;
```

## What counts as success

`tests_pass_and_baseline_failed` — the task's command must fail before the change and pass after it. Any change satisfying that condition is accepted; matching the sketch above is not required
and the grader never compares against it.

## Judging note for dataset review

The guard that rejects a span ending before it starts is correct and must survive: without it the
plus one turns a backwards span into a charge for one day.
