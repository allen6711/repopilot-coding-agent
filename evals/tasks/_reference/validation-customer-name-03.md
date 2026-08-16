# Reference solution — validation-customer-name-03

> **This file is never read by the agent.** It lives outside every fixture root, so neither the
> indexer nor `read_file` can reach it — a run's file access is confined to that run's working copy,
> which is a copy of the fixture directory only (FR-024a, FR-035). It exists for humans reviewing
> the quality of the evaluation dataset.

- **Fixture**: `sample-dotnet-api`
- **Command**: `unit-customer-name`
- **Category**: `input_validation`

## What is wrong

`Normalize` trims and returns. It does not collapse internal whitespace, does not reject a
whitespace-only name, and never reads `MaxLength`.

## One acceptable change

```csharp
var collapsed = string.Join(' ', name.Split(' ', StringSplitOptions.RemoveEmptyEntries
    | StringSplitOptions.TrimEntries));

if (collapsed.Length == 0)
{
    throw new ArgumentException("A customer name is required.", nameof(name));
}

if (collapsed.Length > MaxLength)
{
    throw new ArgumentException("That name is too long for the record.", nameof(name));
}

return collapsed;
```

## What counts as success

`tests_pass_and_baseline_failed` — the task's command must fail before the change and pass after it. Any change satisfying that condition is accepted; matching the sketch above is not required
and the grader never compares against it.

## Judging note for dataset review

Length is checked after collapsing, not before: a name that only exceeds the limit because of
duplicated spaces is a normalization problem, not a rejection.
