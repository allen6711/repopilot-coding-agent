# Reference solution — bugfix-retry-exhaustion-08

> **This file is never read by the agent.** It lives outside every fixture root, so neither the
> indexer nor `read_file` can reach it — a run's file access is confined to that run's working copy,
> which is a copy of the fixture directory only (FR-024a, FR-035). It exists for humans reviewing
> the quality of the evaluation dataset.

- **Fixture**: `sample-dotnet-billing`
- **Command**: `unit-payment-retry`
- **Category**: `bug_fix`

## What is wrong

The discard arm of the switch returns `0`, which boxes to a non-null `int?`. The return type is
nullable precisely so that exhaustion is distinguishable from an immediate retry.

## One acceptable change

```csharp
_ => null,
```

## What counts as success

`tests_pass_and_baseline_failed` — the task's command must fail before the change and pass after it. Any change satisfying that condition is accepted; matching the sketch above is not required
and the grader never compares against it.

## Judging note for dataset review

Attempt 0 and negative attempts must also answer null. A fix that only special-cases attempts
above `MaxAttempts` leaves the lower end returning a delay.
