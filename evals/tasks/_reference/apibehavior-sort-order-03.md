# Reference solution — apibehavior-sort-order-03

> **This file is never read by the agent.** It lives outside every fixture root, so neither the
> indexer nor `read_file` can reach it — a run's file access is confined to that run's working copy,
> which is a copy of the fixture directory only (FR-024a, FR-035). It exists for humans reviewing
> the quality of the evaluation dataset.

- **Fixture**: `sample-dotnet-api`
- **Command**: `unit-order-sorter`
- **Category**: `api_behavior`

## What is wrong

`ForDisplay` sorts ascending by `PlacedOn`.

## One acceptable change

```csharp
return [.. orders.OrderByDescending(o => o.PlacedOn)];
```

## What counts as success

`tests_pass_and_baseline_failed` — the task's command must fail before the change and pass after it. Any change satisfying that condition is accepted; matching the sketch above is not required
and the grader never compares against it.

## Judging note for dataset review

`OrderByDescending` is a stable sort in LINQ to Objects, so equal dates keep their arrival order
for free. A fix that sorts ascending and then reverses passes the first test and fails the
stability one — which is why the stability test is there.
