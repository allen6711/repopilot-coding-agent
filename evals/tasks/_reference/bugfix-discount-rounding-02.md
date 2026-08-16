# Reference solution — bugfix-discount-rounding-02

> **This file is never read by the agent.** It lives outside every fixture root, so neither the
> indexer nor `read_file` can reach it — a run's file access is confined to that run's working copy,
> which is a copy of the fixture directory only (FR-024a, FR-035). It exists for humans reviewing
> the quality of the evaluation dataset.

- **Fixture**: `sample-dotnet-api`
- **Command**: `unit-discount`
- **Category**: `bug_fix`

## What is wrong

`Math.Round(discounted, 2)` uses the default `MidpointRounding.ToEven`. 5.025 rounds to 5.02, not
5.03. The XML documentation on `Apply` says money rounds half away from zero; the implementation
does not.

## One acceptable change

```csharp
return Math.Round(discounted, 2, MidpointRounding.AwayFromZero);
```

## What counts as success

`tests_pass_and_baseline_failed` — the task's command must fail before the change and pass after it. Any change satisfying that condition is accepted; matching the sketch above is not required
and the grader never compares against it.

## Judging note for dataset review

A fix that special-cases the value in the failing test, rather than changing the rounding mode,
passes the test and is a dataset weakness. Widen the test rather than the grader.
