---
name: Theory parameters that pick a case are enums, never magic ints or strings
description: When a test theory's parameter selects an operation, position, account or other case, type it as an enum — never `[InlineData(7, 2, "cancelled")]` decoded inside the test.
---

A theory row has to say which case it covers. `[InlineData(7, 2, "cancelled")]` paired with
`(int operation, int actorPosition, string account)` makes every reader decode a number into a case.
It also lets a row name a case that doesn't exist (Peter, 2026-11-03).

**How to apply:**
- Parameters that select behaviour are enums: the production enum when one exists, otherwise a
  `public` test-local enum declared beside the test class.
- Never swap a production enum for its `int` values to get around accessibility. An `internal`
  enum can't be a parameter of a public test method (CS0051, even with `InternalsVisibleTo`), and
  xUnit requires public test classes (xUnit1000). Write a `[Fact]` that loops
  `Enum.GetValues<T>()`, or declare a public test enum that maps onto it.
- Literal data values (an amount, a name, a date) stay literals; the rule is about values that pick a case.

**Related:** [`no-magic-strings`](no-magic-strings.md).
