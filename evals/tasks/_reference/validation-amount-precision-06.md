# Reference solution — validation-amount-precision-06

> **This file is never read by the agent.** It lives outside every fixture root, so neither the
> indexer nor `read_file` can reach it — a run's file access is confined to that run's working copy,
> which is a copy of the fixture directory only (FR-024a, FR-035). It exists for humans reviewing
> the quality of the evaluation dataset.

- **Fixture**: `sample-dotnet-billing`
- **Command**: `unit-amount`
- **Category**: `input_validation`

## What is wrong

`Validate` rejects negatives only. `MaxAmount` and `Scale` are declared, documented and never
read.

## One acceptable change

```csharp
if (amount > MaxAmount)
{
    throw new ArgumentOutOfRangeException(
        nameof(amount), amount, "That amount needs manual review.");
}

if (decimal.Round(amount, Scale) != amount)
{
    throw new ArgumentException("An amount is held to whole cents.", nameof(amount));
}
```

## What counts as success

`tests_pass_and_baseline_failed` — the task's command must fail before the change and pass after it. Any change satisfying that condition is accepted; matching the sketch above is not required
and the grader never compares against it.

## Judging note for dataset review

Reading `decimal.GetBits` for the scale also works and rejects 10.00m written with a trailing zero
— which is the same value and must stay acceptable. Compare values, not representations.
