# HUM0030 Date/Time Format-String Analyzer — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Centralise every date/time format-string literal into one home (`Humans.Application.Extensions.DateFormattingExtensions`) and add Roslyn analyzer **HUM0030** that makes a hand-rolled date/time format string anywhere else a build error.

**Architecture:** Consolidate the three existing formatting homes into one; backfill all `.cs` and `.cshtml` call sites to named methods; then add the analyzer last so the solution build is the proof that the backfill is complete (the analyzer auto-applies to every `src/` project via `src/Directory.Build.props`). Ship as **Error**, no grandfathering, clean build.

**Tech Stack:** C# Roslyn `DiagnosticAnalyzer` over `IOperation`, NodaTime, xUnit + AwesomeAssertions (`[HumansFact]`), `AnalyzerTestHarness`.

**Spec:** `docs/superpowers/specs/2026-06-04-datetime-format-analyzer-design.md`

**Ordering invariant:** the analyzer class is added **last** (Phase 4). Until then the build stays green because the analyzer does not yet exist. Every commit must build green (`dotnet build Humans.slnx -v quiet`).

---

## Reference: format-literal → named-method mapping

This is the single source of truth for Phases 1–3. "Exists" = already on a home method after consolidation; "NEW" = method added in Task 3.

| Format literal | Intent | Home method | Status |
|---|---|---|---|
| `d MMM yyyy` | display date | `ToDisplayDate` | exists |
| `d MMMM yyyy` | display long date | `ToDisplayLongDate` | exists |
| `d MMMM yyyy HH:mm` | display long datetime | `ToDisplayLongDateTime` | exists |
| `d MMM yyyy HH:mm` | display datetime | `ToDisplayDateTime` | exists |
| `MMM d, yyyy` | compact date | `ToDisplayCompactDate` | exists |
| `MMM d, yyyy HH:mm` | compact datetime | `ToDisplayCompactDateTime` | exists |
| `MMM yyyy` | month + year | `ToDisplayMonthYear` | exists |
| `d MMM` | day + month | `ToDisplayDayMonth` | exists |
| `ddd MMM d HH:mm` | short zoned datetime | `ToDisplayShortDateTime` | exists |
| `MMM d HH:mm` | short month-day-time | `ToDisplayShortMonthDayTime` | exists |
| `H:mm` | time (no pad) | `ToDisplayTime` | exists |
| `MMMM d, yyyy` | invariant long date (email) | `ToInvariantLongDate` | exists (moved from Email) |
| `yyyy-MM-dd` / `uuuu-MM-dd` | ISO date | `ToIsoDateString` | exists |
| `yyyy-MM-dd HH:mm:ss` | audit timestamp | `ToAuditTimestamp` | exists |
| `yyyy-MM-dd HH:mm` | audit minute timestamp | `ToAuditMinuteTimestamp` | exists |
| `MMM d` | month + day (e.g. week label) | `ToDisplayMonthDay` | **NEW** |
| `ddd d MMM` | weekday + day + month | `ToDisplayWeekdayDayMonth` | **NEW** |
| `dddd d MMMM` | full weekday + day + month | `ToDisplayFullWeekdayDayMonth` | **NEW** |
| `MMMM` | month name only | `ToDisplayMonthName` | **NEW** |
| `HH:mm` | time, zero-padded 24h | `ToDisplayTime24` | **NEW** |
| `yyyy-MM-ddTHH:mm:ss` | ISO datetime (SEPA) | `ToIsoDateTimeString` | **NEW** |
| `uuuu-MM-dd HH:mm 'UTC'` | invariant UTC minute label | `ToInvariantUtcMinuteLabel` | **NEW** |
| `HHmm` / `HHmmss` / `yyyyMMddHHmmss` / `yyyy-MM-dd-HHmm` / `yyyy-MM-dd HHmm` | filename/export stamp | `ToFileStamp` (+ overloads) | **NEW** |

**Per-site judgement rule:** if a literal appears in user-facing output (a view, a label, an email body) use the display method; if it appears in a filename, CSV/XLSX column, XML payload, JSON, audit key, or LLM prompt use the ISO/invariant/file method. When a site's existing format does not exactly match an existing display method (e.g. zero-padded vs non-padded time), prefer the method whose literal matches exactly — never silently change the rendered output.

---

## Phase 1 — Consolidate to one home

### Task 1: Move pure format methods Web → Application home

**Files:**
- Modify: `src/Humans.Application/Extensions/DateFormattingExtensions.cs`
- Modify: `src/Humans.Web/Extensions/DateTimeDisplayExtensions.cs`

- [ ] **Step 1: Move every format-literal-bearing method that does NOT need the HTTP session** from `DateTimeDisplayExtensions` into `DateFormattingExtensions`, verbatim (signatures and bodies unchanged). These are the methods whose receiver is `DateTime`/`DateTime?`/`LocalDate`/`LocalTime`/`ZonedDateTime`/`ZonedDateTime?` or an `Instant` overload that takes an explicit `DateTimeZone`:

  `ToDisplayDate(DateTime)`, `ToDisplayDate(DateTime?)`, `ToDisplayDate(LocalDate)`, `ToDisplayDate(LocalDate?)`,
  `ToDisplayLongDate(DateTime/DateTime?)`, `ToDisplayLongDateTime(DateTime/DateTime?)`,
  `ToDisplayDateTime(DateTime/DateTime?)`, `ToDisplayCompactDate(DateTime/DateTime?)`,
  `ToDisplayCompactDateTime(DateTime/DateTime?)`, `ToDisplayCompactDayTime(DateTime)`,
  `ToDisplayMonthYear(DateTime/DateTime?)`, `ToDisplayDayMonth(DateTime/DateTime?)`,
  `ToDisplayTime(DateTime/DateTime?)`, `ToDisplayTime(LocalTime)`,
  `ToDisplayShortDateTime(ZonedDateTime/ZonedDateTime?)`, `ToDisplayShortMonthDayTime(ZonedDateTime/ZonedDateTime?)`,
  `ToAuditTimestamp(DateTime)`, `ToAuditMinuteTimestamp(DateTime)`, `ToDisplayGeneralDateTime(DateTime)`,
  and the explicit-zone `Instant` overloads: `ToDisplayDate(Instant, DateTimeZone)`, `ToDisplayDateTime(Instant, DateTimeZone)`, `ToDisplayCompactDate(Instant, DateTimeZone)`, `ToDisplayCompactDateTime(Instant, DateTimeZone)`, `ToDisplayCompactDayTime(Instant, DateTimeZone)`, `ToDisplayTime(Instant, DateTimeZone)`.

  `DateFormattingExtensions` already has `ToDisplayShiftDate(LocalDate)`, `ToIsoDateString`, `ToInvariantInstantString` — keep those, and delete the now-duplicate `ToDisplayShiftDate` from the Web file (its comment already says it mirrors this one). Add `using System.Globalization;` and the existing `using NodaTime;` to the home file as needed.

- [ ] **Step 2: Verify the home file has no missing references** (it must not reference `IHttpContextAccessor`, `ISession`, or `TimezoneApiController` — those stay in Web).

Run: `dotnet build src/Humans.Application -v quiet`
Expected: build succeeds.

- [ ] **Step 3: Commit**

```bash
git add src/Humans.Application/Extensions/DateFormattingExtensions.cs src/Humans.Web/Extensions/DateTimeDisplayExtensions.cs
git commit -m "refactor(formatting): move pure date/time format methods into the Application home"
```

### Task 2: Reduce the Web file to a literal-free timezone seam

**Files:**
- Modify: `src/Humans.Web/Extensions/DateTimeDisplayExtensions.cs`

- [ ] **Step 1:** The Web file must keep ONLY: `Initialize(IHttpContextAccessor)`, `GetCurrentUserTimeZone()`, `GetUserTimeZone(this ISession, string?)`, and the **ambient parameterless `Instant`/`Instant?` overloads** (`ToDisplayDate(Instant)`, `ToDisplayDate(Instant?)`, `ToDisplayDateShort(Instant/Instant?)`, `ToDisplayLongDate(Instant/Instant?)`, `ToDisplayLongDateTime(Instant/Instant?)`, `ToDisplayDateTime(Instant/Instant?)`, `ToDisplayCompactDate(Instant/Instant?)`, `ToDisplayCompactDateTime(Instant/Instant?)`, `ToDisplayCompactDayTime(Instant)`, `ToAuditTimestamp(Instant/Instant?)`, `ToAuditMinuteTimestamp(Instant/Instant?)`). Rewrite each ambient overload so it resolves the zone then delegates into the home — it must contain **no string literal**. Example:

```csharp
// before (had inline literal via the DateTime overload that now lives in Application):
public static string ToDisplayDateShort(this Instant value) =>
    value.InZone(GetCurrentUserTimeZone()).ToDateTimeUnspecified().ToString("d MMM", CultureInfo.CurrentCulture);

// after (delegates to the home; no literal in Web):
public static string ToDisplayDateShort(this Instant value) =>
    value.InZone(GetCurrentUserTimeZone()).ToDateTimeUnspecified().ToDisplayDayMonth();
```

- [ ] **Step 2:** Add `using Humans.Application.Extensions;` to the Web file (the ambient overloads now call the home methods). Confirm zero remaining format literals:

Run: `grep -nE 'ToString\("|:[^"]*[yMdHhms]{2,}' src/Humans.Web/Extensions/DateTimeDisplayExtensions.cs`
Expected: no output.

- [ ] **Step 3: Commit**

```bash
git add src/Humans.Web/Extensions/DateTimeDisplayExtensions.cs
git commit -m "refactor(formatting): reduce Web DateTimeDisplayExtensions to a literal-free timezone seam"
```

### Task 3: Add the NEW named methods to the home

**Files:**
- Modify: `src/Humans.Application/Extensions/DateFormattingExtensions.cs`

- [ ] **Step 1: Add the methods below** (cultures match the intent: display = `CurrentCulture`; machine/file = `InvariantCulture`). For NodaTime receivers add `LocalDate`/`ZonedDateTime` overloads where the call sites need them (see Phase 2 file lists).

```csharp
// --- display ---
public static string ToDisplayMonthDay(this DateTime value) =>
    value.ToString("MMM d", CultureInfo.CurrentCulture);

public static string ToDisplayWeekdayDayMonth(this DateTime value) =>
    value.ToString("ddd d MMM", CultureInfo.CurrentCulture);

public static string ToDisplayFullWeekdayDayMonth(this DateTime value) =>
    value.ToString("dddd d MMMM", CultureInfo.CurrentCulture);

public static string ToDisplayMonthName(this DateTime value) =>
    value.ToString("MMMM", CultureInfo.CurrentCulture);

public static string ToDisplayTime24(this DateTime value) =>
    value.ToString("HH:mm", CultureInfo.CurrentCulture);

// --- machine / interchange ---
public static string ToIsoDateTimeString(this DateTime value) =>
    value.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);

public static string ToInvariantUtcMinuteLabel(this DateTime value) =>
    value.ToString("uuuu-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);

// --- filename / export stamps ---
public static string ToFileStamp(this DateTime value) =>
    value.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);

public static string ToFileStampMinute(this DateTime value) =>
    value.ToString("yyyy-MM-dd-HHmm", CultureInfo.InvariantCulture);
```

  Add LocalDate/ZonedDateTime overloads only for the literals actually used on those types in Phase 2 (e.g. `ToDisplayWeekdayDayMonth(this LocalDate value) => value.ToString("ddd d MMM", null);` if a NodaTime site needs it). Do not add unused overloads (YAGNI).

- [ ] **Step 2: Build**

Run: `dotnet build src/Humans.Application -v quiet`
Expected: build succeeds.

- [ ] **Step 3: Commit**

```bash
git add src/Humans.Application/Extensions/DateFormattingExtensions.cs
git commit -m "feat(formatting): add named formatters for weekday/month/file/ISO date variants"
```

### Task 4: Delete the Infrastructure email helper; fix usings

**Files:**
- Delete: `src/Humans.Infrastructure/Helpers/EmailDateTimeExtensions.cs`
- Modify: callers of `ToInvariantLongDate` (find in Step 1)

- [ ] **Step 1:** `ToInvariantLongDate(DateTime)` was moved to the home in Task 1. Find its callers and the files importing the old namespace:

```bash
grep -rln 'ToInvariantLongDate' src --include=*.cs --include=*.cshtml
grep -rln 'using Humans.Infrastructure.Helpers' src --include=*.cs
```

- [ ] **Step 2:** For each caller, ensure `using Humans.Application.Extensions;` is present; remove `using Humans.Infrastructure.Helpers;` where it was only for this helper. Delete `EmailDateTimeExtensions.cs`.

- [ ] **Step 3:** Across the 22 files importing `Humans.Web.Extensions`, the moved pure methods (Task 1) now live in `Humans.Application.Extensions`. Build the solution; for every `CS1061`/`CS0103` about a `ToDisplay*`/`ToAudit*` method, add `using Humans.Application.Extensions;` to that file (ReSharper "import namespace" or add by hand). The ambient `Instant` overloads still resolve from `Humans.Web.Extensions`, so keep that using where Instant overloads are used.

Run: `dotnet build Humans.slnx -v quiet`
Expected: build succeeds (no analyzer yet).

- [ ] **Step 4: Run tests**

Run: `dotnet test Humans.slnx -v quiet`
Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "refactor(formatting): delete EmailDateTimeExtensions; route callers through the single home"
```

---

## Phase 2 — Backfill `.cs` sites

Apply the mapping table to every flagged `.cs` site. Re-enumerate at the start of each task:

```bash
grep -rEn '\.ToString\("[^"]*(yyyy|uuuu|MMM|ddd|dddd|MMMM|HH:mm|H:mm)[^"]*"|(LocalDate|LocalTime|LocalDateTime|Instant|ZonedDateTime|OffsetDateTime|Duration|Period)Pattern\.Create|\{[^{}]*:[^{}]*(yyyy|uuuu|MMM|ddd|HH:?mm)[^{}]*\}' <files> --include=*.cs
```

**Transformation recipe (every site):** replace the literal `ToString`/interpolation/`Pattern.Create` with the mapped named method; add `using Humans.Application.Extensions;` if missing; if no method matches exactly, add one to the home (Task 3 pattern) rather than inlining the literal. Do not change rendered output.

Example — `BudgetService.cs` week label:
```csharp
// before
return $"{monday.ToString("MMM d", null)}–{sunday.ToString("MMM d", null)}";
// after
return $"{monday.ToDisplayMonthDay()}–{sunday.ToDisplayMonthDay()}";
```
Example — `SepaPaymentFileBuilder.cs` (machine, ISO-20022):
```csharp
// before
var creDtTm = generatedAt.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
// after
var creDtTm = generatedAt.ToIsoDateTimeString();
```

### Task 5: Application services

**Files (modify):** `Services/Shifts/RotaCoordinatorMessageService.cs`, `Services/Shifts/ShiftSignupService.cs`, `Services/Expenses/SepaPaymentFileBuilder.cs`, `Services/Events/EventService.cs`, `Services/Calendar/CalendarService.cs`, `Services/Tickets/TicketingBudgetService.cs`, `Services/Tickets/TicketQueryService.cs`, `Services/Teams/TeamService.cs`, `Services/Email/EmailMessageFactory.cs`, `Services/CityPlanning/CityPlanningService.cs`, `Services/Budget/BudgetService.cs`, `Services/AuditLog/AuditEvent.cs` (all under `src/Humans.Application/`).

- [ ] **Step 1:** Apply the recipe to every flagged site in these files.
- [ ] **Step 2:** Build + test.

Run: `dotnet build src/Humans.Application -v quiet && dotnet test tests/Humans.Application.Tests -v quiet`
Expected: succeeds; tests pass.

- [ ] **Step 3: Commit** `git commit -am "refactor(formatting): route Application services through named date formatters"`

### Task 6: Infrastructure

**Files (modify):** `src/Humans.Infrastructure/Repositories/Budget/BudgetRepository.cs`, `src/Humans.Infrastructure/Services/Agent/AgentPromptAssembler.cs`, `src/Humans.Infrastructure/Services/Agent/AgentToolDispatcher.cs`, `src/Humans.Infrastructure/Jobs/TermRenewalReminderJob.cs`.

Note `AgentPromptAssembler` uses NodaTime `"uuuu-MM-dd"` and `"uuuu-MM-dd HH:mm 'UTC'"` → `ToIsoDateString` / `ToInvariantUtcMinuteLabel` (add NodaTime overloads to the home if the receiver is a NodaTime type).

- [ ] **Step 1:** Apply the recipe.
- [ ] **Step 2:** Build + test. Run: `dotnet build src/Humans.Infrastructure -v quiet && dotnet test tests/Humans.Integration.Tests -v quiet`
- [ ] **Step 3: Commit** `git commit -am "refactor(formatting): route Infrastructure through named date formatters"`

### Task 7: Web controllers, view models, CSV/XLSX writers

**Files (modify):** `Controllers/EventsController.cs` (11 sites), `Controllers/EventsExportController.cs`, `Controllers/ShiftsController.cs`, `Controllers/CantinaController.cs`, `Controllers/TeamController.cs`, `Controllers/EventsDashboardController.cs`, `Controllers/CityPlanningController.cs`, `Controllers/AccountController.cs`, `Models/ProfileViewModel.cs` (5 sites), `Models/ProfileCardViewModel.cs`, `Models/VolunteerTracking/VolunteerTrackingXlsxBuilder.cs`, `Cantina/CantinaRosterCsvWriter.cs`, `Cantina/CantinaDailyMatrixCsvWriter.cs` (all under `src/Humans.Web/`).

- [ ] **Step 1:** Apply the recipe. CSV/XLSX writers and export controllers are machine output → ISO/file methods; controllers building display labels → display methods.
- [ ] **Step 2:** Build + test. Run: `dotnet build src/Humans.Web -v quiet && dotnet test tests/Humans.Web.Tests -v quiet`
- [ ] **Step 3: Commit** `git commit -am "refactor(formatting): route Web controllers/models/writers through named date formatters"`

---

## Phase 3 — Backfill `.cshtml` views

Same recipe. In Razor, `@using Humans.Application.Extensions` may be added to `src/Humans.Web/Views/_ViewImports.cshtml` once so every view sees the home methods — check whether it (or `Humans.Web.Extensions`) is already there:

```bash
grep -nE 'Humans.(Web|Application).Extensions' src/Humans.Web/Views/_ViewImports.cshtml
```
Add `@using Humans.Application.Extensions` to `_ViewImports.cshtml` if missing (one edit covers all views).

### Task 8: Calendar + Events + Shift views

**Files (modify):** all `.cshtml` under `Views/Calendar/`, `Views/Events/`, `Views/EventsModeration/`, `Views/EventsExport/`, `Views/Shared/_BuildStrikeRotaTable.cshtml`, `Views/ShiftDashboard/Index.cshtml`, `Views/ShiftAdmin/Index.cshtml`.

- [ ] **Step 1:** Apply the recipe to every flagged site (re-enumerate with the Phase 2 grep over these paths, `--include=*.cshtml`).
- [ ] **Step 2:** Build. Run: `dotnet build src/Humans.Web -v quiet` Expected: succeeds (Razor compiles).
- [ ] **Step 3: Commit** `git commit -am "refactor(formatting): route Calendar/Events/Shift views through named date formatters"`

### Task 9: Remaining views

**Files (modify):** all remaining flagged `.cshtml` — `Views/VolunteerTracking/*`, `Views/Finance/*`, `Views/Feedback/*`, `Views/CityPlanning/Admin.cshtml`, `Views/Team*/`, `Views/Store/Order.cshtml`, `Views/Notifications/_NotificationRow.cshtml`, `Views/Expenses/Detail.cshtml`, `Views/Budget/CategoryDetail.cshtml`, `Views/Cantina/Roster.cshtml`, `Views/CampAdmin/Index.cshtml`, `Views/EarlyEntryRoster/Index.cshtml`, `Views/Shared/_ProfileCard.cshtml`, `Views/Shared/Components/TicketStub/Default.cshtml`.

- [ ] **Step 1:** Apply the recipe.
- [ ] **Step 2:** Build. Run: `dotnet build src/Humans.Web -v quiet`
- [ ] **Step 3: Verify no residual literals remain anywhere outside the home:**

```bash
grep -rEn '\.ToString\("[^"]*(yyyy|uuuu|MMM|ddd|dddd|MMMM|HH:mm|H:mm)[^"]*"|(LocalDate|LocalTime|LocalDateTime|Instant|ZonedDateTime|OffsetDateTime|Duration|Period)Pattern\.Create|\{[^{}]*:[^{}]*(yyyy|uuuu|MMM|ddd|HH:?mm)[^{}]*\}' src --include=*.cs --include=*.cshtml | grep -viE 'DateFormattingExtensions|/Migrations/'
```
Expected: no output (the only allowed home is `DateFormattingExtensions`).

- [ ] **Step 4: Commit** `git commit -am "refactor(formatting): route remaining views through named date formatters"`

---

## Phase 4 — Add analyzer HUM0030 (TDD) and enable

### Task 10: Write the failing analyzer tests

**Files:**
- Create: `tests/Humans.Analyzers.Tests/DateTimeFormatStringAnalyzerTests.cs`

- [ ] **Step 1: Write the tests** (mirror `ConcurrencyTokenAnalyzerTests` conventions — `[HumansFact]`, `AnalyzerTestHarness.RunAsync`, AwesomeAssertions). `System.DateTime`/`DateOnly` are platform types (no stub); NodaTime cases stub minimal types inline.

```csharp
using AwesomeAssertions;

namespace Humans.Analyzers.Tests;

public sealed class DateTimeFormatStringAnalyzerTests
{
    private const string NodaStub = """
        namespace NodaTime
        {
            public struct LocalDate { public override string ToString() => ""; public string ToString(string p, System.IFormatProvider f) => ""; }
        }
        namespace NodaTime.Text
        {
            public sealed class LocalDatePattern
            {
                public static LocalDatePattern Iso => new();
                public static LocalDatePattern Create(string patternText, System.Globalization.CultureInfo culture) => new();
            }
        }
        """;

    private static bool IsHum0030(Microsoft.CodeAnalysis.Diagnostic d) =>
        string.Equals(d.Id, DateTimeFormatStringAnalyzer.DiagnosticId, System.StringComparison.Ordinal);

    [HumansFact]
    public async Task Fires_on_DateTime_ToString_custom_format()
    {
        var source = """
            using System;
            namespace Humans.Web.Models
            {
                public class Vm { public string F(DateTime d) => d.ToString("d MMMM yyyy"); }
            }
            """;
        var diagnostics = await AnalyzerTestHarness.RunAsync(new DateTimeFormatStringAnalyzer(), "Humans.Web", source);
        diagnostics.Should().ContainSingle(IsHum0030);
    }

    [HumansFact]
    public async Task Fires_on_interpolation_custom_format()
    {
        var source = """
            using System;
            namespace Humans.Application.Services
            {
                public class S { public string F(DateTime d) => $"{d:MMM d}"; }
            }
            """;
        var diagnostics = await AnalyzerTestHarness.RunAsync(new DateTimeFormatStringAnalyzer(), "Humans.Application", source);
        diagnostics.Should().ContainSingle(IsHum0030);
    }

    [HumansFact]
    public async Task Fires_on_NodaTime_Pattern_Create()
    {
        var source = NodaStub + """

            namespace Humans.Infrastructure.Services
            {
                public class S { public object F() => NodaTime.Text.LocalDatePattern.Create("uuuu-MM-dd", null); }
            }
            """;
        var diagnostics = await AnalyzerTestHarness.RunAsync(new DateTimeFormatStringAnalyzer(), "Humans.Infrastructure", source);
        diagnostics.Should().ContainSingle(IsHum0030);
    }

    [HumansFact]
    public async Task Does_not_fire_on_NodaTime_standard_Iso_pattern()
    {
        var source = NodaStub + """

            namespace Humans.Infrastructure.Services
            {
                public class S { public object F() => NodaTime.Text.LocalDatePattern.Iso; }
            }
            """;
        var diagnostics = await AnalyzerTestHarness.RunAsync(new DateTimeFormatStringAnalyzer(), "Humans.Infrastructure", source);
        diagnostics.Should().BeEmpty();
    }

    [HumansFact]
    public async Task Does_not_fire_on_single_char_standard_format()
    {
        var source = """
            using System;
            namespace Humans.Web.Models
            {
                public class Vm { public string F(DateTime d) => d.ToString("g") + d.ToString("o") + d.ToString("d"); }
            }
            """;
        var diagnostics = await AnalyzerTestHarness.RunAsync(new DateTimeFormatStringAnalyzer(), "Humans.Web", source);
        diagnostics.Should().BeEmpty();
    }

    [HumansFact]
    public async Task Does_not_fire_on_non_date_ToString()
    {
        var source = """
            namespace Humans.Web.Models
            {
                public class Vm { public string F(decimal x, System.Guid g) => x.ToString("N2") + g.ToString("D"); }
            }
            """;
        var diagnostics = await AnalyzerTestHarness.RunAsync(new DateTimeFormatStringAnalyzer(), "Humans.Web", source);
        diagnostics.Should().BeEmpty();
    }

    [HumansFact]
    public async Task Does_not_fire_inside_the_home_type()
    {
        var source = """
            using System;
            namespace Humans.Application.Extensions
            {
                public static class DateFormattingExtensions
                {
                    public static string ToDisplayLongDate(this DateTime v) => v.ToString("d MMMM yyyy");
                }
            }
            """;
        var diagnostics = await AnalyzerTestHarness.RunAsync(new DateTimeFormatStringAnalyzer(), "Humans.Application", source);
        diagnostics.Should().BeEmpty();
    }

    [HumansFact]
    public async Task Does_not_fire_in_migration_namespace()
    {
        var source = """
            using System;
            namespace Humans.Infrastructure.Migrations
            {
                public class M { public string F(DateTime d) => d.ToString("yyyy-MM-dd"); }
            }
            """;
        var diagnostics = await AnalyzerTestHarness.RunAsync(new DateTimeFormatStringAnalyzer(), "Humans.Infrastructure", source);
        diagnostics.Should().BeEmpty();
    }

    [HumansFact]
    public async Task Does_not_fire_outside_production_assemblies()
    {
        var source = """
            using System;
            namespace Whatever
            {
                public class C { public string F(DateTime d) => d.ToString("d MMMM yyyy"); }
            }
            """;
        var diagnostics = await AnalyzerTestHarness.RunAsync(new DateTimeFormatStringAnalyzer(), "SomeTestAssembly", source);
        diagnostics.Should().BeEmpty();
    }
}
```

- [ ] **Step 2: Run — expect compile failure** (analyzer type does not exist yet).

Run: `dotnet test tests/Humans.Analyzers.Tests -v quiet`
Expected: FAIL — `DateTimeFormatStringAnalyzer` not found.

### Task 11: Implement the analyzer

**Files:**
- Create: `src/Humans.Analyzers/DateTimeFormatStringAnalyzer.cs`

- [ ] **Step 1: Write the analyzer:**

```csharp
using System;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Humans.Analyzers;

/// <summary>
/// HUM0030 -- date/time format-string literals may live only in the single sanctioned
/// formatting home (Humans.Application.Extensions.DateFormattingExtensions). Anywhere else a
/// hand-rolled custom format string is forbidden; call a named formatter on the home instead.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class DateTimeFormatStringAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "HUM0030";

    private const string HomeTypeFullName = "Humans.Application.Extensions.DateFormattingExtensions";
    private const string MigrationsNamespacePrefix = "Humans.Infrastructure.Migrations";

    private static readonly LocalizableString Title = "Hand-rolled date/time format string";

    private static readonly LocalizableString MessageFormat =
        "Date/time format string \"{0}\" must not be hand-rolled here. Call a named formatter on " +
        "Humans.Application.Extensions.DateFormattingExtensions (or add one there) — it is the single " +
        "sanctioned home for date/time format strings.";

    public static readonly DiagnosticDescriptor Rule = new(
        id: DiagnosticId,
        title: Title,
        messageFormat: MessageFormat,
        category: AnalyzerCategories.Architecture,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description:
            "Custom (multi-character) date/time format strings on DateTime/DateTimeOffset/DateOnly/TimeOnly " +
            "or NodaTime value types, custom interpolation format clauses, and NodaTime *Pattern.Create(...) " +
            "calls must route through the named extensions in the single formatting home.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

    private static readonly ImmutableHashSet<string> ProductionAssemblies =
        ImmutableHashSet.Create(
            StringComparer.Ordinal,
            "Humans.Application", "Humans.Domain", "Humans.Infrastructure", "Humans.Web");

    private static readonly string[] TargetTypeMetadataNames =
    [
        "System.DateTime", "System.DateTimeOffset", "System.DateOnly", "System.TimeOnly",
        "NodaTime.Instant", "NodaTime.LocalDate", "NodaTime.LocalTime", "NodaTime.LocalDateTime",
        "NodaTime.ZonedDateTime", "NodaTime.OffsetDateTime", "NodaTime.OffsetDate", "NodaTime.OffsetTime",
        "NodaTime.Duration", "NodaTime.Period", "NodaTime.YearMonth", "NodaTime.AnnualDate",
    ];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(OnCompilationStart);
    }

    private static void OnCompilationStart(CompilationStartAnalysisContext context)
    {
        if (!ProductionAssemblies.Contains(context.Compilation.Assembly.Name))
            return;

        var targets = ImmutableHashSet.CreateBuilder<INamedTypeSymbol>(SymbolEqualityComparer.Default);
        foreach (var name in TargetTypeMetadataNames)
        {
            var symbol = context.Compilation.GetTypeByMetadataName(name);
            if (symbol is not null)
                targets.Add(symbol);
        }
        var targetTypes = targets.ToImmutable();
        if (targetTypes.IsEmpty)
            return;

        context.RegisterOperationAction(ctx => AnalyzeInvocation(ctx, targetTypes), OperationKind.Invocation);
        context.RegisterOperationAction(ctx => AnalyzeInterpolation(ctx, targetTypes), OperationKind.Interpolation);
    }

    private static void AnalyzeInvocation(OperationAnalysisContext context, ImmutableHashSet<INamedTypeSymbol> targetTypes)
    {
        if (IsExempt(context.ContainingSymbol, context.Operation.Syntax.SyntaxTree.FilePath))
            return;

        var op = (IInvocationOperation)context.Operation;
        var method = op.TargetMethod;

        // <dateTimeValue>.ToString("<custom format>", ...)
        if (method.Name == "ToString" &&
            Unwrap(op.Instance?.Type) is { } receiver && targetTypes.Contains(receiver) &&
            method.Parameters.Length >= 1 &&
            method.Parameters[0].Type.SpecialType == SpecialType.System_String &&
            op.Arguments.Length >= 1 &&
            TryGetCustomFormat(op.Arguments[0].Value, out var fmt))
        {
            Report(context, op.Syntax.GetLocation(), fmt);
            return;
        }

        // NodaTime.Text.*Pattern.Create*("<literal>")
        if (method.IsStatic &&
            method.Name.StartsWith("Create", StringComparison.Ordinal) &&
            method.ContainingType is { } ct &&
            ct.Name.EndsWith("Pattern", StringComparison.Ordinal) &&
            string.Equals(ct.ContainingNamespace?.ToDisplayString(), "NodaTime.Text", StringComparison.Ordinal) &&
            op.Arguments.Length >= 1 &&
            TryGetCustomFormat(op.Arguments[0].Value, out var patternText))
        {
            Report(context, op.Syntax.GetLocation(), patternText);
        }
    }

    private static void AnalyzeInterpolation(OperationAnalysisContext context, ImmutableHashSet<INamedTypeSymbol> targetTypes)
    {
        if (IsExempt(context.ContainingSymbol, context.Operation.Syntax.SyntaxTree.FilePath))
            return;

        var op = (IInterpolationOperation)context.Operation;
        if (op.FormatString is null)
            return;
        if (Unwrap(op.Expression.Type) is not { } exprType || !targetTypes.Contains(exprType))
            return;
        if (TryGetCustomFormat(op.FormatString, out var fmt))
            Report(context, op.Syntax.GetLocation(), fmt);
    }

    private static INamedTypeSymbol? Unwrap(ITypeSymbol? type)
    {
        if (type is not INamedTypeSymbol named)
            return null;
        if (named.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T &&
            named.TypeArguments.FirstOrDefault() is INamedTypeSymbol arg)
            return arg;
        return named;
    }

    private static bool TryGetCustomFormat(IOperation operation, out string format)
    {
        format = "";
        var constant = operation.ConstantValue;
        if (!constant.HasValue || constant.Value is not string s)
            return false;
        if (s.Length < 2) // single-char standard specifiers ("d","g","o",...) are allowed in v1
            return false;
        format = s;
        return true;
    }

    private static void Report(OperationAnalysisContext context, Location location, string format) =>
        context.ReportDiagnostic(Diagnostic.Create(Rule, location, format));

    private static bool IsExempt(ISymbol? containingSymbol, string? filePath)
    {
        for (var type = containingSymbol?.ContainingType; type is not null; type = type.ContainingType)
        {
            if (string.Equals(type.ToDisplayString(), HomeTypeFullName, StringComparison.Ordinal))
                return true;
        }

        var ns = containingSymbol?.ContainingNamespace?.ToDisplayString();
        if (ns is not null && ns.StartsWith(MigrationsNamespacePrefix, StringComparison.Ordinal))
            return true;

        if (!string.IsNullOrEmpty(filePath) &&
            filePath!.Replace('\\', '/').Contains("/Humans.Infrastructure/Migrations/", StringComparison.Ordinal))
            return true;

        return false;
    }
}
```

- [ ] **Step 2: Run the analyzer tests**

Run: `dotnet test tests/Humans.Analyzers.Tests -v quiet`
Expected: PASS (all `DateTimeFormatStringAnalyzerTests`).

- [ ] **Step 3: Commit**

```bash
git add src/Humans.Analyzers/DateTimeFormatStringAnalyzer.cs tests/Humans.Analyzers.Tests/DateTimeFormatStringAnalyzerTests.cs
git commit -m "feat(analyzer): HUM0030 forbid hand-rolled date/time format strings"
```

### Task 12: Register the rule + prove the solution is clean

**Files:**
- Modify: `src/Humans.Analyzers/AnalyzerReleases.Unshipped.md`
- Modify: `docs/architecture/code-analysis.md`

- [ ] **Step 1: Add the release row** to `AnalyzerReleases.Unshipped.md` under `### New Rules`:

```
HUM0030 | Humans.Architecture   | Error    | Date/time format-string literal used outside the single sanctioned home (Humans.Application.Extensions.DateFormattingExtensions)
```

- [ ] **Step 2: Document the rule** in `docs/architecture/code-analysis.md` (HUMxxxx catalogue) — one row matching the others.

- [ ] **Step 3: Build the whole solution** — the analyzer now runs on every `src/` project. A green build proves the Phase 2–3 backfill was complete.

Run: `dotnet build Humans.slnx -v quiet`
Expected: build succeeds with **zero HUM0030 errors**. If any HUM0030 error appears, fix that straggler with the mapping recipe and rebuild.

- [ ] **Step 4: Full test run**

Run: `dotnet test Humans.slnx -v quiet`
Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add src/Humans.Analyzers/AnalyzerReleases.Unshipped.md docs/architecture/code-analysis.md
git commit -m "feat(analyzer): register HUM0030 release note and catalogue entry"
```

### Task 13: Capture the durable rule in repo memory

**Files:**
- Create: `memory/architecture/datetime-format-single-home.md`
- Modify: `memory/INDEX.md`

- [ ] **Step 1:** Add a one-rule atom (per `memory/META.md` format): date/time format strings live only in `Humans.Application.Extensions.DateFormattingExtensions`; enforced by HUM0030; add a named method rather than inlining a literal; analyzer covers `.cs` only (Razor not enforced). Add the matching `INDEX.md` line in the same commit.

- [ ] **Step 2: Commit** `git commit -m "docs(memory): record the single date/time formatting home rule (HUM0030)"`

---

## Self-review notes

- **Spec coverage:** one-home consolidation (Tasks 1–4), named machine methods (Task 3), backfill `.cs` (Tasks 5–7) and `.cshtml` (Tasks 8–9), analyzer with the three flagged shapes + single-char exemption + home/migration/non-production exemptions (Tasks 10–11), Error severity + release note (Task 12), `.cshtml` limitation acknowledged (analyzer is `.cs`-only; views fixed in backfill but not enforced going forward). All covered.
- **Clean-build ordering:** analyzer added last; every commit builds green; Task 12 Step 3 is the completeness proof.
- **Type consistency:** method names in the mapping table match the code in Task 3 and the examples in Phases 2–3 (`ToDisplayMonthDay`, `ToIsoDateTimeString`, `ToInvariantUtcMinuteLabel`, `ToFileStamp`, `ToDisplayTime24`, `ToDisplayWeekdayDayMonth`, `ToDisplayFullWeekdayDayMonth`, `ToDisplayMonthName`).
- **Known limitation (carried from spec):** Razor `.cshtml` is not analyzer-enforced; v1 fixes those sites once and leaves ongoing enforcement as a possible follow-up.
