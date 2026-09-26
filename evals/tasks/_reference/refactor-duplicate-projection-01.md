# Reference solution — refactor-duplicate-projection-01

> **This file is never read by the agent.** It lives outside every fixture root, so neither the
> indexer nor `read_file` can reach it — a run's file access is confined to that run's working copy,
> which is a copy of the fixture directory only (FR-024a, FR-035). It exists for humans reviewing
> the quality of the evaluation dataset.

- **Fixture**: `sample-dotnet-api`
- **Command**: `unit-order-projection`
- **Category**: `refactor`

## What is wrong

No defect. `ForDetail` and `ForList` each build an `OrderView` with the same null handling,
spelled out twice.

## One acceptable change

```csharp
public static IReadOnlyList<OrderView> ForList(IReadOnlyList<Order> orders) =>
    [.. orders.Select(ForDetail)];
```

## What counts as success

`tests_pass` — the task's command must pass after the change. Any change satisfying that condition is accepted; matching the sketch above is not required
and the grader never compares against it.

## Judging note for dataset review

The tests pass before and after, so the success condition cannot distinguish a real refactor from
no change at all. Judge the diff: an empty proposal is a legitimate no-change outcome for the run
but not a completion of this task.
