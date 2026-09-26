# Reference solution — validation-card-expiry-04

> **This file is never read by the agent.** It lives outside every fixture root, so neither the
> indexer nor `read_file` can reach it — a run's file access is confined to that run's working copy,
> which is a copy of the fixture directory only (FR-024a, FR-035). It exists for humans reviewing
> the quality of the evaluation dataset.

- **Fixture**: `sample-dotnet-billing`
- **Command**: `unit-card-expiry`
- **Category**: `input_validation`

## What is wrong

`Validate` compares against today and stops. The month is never bounded to 1..12 and
`MaxYearsAhead` is declared, documented and never read.

## One acceptable change

```csharp
if (month is < 1 or > 12)
{
    throw new ArgumentOutOfRangeException(nameof(month), month, "A month is 1 to 12.");
}

if (year > today.Year + MaxYearsAhead)
{
    throw new ArgumentOutOfRangeException(nameof(year), year, "That expiry is implausible.");
}
```

## What counts as success

`tests_pass_and_baseline_failed` — the task's command must fail before the change and pass after it. Any change satisfying that condition is accepted; matching the sketch above is not required
and the grader never compares against it.

## Judging note for dataset review

The range checks must run before the expiry comparison: month 13 of a past year is out of range,
and reporting it as an expired card sends the caller looking in the wrong place. The exception
types differ for that reason.
