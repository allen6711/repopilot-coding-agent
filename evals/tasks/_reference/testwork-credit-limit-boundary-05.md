# Reference solution — testwork-credit-limit-boundary-05

> **This file is never read by the agent.** It lives outside every fixture root, so neither the
> indexer nor `read_file` can reach it — a run's file access is confined to that run's working copy,
> which is a copy of the fixture directory only (FR-024a, FR-035). It exists for humans reviewing
> the quality of the evaluation dataset.

- **Fixture**: `sample-dotnet-billing`
- **Command**: `unit-credit-limit`
- **Category**: `test_work`

## What is wrong

No defect. Two tests cover well inside and well outside the limit. The equality case, and the
uncapped account, are uncovered.

## One acceptable change

```csharp
[Fact]
public void Allows_AChargeThatLandsExactlyOnTheLimit() =>
    Assert.True(CreditLimit.Allows(currentBalance: 450m, charge: 50m, limit: 500m));
```

## What counts as success

`output_matches` — the command must report zero failures and at least three passing tests, so a proposal that changes nothing cannot satisfy it. Any change satisfying that condition is accepted; matching the sketch above is not required
and the grader never compares against it.

## Judging note for dataset review

Adding the uncapped-account case as well is in scope and welcome. Adding a test that asserts the
argument guard is not — it is coverage of a different documented behaviour and does not address
the boundary the task names.
