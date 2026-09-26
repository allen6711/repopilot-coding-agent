# Reference solution — testwork-refund-boundary-01

> **This file is never read by the agent.** It lives outside every fixture root, so neither the
> indexer nor `read_file` can reach it — a run's file access is confined to that run's working copy,
> which is a copy of the fixture directory only (FR-024a, FR-035). It exists for humans reviewing
> the quality of the evaluation dataset.

- **Fixture**: `sample-dotnet-api`
- **Command**: `unit-refund-policy`
- **Category**: `test_work`

## What is wrong

No defect. Two tests exist: the never-delivered case and a day well inside the window. Day 30, day
31 and day 0 are all uncovered.

## One acceptable change

```csharp
[Fact]
public void IsRefundable_IsTrue_OnTheLastDayOfTheWindow() =>
    Assert.True(RefundPolicy.IsRefundable(Delivered, Delivered.AddDays(30)));

[Fact]
public void IsRefundable_IsFalse_TheDayAfterTheWindow() =>
    Assert.False(RefundPolicy.IsRefundable(Delivered, Delivered.AddDays(31)));
```

## What counts as success

`output_matches` — the command must report zero failures and at least three passing tests, so a proposal that changes nothing cannot satisfy it. Any change satisfying that condition is accepted; matching the sketch above is not required
and the grader never compares against it.

## Judging note for dataset review

The condition counts passing tests, so a proposal that adds a trivially true test also satisfies
it. Judge the diff for whether the boundary is actually covered; if agents start gaming it,
tighten the condition to a named-test pattern rather than the grader.
