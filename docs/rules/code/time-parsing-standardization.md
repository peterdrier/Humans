---
name: Time parsing via shared invariant-culture helpers
description: Use `TryParseInvariantTimeOnly` / `TryParseInvariantLocalTime` from `Humans.Web.Extensions.TimeParsingExtensions`. Keeps locale-stable parsing.
---

Use shared parser helpers for converting time input strings into `TimeOnly`/`LocalTime`.

**Rule:**
- Use `TryParseInvariantTimeOnly` and `TryParseInvariantLocalTime` from `Humans.Web.Extensions.TimeParsingExtensions` for shift/admin time parsing
- Keep parsing locale-stable (`CultureInfo.InvariantCulture`) to avoid culture-dependent acceptance differences

**Why:** Removes repeated parsing logic and avoids subtle parse differences when the server default culture changes.
