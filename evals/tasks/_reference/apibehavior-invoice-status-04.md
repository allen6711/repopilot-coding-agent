# Reference solution — apibehavior-invoice-status-04

> **This file is never read by the agent.** It lives outside every fixture root, so neither the
> indexer nor `read_file` can reach it — a run's file access is confined to that run's working copy,
> which is a copy of the fixture directory only (FR-024a, FR-035). It exists for humans reviewing
> the quality of the evaluation dataset.

- **Fixture**: `sample-dotnet-billing`
- **Command**: `unit-invoice-status`
- **Category**: `api_behavior`

## What is wrong

`Status` collapses four documented states into two: anything short of the invoiced amount is
`Unpaid` and anything at or above it is `Paid`.

## One acceptable change

```csharp
return received switch
{
    _ when received > invoiced => InvoiceStatus.Overpaid,
    _ when received == invoiced => InvoiceStatus.Paid,
    0m => InvoiceStatus.Unpaid,
    _ => InvoiceStatus.PartiallyPaid,
};
```

## What counts as success

`tests_pass_and_baseline_failed` — the task's command must fail before the change and pass after it. Any change satisfying that condition is accepted; matching the sketch above is not required
and the grader never compares against it.

## Judging note for dataset review

A zero-value invoice with nothing received is `Paid`, not `Unpaid` — the order of the arms above
gets that right. It is not covered by a test; do not fail a proposal that orders them differently
unless a test says so.
