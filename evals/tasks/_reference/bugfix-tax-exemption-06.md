# Reference solution — bugfix-tax-exemption-06

> **This file is never read by the agent.** It lives outside every fixture root, so neither the
> indexer nor `read_file` can reach it — a run's file access is confined to that run's working copy,
> which is a copy of the fixture directory only (FR-024a, FR-035). It exists for humans reviewing
> the quality of the evaluation dataset.

- **Fixture**: `sample-dotnet-billing`
- **Command**: `unit-tax`
- **Category**: `bug_fix`

## What is wrong

`WithTax` takes `isExempt` and never reads it.

## One acceptable change

```csharp
if (isExempt)
{
    return amount;
}
```

## What counts as success

`tests_pass_and_baseline_failed` — the task's command must fail before the change and pass after it. Any change satisfying that condition is accepted; matching the sketch above is not required
and the grader never compares against it.

## Judging note for dataset review

Returning early before the rate check would also stop an exempt customer's negative rate being
rejected. Either order passes the tests; the earlier guard is the smaller change and the later one
is defensible — do not treat either as wrong.
