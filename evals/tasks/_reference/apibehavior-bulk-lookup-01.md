# Reference solution — apibehavior-bulk-lookup-01

> **This file is never read by the agent.** It lives outside every fixture root, so neither the
> indexer nor `read_file` can reach it — a run's file access is confined to that run's working copy,
> which is a copy of the fixture directory only (FR-024a, FR-035). It exists for humans reviewing
> the quality of the evaluation dataset.

- **Fixture**: `sample-dotnet-api`
- **Command**: `unit-order-query`
- **Category**: `api_behavior`

## What is wrong

`FindMany` appends only orders it found, so the result is shorter than the request and positions
no longer line up. The return type is already `IReadOnlyList<Order?>`, which says what was
intended.

## One acceptable change

```csharp
foreach (var id in orderIds)
{
    found.Add(_repository.Find(id));
}
```

## What counts as success

`tests_pass_and_baseline_failed` — the task's command must fail before the change and pass after it. Any change satisfying that condition is accepted; matching the sketch above is not required
and the grader never compares against it.

## Judging note for dataset review

Changing the signature to return a dictionary also solves the caller's problem but changes a
public shape the task did not ask about. Treat it as over-scoped.
