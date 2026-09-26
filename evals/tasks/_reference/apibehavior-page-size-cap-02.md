# Reference solution — apibehavior-page-size-cap-02

> **This file is never read by the agent.** It lives outside every fixture root, so neither the
> indexer nor `read_file` can reach it — a run's file access is confined to that run's working copy,
> which is a copy of the fixture directory only (FR-024a, FR-035). It exists for humans reviewing
> the quality of the evaluation dataset.

- **Fixture**: `sample-dotnet-api`
- **Command**: `unit-order-paging`
- **Category**: `api_behavior`

## What is wrong

`Resolve` floors the size at the default but never ceilings it. `MaxSize` is declared and
documented and never read.

## One acceptable change

```csharp
var resolvedSize = size is null or < 1 ? DefaultSize : Math.Min(size.Value, MaxSize);
```

## What counts as success

`tests_pass_and_baseline_failed` — the task's command must fail before the change and pass after it. Any change satisfying that condition is accepted; matching the sketch above is not required
and the grader never compares against it.

## Judging note for dataset review

Refusing an oversized request with an exception is a different behaviour from capping it, and the
documentation says the service serves the capped page. A throwing fix is wrong even though it
stops the stall.
