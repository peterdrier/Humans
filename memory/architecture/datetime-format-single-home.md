---
name: Date/time formatting lives in DateFormattingExtensions
description: HARD RULE. Rendering, exporting or parsing a date/time: call a named method on `DateFormattingExtensions`; an inline format string is a build error.
---

Custom date/time format strings live only in `Humans.Base.Extensions.DateFormattingExtensions`. Anywhere else in production code a hand-rolled format string is a build error (the date-format analyzer, HUM0030).

**Which method:**

- **Display** (day/month order and names follow `CurrentUICulture`): `ToTime`, `ToMonthDayTime`, `ToWeekdayDayMonth`, `ToDate`, `ToDateTime`; niche shapes `ToMonthYear`, `ToMonthName`, `ToMonthAbbrev`, `ToTimeWithSeconds`. There is deliberately no bare month-day: a date worth showing gets a year or a weekday. For an `Instant` in a request or view, the ambient overloads in `DateTimeDisplayExtensions` resolve the user's zone from session; pass an explicit `DateTimeZone` for an event's zone.
- **Machine** (exports, APIs, filenames, audit, JSON; `InvariantCulture`): `ToInvariantDate`, `ToInvariantTimestamp`, `ToInvariantTime`, `ToIso8601`, `ToSepaDateTime`, `ToFileTimestamp`, `ToInvariantLongDate`.
- **Parse/format patterns** are named `static readonly *Pattern` fields on the same class (`TimeOfDayPattern`, `PlacementDateTimePattern`, the iCal patterns, `OpsNoticeDatePattern`).
- **No method fits?** Add one to `DateFormattingExtensions`; never inline the literal.

**Emails and ops notices** render outside a web request, so they use the invariant methods today. Localising them means formatting inside the sending section's `CultureScope`.

**Not caught by the analyzer, fix by hand when you touch them:** `DateTime.ParseExact`, composite `string.Format`, and anything in `.cshtml`. Single-character standard specifiers (`"d"`, `"g"`, `"o"`) are allowed.

**Why:** scattered format strings rendered the same concept four different ways and ignored the reader's language.

**Related:** [`nodatime-for-dates`](../code/nodatime-for-dates.md)
