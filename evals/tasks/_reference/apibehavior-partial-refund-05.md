# Reference solution — apibehavior-partial-refund-05

> **This file is never read by the agent.** It lives outside every fixture root, so neither the
> indexer nor `read_file` can reach it — a run's file access is confined to that run's working copy,
> which is a copy of the fixture directory only (FR-024a, FR-035). It exists for humans reviewing
> the quality of the evaluation dataset.

- **Fixture**: `sample-dotnet-billing`
- **Command**: `unit-refund`
- **Category**: `api_behavior`

## What is wrong

`Refund` ignores `requested`, refunds `ChargedAmount` in full, and refuses any call once anything
has been refunded.

## One acceptable change

```csharp
if (Refunded + requested > ChargedAmount)
{
    throw new InvalidOperationException(
        "That would refund more than was charged.");
}

Refunded += requested;

return requested;
```

## What counts as success

`tests_pass_and_baseline_failed` — the task's command must fail before the change and pass after it. Any change satisfying that condition is accepted; matching the sketch above is not required
and the grader never compares against it.

## Judging note for dataset review

Refunding the remainder rather than throwing when the request overshoots is a different behaviour,
and the test pins the throw. `InvalidOperationException` is the right type: the request is well
formed and the object's state forbids it.
