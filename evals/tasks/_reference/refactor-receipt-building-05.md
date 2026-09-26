# Reference solution — refactor-receipt-building-05

> **This file is never read by the agent.** It lives outside every fixture root, so neither the
> indexer nor `read_file` can reach it — a run's file access is confined to that run's working copy,
> which is a copy of the fixture directory only (FR-024a, FR-035). It exists for humans reviewing
> the quality of the evaluation dataset.

- **Fixture**: `sample-dotnet-billing`
- **Command**: `unit-receipt`
- **Category**: `refactor`

## What is wrong

No defect. `Render` uses `text = text + ...` inside a loop, grows `description` and `totalLabel`
with `while` loops one space per iteration, and indexes `lines[i]` three times per iteration.

## One acceptable change

```csharp
var receipt = new StringBuilder();

foreach (var line in lines)
{
    var description = line.Description.Length > DescriptionWidth
        ? line.Description[..DescriptionWidth]
        : line.Description.PadRight(DescriptionWidth);

    receipt.Append($"{description} {currencyCode} {line.Amount:0.00}\n");
    total += line.Amount;
}

receipt.Append($"{"TOTAL".PadRight(DescriptionWidth)} {currencyCode} {total:0.00}\n");
```

## What counts as success

`tests_pass` — the task's command must pass after the change. Any change satisfying that condition is accepted; matching the sketch above is not required
and the grader never compares against it.

## Judging note for dataset review

The line separator is a literal `\n`, not `Environment.NewLine`. A rewrite that reaches for
`AppendLine` produces CRLF on Windows and fails the byte-identical requirement even where the
tests happen to pass.
