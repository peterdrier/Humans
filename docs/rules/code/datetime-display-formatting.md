---
name: Date/time display via shared extensions
description: Use `ToDisplayDate`, `ToDisplayDateTime`, `ToAuditTimestamp`, etc. — don't introduce inline `ToString("d MMM yyyy")` format strings.
---

Display formatting should be standardized through shared extensions instead of scattered inline format strings.

**Rule:**
- Prefer shared extensions such as `ToDisplayDate`, `ToDisplayLongDate`, `ToDisplayDateTime`, `ToDisplayCompactDate`, `ToDisplayCompactDateTime`, `ToDisplayTime`, `ToAuditTimestamp`, `ToDisplayGeneralDateTime`
- Avoid introducing new inline Razor format strings like `ToString("d MMM yyyy")` unless the format is genuinely one-off and not part of an established display convention

**Why:** Keeps view formatting consistent and makes date/time policy easy to evolve.

For CLR date formatting used outside display-only views (for example, outbound email payload strings), use shared helper extensions in the layer owning the content. Email templates should use `Humans.Infrastructure.Helpers.EmailDateTimeExtensions` rather than repeating long-date literals.
