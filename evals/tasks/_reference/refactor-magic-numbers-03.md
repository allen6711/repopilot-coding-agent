# Reference solution — refactor-magic-numbers-03

> **This file is never read by the agent.** It lives outside every fixture root, so neither the
> indexer nor `read_file` can reach it — a run's file access is confined to that run's working copy,
> which is a copy of the fixture directory only (FR-024a, FR-035). It exists for humans reviewing
> the quality of the evaluation dataset.

- **Fixture**: `sample-dotnet-api`
- **Command**: `unit-loyalty-tiers`
- **Category**: `refactor`

## What is wrong

No defect. 5000, 1500 and 250 appear in both `For` and `PointsFor`; 3, 2 and 1 appear in both
`PointsPerPound` and `PointsFor`.

## One acceptable change

```csharp
public static int PointsFor(decimal annualSpend, decimal orderTotal) =>
    (int)(orderTotal * PointsPerPound(For(annualSpend)));
```

## What counts as success

`tests_pass` — the task's command must pass after the change. Any change satisfying that condition is accepted; matching the sketch above is not required
and the grader never compares against it.

## Judging note for dataset review

`PointsFor` casting to `int` truncates, and the tests pin that. A refactor that rounds instead
changes behaviour at every non-integer total.
