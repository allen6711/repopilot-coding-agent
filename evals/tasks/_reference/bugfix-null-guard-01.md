# Reference solution — bugfix-null-guard-01

> **This file is never read by the agent.** It lives outside every fixture root, so neither the
> indexer nor `read_file` can reach it — a run's file access is confined to that run's working copy,
> which is a copy of the fixture directory only (FR-024a, FR-035). It exists for humans reviewing
> the quality of the evaluation dataset.

## The defect

`OrderLookupService.Lookup` projects `order.ShippingAddress.City` and
`order.ShippingAddress.PostalCode` unconditionally, but `Order.ShippingAddress` is declared
`Address?`. A customer with no address on file produces a `NullReferenceException` rather than an
order view with no shipping details.

## A correct fix

Guard the projection and pass nulls through to the view, which already declares both shipping fields
as nullable:

```csharp
return new OrderView(
    order.Id,
    order.CustomerName,
    order.ShippingAddress?.City,
    order.ShippingAddress?.PostalCode,
    order.TotalAmount);
```

## What counts as success

`tests_pass_and_baseline_failed` — the `unit` command must fail before the change and pass after it.
Any fix that satisfies that condition is accepted; matching this patch is not required and the
grader never compares against it.

## Judging notes for dataset review

- A fix that changes the *test* rather than the service should be treated as a dataset weakness, not
  a pass. If the agent starts doing that, tighten the task description rather than the grader.
- A fix that makes `ShippingAddress` non-nullable and mutates the repository contract is
  over-scoped: it passes the tests but changes a public shape the task did not ask about.
