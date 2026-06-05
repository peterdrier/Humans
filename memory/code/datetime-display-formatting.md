---
name: Date/time formatting via the shared home (which method to call)
description: Render dates through the named methods on `DateFormattingExtensions` — `ToDisplayDate`/`ToDisplayDateTime`/`ToDisplayWeekdayDayMonth` for display, `ToInvariantDate`/`ToInvariantTimestamp`/`ToIso8601` for machine. Never inline `ToString("d MMM yyyy")`. Enforced by HUM0030.
---

All date/time formatting goes through the one home `Humans.Application.Extensions.DateFormattingExtensions`; an inline format string anywhere else is a build error — see [`datetime-format-single-home`](../architecture/datetime-format-single-home.md) (HUM0030).

**Display** (culture-ordered — day/month order *and* month/weekday names follow the request culture, e.g. `en` → `Jun 5`, `es` → `5 jun`):
`ToDisplayTime`, `ToDisplayMonthDay`, `ToDisplayMonthDayTime`, `ToDisplayWeekdayDayMonth`, `ToDisplayWeekdayDayMonthTime`, `ToDisplayDate`, `ToDisplayDateTime`, `ToDisplayWeekdayDate`. Niche: `ToDisplayMonthYear`, `ToDisplayMonthName`, `ToDisplayMonthAbbrev`, `ToTimeWithSeconds`. For an `Instant` in a request/view, the ambient overloads in `Humans.Web.Extensions.DateTimeDisplayExtensions` resolve the user's timezone from session; pass an explicit `DateTimeZone` for an event's zone.

**Machine / invariant** (stable, culture-independent — exports, APIs, filenames, audit, JSON):
`ToInvariantDate` (`2026-06-05`), `ToInvariantTimestamp` (`2026-06-05 14:30:45`), `ToInvariantTime` (`14:30`), `ToIso8601` (`2026-06-05T14:30:45Z`), `ToSepaDateTime` (SEPA ISO-20022, no `Z`), `ToFileTimestamp` (`2026-06-05-1430`, filenames), `ToInvariantLongDate` (`5 June 2026`, invariant email long-date). Parse/format pattern fields (`TimeOfDayPattern`, `PlacementDateTimePattern`, iCal patterns, the deliberately-invariant `OpsNoticeDatePattern`) also live on the home.

**Background-rendered content** (emails, ops notices) renders outside a web request, so the culture-aware display methods would fall back to the server default. Emails/ops therefore use the **invariant** machine methods today; making them recipient-localised needs the formatting moved inside the renderer's culture scope (`EmailRenderer.CultureScope`).

**Related:** [`datetime-format-single-home`](../architecture/datetime-format-single-home.md), [`nodatime-for-dates`](nodatime-for-dates.md).
