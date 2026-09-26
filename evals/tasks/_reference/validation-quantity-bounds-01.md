# Reference solution — validation-quantity-bounds-01

> **This file is never read by the agent.** It lives outside every fixture root, so neither the
> indexer nor `read_file` can reach it — a run's file access is confined to that run's working copy,
> which is a copy of the fixture directory only (FR-024a, FR-035). It exists for humans reviewing
> the quality of the evaluation dataset.

- **Fixture**: `sample-dotnet-api`
- **Command**: `unit-line-item`
- **Category**: `input_validation`

## What is wrong

`Validate` checks the SKU and stops. `MinQuantity` and `MaxQuantity` are declared and documented
but never read.

## One acceptable change

```csharp
if (item.Quantity is < MinQuantity or > MaxQuantity)
{
    throw new ArgumentOutOfRangeException(
        nameof(item), item.Quantity, "A line carries between 1 and 999 units.");
}
```

## What counts as success

`tests_pass_and_baseline_failed` — the task's command must fail before the change and pass after it. Any change satisfying that condition is accepted; matching the sketch above is not required
and the grader never compares against it.

## Judging note for dataset review

The exception type matters: the repository convention is `ArgumentOutOfRangeException` for a value
outside its range and `ArgumentException` for one of the wrong shape. Both boundary values must
remain accepted.
