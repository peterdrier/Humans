---
name: NodaTime for all dates/times — server-side always UTC
description: Use NodaTime types (`Instant`, `LocalDate`, `ZonedDateTime`) instead of `DateTime`/`DateOnly`/`TimeOnly`. Server-side ALWAYS UTC. Client-side translates to local at display.
---

**Prefer NodaTime for internal time handling:**
- Use NodaTime types (`Instant`, `LocalDate`, `ZonedDateTime`) instead of `DateTime`/`DateOnly`/`TimeOnly`

**Server-side ALWAYS uses UTC:**
- Use NodaTime `Instant` or `SystemClock.Instance.GetCurrentInstant()` for current time
- Store all dates/times in UTC (database, JSON, APIs)
- Never store or transmit local timezones from server
- All server-side calculations and comparisons in UTC

**Client-side translates to local time at display:**
- Convert UTC to user's local timezone only at the final display step
- Never send local times back to server — convert to UTC first

**Web/view-model exception:**
- `DateTime` is allowed in web-layer view models and Razor views **only after** a NodaTime value has been explicitly converted for display (e.g. via `.ToDateTimeUtc()`)
- Do not introduce `DateTime` into domain logic, persistence models, APIs, or service boundaries when NodaTime can be used instead

**Why:** NodaTime provides safer time handling. Prevents timezone bugs, ensures consistent server behavior across deployments, simplifies testing.

**Related:** [`time-parsing-standardization`](time-parsing-standardization.md), [`datetime-display-formatting`](datetime-display-formatting.md).
