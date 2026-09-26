# Reference solution — testwork-dunning-coverage-03

> **This file is never read by the agent.** It lives outside every fixture root, so neither the
> indexer nor `read_file` can reach it — a run's file access is confined to that run's working copy,
> which is a copy of the fixture directory only (FR-024a, FR-035). It exists for humans reviewing
> the quality of the evaluation dataset.

- **Fixture**: `sample-dotnet-billing`
- **Command**: `unit-dunning`
- **Category**: `test_work`

## What is wrong

No defect. Two tests exist, covering day 0 and day 1. Days 14, 15, 29 and 30 are all uncovered,
and those are where the inclusive boundaries live.

## One acceptable change

```csharp
[Theory]
[InlineData(14, DunningAction.Reminder)]
[InlineData(15, DunningAction.FinalNotice)]
[InlineData(29, DunningAction.FinalNotice)]
[InlineData(30, DunningAction.Suspend)]
public void ActionFor_EscalatesOnTheBoundaryDay(int daysOverdue, DunningAction expected) =>
    Assert.Equal(expected, DunningSchedule.ActionFor(daysOverdue));
```

## What counts as success

`output_matches` — the command must report zero failures and at least three passing tests, so a proposal that changes nothing cannot satisfy it. Any change satisfying that condition is accepted; matching the sketch above is not required
and the grader never compares against it.

## Judging note for dataset review

A `[Theory]` counts one passing test per data row, so the condition's threshold is met comfortably
by the version above. Judge the diff for whether both boundaries are actually covered rather than
trusting the count.
