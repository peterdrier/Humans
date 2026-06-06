---
name: Date/time format strings live in one home (HUM0030)
description: HARD RULE. Custom date/time format strings (`.ToString("d MMM yyyy")`, interpolation `{x:MMM d}`, NodaTime `*Pattern.Create("…")`) may appear only inside `Humans.Application.Extensions.DateFormattingExtensions`. Everywhere else, call a named formatter — add one to the home rather than inlining a literal. Enforced by analyzer HUM0030.
---

Custom (multi-character) date/time format strings may live **only** in the single sanctioned home, `Humans.Application.Extensions.DateFormattingExtensions`. Anywhere else in a production assembly (`Humans.Application`/`Domain`/`Infrastructure`/`Web`) a hand-rolled format string is a build **error** (HUM0030).

**Why:** Format strings scattered across layers produce inconsistent display output and ad-hoc one-offs (the same concept rendered four different ways). One home keeps user-facing rendering consistent and localised (display methods use `CurrentCulture`, so month/weekday names follow the request culture) and machine/interchange formats stable (`InvariantCulture`). The home owns **both directions** — display formatters (`ToDisplay*`, culture-ordered), machine formatters (`ToInvariant*`, `ToIso8601`, `ToSepaDateTime`, `ToFileTimestamp`), and parse/format **pattern fields** (`TimeOfDayPattern`, `PlacementDateTimePattern`, iCal patterns, `OpsNoticeDatePattern`).

**How to apply:**

- Need a date rendered? Call a named method on the home (`value.ToDate()`, `.ToWeekdayDayMonth()` for culture display, `.ToInvariantDate()`/`.ToIso8601()` for machine, …). For an `Instant` in a request/view, the ambient overloads in `Humans.Web.Extensions.DateTimeDisplayExtensions` resolve the user's timezone from session.
- No method fits? **Add one to `DateFormattingExtensions`** (display → `CurrentCulture`; CSV/API/filename/JSON → `InvariantCulture`) — never inline the literal at the call site. NodaTime parse/format patterns become named `static readonly *Pattern` fields on the home.
- The analyzer flags `.ToString("<custom>")`, interpolation `{x:<custom>}`, and `NodaTime.Text.*Pattern.Create…("…")` on `DateTime`/`DateTimeOffset`/`DateOnly`/`TimeOnly`/NodaTime value types. Single-char standard specifiers (`"d"`, `"g"`, `"o"`) are allowed; numeric formats (`"N2"`, `"0.##"`) are not in scope (not a date receiver).
- **v1 gaps (not analyzer-enforced):** `DateTime.ParseExact` / composite `string.Format` format strings, and `.cshtml` Razor (Roslyn analyzers don't see Razor). Fix these by hand when you touch them; don't assume the analyzer caught them.

**Related:** [`analyzer-exceptions-via-attributes`](analyzer-exceptions-via-attributes.md), [`display-sort-in-controllers`](display-sort-in-controllers.md), [`universal-enforcement-over-per-section`](universal-enforcement-over-per-section.md).
