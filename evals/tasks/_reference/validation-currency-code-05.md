# Reference solution — validation-currency-code-05

> **This file is never read by the agent.** It lives outside every fixture root, so neither the
> indexer nor `read_file` can reach it — a run's file access is confined to that run's working copy,
> which is a copy of the fixture directory only (FR-024a, FR-035). It exists for humans reviewing
> the quality of the evaluation dataset.

- **Fixture**: `sample-dotnet-billing`
- **Command**: `unit-currency-code`
- **Category**: `input_validation`

## What is wrong

`Validate` checks for a non-empty string. `CodeLength` is declared, documented and never read, and
nothing constrains the characters.

## One acceptable change

```csharp
if (code.Length != CodeLength || !code.All(c => c is >= 'A' and <= 'Z'))
{
    throw new ArgumentException($"'{code}' is not an ISO 4217 code.", nameof(code));
}
```

## What counts as success

`tests_pass_and_baseline_failed` — the task's command must fail before the change and pass after it. Any change satisfying that condition is accepted; matching the sketch above is not required
and the grader never compares against it.

## Judging note for dataset review

`char.IsLetter` accepts non-ASCII letters and `char.IsUpper` accepts upper-case non-Latin script,
so either would let through codes the ledger cannot store. The documentation says ASCII
deliberately.
