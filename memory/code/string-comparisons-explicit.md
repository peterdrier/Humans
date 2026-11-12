---
name: Always use explicit StringComparison
description: "`StringComparison.Ordinal` for exact matches, `OrdinalIgnoreCase` for case-insensitive. For user search input, use shared `Humans.Base.Extensions.StringSearchExtensions` helpers."
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
- For user-entered search terms, prefer the shared helpers in `Humans.Base.Extensions.StringSearchExtensions` (`HasSearchTerm`, `ContainsOrdinalIgnoreCase`) instead of open-coding whitespace/length guards or ad hoc case handling
- For person search specifically, use `IUserService.SearchUsersAsync` with the `PersonSearchFields` flags ([`person-search`](../architecture/person-search.md)); it runs in memory over the cached `UserInfo` snapshot
