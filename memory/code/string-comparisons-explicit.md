---
name: Always use explicit StringComparison
description: `StringComparison.Ordinal` for exact matches, `OrdinalIgnoreCase` for case-insensitive. For user search input, use shared `Humans.Web.Extensions` helpers.
---

<!-- freshness:triggers
  src/Humans.Application/Services/Profiles/PersonSearchFields.cs
-->
<!-- freshness:flag-on-change
  Rule: Always use explicit StringComparison
  Flag only if the code this rule constrains changed in a way that makes the rule
  wrong or unenforceable — a renamed/removed symbol, a moved namespace, a dropped
  analyzer. Routine edits to these files are the rule being followed, not drift.
-->

Always use explicit `StringComparison` parameter on string operations.

**Rule:** Use `StringComparison.Ordinal` for exact matches, `StringComparison.OrdinalIgnoreCase` for case-insensitive.

**Example:**
```csharp
// WRONG
if (status == "submitted")

// CORRECT
if (string.Equals(status, "submitted", StringComparison.Ordinal))
```

**Search/input convention:**
- For user-entered search terms, prefer the shared helpers in `Humans.Web.Extensions` (`HasSearchTerm`, `ContainsOrdinalIgnoreCase`) instead of open-coding whitespace/length guards or ad hoc case handling
- For person search specifically, use `IProfileService.SearchProfilesAsync` with the `PersonSearchFields` bit-flag — never reach for `IQueryable` helpers; the search runs in-memory over the FullProfile cache snapshot
