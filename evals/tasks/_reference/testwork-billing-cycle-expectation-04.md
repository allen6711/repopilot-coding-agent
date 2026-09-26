# Reference solution — testwork-billing-cycle-expectation-04

> **This file is never read by the agent.** It lives outside every fixture root, so neither the
> indexer nor `read_file` can reach it — a run's file access is confined to that run's working copy,
> which is a copy of the fixture directory only (FR-024a, FR-035). It exists for humans reviewing
> the quality of the evaluation dataset.

- **Fixture**: `sample-dotnet-billing`
- **Command**: `unit-billing-cycle`
- **Category**: `test_work`

## What is wrong

`Next_FallsBackToTheLastDay_WhenTheNextMonthIsShorter` expects 2026-02-27. The documented fallback
is the last day of the month, and 2026 is not a leap year.

## One acceptable change

```csharp
new DateOnly(2026, 2, 28),
```

## What counts as success

`tests_pass_and_baseline_failed` — the task's command must fail before the change and pass after it. Any change satisfying that condition is accepted; matching the sketch above is not required
and the grader never compares against it.

## Judging note for dataset review

The following test pins the return to the anchor in March. A proposal that changes `BillingCycle`
to satisfy the wrong expectation breaks that one, which is why it is there.
