# HUM0030 — Analyzer: hand-rolled date/time format strings

- **Status:** Design approved (2026-06-04), pending implementation plan
- **Branch:** `feat/hum0030-datetime-format`
- **Section:** Architecture (cross-cutting analyzer in `Humans.Analyzers`)

## Problem

Date/time formatting is hand-rolled all over the codebase — `dt.ToString("d MMMM yyyy")`,
`$"{monday:MMM d}"`, `LocalDatePattern.Create("uuuu-MM-dd")` — instead of routing through the
project's named formatting extensions. This produces inconsistent display output, scatters format
literals across every layer, and is the single most common formatting mistake an LLM contributor
makes. There is no compile-time guard, so each new occurrence has to be caught by hand in review.

There are already **three** "blessed" formatting homes that themselves hold format literals, which
is its own smell and includes outright duplication (`ToDisplayShiftDate` exists in both the Web and
Application files; the Application copy's comment says it "mirrors the Web-layer extension"):

- `Humans.Web/Extensions/DateTimeDisplayExtensions.cs`
- `Humans.Application/Extensions/DateFormattingExtensions.cs`
- `Humans.Infrastructure/Helpers/EmailDateTimeExtensions.cs`

## Decision

Introduce Roslyn analyzer **HUM0030**: a date/time **format-string literal may appear only inside the
single sanctioned home**, `Humans.Application.Extensions.DateFormattingExtensions`. Everywhere else
in a production assembly it is a build **Error**.

- The home is a **hard-coded constant** in the analyzer — not configurable, not attribute-overridable.
- **No `[Grandfathered]` support and no baseline.** The only sanctioned "exception" is to add a named
  method to the home. There is deliberately no `InvariantCulture`/machine-format escape hatch:
  machine/interchange formats (ISO-8601, SEPA ISO-20022, audit keys, LLM-prompt dates) get **named**
  ISO methods in the home, same as display formats.

### Why one home, and why it can be one

The reason it is currently three is a conflation of two separable concerns:

- **Format-string literals** (`"d MMM yyyy"`, `"ddd MMM d HH:mm"`, `"yyyy-MM-dd"`, etc.) — these are
  layer-agnostic and all move into the Application home.
- **Ambient timezone resolution** (`Initialize(IHttpContextAccessor)`, `GetCurrentUserTimeZone()`,
  `GetUserTimeZone(this ISession)`) — Web-only because it reads the HTTP session. It contains **zero
  format literals**, so it stays in Web and simply drops off the analyzer's allowlist. The ambient
  parameterless overloads (`instant.ToDisplayDate()`) survive in Web by **delegating** into the home.

`Humans.Application.Extensions.DateFormattingExtensions` is the right home: Application is reachable by
both Infrastructure and Web, display formatting is a presentation concern (not a Domain invariant), and
the file already exists (reuse-first).

## Analyzer behaviour

Implemented as a `DiagnosticAnalyzer` over `IOperation`, restricted to production assemblies
(`Humans.Application`, `Humans.Domain`, `Humans.Infrastructure`, `Humans.Web`) and to **`.cs`** source.
Patterned on `ConcurrencyTokenAnalyzer`.

### Flagged shapes

1. **`.ToString("…", …)`** where the receiver type is `System.DateTime`, `System.DateTimeOffset`, or a
   NodaTime value type (`Instant`, `LocalDate`, `LocalTime`, `LocalDateTime`, `ZonedDateTime`,
   `OffsetDateTime`, `OffsetDate`, `OffsetTime`, `Duration`, `Period`) **and** the first argument is a
   string literal of length ≥ 2.
2. **Interpolation** `$"{expr:format}"` (`OperationKind.Interpolation`) where `expr.Type` is one of the
   above types and the format clause literal has length ≥ 2.
3. **NodaTime pattern factories** — a static invocation on a `NodaTime.Text.*Pattern` type whose method
   name starts with `Create` (`Create`, `CreateWithInvariantCulture`, `CreateWithCurrentCulture`) with a
   string-literal first argument. Standard built-in patterns (`LocalDatePattern.Iso`, `.General*`,
   `.ExtendedIso`, …) are property reads, so they are naturally exempt.

### Custom-vs-standard predicate

Flag iff the format literal **length ≥ 2**. A single-character literal (`"d"`, `"g"`, `"o"`, `"s"`,
`"u"`, `"R"`) is a standard .NET/NodaTime format specifier — already culture- or round-trip-correct —
and is exempt in v1. (There are no multi-character standard specifiers, so length ≥ 2 reliably means
"custom".)

### Exemptions

- The home type `Humans.Application.Extensions.DateFormattingExtensions` (hard-coded constant).
- Generated code (`ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None)`).
- `Humans.Infrastructure.Migrations` (namespace + `/Migrations/` path), mirroring `ConcurrencyTokenAnalyzer`.
- Non-production assemblies — so `Humans.Analyzers` itself and test projects do not trip.

### Diagnostic

- **Id:** `HUM0030` · **Category:** `Humans.Architecture` · **Severity:** `Error` · enabled by default.
- **Message (shape):** "Date/time format string '{0}' must not be hand-rolled here. Call a named
  formatter on `Humans.Application.Extensions.DateFormattingExtensions` (or add one there) — it is the
  single sanctioned home for date/time format strings."
- Add the row to `AnalyzerReleases.Unshipped.md`.

## Out of scope (v1)

- `string.Format("{0:…}", dt)` / composite format strings — composite-string parsing plus argument
  type inference is high-effort, low-frequency. Punted.
- Single-character standard format specifiers (see predicate above).

## Known limitation — Razor (`.cshtml`)

Roslyn analyzers run on the C# compilation, **not** on Razor. The many `.ToString("…")` / `$"{d:…}"`
sites in `.cshtml` views will **not** be caught by HUM0030. Accepted for v1:

- The backfill **does** fix `.cshtml` display sites (do it right once).
- **Ongoing** `.cshtml` enforcement is out of v1 scope. The analyzer protects all `.cs` (services,
  controllers, view models, jobs) — where hand-rolled formats actually originate. A lightweight separate
  `.cshtml` check is a possible later follow-up.

## Rollout — fix-all-first, clean build

Ship HUM0030 as **Error** with **zero** violations at merge — no grandfathering, no warning sea.

1. **Consolidate 3 homes → 1.** Move every format literal from `EmailDateTimeExtensions` (Infra, 1
   method) and the format-bearing half of `Web/DateTimeDisplayExtensions` into `DateFormattingExtensions`.
   Delete the now-empty Infra file. Web retains only the format-free timezone seam + delegating ambient
   overloads. Remove the `ToDisplayShiftDate` duplication.
2. **Add named methods** for machine cases lacking one (e.g. `"yyyy-MM-ddTHH:mm:ss"` → `ToIsoDateTimeString`;
   `ToIsoDateString` already exists). Each distinct format that survives gets exactly one named method.
3. **Backfill all `.cs` sites** (~54 raw `.ToString`, ~29 interpolation, ~25 NodaTime pattern — plus the
   `.cshtml` display sites) to call named methods.
4. **Add the analyzer** (HUM0030) as Error + release note.
5. **Build + test green** with no violations.

## Testing

Analyzer unit tests per the existing `Humans.Analyzers` test harness:

- Flags raw `.ToString` custom format on `DateTime` and on a NodaTime type.
- Flags interpolation `$"{d:MMM d}"`.
- Flags `LocalDatePattern.Create("uuuu-MM-dd")`; does **not** flag `LocalDatePattern.Iso`.
- Does **not** flag single-char standard formats (`"d"`, `"g"`, `"o"`).
- Does **not** flag inside `DateFormattingExtensions`, in migrations, or in non-production assemblies.

Solution build (`dotnet build Humans.slnx -v quiet`) and test (`dotnet test Humans.slnx -v quiet`) pass
with the rule live.
