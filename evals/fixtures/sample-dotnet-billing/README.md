# Sample Billing Service

A small .NET library standing in for the billing part of a subscription back end. It is an
evaluation fixture: it exists to be worked on by an agent under review, not to be shipped.

## Layout

- `src/Billing/` — the library. One type per concern, no framework dependencies.
- `tests/Billing.Tests/` — xUnit tests, one test class per source type.

## Conventions

- Public behaviour is described in XML documentation on the type or member. Where the documentation
  and the implementation disagree, the documentation is the specification.
- Money is a `decimal`, held to two decimal places, and rounded half away from zero.
- Dates are `DateOnly`. A period described by two dates is inclusive at both ends.
- Arguments are validated at the entry point of the public method that receives them. A value
  outside its permitted range raises `ArgumentOutOfRangeException`; a value of the wrong shape
  raises `ArgumentException`. A call that the object's current state forbids raises
  `InvalidOperationException`.
- Nothing here reaches a network, a clock, or a database. Anything time-dependent takes the date it
  should treat as today as an argument, which is what makes the tests deterministic.

## Running the tests

Each test class has its own command in `repopilot.fixture.json`, because the fixture carries several
independent deliberate defects at once and a whole-suite run would never be green.
