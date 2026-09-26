# Reference solution — refactor-nested-conditionals-02

> **This file is never read by the agent.** It lives outside every fixture root, so neither the
> indexer nor `read_file` can reach it — a run's file access is confined to that run's working copy,
> which is a copy of the fixture directory only (FR-024a, FR-035). It exists for humans reviewing
> the quality of the evaluation dataset.

- **Fixture**: `sample-dotnet-api`
- **Command**: `unit-shipping-cost`
- **Category**: `refactor`

## What is wrong

No defect. `Charge` nests speed, membership and threshold checks four levels deep with explicit
`else` blocks around every return.

## One acceptable change

```csharp
return (speed, isMember, orderTotal >= FreeThreshold) switch
{
    (ShippingSpeed.Standard, true, _) => 0m,
    (ShippingSpeed.Standard, false, true) => 0m,
    (ShippingSpeed.Standard, false, false) => Standard,
    (ShippingSpeed.Express, true, true) => 0m,
    (ShippingSpeed.Express, true, false) => Standard,
    (ShippingSpeed.Express, false, true) => Standard,
    (ShippingSpeed.Express, false, false) => Express,
};
```

## What counts as success

`tests_pass` — the task's command must pass after the change. Any change satisfying that condition is accepted; matching the sketch above is not required
and the grader never compares against it.

## Judging note for dataset review

The threshold test pins 50 as inside the free band and 49.99 as outside it. A restructure that
flips that boundary is not behaviour-preserving even though the rate card reads the same.
