# Sample Orders Service

A small .NET library standing in for the order-management part of a commerce back end. It is an
evaluation fixture: it exists to be worked on by an agent under review, not to be shipped.

## Layout

- `src/Orders/` — the library. One type per concern, no framework dependencies.
- `tests/Orders.Tests/` — xUnit tests, one test class per source type.

## Conventions

- Public behaviour is described in XML documentation on the type or member. Where the documentation
  and the implementation disagree, the documentation is the specification.
- Money is a `decimal` and is rounded half away from zero to whole cents.
- Dates are `DateOnly`. Windows described in days are inclusive at both ends.
- Arguments are validated at the entry point of the public method that receives them. A value
  outside its permitted range raises `ArgumentOutOfRangeException`; a value of the wrong shape
  raises `ArgumentException`.
- Nothing here reaches a network, a clock, or a database. Every type is a pure function of its
  arguments, which is what makes the tests deterministic.

## Running the tests

Each test class has its own command in `repopilot.fixture.json`, because the fixture carries several
independent deliberate defects at once and a whole-suite run would never be green.
