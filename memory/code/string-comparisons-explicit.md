---
name: Always use explicit StringComparison
description: `StringComparison.Ordinal` for exact matches, `OrdinalIgnoreCase` for case-insensitive. For user search input, use shared `Humans.Web.Extensions` helpers.
---

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
