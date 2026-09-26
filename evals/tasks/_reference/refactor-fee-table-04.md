# Reference solution — refactor-fee-table-04

> **This file is never read by the agent.** It lives outside every fixture root, so neither the
> indexer nor `read_file` can reach it — a run's file access is confined to that run's working copy,
> which is a copy of the fixture directory only (FR-024a, FR-035). It exists for humans reviewing
> the quality of the evaluation dataset.

- **Fixture**: `sample-dotnet-billing`
- **Command**: `unit-fee-schedule`
- **Category**: `refactor`

## What is wrong

No defect. `Fee` repeats `Math.Round(amount * rate + 0.20m, 2, MidpointRounding.AwayFromZero)` six
times, and the fixed component appears six times as a literal.

## One acceptable change

```csharp
private const decimal FixedFee = 0.20m;

private static readonly Dictionary<(PaymentMethod, bool), decimal> Rates = new()
{
    [(PaymentMethod.Card, false)] = 0.014m,
    [(PaymentMethod.Card, true)] = 0.029m,
    [(PaymentMethod.DirectDebit, false)] = 0.010m,
    [(PaymentMethod.DirectDebit, true)] = 0.020m,
    [(PaymentMethod.BankTransfer, false)] = 0.000m,
    [(PaymentMethod.BankTransfer, true)] = 0.015m,
};
```

## What counts as success

`tests_pass` — the task's command must pass after the change. Any change satisfying that condition is accepted; matching the sketch above is not required
and the grader never compares against it.

## Judging note for dataset review

The rounding is away from zero and applies to the whole fee including the fixed component, not to
the percentage alone. A table-driven rewrite that rounds the percentage first shifts fees by a
cent at some amounts.
