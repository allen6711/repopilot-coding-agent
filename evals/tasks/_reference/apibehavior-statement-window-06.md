# Reference solution — apibehavior-statement-window-06

> **This file is never read by the agent.** It lives outside every fixture root, so neither the
> indexer nor `read_file` can reach it — a run's file access is confined to that run's working copy,
> which is a copy of the fixture directory only (FR-024a, FR-035). It exists for humans reviewing
> the quality of the evaluation dataset.

- **Fixture**: `sample-dotnet-billing`
- **Command**: `unit-statement-period`
- **Category**: `api_behavior`

## What is wrong

`Contains` uses `date < End`, excluding the last day, and `DayCount` measures the gap between the
two dates rather than the days covered.

## One acceptable change

```csharp
public int DayCount => End.DayNumber - Start.DayNumber + 1;

public bool Contains(DateOnly date) => date >= Start && date <= End;
```

## What counts as success

`tests_pass_and_baseline_failed` — the task's command must fail before the change and pass after it. Any change satisfying that condition is accepted; matching the sketch above is not required
and the grader never compares against it.

## Judging note for dataset review

Both members have to change together. Fixing `Contains` alone leaves a statement that covers 30
days reporting 29, which is the discrepancy finance noticed first.
