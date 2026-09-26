# Reference solution — validation-postal-code-02

> **This file is never read by the agent.** It lives outside every fixture root, so neither the
> indexer nor `read_file` can reach it — a run's file access is confined to that run's working copy,
> which is a copy of the fixture directory only (FR-024a, FR-035). It exists for humans reviewing
> the quality of the evaluation dataset.

- **Fixture**: `sample-dotnet-api`
- **Command**: `unit-address`
- **Category**: `input_validation`

## What is wrong

`Validate` checks the city and postal code for presence only. The documented format — one or two
letters, one or two digits, an optional trailing letter, upper case, no space — is unenforced.

## One acceptable change

```csharp
if (!Regex.IsMatch(address.PostalCode, "^[A-Z]{1,2}[0-9]{1,2}[A-Z]?$"))
{
    throw new ArgumentException(
        $"'{address.PostalCode}' is not an outward code.", nameof(address));
}
```

## What counts as success

`tests_pass_and_baseline_failed` — the task's command must fail before the change and pass after it. Any change satisfying that condition is accepted; matching the sketch above is not required
and the grader never compares against it.

## Judging note for dataset review

A full UK postcode such as `CB1 2AB` must be rejected: the carrier integration takes the outward
code alone. A fix that accepts it has read the examples rather than the rule.
