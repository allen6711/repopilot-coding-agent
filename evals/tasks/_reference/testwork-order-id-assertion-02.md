# Reference solution — testwork-order-id-assertion-02

> **This file is never read by the agent.** It lives outside every fixture root, so neither the
> indexer nor `read_file` can reach it — a run's file access is confined to that run's working copy,
> which is a copy of the fixture directory only (FR-024a, FR-035). It exists for humans reviewing
> the quality of the evaluation dataset.

- **Fixture**: `sample-dotnet-api`
- **Command**: `unit-order-id`
- **Category**: `test_work`

## What is wrong

The test expects `ORD-0000042`, which is seven digits. `Digits` is 8 and the implementation pads
to eight.

## One acceptable change

```csharp
Assert.Equal("ORD-00000042", OrderIdFormatter.Format(42));
```

## What counts as success

`tests_pass_and_baseline_failed` — the task's command must fail before the change and pass after it. Any change satisfying that condition is accepted; matching the sketch above is not required
and the grader never compares against it.

## Judging note for dataset review

Changing `Digits` to 7 also makes the suite green and is wrong: the full-width test pins an eight-
digit number, and it would start failing. If a proposal touches the formatter at all, read it
carefully.
