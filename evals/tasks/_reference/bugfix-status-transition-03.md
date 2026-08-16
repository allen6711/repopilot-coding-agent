# Reference solution — bugfix-status-transition-03

> **This file is never read by the agent.** It lives outside every fixture root, so neither the
> indexer nor `read_file` can reach it — a run's file access is confined to that run's working copy,
> which is a copy of the fixture directory only (FR-024a, FR-035). It exists for humans reviewing
> the quality of the evaluation dataset.

- **Fixture**: `sample-dotnet-api`
- **Command**: `unit-order-status`
- **Category**: `bug_fix`

## What is wrong

The arm for `Shipped` accepts `from is OrderStatus.Paid or OrderStatus.Cancelled`. A cancelled
order is a terminal state and must not lead anywhere.

## One acceptable change

```csharp
OrderStatus.Shipped => from is OrderStatus.Paid,
```

## What counts as success

`tests_pass_and_baseline_failed` — the task's command must fail before the change and pass after it. Any change satisfying that condition is accepted; matching the sketch above is not required
and the grader never compares against it.

## Judging note for dataset review

Over-scoped if the fix also changes the `Cancelled` arm, which is already correct: cancelling is
permitted from every state except `Shipped`.
