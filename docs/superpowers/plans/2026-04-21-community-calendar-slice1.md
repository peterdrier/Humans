# Community Calendar (Slice 1 / v1) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the v1 community calendar: a new Calendar section with month + agenda views, standalone events (single and recurring via RFC 5545 RRULE), owned by Teams, editable by team coordinators + Admin, visible to all logged-in humans.

**Architecture:** New `CalendarEvent` + `CalendarEventException` entities owned by a new `CalendarService`. Recurrence expanded in memory via `Ical.Net` against an IANA `RecurrenceTimezone` (default `Europe/Madrid`); occurrences rendered in viewer's browser TZ. Authorization via a resource-based handler against the owning `Team`. `IMemoryCache` caches the full active-events set; invalidated on writes.

**Tech Stack:** ASP.NET Core 10 MVC, EF Core, PostgreSQL, NodaTime, `Ical.Net` (new — RFC 5545 library, MIT), xUnit + FluentAssertions, Razor views.

**Source spec:** `docs/superpowers/specs/2026-04-21-community-calendar-design.md` (PR #238)

**Scope check:** Slice 1 only. Post-v1 slices (module aggregation, audience scoping, iCal feed, public view, notifications, RSVP) are explicitly deferred and will each get their own plan.

**Worktree:** Execute this plan in the existing worktree `H:\source\Humans\.worktrees\design-community-calendar-513` on branch `design/community-calendar-513`. (The design doc is already committed there; implementation commits land on the same branch and ship via PR #238.) Alternatively create a new worktree off `origin/main` if preferred — the spec file can be cherry-picked over.

---

## File Structure

**New files:**

- `src/Humans.Domain/Entities/CalendarEvent.cs`
- `src/Humans.Domain/Entities/CalendarEventException.cs`
- `src/Humans.Application/Interfaces/ICalendarService.cs`
- `src/Humans.Application/DTOs/Calendar/CreateCalendarEventDto.cs`
- `src/Humans.Application/DTOs/Calendar/UpdateCalendarEventDto.cs`
- `src/Humans.Application/DTOs/Calendar/OverrideOccurrenceDto.cs`
- `src/Humans.Application/DTOs/Calendar/CalendarOccurrence.cs`
- `src/Humans.Application/Authorization/CalendarEditorRequirement.cs`
- `src/Humans.Application/Authorization/CalendarEditorAuthorizationHandler.cs`
- `src/Humans.Infrastructure/Data/Configurations/CalendarEventConfiguration.cs`
- `src/Humans.Infrastructure/Data/Configurations/CalendarEventExceptionConfiguration.cs`
- `src/Humans.Infrastructure/Services/CalendarService.cs`
- `src/Humans.Infrastructure/Migrations/<timestamp>_AddCalendar.cs` (generated)
- `src/Humans.Web/Controllers/CalendarController.cs`
- `src/Humans.Web/Models/Calendar/*.cs` (ViewModels: `CalendarMonthViewModel`, `CalendarAgendaViewModel`, `CalendarEventViewModel`, `CalendarEventFormViewModel`, `OccurrenceOverrideFormViewModel`)
- `src/Humans.Web/Views/Calendar/Index.cshtml` (month)
- `src/Humans.Web/Views/Calendar/Agenda.cshtml`
- `src/Humans.Web/Views/Calendar/Team.cshtml`
- `src/Humans.Web/Views/Calendar/Event.cshtml`
- `src/Humans.Web/Views/Calendar/Create.cshtml`
- `src/Humans.Web/Views/Calendar/Edit.cshtml`
- `src/Humans.Web/Views/Calendar/OccurrenceEdit.cshtml`
- `docs/sections/Calendar.md`
- `docs/features/39-community-calendar.md`
- `tests/Humans.Domain.Tests/Entities/CalendarEventTests.cs`
- `tests/Humans.Domain.Tests/Entities/CalendarEventExceptionTests.cs`
- `tests/Humans.Integration.Tests/Services/CalendarServiceTests.cs`
- `tests/Humans.Integration.Tests/Controllers/CalendarControllerTests.cs`
- `tests/e2e/tests/calendar.spec.ts` (Playwright smoke)

**Modified files:**

- `src/Humans.Infrastructure/Data/HumansDbContext.cs` — add `DbSet<CalendarEvent>`, `DbSet<CalendarEventException>`.
- `src/Humans.Infrastructure/Humans.Infrastructure.csproj` — add `Ical.Net` package.
- `src/Humans.Web/Authorization/PolicyNames.cs` — add `CalendarEditor` constant.
- `src/Humans.Web/Authorization/AuthorizationPolicyExtensions.cs` (or equivalent registration file) — register policy + handler.
- `src/Humans.Web/Program.cs` — register `ICalendarService`, `CalendarEditorAuthorizationHandler` if not via existing convention.
- `src/Humans.Web/Views/Shared/_Layout.cshtml` (or the nav partial the project uses) — add "Calendar" top-level link.
- `src/Humans.Web/Views/Team/Details.cshtml` (or the canonical team detail view) — add "Team calendar" link.
- `src/Humans.Web/Views/About/Index.cshtml` — add Ical.Net to the package list.
- `docs/architecture/data-model.md` — add CalendarEvent / CalendarEventException.
- `docs/architecture/maintenance-log.md` — note new entity.

---

## Notes for the implementer

- Follow the existing service pattern (see `src/Humans.Infrastructure/Services/BudgetService.cs` or `CampService.cs` as references for: constructor injection of `HumansDbContext` + `IMemoryCache` + `ILogger`, EF query patterns, cache-invalidation style).
- Follow the existing controller pattern (see `src/Humans.Web/Controllers/BudgetController.cs`): derive from `HumansControllerBase` where appropriate, use `[Authorize]` at class level, call `IAuthorizationService.AuthorizeAsync` for resource-based checks.
- **NodaTime everywhere.** Use `Instant` for timestamps, `LocalDate` for date-only values, `DateTimeZone` for zones. Do NOT use `DateTime` or `DateTimeOffset`.
- **NEVER skip the EF migration review gate.** After creating the migration, run the EF migration reviewer agent (`.claude/agents/ef-migration-reviewer.md`). Do not commit the migration if it flags CRITICAL issues.
- **Ical.Net version:** use the latest stable 4.x at implementation time (check `nuget list Ical.Net` or nuget.org). Current known-good: 4.3.1.
- **Integration tests hit the real DB.** The project uses `Humans.Integration.Tests` with a test fixture that spins up PostgreSQL. Follow the existing fixture pattern — do NOT mock `HumansDbContext`.
- **Every page needs a nav link** (per CLAUDE.md). Task 24 covers this; don't skip it.
- **Commit after each task.** Don't batch commits across tasks. Message format follows the project convention — short imperative subject line, body if needed, no AI co-author trailer (the project doesn't use it in existing commits; check `git log -5` to pattern-match the style in use).

---

## Task 1: Add Ical.Net package

**Files:**
- Modify: `src/Humans.Infrastructure/Humans.Infrastructure.csproj`

- [ ] **Step 1: Add the package reference**

Run:
```bash
dotnet add src/Humans.Infrastructure/Humans.Infrastructure.csproj package Ical.Net
```

- [ ] **Step 2: Verify it restored**

Run:
```bash
dotnet build src/Humans.Infrastructure/Humans.Infrastructure.csproj
```
Expected: build succeeds.

- [ ] **Step 3: Commit**

```bash
git add src/Humans.Infrastructure/Humans.Infrastructure.csproj src/Humans.Infrastructure/packages.lock.json Humans.slnx
git commit -m "Add Ical.Net package for RFC 5545 RRULE expansion"
```

(If the project uses `packages.lock.json` or a Directory.Packages.props, add the relevant generated/updated file.)

---

## Task 2: `CalendarEvent` entity

**Files:**
- Create: `src/Humans.Domain/Entities/CalendarEvent.cs`
- Create: `tests/Humans.Domain.Tests/Entities/CalendarEventTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `tests/Humans.Domain.Tests/Entities/CalendarEventTests.cs`:

```csharp
using FluentAssertions;
using Humans.Domain.Entities;
using NodaTime;
using Xunit;

namespace Humans.Domain.Tests.Entities;

public class CalendarEventTests
{
    private static Instant Jan1 => Instant.FromUtc(2026, 1, 1, 10, 0);
    private static Instant Jan1End => Instant.FromUtc(2026, 1, 1, 11, 0);

    [Fact]
    public void TimedEvent_requires_EndUtc()
    {
        var ev = new CalendarEvent
        {
            Id = Guid.NewGuid(),
            Title = "Test",
            OwningTeamId = Guid.NewGuid(),
            StartUtc = Jan1,
            EndUtc = null,
            IsAllDay = false,
            CreatedByUserId = Guid.NewGuid(),
            CreatedAt = Jan1,
            UpdatedAt = Jan1,
        };

        ev.Validate().Should().Contain(e => e.Contains("EndUtc"));
    }

    [Fact]
    public void AllDayEvent_allows_null_EndUtc()
    {
        var ev = new CalendarEvent
        {
            Id = Guid.NewGuid(),
            Title = "All day",
            OwningTeamId = Guid.NewGuid(),
            StartUtc = Jan1,
            EndUtc = null,
            IsAllDay = true,
            CreatedByUserId = Guid.NewGuid(),
            CreatedAt = Jan1,
            UpdatedAt = Jan1,
        };

        ev.Validate().Should().BeEmpty();
    }

    [Fact]
    public void RecurrenceRule_without_timezone_is_invalid()
    {
        var ev = new CalendarEvent
        {
            Id = Guid.NewGuid(),
            Title = "Recurring",
            OwningTeamId = Guid.NewGuid(),
            StartUtc = Jan1,
            EndUtc = Jan1End,
            RecurrenceRule = "FREQ=WEEKLY;BYDAY=TU",
            RecurrenceTimezone = null,
            CreatedByUserId = Guid.NewGuid(),
            CreatedAt = Jan1,
            UpdatedAt = Jan1,
        };

        ev.Validate().Should().Contain(e => e.Contains("RecurrenceTimezone"));
    }

    [Fact]
    public void RecurrenceTimezone_without_rule_is_invalid()
    {
        var ev = new CalendarEvent
        {
            Id = Guid.NewGuid(),
            Title = "Recurring?",
            OwningTeamId = Guid.NewGuid(),
            StartUtc = Jan1,
            EndUtc = Jan1End,
            RecurrenceRule = null,
            RecurrenceTimezone = "Europe/Madrid",
            CreatedByUserId = Guid.NewGuid(),
            CreatedAt = Jan1,
            UpdatedAt = Jan1,
        };

        ev.Validate().Should().Contain(e => e.Contains("RecurrenceTimezone"));
    }

    [Fact]
    public void StartAfterEnd_is_invalid()
    {
        var ev = new CalendarEvent
        {
            Id = Guid.NewGuid(),
            Title = "Backwards",
            OwningTeamId = Guid.NewGuid(),
            StartUtc = Jan1End,
            EndUtc = Jan1,
            CreatedByUserId = Guid.NewGuid(),
            CreatedAt = Jan1,
            UpdatedAt = Jan1,
        };

        ev.Validate().Should().Contain(e => e.Contains("StartUtc"));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run:
```bash
dotnet test tests/Humans.Domain.Tests/Humans.Domain.Tests.csproj --filter "FullyQualifiedName~CalendarEventTests"
```
Expected: all fail with compilation errors (type does not exist).

- [ ] **Step 3: Implement the entity**

Create `src/Humans.Domain/Entities/CalendarEvent.cs`:

```csharp
using NodaTime;

namespace Humans.Domain.Entities;

/// <summary>
/// An event on the community calendar. May be a single event or a recurring event
/// defined by an RFC 5545 RRULE, expanded against <see cref="RecurrenceTimezone"/>.
/// </summary>
public class CalendarEvent
{
    public Guid Id { get; init; }

    public string Title { get; set; } = string.Empty;

    public string? Description { get; set; }

    public string? Location { get; set; }

    public string? LocationUrl { get; set; }

    /// <summary>
    /// Owning team. Coordinators of this team (plus Admin) can create/edit/delete.
    /// </summary>
    public Guid OwningTeamId { get; set; }

    public Team OwningTeam { get; set; } = null!;

    /// <summary>
    /// Start of the first occurrence (for recurring events) or the only occurrence (for single events), in UTC.
    /// </summary>
    public Instant StartUtc { get; set; }

    /// <summary>
    /// End of the event. Null iff <see cref="IsAllDay"/> is true.
    /// </summary>
    public Instant? EndUtc { get; set; }

    public bool IsAllDay { get; set; }

    /// <summary>
    /// RFC 5545 RRULE (without the "RRULE:" prefix). Null = single event.
    /// </summary>
    public string? RecurrenceRule { get; set; }

    /// <summary>
    /// IANA timezone used to expand <see cref="RecurrenceRule"/>. Required iff <see cref="RecurrenceRule"/> is set.
    /// </summary>
    public string? RecurrenceTimezone { get; set; }

    /// <summary>
    /// Denormalised copy of the RRULE's UNTIL (or null for open-ended rules). Enables indexable
    /// "does this recur into my window" queries without parsing RRULE at the DB layer.
    /// </summary>
    public Instant? RecurrenceUntilUtc { get; set; }

    public Guid CreatedByUserId { get; set; }

    public Instant CreatedAt { get; set; }

    public Instant UpdatedAt { get; set; }

    public Instant? DeletedAt { get; set; }

    public ICollection<CalendarEventException> Exceptions { get; set; } = new List<CalendarEventException>();

    /// <summary>
    /// Returns a list of validation error messages; empty if valid.
    /// </summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        if (!IsAllDay && EndUtc is null)
            errors.Add("EndUtc is required for timed events.");

        if (EndUtc is { } end && end < StartUtc)
            errors.Add("StartUtc must be on or before EndUtc.");

        var hasRule = !string.IsNullOrWhiteSpace(RecurrenceRule);
        var hasZone = !string.IsNullOrWhiteSpace(RecurrenceTimezone);
        if (hasRule != hasZone)
            errors.Add("RecurrenceRule and RecurrenceTimezone must be set together (both or neither).");

        if (string.IsNullOrWhiteSpace(Title))
            errors.Add("Title is required.");

        return errors;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

```bash
dotnet test tests/Humans.Domain.Tests/Humans.Domain.Tests.csproj --filter "FullyQualifiedName~CalendarEventTests"
```
Expected: all 5 pass.

- [ ] **Step 5: Commit**

```bash
git add src/Humans.Domain/Entities/CalendarEvent.cs tests/Humans.Domain.Tests/Entities/CalendarEventTests.cs
git commit -m "Add CalendarEvent entity with validation"
```

---

## Task 3: `CalendarEventException` entity

**Files:**
- Create: `src/Humans.Domain/Entities/CalendarEventException.cs`
- Create: `tests/Humans.Domain.Tests/Entities/CalendarEventExceptionTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
using FluentAssertions;
using Humans.Domain.Entities;
using NodaTime;
using Xunit;

namespace Humans.Domain.Tests.Entities;

public class CalendarEventExceptionTests
{
    private static Instant When => Instant.FromUtc(2026, 2, 10, 18, 0);

    [Fact]
    public void Empty_exception_is_invalid()
    {
        var x = new CalendarEventException
        {
            Id = Guid.NewGuid(),
            EventId = Guid.NewGuid(),
            OriginalOccurrenceStartUtc = When,
            IsCancelled = false,
            CreatedByUserId = Guid.NewGuid(),
            CreatedAt = When,
            UpdatedAt = When,
        };

        x.Validate().Should().NotBeEmpty();
    }

    [Fact]
    public void Cancelled_exception_is_valid()
    {
        var x = new CalendarEventException
        {
            Id = Guid.NewGuid(),
            EventId = Guid.NewGuid(),
            OriginalOccurrenceStartUtc = When,
            IsCancelled = true,
            CreatedByUserId = Guid.NewGuid(),
            CreatedAt = When,
            UpdatedAt = When,
        };

        x.Validate().Should().BeEmpty();
    }

    [Fact]
    public void Override_exception_is_valid()
    {
        var x = new CalendarEventException
        {
            Id = Guid.NewGuid(),
            EventId = Guid.NewGuid(),
            OriginalOccurrenceStartUtc = When,
            IsCancelled = false,
            OverrideTitle = "Moved!",
            CreatedByUserId = Guid.NewGuid(),
            CreatedAt = When,
            UpdatedAt = When,
        };

        x.Validate().Should().BeEmpty();
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

```bash
dotnet test tests/Humans.Domain.Tests/Humans.Domain.Tests.csproj --filter "FullyQualifiedName~CalendarEventExceptionTests"
```
Expected: compile-fail.

- [ ] **Step 3: Implement the entity**

Create `src/Humans.Domain/Entities/CalendarEventException.cs`:

```csharp
using NodaTime;

namespace Humans.Domain.Entities;

/// <summary>
/// An override for a single occurrence of a recurring <see cref="CalendarEvent"/>.
/// Either cancels the occurrence or overrides one or more fields for it.
/// </summary>
public class CalendarEventException
{
    public Guid Id { get; init; }

    public Guid EventId { get; set; }

    public CalendarEvent Event { get; set; } = null!;

    /// <summary>
    /// The original (unmodified) start instant of the occurrence this exception targets.
    /// </summary>
    public Instant OriginalOccurrenceStartUtc { get; set; }

    public bool IsCancelled { get; set; }

    public Instant? OverrideStartUtc { get; set; }

    public Instant? OverrideEndUtc { get; set; }

    public string? OverrideTitle { get; set; }

    public string? OverrideDescription { get; set; }

    public string? OverrideLocation { get; set; }

    public string? OverrideLocationUrl { get; set; }

    public Guid CreatedByUserId { get; set; }

    public Instant CreatedAt { get; set; }

    public Instant UpdatedAt { get; set; }

    public IReadOnlyList<string> Validate()
    {
        var hasOverride =
            OverrideStartUtc is not null ||
            OverrideEndUtc is not null ||
            OverrideTitle is not null ||
            OverrideDescription is not null ||
            OverrideLocation is not null ||
            OverrideLocationUrl is not null;

        if (!IsCancelled && !hasOverride)
            return new[] { "Exception must either cancel the occurrence or override at least one field." };

        return Array.Empty<string>();
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

```bash
dotnet test tests/Humans.Domain.Tests/Humans.Domain.Tests.csproj --filter "FullyQualifiedName~CalendarEventExceptionTests"
```
Expected: 3 pass.

- [ ] **Step 5: Commit**

```bash
git add src/Humans.Domain/Entities/CalendarEventException.cs tests/Humans.Domain.Tests/Entities/CalendarEventExceptionTests.cs
git commit -m "Add CalendarEventException entity"
```

---

## Task 4: EF configurations

**Files:**
- Create: `src/Humans.Infrastructure/Data/Configurations/CalendarEventConfiguration.cs`
- Create: `src/Humans.Infrastructure/Data/Configurations/CalendarEventExceptionConfiguration.cs`
- Modify: `src/Humans.Infrastructure/Data/HumansDbContext.cs`

- [ ] **Step 1: Create `CalendarEventConfiguration`**

```csharp
using Humans.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Infrastructure.Data.Configurations;

public class CalendarEventConfiguration : IEntityTypeConfiguration<CalendarEvent>
{
    public void Configure(EntityTypeBuilder<CalendarEvent> b)
    {
        b.ToTable("calendar_events");

        b.HasKey(e => e.Id);

        b.Property(e => e.Title).IsRequired().HasMaxLength(200);
        b.Property(e => e.Description).HasMaxLength(4000);
        b.Property(e => e.Location).HasMaxLength(500);
        b.Property(e => e.LocationUrl).HasMaxLength(2000);

        b.Property(e => e.RecurrenceRule).HasMaxLength(500);
        b.Property(e => e.RecurrenceTimezone).HasMaxLength(100);

        b.Property(e => e.OwningTeamId).IsRequired();
        b.HasOne(e => e.OwningTeam)
         .WithMany()
         .HasForeignKey(e => e.OwningTeamId)
         .OnDelete(DeleteBehavior.Restrict);

        b.HasMany(e => e.Exceptions)
         .WithOne(x => x.Event)
         .HasForeignKey(x => x.EventId)
         .OnDelete(DeleteBehavior.Cascade);

        b.HasQueryFilter(e => e.DeletedAt == null);

        b.HasIndex(e => new { e.OwningTeamId, e.StartUtc });
        b.HasIndex(e => new { e.StartUtc, e.RecurrenceUntilUtc });
    }
}
```

- [ ] **Step 2: Create `CalendarEventExceptionConfiguration`**

```csharp
using Humans.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Infrastructure.Data.Configurations;

public class CalendarEventExceptionConfiguration : IEntityTypeConfiguration<CalendarEventException>
{
    public void Configure(EntityTypeBuilder<CalendarEventException> b)
    {
        b.ToTable("calendar_event_exceptions");

        b.HasKey(x => x.Id);

        b.Property(x => x.OverrideTitle).HasMaxLength(200);
        b.Property(x => x.OverrideDescription).HasMaxLength(4000);
        b.Property(x => x.OverrideLocation).HasMaxLength(500);
        b.Property(x => x.OverrideLocationUrl).HasMaxLength(2000);

        b.HasIndex(x => new { x.EventId, x.OriginalOccurrenceStartUtc })
         .IsUnique();
    }
}
```

- [ ] **Step 3: Register DbSets on `HumansDbContext`**

In `src/Humans.Infrastructure/Data/HumansDbContext.cs`, add to the DbSets section (alphabetically):

```csharp
public DbSet<CalendarEvent> CalendarEvents => Set<CalendarEvent>();
public DbSet<CalendarEventException> CalendarEventExceptions => Set<CalendarEventException>();
```

(The project uses `ApplyConfigurationsFromAssembly` in `OnModelCreating`, so the new configurations are picked up automatically. Verify by grepping `ApplyConfigurationsFromAssembly` in the DbContext. If the project instead registers configurations manually, add the two new ones there.)

- [ ] **Step 4: Build and verify**

```bash
dotnet build src/Humans.Infrastructure/Humans.Infrastructure.csproj
```
Expected: success.

- [ ] **Step 5: Commit**

```bash
git add src/Humans.Infrastructure/Data/Configurations/CalendarEventConfiguration.cs src/Humans.Infrastructure/Data/Configurations/CalendarEventExceptionConfiguration.cs src/Humans.Infrastructure/Data/HumansDbContext.cs
git commit -m "Configure calendar_events and calendar_event_exceptions"
```

---

## Task 5: EF migration

**Files:**
- Generated: `src/Humans.Infrastructure/Migrations/<timestamp>_AddCalendar.cs`
- Generated: `src/Humans.Infrastructure/Migrations/<timestamp>_AddCalendar.Designer.cs`
- Modified: `src/Humans.Infrastructure/Migrations/HumansDbContextModelSnapshot.cs`

- [ ] **Step 1: Generate the migration**

```bash
dotnet ef migrations add AddCalendar --project src/Humans.Infrastructure --startup-project src/Humans.Web
```

- [ ] **Step 2: Run the EF migration reviewer agent**

Invoke the agent at `.claude/agents/ef-migration-reviewer.md` on the generated migration file. Address any CRITICAL findings before proceeding. **Do not commit** until the reviewer passes clean.

- [ ] **Step 3: Apply the migration locally to verify**

```bash
dotnet ef database update --project src/Humans.Infrastructure --startup-project src/Humans.Web
```
Expected: two new tables in the local DB.

- [ ] **Step 4: Build the whole solution**

```bash
dotnet build Humans.slnx
```
Expected: success.

- [ ] **Step 5: Commit**

```bash
git add src/Humans.Infrastructure/Migrations/
git commit -m "Add EF migration for calendar tables"
```

---

## Task 6: ICalendarService interface + DTOs

**Files:**
- Create: `src/Humans.Application/DTOs/Calendar/CalendarOccurrence.cs`
- Create: `src/Humans.Application/DTOs/Calendar/CreateCalendarEventDto.cs`
- Create: `src/Humans.Application/DTOs/Calendar/UpdateCalendarEventDto.cs`
- Create: `src/Humans.Application/DTOs/Calendar/OverrideOccurrenceDto.cs`
- Create: `src/Humans.Application/Interfaces/ICalendarService.cs`

- [ ] **Step 1: Create the DTOs**

`src/Humans.Application/DTOs/Calendar/CalendarOccurrence.cs`:

```csharp
using NodaTime;

namespace Humans.Application.DTOs.Calendar;

public sealed record CalendarOccurrence(
    Guid EventId,
    Instant OccurrenceStartUtc,
    Instant? OccurrenceEndUtc,
    bool IsAllDay,
    string Title,
    string? Description,
    string? Location,
    string? LocationUrl,
    Guid OwningTeamId,
    string OwningTeamName,
    bool IsRecurring,
    Instant? OriginalOccurrenceStartUtc);
```

`src/Humans.Application/DTOs/Calendar/CreateCalendarEventDto.cs`:

```csharp
using NodaTime;

namespace Humans.Application.DTOs.Calendar;

public sealed record CreateCalendarEventDto(
    string Title,
    string? Description,
    string? Location,
    string? LocationUrl,
    Guid OwningTeamId,
    Instant StartUtc,
    Instant? EndUtc,
    bool IsAllDay,
    string? RecurrenceRule,
    string? RecurrenceTimezone);
```

`src/Humans.Application/DTOs/Calendar/UpdateCalendarEventDto.cs`:

```csharp
using NodaTime;

namespace Humans.Application.DTOs.Calendar;

public sealed record UpdateCalendarEventDto(
    string Title,
    string? Description,
    string? Location,
    string? LocationUrl,
    Guid OwningTeamId,
    Instant StartUtc,
    Instant? EndUtc,
    bool IsAllDay,
    string? RecurrenceRule,
    string? RecurrenceTimezone);
```

`src/Humans.Application/DTOs/Calendar/OverrideOccurrenceDto.cs`:

```csharp
using NodaTime;

namespace Humans.Application.DTOs.Calendar;

public sealed record OverrideOccurrenceDto(
    Instant? OverrideStartUtc,
    Instant? OverrideEndUtc,
    string? OverrideTitle,
    string? OverrideDescription,
    string? OverrideLocation,
    string? OverrideLocationUrl);
```

- [ ] **Step 2: Create the interface**

`src/Humans.Application/Interfaces/ICalendarService.cs`:

```csharp
using Humans.Application.DTOs.Calendar;
using Humans.Domain.Entities;
using NodaTime;

namespace Humans.Application.Interfaces;

public interface ICalendarService
{
    Task<IReadOnlyList<CalendarOccurrence>> GetOccurrencesInWindowAsync(
        Instant from,
        Instant to,
        Guid? teamId = null,
        CancellationToken ct = default);

    Task<CalendarEvent?> GetEventByIdAsync(Guid id, CancellationToken ct = default);

    Task<CalendarEvent> CreateEventAsync(CreateCalendarEventDto dto, Guid createdByUserId, CancellationToken ct = default);

    Task<CalendarEvent> UpdateEventAsync(Guid id, UpdateCalendarEventDto dto, Guid updatedByUserId, CancellationToken ct = default);

    Task DeleteEventAsync(Guid id, Guid deletedByUserId, CancellationToken ct = default);

    Task CancelOccurrenceAsync(Guid eventId, Instant originalOccurrenceStartUtc, Guid userId, CancellationToken ct = default);

    Task OverrideOccurrenceAsync(Guid eventId, Instant originalOccurrenceStartUtc, OverrideOccurrenceDto dto, Guid userId, CancellationToken ct = default);
}
```

- [ ] **Step 3: Build**

```bash
dotnet build src/Humans.Application/Humans.Application.csproj
```
Expected: success.

- [ ] **Step 4: Commit**

```bash
git add src/Humans.Application/DTOs/Calendar/ src/Humans.Application/Interfaces/ICalendarService.cs
git commit -m "Add ICalendarService interface and DTOs"
```

---

## Task 7: CalendarService — skeleton + DI registration

**Files:**
- Create: `src/Humans.Infrastructure/Services/CalendarService.cs`
- Modify: `src/Humans.Web/Program.cs` (service registration — follow existing pattern)

- [ ] **Step 1: Create service skeleton**

`src/Humans.Infrastructure/Services/CalendarService.cs`:

```csharp
using Humans.Application.DTOs.Calendar;
using Humans.Application.Interfaces;
using Humans.Domain.Entities;
using Humans.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using NodaTime;

namespace Humans.Infrastructure.Services;

public class CalendarService : ICalendarService
{
    private const string CacheKeyActiveEvents = "calendar:active-events";

    private readonly HumansDbContext _db;
    private readonly IMemoryCache _cache;
    private readonly IClock _clock;
    private readonly ILogger<CalendarService> _logger;

    public CalendarService(
        HumansDbContext db,
        IMemoryCache cache,
        IClock clock,
        ILogger<CalendarService> logger)
    {
        _db = db;
        _cache = cache;
        _clock = clock;
        _logger = logger;
    }

    public Task<IReadOnlyList<CalendarOccurrence>> GetOccurrencesInWindowAsync(
        Instant from, Instant to, Guid? teamId = null, CancellationToken ct = default)
        => throw new NotImplementedException();

    public Task<CalendarEvent?> GetEventByIdAsync(Guid id, CancellationToken ct = default)
        => throw new NotImplementedException();

    public Task<CalendarEvent> CreateEventAsync(CreateCalendarEventDto dto, Guid createdByUserId, CancellationToken ct = default)
        => throw new NotImplementedException();

    public Task<CalendarEvent> UpdateEventAsync(Guid id, UpdateCalendarEventDto dto, Guid updatedByUserId, CancellationToken ct = default)
        => throw new NotImplementedException();

    public Task DeleteEventAsync(Guid id, Guid deletedByUserId, CancellationToken ct = default)
        => throw new NotImplementedException();

    public Task CancelOccurrenceAsync(Guid eventId, Instant originalOccurrenceStartUtc, Guid userId, CancellationToken ct = default)
        => throw new NotImplementedException();

    public Task OverrideOccurrenceAsync(Guid eventId, Instant originalOccurrenceStartUtc, OverrideOccurrenceDto dto, Guid userId, CancellationToken ct = default)
        => throw new NotImplementedException();
}
```

- [ ] **Step 2: Register in DI**

In `src/Humans.Web/Program.cs`, follow the existing pattern for service registration (look for `AddScoped<IBudgetService, BudgetService>()` or similar). Add:

```csharp
builder.Services.AddScoped<ICalendarService, CalendarService>();
```

- [ ] **Step 3: Build**

```bash
dotnet build Humans.slnx
```
Expected: success.

- [ ] **Step 4: Commit**

```bash
git add src/Humans.Infrastructure/Services/CalendarService.cs src/Humans.Web/Program.cs
git commit -m "Register CalendarService skeleton"
```

---

## Task 8: CalendarService — `GetEventByIdAsync` + `CreateEventAsync`

**Files:**
- Modify: `src/Humans.Infrastructure/Services/CalendarService.cs`
- Create: `tests/Humans.Integration.Tests/Services/CalendarServiceTests.cs`

This task bundles the simplest read + write first so later tests can use them as setup.

- [ ] **Step 1: Write integration tests**

`tests/Humans.Integration.Tests/Services/CalendarServiceTests.cs` — base skeleton; we'll extend it in later tasks. Look at an existing integration test (e.g. `BudgetServiceTests.cs`) to match the fixture pattern (typically `IClassFixture<DatabaseFixture>` and a helper to seed a user + team).

```csharp
using FluentAssertions;
using Humans.Application.DTOs.Calendar;
using Humans.Application.Interfaces;
using Humans.Domain.Entities;
using NodaTime;
using Xunit;

namespace Humans.Integration.Tests.Services;

public class CalendarServiceTests : IClassFixture<DatabaseFixture>
{
    private readonly DatabaseFixture _fx;

    public CalendarServiceTests(DatabaseFixture fx) => _fx = fx;

    [Fact]
    public async Task CreateEventAsync_persists_and_GetEventById_returns_it()
    {
        await using var scope = _fx.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<ICalendarService>();
        var team = await _fx.SeedTeamAsync(scope, "Test Team");
        var userId = await _fx.SeedUserAsync(scope);

        var start = Instant.FromUtc(2026, 5, 1, 17, 0);
        var end   = Instant.FromUtc(2026, 5, 1, 18, 0);

        var created = await svc.CreateEventAsync(
            new CreateCalendarEventDto(
                Title: "Community call",
                Description: "Monthly sync",
                Location: "Zoom",
                LocationUrl: "https://meet.google.com/abc",
                OwningTeamId: team.Id,
                StartUtc: start,
                EndUtc: end,
                IsAllDay: false,
                RecurrenceRule: null,
                RecurrenceTimezone: null),
            createdByUserId: userId);

        created.Id.Should().NotBe(Guid.Empty);

        var fetched = await svc.GetEventByIdAsync(created.Id);
        fetched.Should().NotBeNull();
        fetched!.Title.Should().Be("Community call");
        fetched.OwningTeamId.Should().Be(team.Id);
        fetched.StartUtc.Should().Be(start);
        fetched.EndUtc.Should().Be(end);
    }
}
```

(If `DatabaseFixture.SeedTeamAsync` / `SeedUserAsync` helpers don't exist, add them to the fixture — follow the existing pattern used by other integration test suites. Grep the tests project for `SeedTeamAsync` or similar to find the right extension point.)

- [ ] **Step 2: Run — expect fail**

```bash
dotnet test tests/Humans.Integration.Tests/Humans.Integration.Tests.csproj --filter "FullyQualifiedName~CalendarServiceTests"
```
Expected: fails with `NotImplementedException`.

- [ ] **Step 3: Implement `CreateEventAsync` and `GetEventByIdAsync`**

Replace the two stub bodies in `CalendarService.cs`:

```csharp
public async Task<CalendarEvent?> GetEventByIdAsync(Guid id, CancellationToken ct = default)
{
    return await _db.CalendarEvents
        .Include(e => e.OwningTeam)
        .Include(e => e.Exceptions)
        .FirstOrDefaultAsync(e => e.Id == id, ct);
}

public async Task<CalendarEvent> CreateEventAsync(CreateCalendarEventDto dto, Guid createdByUserId, CancellationToken ct = default)
{
    var now = _clock.GetCurrentInstant();

    var ev = new CalendarEvent
    {
        Id = Guid.NewGuid(),
        Title = dto.Title,
        Description = dto.Description,
        Location = dto.Location,
        LocationUrl = dto.LocationUrl,
        OwningTeamId = dto.OwningTeamId,
        StartUtc = dto.StartUtc,
        EndUtc = dto.EndUtc,
        IsAllDay = dto.IsAllDay,
        RecurrenceRule = dto.RecurrenceRule,
        RecurrenceTimezone = dto.RecurrenceTimezone,
        RecurrenceUntilUtc = ComputeRecurrenceUntilUtc(dto.RecurrenceRule, dto.RecurrenceTimezone),
        CreatedByUserId = createdByUserId,
        CreatedAt = now,
        UpdatedAt = now,
    };

    var errors = ev.Validate();
    if (errors.Count > 0)
        throw new InvalidOperationException("CalendarEvent is invalid: " + string.Join("; ", errors));

    _db.CalendarEvents.Add(ev);
    await _db.SaveChangesAsync(ct);

    InvalidateCache();
    return ev;
}

private void InvalidateCache() => _cache.Remove(CacheKeyActiveEvents);

private static Instant? ComputeRecurrenceUntilUtc(string? rrule, string? tz)
{
    // Parse the UNTIL component of the RRULE if present, converting from the
    // rule's timezone to UTC. For open-ended or count-bounded rules, returns null.
    if (string.IsNullOrWhiteSpace(rrule) || string.IsNullOrWhiteSpace(tz)) return null;

    // RRULE parts are semicolon-separated "KEY=VALUE".
    foreach (var part in rrule.Split(';', StringSplitOptions.RemoveEmptyEntries))
    {
        var eq = part.IndexOf('=');
        if (eq <= 0) continue;
        var key = part[..eq];
        var val = part[(eq + 1)..];
        if (!string.Equals(key, "UNTIL", StringComparison.OrdinalIgnoreCase)) continue;

        // UNTIL values are either floating (local, in the rule's TZ) or UTC (trailing 'Z').
        // See RFC 5545 §3.3.10. We accept both.
        if (val.EndsWith('Z'))
        {
            var dt = System.Globalization.DateTimeOffset.ParseExact(
                val, "yyyyMMdd'T'HHmmss'Z'", System.Globalization.CultureInfo.InvariantCulture);
            return Instant.FromDateTimeOffset(dt);
        }
        else
        {
            // "YYYYMMDDTHHMMSS" in local rule TZ
            var local = NodaTime.Text.LocalDateTimePattern.CreateWithInvariantCulture("yyyyMMdd'T'HHmmss")
                .Parse(val).Value;
            var zone = DateTimeZoneProviders.Tzdb[tz];
            return local.InZoneStrictly(zone).ToInstant();
        }
    }
    return null;
}
```

- [ ] **Step 4: Run tests**

```bash
dotnet test tests/Humans.Integration.Tests/Humans.Integration.Tests.csproj --filter "FullyQualifiedName~CalendarServiceTests"
```
Expected: 1 pass.

- [ ] **Step 5: Commit**

```bash
git add src/Humans.Infrastructure/Services/CalendarService.cs tests/Humans.Integration.Tests/Services/CalendarServiceTests.cs
git commit -m "CalendarService: create + get by id"
```

---

## Task 9: `GetOccurrencesInWindowAsync` — non-recurring events

**Files:**
- Modify: `src/Humans.Infrastructure/Services/CalendarService.cs`
- Modify: `tests/Humans.Integration.Tests/Services/CalendarServiceTests.cs`

- [ ] **Step 1: Add test**

Append to `CalendarServiceTests.cs`:

```csharp
[Fact]
public async Task GetOccurrencesInWindow_returns_single_event_when_overlapping()
{
    await using var scope = _fx.CreateScope();
    var svc = scope.ServiceProvider.GetRequiredService<ICalendarService>();
    var team = await _fx.SeedTeamAsync(scope, "T");
    var userId = await _fx.SeedUserAsync(scope);

    await svc.CreateEventAsync(new CreateCalendarEventDto(
        "Inside", null, null, null, team.Id,
        Instant.FromUtc(2026, 6, 15, 17, 0),
        Instant.FromUtc(2026, 6, 15, 18, 0),
        false, null, null), userId);

    await svc.CreateEventAsync(new CreateCalendarEventDto(
        "Outside", null, null, null, team.Id,
        Instant.FromUtc(2027, 1, 1, 0, 0),
        Instant.FromUtc(2027, 1, 1, 1, 0),
        false, null, null), userId);

    var occ = await svc.GetOccurrencesInWindowAsync(
        from: Instant.FromUtc(2026, 6, 1, 0, 0),
        to:   Instant.FromUtc(2026, 7, 1, 0, 0));

    occ.Should().HaveCount(1);
    occ[0].Title.Should().Be("Inside");
    occ[0].IsRecurring.Should().BeFalse();
}

[Fact]
public async Task GetOccurrencesInWindow_filters_by_team()
{
    await using var scope = _fx.CreateScope();
    var svc = scope.ServiceProvider.GetRequiredService<ICalendarService>();
    var a = await _fx.SeedTeamAsync(scope, "A");
    var b = await _fx.SeedTeamAsync(scope, "B");
    var uid = await _fx.SeedUserAsync(scope);

    await svc.CreateEventAsync(new CreateCalendarEventDto(
        "A-evt", null, null, null, a.Id,
        Instant.FromUtc(2026, 6, 15, 17, 0),
        Instant.FromUtc(2026, 6, 15, 18, 0), false, null, null), uid);

    await svc.CreateEventAsync(new CreateCalendarEventDto(
        "B-evt", null, null, null, b.Id,
        Instant.FromUtc(2026, 6, 15, 19, 0),
        Instant.FromUtc(2026, 6, 15, 20, 0), false, null, null), uid);

    var occ = await svc.GetOccurrencesInWindowAsync(
        Instant.FromUtc(2026, 6, 1, 0, 0),
        Instant.FromUtc(2026, 7, 1, 0, 0),
        teamId: a.Id);

    occ.Should().ContainSingle(o => o.Title == "A-evt");
    occ.Should().NotContain(o => o.Title == "B-evt");
}

[Fact]
public async Task Soft_deleted_events_do_not_appear_in_window()
{
    await using var scope = _fx.CreateScope();
    var svc = scope.ServiceProvider.GetRequiredService<ICalendarService>();
    var team = await _fx.SeedTeamAsync(scope, "T");
    var uid = await _fx.SeedUserAsync(scope);

    var ev = await svc.CreateEventAsync(new CreateCalendarEventDto(
        "DoomedEvent", null, null, null, team.Id,
        Instant.FromUtc(2026, 6, 15, 17, 0),
        Instant.FromUtc(2026, 6, 15, 18, 0), false, null, null), uid);

    await svc.DeleteEventAsync(ev.Id, uid);

    var occ = await svc.GetOccurrencesInWindowAsync(
        Instant.FromUtc(2026, 6, 1, 0, 0),
        Instant.FromUtc(2026, 7, 1, 0, 0));

    occ.Should().BeEmpty();
}
```

(The soft-delete test depends on `DeleteEventAsync`; it's added in Task 13. To keep this task green on its own, mark the test `Skip = "Needs DeleteEventAsync"` if you run tasks strictly in order, and un-skip it in Task 13. Simpler: accept that this test will fail until Task 13 lands and track that explicitly.)

- [ ] **Step 2: Run — expect fail**

```bash
dotnet test tests/Humans.Integration.Tests/Humans.Integration.Tests.csproj --filter "FullyQualifiedName~CalendarServiceTests.GetOccurrencesInWindow"
```

- [ ] **Step 3: Implement (non-recurring path only for now)**

Replace `GetOccurrencesInWindowAsync` in `CalendarService.cs`:

```csharp
public async Task<IReadOnlyList<CalendarOccurrence>> GetOccurrencesInWindowAsync(
    Instant from, Instant to, Guid? teamId = null, CancellationToken ct = default)
{
    var query = _db.CalendarEvents
        .Include(e => e.OwningTeam)
        .Include(e => e.Exceptions)
        .AsQueryable();

    query = query.Where(e => e.StartUtc <= to
        && (e.RecurrenceUntilUtc == null || e.RecurrenceUntilUtc >= from));

    if (teamId is { } t)
        query = query.Where(e => e.OwningTeamId == t);

    var events = await query.ToListAsync(ct);
    var results = new List<CalendarOccurrence>();

    foreach (var e in events)
    {
        if (string.IsNullOrWhiteSpace(e.RecurrenceRule))
        {
            var end = e.EndUtc ?? e.StartUtc;
            if (end < from || e.StartUtc > to) continue;
            results.Add(new CalendarOccurrence(
                EventId: e.Id,
                OccurrenceStartUtc: e.StartUtc,
                OccurrenceEndUtc: e.EndUtc,
                IsAllDay: e.IsAllDay,
                Title: e.Title,
                Description: e.Description,
                Location: e.Location,
                LocationUrl: e.LocationUrl,
                OwningTeamId: e.OwningTeamId,
                OwningTeamName: e.OwningTeam.Name,
                IsRecurring: false,
                OriginalOccurrenceStartUtc: null));
        }
        else
        {
            // Recurrence expansion lands in Task 10.
            continue;
        }
    }

    return results
        .OrderBy(o => o.OccurrenceStartUtc)
        .ToList();
}
```

- [ ] **Step 4: Run tests**

Expected: the first two tests pass; the soft-delete test remains red until Task 13.

- [ ] **Step 5: Commit**

```bash
git add src/Humans.Infrastructure/Services/CalendarService.cs tests/Humans.Integration.Tests/Services/CalendarServiceTests.cs
git commit -m "CalendarService: window query for non-recurring events"
```

---

## Task 10: `GetOccurrencesInWindowAsync` — recurring events (incl. DST test)

**Files:**
- Modify: `src/Humans.Infrastructure/Services/CalendarService.cs`
- Modify: `tests/Humans.Integration.Tests/Services/CalendarServiceTests.cs`

- [ ] **Step 1: Add DST test**

Append to `CalendarServiceTests.cs`:

```csharp
[Fact]
public async Task Recurring_weekly_event_stays_at_local_time_across_Madrid_DST()
{
    await using var scope = _fx.CreateScope();
    var svc = scope.ServiceProvider.GetRequiredService<ICalendarService>();
    var team = await _fx.SeedTeamAsync(scope, "T");
    var uid  = await _fx.SeedUserAsync(scope);

    // Start: Tuesday 24 March 2026, 19:00 Madrid (CET, UTC+1).
    // 19:00 Madrid on 24 Mar = 18:00 UTC.
    // After DST flip on 29 Mar 2026, 19:00 Madrid (CEST, UTC+2) = 17:00 UTC.
    var zone = NodaTime.DateTimeZoneProviders.Tzdb["Europe/Madrid"];
    var firstLocal = new NodaTime.LocalDateTime(2026, 3, 24, 19, 0);
    var firstUtc   = firstLocal.InZoneStrictly(zone).ToInstant();

    await svc.CreateEventAsync(new CreateCalendarEventDto(
        "Tuesday call", null, null, null, team.Id,
        StartUtc: firstUtc,
        EndUtc:   firstUtc.Plus(NodaTime.Duration.FromHours(1)),
        IsAllDay: false,
        RecurrenceRule: "FREQ=WEEKLY;BYDAY=TU;COUNT=4",
        RecurrenceTimezone: "Europe/Madrid"), uid);

    var occ = await svc.GetOccurrencesInWindowAsync(
        from: Instant.FromUtc(2026, 3, 1, 0, 0),
        to:   Instant.FromUtc(2026, 5, 1, 0, 0));

    occ.Should().HaveCount(4);
    foreach (var o in occ)
    {
        var localStart = o.OccurrenceStartUtc.InZone(zone).LocalDateTime;
        localStart.Hour.Should().Be(19);
        localStart.Minute.Should().Be(0);
    }
}

[Fact]
public async Task Recurring_bounded_event_is_skipped_when_until_before_window()
{
    await using var scope = _fx.CreateScope();
    var svc = scope.ServiceProvider.GetRequiredService<ICalendarService>();
    var team = await _fx.SeedTeamAsync(scope, "T");
    var uid  = await _fx.SeedUserAsync(scope);

    await svc.CreateEventAsync(new CreateCalendarEventDto(
        "Old", null, null, null, team.Id,
        StartUtc: Instant.FromUtc(2024, 1, 7, 18, 0),
        EndUtc:   Instant.FromUtc(2024, 1, 7, 19, 0),
        IsAllDay: false,
        RecurrenceRule: "FREQ=WEEKLY;UNTIL=20240201T000000Z",
        RecurrenceTimezone: "Europe/Madrid"), uid);

    var occ = await svc.GetOccurrencesInWindowAsync(
        Instant.FromUtc(2026, 1, 1, 0, 0),
        Instant.FromUtc(2026, 2, 1, 0, 0));

    occ.Should().BeEmpty();
}
```

- [ ] **Step 2: Run — expect the DST test to fail (empty results)**

```bash
dotnet test tests/Humans.Integration.Tests/Humans.Integration.Tests.csproj --filter "FullyQualifiedName~CalendarServiceTests.Recurring"
```

- [ ] **Step 3: Implement the recurrence branch**

Add using statements at the top of `CalendarService.cs`:

```csharp
using Ical.Net;
using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;
using IcalEvent = Ical.Net.CalendarComponents.CalendarEvent;
```

(Alias needed because our domain also has a type called `CalendarEvent`.)

Replace the `else` branch in the expansion loop with:

```csharp
else
{
    var zone = DateTimeZoneProviders.Tzdb.GetZoneOrNull(e.RecurrenceTimezone!);
    if (zone is null)
    {
        _logger.LogWarning(
            "CalendarEvent {Id} has unknown timezone {Tz}; skipping occurrence expansion",
            e.Id, e.RecurrenceTimezone);
        continue;
    }

    var icalEv = new IcalEvent
    {
        DtStart = new CalDateTime(
            e.StartUtc.InZone(zone).LocalDateTime.ToDateTimeUnspecified(),
            e.RecurrenceTimezone),
        Duration = (e.EndUtc ?? e.StartUtc) - e.StartUtc is var dur
            ? System.TimeSpan.FromTicks(dur.BclCompatibleTicks)
            : System.TimeSpan.Zero,
        RecurrenceRules = { new RecurrencePattern(e.RecurrenceRule!) },
    };

    var fromLocal = from.InZone(zone).LocalDateTime.ToDateTimeUnspecified();
    var toLocal   = to.InZone(zone).LocalDateTime.ToDateTimeUnspecified();

    foreach (var occ in icalEv.GetOccurrences(fromLocal, toLocal))
    {
        // occ.Period.StartTime is a CalDateTime in the rule's TZ.
        var occLocal = LocalDateTime.FromDateTime(occ.Period.StartTime.Value);
        var startInstant = occLocal.InZoneStrictly(zone).ToInstant();
        var endInstant = e.EndUtc is null
            ? (Instant?)null
            : startInstant.Plus(e.EndUtc.Value - e.StartUtc);

        results.Add(new CalendarOccurrence(
            EventId: e.Id,
            OccurrenceStartUtc: startInstant,
            OccurrenceEndUtc: endInstant,
            IsAllDay: e.IsAllDay,
            Title: e.Title,
            Description: e.Description,
            Location: e.Location,
            LocationUrl: e.LocationUrl,
            OwningTeamId: e.OwningTeamId,
            OwningTeamName: e.OwningTeam.Name,
            IsRecurring: true,
            OriginalOccurrenceStartUtc: startInstant));
    }
}
```

(Note: the exact `Ical.Net` API shape may differ slightly between major versions. If `GetOccurrences(DateTime, DateTime)` isn't the signature in the installed version, consult the installed assembly and adjust. The semantic intent: expand the RRULE anchored at `DtStart` in the declared TZ, return occurrences within `[fromLocal, toLocal]`.)

- [ ] **Step 4: Run tests**

```bash
dotnet test tests/Humans.Integration.Tests/Humans.Integration.Tests.csproj --filter "FullyQualifiedName~CalendarServiceTests.Recurring"
```
Expected: both pass. The DST test is the critical one — it confirms 19:00 Madrid holds across the spring DST flip.

- [ ] **Step 5: Commit**

```bash
git add src/Humans.Infrastructure/Services/CalendarService.cs tests/Humans.Integration.Tests/Services/CalendarServiceTests.cs
git commit -m "CalendarService: expand recurring events via Ical.Net"
```

---

## Task 11: Exception handling in expansion

**Files:**
- Modify: `src/Humans.Infrastructure/Services/CalendarService.cs`
- Modify: `tests/Humans.Integration.Tests/Services/CalendarServiceTests.cs`

- [ ] **Step 1: Add tests**

```csharp
[Fact]
public async Task Cancelled_exception_removes_that_occurrence()
{
    await using var scope = _fx.CreateScope();
    var svc = scope.ServiceProvider.GetRequiredService<ICalendarService>();
    var team = await _fx.SeedTeamAsync(scope, "T");
    var uid  = await _fx.SeedUserAsync(scope);
    var zone = NodaTime.DateTimeZoneProviders.Tzdb["Europe/Madrid"];

    var first = new NodaTime.LocalDateTime(2026, 5, 5, 19, 0).InZoneStrictly(zone).ToInstant();

    var ev = await svc.CreateEventAsync(new CreateCalendarEventDto(
        "Weekly", null, null, null, team.Id,
        first, first.Plus(NodaTime.Duration.FromHours(1)),
        false, "FREQ=WEEKLY;BYDAY=TU;COUNT=4", "Europe/Madrid"), uid);

    // Cancel the 3rd occurrence (2026-05-19 19:00 Madrid = 17:00 UTC during DST).
    var cancel = new NodaTime.LocalDateTime(2026, 5, 19, 19, 0).InZoneStrictly(zone).ToInstant();
    await svc.CancelOccurrenceAsync(ev.Id, cancel, uid);

    var occ = await svc.GetOccurrencesInWindowAsync(
        Instant.FromUtc(2026, 5, 1, 0, 0),
        Instant.FromUtc(2026, 6, 1, 0, 0));

    occ.Should().HaveCount(3);
    occ.Select(o => o.OccurrenceStartUtc).Should().NotContain(cancel);
}

[Fact]
public async Task Override_changes_title_and_moves_occurrence()
{
    await using var scope = _fx.CreateScope();
    var svc = scope.ServiceProvider.GetRequiredService<ICalendarService>();
    var team = await _fx.SeedTeamAsync(scope, "T");
    var uid  = await _fx.SeedUserAsync(scope);
    var zone = NodaTime.DateTimeZoneProviders.Tzdb["Europe/Madrid"];

    var first = new NodaTime.LocalDateTime(2026, 5, 5, 19, 0).InZoneStrictly(zone).ToInstant();

    var ev = await svc.CreateEventAsync(new CreateCalendarEventDto(
        "Weekly", null, null, null, team.Id,
        first, first.Plus(NodaTime.Duration.FromHours(1)),
        false, "FREQ=WEEKLY;BYDAY=TU;COUNT=4", "Europe/Madrid"), uid);

    // Move the 2nd occurrence from 19:00 to 20:00.
    var original = new NodaTime.LocalDateTime(2026, 5, 12, 19, 0).InZoneStrictly(zone).ToInstant();
    var moved    = new NodaTime.LocalDateTime(2026, 5, 12, 20, 0).InZoneStrictly(zone).ToInstant();

    await svc.OverrideOccurrenceAsync(ev.Id, original, new OverrideOccurrenceDto(
        OverrideStartUtc: moved,
        OverrideEndUtc:   moved.Plus(NodaTime.Duration.FromHours(1)),
        OverrideTitle:    "Special week",
        OverrideDescription: null,
        OverrideLocation:    null,
        OverrideLocationUrl: null), uid);

    var occ = await svc.GetOccurrencesInWindowAsync(
        Instant.FromUtc(2026, 5, 1, 0, 0),
        Instant.FromUtc(2026, 6, 1, 0, 0));

    occ.Should().HaveCount(4);
    var special = occ.Single(o => o.Title == "Special week");
    special.OccurrenceStartUtc.Should().Be(moved);
    special.OriginalOccurrenceStartUtc.Should().Be(original);
}
```

- [ ] **Step 2: Run — expect fail (cancel/override not implemented yet, plus expansion doesn't apply exceptions)**

- [ ] **Step 3: Apply exceptions in the expansion**

In `GetOccurrencesInWindowAsync`, after building the initial `results` list, add an exception-application pass (do this BEFORE the final sort):

```csharp
// Build a per-event exception lookup once.
var exceptionsByEvent = events
    .ToDictionary(e => e.Id, e =>
        e.Exceptions.ToDictionary(x => x.OriginalOccurrenceStartUtc));

var finalResults = new List<CalendarOccurrence>();
foreach (var occ in results)
{
    if (!occ.IsRecurring || occ.OriginalOccurrenceStartUtc is null)
    {
        finalResults.Add(occ);
        continue;
    }

    if (!exceptionsByEvent.TryGetValue(occ.EventId, out var perEvent) ||
        !perEvent.TryGetValue(occ.OriginalOccurrenceStartUtc.Value, out var ex))
    {
        finalResults.Add(occ);
        continue;
    }

    if (ex.IsCancelled) continue; // drop

    // Apply overrides; if override moves the occurrence outside the window, drop it.
    var newStart = ex.OverrideStartUtc ?? occ.OccurrenceStartUtc;
    var newEnd   = ex.OverrideEndUtc   ?? occ.OccurrenceEndUtc;
    if (newStart > to || (newEnd ?? newStart) < from) continue;

    finalResults.Add(occ with
    {
        OccurrenceStartUtc = newStart,
        OccurrenceEndUtc   = newEnd,
        Title              = ex.OverrideTitle       ?? occ.Title,
        Description        = ex.OverrideDescription ?? occ.Description,
        Location           = ex.OverrideLocation    ?? occ.Location,
        LocationUrl        = ex.OverrideLocationUrl ?? occ.LocationUrl,
    });
}

return finalResults.OrderBy(o => o.OccurrenceStartUtc).ToList();
```

(Replace the existing final `return results.OrderBy(...)` with the new `finalResults.OrderBy(...)`.)

- [ ] **Step 4: Implement `CancelOccurrenceAsync` and `OverrideOccurrenceAsync`**

```csharp
public async Task CancelOccurrenceAsync(Guid eventId, Instant originalOccurrenceStartUtc, Guid userId, CancellationToken ct = default)
{
    await UpsertExceptionAsync(eventId, originalOccurrenceStartUtc, userId, apply: x => x.IsCancelled = true, ct);
}

public async Task OverrideOccurrenceAsync(Guid eventId, Instant originalOccurrenceStartUtc, OverrideOccurrenceDto dto, Guid userId, CancellationToken ct = default)
{
    await UpsertExceptionAsync(eventId, originalOccurrenceStartUtc, userId, apply: x =>
    {
        x.IsCancelled        = false;
        x.OverrideStartUtc   = dto.OverrideStartUtc;
        x.OverrideEndUtc     = dto.OverrideEndUtc;
        x.OverrideTitle      = dto.OverrideTitle;
        x.OverrideDescription = dto.OverrideDescription;
        x.OverrideLocation   = dto.OverrideLocation;
        x.OverrideLocationUrl = dto.OverrideLocationUrl;
    }, ct);
}

private async Task UpsertExceptionAsync(
    Guid eventId, Instant originalUtc, Guid userId,
    Action<CalendarEventException> apply, CancellationToken ct)
{
    var existing = await _db.CalendarEventExceptions
        .FirstOrDefaultAsync(x => x.EventId == eventId && x.OriginalOccurrenceStartUtc == originalUtc, ct);

    var now = _clock.GetCurrentInstant();

    if (existing is null)
    {
        existing = new CalendarEventException
        {
            Id = Guid.NewGuid(),
            EventId = eventId,
            OriginalOccurrenceStartUtc = originalUtc,
            CreatedByUserId = userId,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.CalendarEventExceptions.Add(existing);
    }
    else
    {
        existing.UpdatedAt = now;
    }

    apply(existing);

    var errors = existing.Validate();
    if (errors.Count > 0)
        throw new InvalidOperationException("Exception is invalid: " + string.Join("; ", errors));

    await _db.SaveChangesAsync(ct);
    InvalidateCache();
}
```

- [ ] **Step 5: Run tests**

```bash
dotnet test tests/Humans.Integration.Tests/Humans.Integration.Tests.csproj --filter "FullyQualifiedName~CalendarServiceTests"
```
Expected: the cancel + override tests pass; previous tests still pass.

- [ ] **Step 6: Commit**

```bash
git add src/Humans.Infrastructure/Services/CalendarService.cs tests/Humans.Integration.Tests/Services/CalendarServiceTests.cs
git commit -m "CalendarService: cancel + override occurrences"
```

---

## Task 12: `UpdateEventAsync` + `DeleteEventAsync`

**Files:**
- Modify: `src/Humans.Infrastructure/Services/CalendarService.cs`
- Modify: `tests/Humans.Integration.Tests/Services/CalendarServiceTests.cs`

- [ ] **Step 1: Add tests**

```csharp
[Fact]
public async Task UpdateEvent_changes_fields_and_preserves_id()
{
    await using var scope = _fx.CreateScope();
    var svc = scope.ServiceProvider.GetRequiredService<ICalendarService>();
    var team = await _fx.SeedTeamAsync(scope, "T");
    var uid = await _fx.SeedUserAsync(scope);

    var ev = await svc.CreateEventAsync(new CreateCalendarEventDto(
        "Original", null, null, null, team.Id,
        Instant.FromUtc(2026, 7, 1, 17, 0),
        Instant.FromUtc(2026, 7, 1, 18, 0), false, null, null), uid);

    await svc.UpdateEventAsync(ev.Id, new UpdateCalendarEventDto(
        "Updated", "new desc", "Hall", null, team.Id,
        Instant.FromUtc(2026, 7, 2, 17, 0),
        Instant.FromUtc(2026, 7, 2, 18, 0), false, null, null), uid);

    var fetched = await svc.GetEventByIdAsync(ev.Id);
    fetched!.Title.Should().Be("Updated");
    fetched.Description.Should().Be("new desc");
    fetched.StartUtc.Should().Be(Instant.FromUtc(2026, 7, 2, 17, 0));
}

[Fact]
public async Task DeleteEvent_soft_deletes_and_hides_from_queries()
{
    await using var scope = _fx.CreateScope();
    var svc = scope.ServiceProvider.GetRequiredService<ICalendarService>();
    var team = await _fx.SeedTeamAsync(scope, "T");
    var uid = await _fx.SeedUserAsync(scope);

    var ev = await svc.CreateEventAsync(new CreateCalendarEventDto(
        "ToDelete", null, null, null, team.Id,
        Instant.FromUtc(2026, 8, 1, 10, 0),
        Instant.FromUtc(2026, 8, 1, 11, 0), false, null, null), uid);

    await svc.DeleteEventAsync(ev.Id, uid);

    (await svc.GetEventByIdAsync(ev.Id)).Should().BeNull();
}
```

- [ ] **Step 2: Implement**

```csharp
public async Task<CalendarEvent> UpdateEventAsync(Guid id, UpdateCalendarEventDto dto, Guid updatedByUserId, CancellationToken ct = default)
{
    var ev = await _db.CalendarEvents.FirstOrDefaultAsync(e => e.Id == id, ct)
        ?? throw new InvalidOperationException($"CalendarEvent {id} not found.");

    ev.Title = dto.Title;
    ev.Description = dto.Description;
    ev.Location = dto.Location;
    ev.LocationUrl = dto.LocationUrl;
    ev.OwningTeamId = dto.OwningTeamId;
    ev.StartUtc = dto.StartUtc;
    ev.EndUtc = dto.EndUtc;
    ev.IsAllDay = dto.IsAllDay;
    ev.RecurrenceRule = dto.RecurrenceRule;
    ev.RecurrenceTimezone = dto.RecurrenceTimezone;
    ev.RecurrenceUntilUtc = ComputeRecurrenceUntilUtc(dto.RecurrenceRule, dto.RecurrenceTimezone);
    ev.UpdatedAt = _clock.GetCurrentInstant();

    var errors = ev.Validate();
    if (errors.Count > 0)
        throw new InvalidOperationException("CalendarEvent is invalid: " + string.Join("; ", errors));

    await _db.SaveChangesAsync(ct);
    InvalidateCache();
    return ev;
}

public async Task DeleteEventAsync(Guid id, Guid deletedByUserId, CancellationToken ct = default)
{
    var ev = await _db.CalendarEvents.FirstOrDefaultAsync(e => e.Id == id, ct);
    if (ev is null) return;
    ev.DeletedAt = _clock.GetCurrentInstant();
    ev.UpdatedAt = ev.DeletedAt.Value;
    await _db.SaveChangesAsync(ct);
    InvalidateCache();
}
```

- [ ] **Step 3: Run tests**

```bash
dotnet test tests/Humans.Integration.Tests/Humans.Integration.Tests.csproj --filter "FullyQualifiedName~CalendarServiceTests"
```
Expected: all previous tests pass; the soft-delete test from Task 9 now passes too.

- [ ] **Step 4: Commit**

```bash
git add src/Humans.Infrastructure/Services/CalendarService.cs tests/Humans.Integration.Tests/Services/CalendarServiceTests.cs
git commit -m "CalendarService: update + soft delete"
```

---

## Task 13: Authorization — policy, requirement, handler

**Files:**
- Create: `src/Humans.Application/Authorization/CalendarEditorRequirement.cs`
- Create: `src/Humans.Application/Authorization/CalendarEditorAuthorizationHandler.cs`
- Modify: `src/Humans.Web/Authorization/PolicyNames.cs`
- Modify: `src/Humans.Web/Program.cs` (or `AuthorizationPolicyExtensions.cs` — follow existing pattern)

- [ ] **Step 1: Add the requirement**

```csharp
using Microsoft.AspNetCore.Authorization;

namespace Humans.Application.Authorization;

public sealed class CalendarEditorRequirement : IAuthorizationRequirement { }
```

- [ ] **Step 2: Add the handler**

```csharp
using Humans.Domain.Constants;
using Humans.Domain.Entities;
using Humans.Application.Interfaces;
using Microsoft.AspNetCore.Authorization;

namespace Humans.Application.Authorization;

/// <summary>
/// Succeeds if the user is Admin OR is a coordinator of the supplied owning team.
/// Resource = the owning <see cref="Team"/> of a CalendarEvent.
/// </summary>
public sealed class CalendarEditorAuthorizationHandler
    : AuthorizationHandler<CalendarEditorRequirement, Team>
{
    private readonly ITeamService _teamService;

    public CalendarEditorAuthorizationHandler(ITeamService teamService)
    {
        _teamService = teamService;
    }

    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        CalendarEditorRequirement requirement,
        Team team)
    {
        if (context.User.IsInRole(RoleNames.Admin))
        {
            context.Succeed(requirement);
            return;
        }

        var userIdClaim = context.User.FindFirst("sub")?.Value
            ?? context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        if (!Guid.TryParse(userIdClaim, out var userId)) return;

        if (await _teamService.IsCoordinatorAsync(team.Id, userId))
            context.Succeed(requirement);
    }
}
```

(Confirm the existing claim name used for user ID in this project by inspecting `RoleAssignmentClaimsTransformation.cs` or a similar handler. The snippet above tries both `sub` and `NameIdentifier`; keep whichever matches. If `ITeamService` does not have an `IsCoordinatorAsync(teamId, userId)` method, add it — there is almost certainly an equivalent already; find it via `grep -r "Coordinator" src/Humans.Infrastructure/Services/TeamService.cs`.)

- [ ] **Step 3: Add policy name**

In `src/Humans.Web/Authorization/PolicyNames.cs`:

```csharp
public const string CalendarEditor = nameof(CalendarEditor);
```

- [ ] **Step 4: Register policy + handler**

In the project's policy-registration file (`Program.cs` or `AuthorizationPolicyExtensions.cs`), add:

```csharp
options.AddPolicy(PolicyNames.CalendarEditor, p => p.Requirements.Add(new CalendarEditorRequirement()));
// ...
services.AddScoped<IAuthorizationHandler, CalendarEditorAuthorizationHandler>();
```

- [ ] **Step 5: Build**

```bash
dotnet build Humans.slnx
```
Expected: success.

- [ ] **Step 6: Commit**

```bash
git add src/Humans.Application/Authorization/CalendarEditorRequirement.cs src/Humans.Application/Authorization/CalendarEditorAuthorizationHandler.cs src/Humans.Web/Authorization/PolicyNames.cs src/Humans.Web/Program.cs
git commit -m "Add CalendarEditor authorization policy and handler"
```

---

## Task 14: CalendarController — month view

**Files:**
- Create: `src/Humans.Web/Controllers/CalendarController.cs`
- Create: `src/Humans.Web/Models/Calendar/CalendarMonthViewModel.cs`
- Create: `src/Humans.Web/Views/Calendar/Index.cshtml`

- [ ] **Step 1: Add view model**

```csharp
using Humans.Application.DTOs.Calendar;
using NodaTime;

namespace Humans.Web.Models.Calendar;

public sealed record CalendarMonthViewModel(
    YearMonth Month,
    IReadOnlyList<CalendarOccurrence> Occurrences,
    Guid? FilterTeamId,
    IReadOnlyList<TeamOption> TeamOptions,
    string ViewerTimezoneLabel);

public sealed record TeamOption(Guid Id, string Name);
```

- [ ] **Step 2: Add controller**

```csharp
using Humans.Application.Interfaces;
using Humans.Web.Models.Calendar;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NodaTime;

namespace Humans.Web.Controllers;

[Authorize]
[Route("Calendar")]
public class CalendarController : HumansControllerBase
{
    private readonly ICalendarService _calendar;
    private readonly ITeamService _teams;
    private readonly IClock _clock;

    public CalendarController(ICalendarService calendar, ITeamService teams, IClock clock)
    {
        _calendar = calendar;
        _teams = teams;
        _clock = clock;
    }

    [HttpGet("")]
    public async Task<IActionResult> Index([FromQuery] int? year, [FromQuery] int? month, [FromQuery] Guid? teamId, CancellationToken ct)
    {
        var zone = DateTimeZoneProviders.Tzdb.GetSystemDefault();
        var today = _clock.GetCurrentInstant().InZone(zone).Date;
        var ym = new YearMonth(year ?? today.Year, month ?? today.Month);

        var from = ym.OnDayOfMonth(1).AtMidnight().InZoneLeniently(zone).ToInstant();
        var to   = ym.OnDayOfMonth(ym.Calendar.GetDaysInMonth(ym.Year, ym.Month))
                     .AtMidnight().InZoneLeniently(zone).PlusHours(24).ToInstant();

        var occ = await _calendar.GetOccurrencesInWindowAsync(from, to, teamId, ct);
        var teams = (await _teams.GetAllTeamsAsync()).Select(t => new TeamOption(t.Id, t.Name)).ToList();

        return View(new CalendarMonthViewModel(
            Month: ym,
            Occurrences: occ,
            FilterTeamId: teamId,
            TeamOptions: teams,
            ViewerTimezoneLabel: zone.Id));
    }
}
```

- [ ] **Step 3: Add Razor view**

`src/Humans.Web/Views/Calendar/Index.cshtml`:

```cshtml
@using NodaTime
@model Humans.Web.Models.Calendar.CalendarMonthViewModel
@{
    ViewData["Title"] = $"Calendar — {Model.Month:MMMM yyyy}";
    var zone = DateTimeZoneProviders.Tzdb[Model.ViewerTimezoneLabel];
}

<div class="calendar-header d-flex justify-content-between align-items-center mb-3">
    <h1 class="h3 mb-0">@Model.Month.ToString("MMMM yyyy", System.Globalization.CultureInfo.CurrentCulture)</h1>
    <div>
        <a class="btn btn-sm btn-outline-secondary" asp-action="Index" asp-route-year="@Model.Month.PlusMonths(-1).Year" asp-route-month="@Model.Month.PlusMonths(-1).Month">&larr; Prev</a>
        <a class="btn btn-sm btn-outline-secondary" asp-action="Index">Today</a>
        <a class="btn btn-sm btn-outline-secondary" asp-action="Index" asp-route-year="@Model.Month.PlusMonths(1).Year" asp-route-month="@Model.Month.PlusMonths(1).Month">Next &rarr;</a>
    </div>
</div>

<p class="text-muted small">All times shown in @Model.ViewerTimezoneLabel.</p>

@* Month grid: days-of-week header + week rows. Lay out a 6-row grid, fill each cell with that date's occurrences. *@
<div class="calendar-grid">
    @* Grid rendering: iterate days-of-week header, then weeks. For each day cell, filter Model.Occurrences where occ.OccurrenceStartUtc.InZone(zone).Date == day. Render up to 3 chips; if more, "… N more" linking to Agenda?from=day&to=day+1. All-day events render as full-width bars. *@
</div>

<p class="mt-3">
    <a class="btn btn-primary" asp-action="Create">New event</a>
    <a class="btn btn-link" asp-action="Agenda">Agenda view</a>
</p>
```

(The month grid markup is sketched, not fully written — a skilled dev can flesh it out. Follow the visual conventions of `Views/Camps/Index.cshtml` or another existing grid view for styling.)

- [ ] **Step 4: Manual smoke**

```bash
dotnet run --project src/Humans.Web
```
Navigate to `http://localhost:5000/Calendar` (or the port the project uses) after logging in via dev-login. Expected: page renders (even if sparse) and shows the month header + TZ label.

- [ ] **Step 5: Commit**

```bash
git add src/Humans.Web/Controllers/CalendarController.cs src/Humans.Web/Models/Calendar/CalendarMonthViewModel.cs src/Humans.Web/Views/Calendar/Index.cshtml
git commit -m "Calendar: month view"
```

---

## Task 15: Agenda view + team-filtered view

**Files:**
- Modify: `src/Humans.Web/Controllers/CalendarController.cs`
- Create: `src/Humans.Web/Models/Calendar/CalendarAgendaViewModel.cs`
- Create: `src/Humans.Web/Views/Calendar/Agenda.cshtml`
- Create: `src/Humans.Web/Views/Calendar/Team.cshtml`

- [ ] **Step 1: Add view model**

```csharp
using Humans.Application.DTOs.Calendar;
using NodaTime;

namespace Humans.Web.Models.Calendar;

public sealed record CalendarAgendaViewModel(
    Instant FromUtc,
    Instant ToUtc,
    IReadOnlyList<CalendarOccurrence> Occurrences,
    Guid? FilterTeamId,
    string ViewerTimezoneLabel);
```

- [ ] **Step 2: Add controller actions**

Append to `CalendarController`:

```csharp
[HttpGet("Agenda")]
public async Task<IActionResult> Agenda([FromQuery] DateTime? from, [FromQuery] DateTime? to, [FromQuery] Guid? teamId, CancellationToken ct)
{
    var zone = DateTimeZoneProviders.Tzdb.GetSystemDefault();
    var today = _clock.GetCurrentInstant().InZone(zone).Date;
    var start = from is null ? today : LocalDate.FromDateTime(from.Value);
    var end   = to   is null ? today.PlusDays(60) : LocalDate.FromDateTime(to.Value);

    var fromUtc = start.AtMidnight().InZoneLeniently(zone).ToInstant();
    var toUtc   = end.PlusDays(1).AtMidnight().InZoneLeniently(zone).ToInstant();

    var occ = await _calendar.GetOccurrencesInWindowAsync(fromUtc, toUtc, teamId, ct);
    return View(new CalendarAgendaViewModel(fromUtc, toUtc, occ, teamId, zone.Id));
}

[HttpGet("Team/{teamId:guid}")]
public async Task<IActionResult> Team(Guid teamId, [FromQuery] int? year, [FromQuery] int? month, CancellationToken ct)
{
    var zone = DateTimeZoneProviders.Tzdb.GetSystemDefault();
    var today = _clock.GetCurrentInstant().InZone(zone).Date;
    var ym = new YearMonth(year ?? today.Year, month ?? today.Month);

    var from = ym.OnDayOfMonth(1).AtMidnight().InZoneLeniently(zone).ToInstant();
    var to   = ym.OnDayOfMonth(ym.Calendar.GetDaysInMonth(ym.Year, ym.Month))
                 .AtMidnight().InZoneLeniently(zone).PlusHours(24).ToInstant();

    var occ = await _calendar.GetOccurrencesInWindowAsync(from, to, teamId, ct);
    var team = await _teams.GetTeamByIdAsync(teamId);
    if (team is null) return NotFound();

    ViewData["TeamName"] = team.Name;
    return View(new CalendarMonthViewModel(ym, occ, teamId, Array.Empty<TeamOption>(), zone.Id));
}
```

- [ ] **Step 3: Add views**

`Views/Calendar/Agenda.cshtml` — chronological list grouped by date, each entry showing time, title, owning team, location, short description. Follow the list-view styling from e.g. `Views/Notifications/Index.cshtml`.

`Views/Calendar/Team.cshtml` — reuse the month grid from `Index.cshtml` (copy then strip the team filter dropdown) with a header showing the team name.

- [ ] **Step 4: Smoke run**

```bash
dotnet run --project src/Humans.Web
```
Open `/Calendar/Agenda` and `/Calendar/Team/<a-known-team-guid>`.

- [ ] **Step 5: Commit**

```bash
git add src/Humans.Web/Controllers/CalendarController.cs src/Humans.Web/Models/Calendar/CalendarAgendaViewModel.cs src/Humans.Web/Views/Calendar/Agenda.cshtml src/Humans.Web/Views/Calendar/Team.cshtml
git commit -m "Calendar: agenda + team views"
```

---

## Task 16: Event detail view

**Files:**
- Modify: `src/Humans.Web/Controllers/CalendarController.cs`
- Create: `src/Humans.Web/Models/Calendar/CalendarEventViewModel.cs`
- Create: `src/Humans.Web/Views/Calendar/Event.cshtml`

- [ ] **Step 1: View model**

```csharp
using Humans.Application.DTOs.Calendar;
using Humans.Domain.Entities;
using NodaTime;

namespace Humans.Web.Models.Calendar;

public sealed record CalendarEventViewModel(
    CalendarEvent Event,
    IReadOnlyList<CalendarOccurrence> UpcomingOccurrences,
    bool CanEdit,
    string ViewerTimezoneLabel);
```

- [ ] **Step 2: Controller action**

```csharp
[HttpGet("Event/{id:guid}")]
public async Task<IActionResult> Event(Guid id, CancellationToken ct)
{
    var ev = await _calendar.GetEventByIdAsync(id, ct);
    if (ev is null) return NotFound();

    var zone = DateTimeZoneProviders.Tzdb.GetSystemDefault();
    var now = _clock.GetCurrentInstant();
    var horizon = now.Plus(Duration.FromDays(180));
    var upcoming = (await _calendar.GetOccurrencesInWindowAsync(now, horizon, ev.OwningTeamId, ct))
        .Where(o => o.EventId == id)
        .Take(5)
        .ToList();

    var canEdit = (await _authz.AuthorizeAsync(User, ev.OwningTeam, PolicyNames.CalendarEditor)).Succeeded;

    return View(new CalendarEventViewModel(ev, upcoming, canEdit, zone.Id));
}
```

(Inject `IAuthorizationService _authz` into the controller; it follows the same constructor pattern used in other controllers — look at `BudgetController` for the exact injection shape.)

- [ ] **Step 3: Razor view**

`Views/Calendar/Event.cshtml` — title, description, location (linked if URL), owning team link, first/next start with TZ label, recurrence summary (use `Ical.Net`'s `RecurrencePattern.ToString()` or a small helper), list of next 5 upcoming occurrences with per-occurrence Cancel/Edit buttons (visible only when `Model.CanEdit`), top-right Edit/Delete buttons.

- [ ] **Step 4: Commit**

```bash
git add src/Humans.Web/Controllers/CalendarController.cs src/Humans.Web/Models/Calendar/CalendarEventViewModel.cs src/Humans.Web/Views/Calendar/Event.cshtml
git commit -m "Calendar: event detail view"
```

---

## Task 17: Create / Edit / Delete event

**Files:**
- Modify: `src/Humans.Web/Controllers/CalendarController.cs`
- Create: `src/Humans.Web/Models/Calendar/CalendarEventFormViewModel.cs`
- Create: `src/Humans.Web/Views/Calendar/Create.cshtml`
- Create: `src/Humans.Web/Views/Calendar/Edit.cshtml`

- [ ] **Step 1: Form view model**

```csharp
using System.ComponentModel.DataAnnotations;

namespace Humans.Web.Models.Calendar;

public class CalendarEventFormViewModel
{
    public Guid? Id { get; set; }

    [Required, StringLength(200)]
    public string Title { get; set; } = string.Empty;

    [StringLength(4000)]
    public string? Description { get; set; }

    [StringLength(500)]
    public string? Location { get; set; }

    [StringLength(2000), Url]
    public string? LocationUrl { get; set; }

    [Required]
    public Guid OwningTeamId { get; set; }

    [Required]
    public DateTime StartLocal { get; set; }

    public DateTime? EndLocal { get; set; }

    public bool IsAllDay { get; set; }

    public bool IsRecurring { get; set; }

    public string? RecurrenceRule { get; set; } // assembled from UI controls client-side; raw-edit also allowed for admins

    public string RecurrenceTimezone { get; set; } = "Europe/Madrid";

    public IReadOnlyList<TeamOption> TeamOptions { get; set; } = Array.Empty<TeamOption>();
}
```

- [ ] **Step 2: Controller actions**

```csharp
[HttpGet("Event/Create")]
public async Task<IActionResult> Create([FromQuery] Guid? teamId, CancellationToken ct)
{
    var teams = await GetEditableTeamsForCurrentUserAsync(ct);
    if (teams.Count == 0) return Forbid();

    return View(new CalendarEventFormViewModel
    {
        OwningTeamId = teamId ?? teams[0].Id,
        StartLocal = DateTime.Today.AddHours(19),
        EndLocal   = DateTime.Today.AddHours(20),
        TeamOptions = teams,
    });
}

[HttpPost("Event/Create")]
[ValidateAntiForgeryToken]
public async Task<IActionResult> Create(CalendarEventFormViewModel form, CancellationToken ct)
{
    var team = await _teams.GetTeamByIdAsync(form.OwningTeamId);
    if (team is null) return NotFound();
    var authz = await _authz.AuthorizeAsync(User, team, PolicyNames.CalendarEditor);
    if (!authz.Succeeded) return Forbid();

    if (!ModelState.IsValid)
    {
        form.TeamOptions = await GetEditableTeamsForCurrentUserAsync(ct);
        return View(form);
    }

    var zone = DateTimeZoneProviders.Tzdb[form.RecurrenceTimezone];
    var start = LocalDateTime.FromDateTime(form.StartLocal).InZoneStrictly(zone).ToInstant();
    Instant? end = form.EndLocal is { } elo
        ? LocalDateTime.FromDateTime(elo).InZoneStrictly(zone).ToInstant()
        : null;

    var ev = await _calendar.CreateEventAsync(new CreateCalendarEventDto(
        form.Title, form.Description, form.Location, form.LocationUrl,
        form.OwningTeamId, start, end, form.IsAllDay,
        form.IsRecurring ? form.RecurrenceRule : null,
        form.IsRecurring ? form.RecurrenceTimezone : null),
        createdByUserId: GetCurrentUserId(), ct);

    return RedirectToAction(nameof(Event), new { id = ev.Id });
}

[HttpGet("Event/{id:guid}/Edit")]
public async Task<IActionResult> Edit(Guid id, CancellationToken ct)
{
    var ev = await _calendar.GetEventByIdAsync(id, ct);
    if (ev is null) return NotFound();
    var authz = await _authz.AuthorizeAsync(User, ev.OwningTeam, PolicyNames.CalendarEditor);
    if (!authz.Succeeded) return Forbid();

    var zone = DateTimeZoneProviders.Tzdb[ev.RecurrenceTimezone ?? "Europe/Madrid"];
    return View(new CalendarEventFormViewModel
    {
        Id = ev.Id,
        Title = ev.Title,
        Description = ev.Description,
        Location = ev.Location,
        LocationUrl = ev.LocationUrl,
        OwningTeamId = ev.OwningTeamId,
        StartLocal = ev.StartUtc.InZone(zone).LocalDateTime.ToDateTimeUnspecified(),
        EndLocal = ev.EndUtc?.InZone(zone).LocalDateTime.ToDateTimeUnspecified(),
        IsAllDay = ev.IsAllDay,
        IsRecurring = ev.RecurrenceRule is not null,
        RecurrenceRule = ev.RecurrenceRule,
        RecurrenceTimezone = ev.RecurrenceTimezone ?? "Europe/Madrid",
        TeamOptions = await GetEditableTeamsForCurrentUserAsync(ct),
    });
}

[HttpPost("Event/{id:guid}/Edit")]
[ValidateAntiForgeryToken]
public async Task<IActionResult> Edit(Guid id, CalendarEventFormViewModel form, CancellationToken ct)
{
    var ev = await _calendar.GetEventByIdAsync(id, ct);
    if (ev is null) return NotFound();
    var authz = await _authz.AuthorizeAsync(User, ev.OwningTeam, PolicyNames.CalendarEditor);
    if (!authz.Succeeded) return Forbid();

    if (!ModelState.IsValid)
    {
        form.TeamOptions = await GetEditableTeamsForCurrentUserAsync(ct);
        return View(form);
    }

    var zone = DateTimeZoneProviders.Tzdb[form.RecurrenceTimezone];
    var start = LocalDateTime.FromDateTime(form.StartLocal).InZoneStrictly(zone).ToInstant();
    Instant? end = form.EndLocal is { } elo
        ? LocalDateTime.FromDateTime(elo).InZoneStrictly(zone).ToInstant()
        : null;

    await _calendar.UpdateEventAsync(id, new UpdateCalendarEventDto(
        form.Title, form.Description, form.Location, form.LocationUrl,
        form.OwningTeamId, start, end, form.IsAllDay,
        form.IsRecurring ? form.RecurrenceRule : null,
        form.IsRecurring ? form.RecurrenceTimezone : null),
        updatedByUserId: GetCurrentUserId(), ct);

    return RedirectToAction(nameof(Event), new { id });
}

[HttpPost("Event/{id:guid}/Delete")]
[ValidateAntiForgeryToken]
public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
{
    var ev = await _calendar.GetEventByIdAsync(id, ct);
    if (ev is null) return NotFound();
    var authz = await _authz.AuthorizeAsync(User, ev.OwningTeam, PolicyNames.CalendarEditor);
    if (!authz.Succeeded) return Forbid();

    await _calendar.DeleteEventAsync(id, deletedByUserId: GetCurrentUserId(), ct);
    return RedirectToAction(nameof(Index));
}

private async Task<IReadOnlyList<TeamOption>> GetEditableTeamsForCurrentUserAsync(CancellationToken ct)
{
    if (User.IsInRole(RoleNames.Admin))
        return (await _teams.GetAllTeamsAsync()).Select(t => new TeamOption(t.Id, t.Name)).ToList();

    var uid = GetCurrentUserId();
    var coordinated = await _teams.GetTeamsCoordinatedByUserAsync(uid);
    return coordinated.Select(t => new TeamOption(t.Id, t.Name)).ToList();
}
```

(If `ITeamService.GetTeamsCoordinatedByUserAsync` doesn't exist, add it — grep the service for existing coordinator-lookup methods and extend the closest match. `GetCurrentUserId()` is the base-controller helper that other controllers use; confirm the name by opening `HumansControllerBase`.)

- [ ] **Step 3: Views**

`Create.cshtml` and `Edit.cshtml` — standard form with the recurrence builder (see design doc §Create/edit form for the controls spec). Start with a plain form and add the recurrence builder incrementally; a first cut can render just a raw-RRULE textbox and be upgraded later.

- [ ] **Step 4: Smoke run**

Create an event, edit it, delete it — verify each.

- [ ] **Step 5: Commit**

```bash
git add src/Humans.Web/Controllers/CalendarController.cs src/Humans.Web/Models/Calendar/CalendarEventFormViewModel.cs src/Humans.Web/Views/Calendar/Create.cshtml src/Humans.Web/Views/Calendar/Edit.cshtml
git commit -m "Calendar: create/edit/delete events"
```

---

## Task 18: Per-occurrence cancel + override

**Files:**
- Modify: `src/Humans.Web/Controllers/CalendarController.cs`
- Create: `src/Humans.Web/Models/Calendar/OccurrenceOverrideFormViewModel.cs`
- Create: `src/Humans.Web/Views/Calendar/OccurrenceEdit.cshtml`

- [ ] **Step 1: Form view model**

```csharp
using NodaTime;
using NodaTime.Text;

namespace Humans.Web.Models.Calendar;

public class OccurrenceOverrideFormViewModel
{
    public Guid EventId { get; set; }

    /// <summary>
    /// ISO-8601 UTC string (e.g. 2026-05-12T17:00:00Z). Used as the URL segment.
    /// </summary>
    public string OriginalOccurrenceStartUtc { get; set; } = string.Empty;

    public DateTime? OverrideStartLocal { get; set; }
    public DateTime? OverrideEndLocal { get; set; }
    public string? OverrideTitle { get; set; }
    public string? OverrideDescription { get; set; }
    public string? OverrideLocation { get; set; }
    public string? OverrideLocationUrl { get; set; }

    public string RecurrenceTimezone { get; set; } = "Europe/Madrid";

    public static Instant ParseOriginal(string s) =>
        InstantPattern.ExtendedIso.Parse(s).Value;
}
```

- [ ] **Step 2: Controller actions**

```csharp
[HttpPost("Event/{id:guid}/Occurrence/{originalStartUtc}/Cancel")]
[ValidateAntiForgeryToken]
public async Task<IActionResult> CancelOccurrence(Guid id, string originalStartUtc, CancellationToken ct)
{
    var ev = await _calendar.GetEventByIdAsync(id, ct);
    if (ev is null) return NotFound();
    var authz = await _authz.AuthorizeAsync(User, ev.OwningTeam, PolicyNames.CalendarEditor);
    if (!authz.Succeeded) return Forbid();

    var original = OccurrenceOverrideFormViewModel.ParseOriginal(originalStartUtc);
    await _calendar.CancelOccurrenceAsync(id, original, GetCurrentUserId(), ct);
    return RedirectToAction(nameof(Event), new { id });
}

[HttpGet("Event/{id:guid}/Occurrence/{originalStartUtc}/Edit")]
public async Task<IActionResult> EditOccurrence(Guid id, string originalStartUtc, CancellationToken ct)
{
    var ev = await _calendar.GetEventByIdAsync(id, ct);
    if (ev is null) return NotFound();
    var authz = await _authz.AuthorizeAsync(User, ev.OwningTeam, PolicyNames.CalendarEditor);
    if (!authz.Succeeded) return Forbid();

    return View("OccurrenceEdit", new OccurrenceOverrideFormViewModel
    {
        EventId = id,
        OriginalOccurrenceStartUtc = originalStartUtc,
        RecurrenceTimezone = ev.RecurrenceTimezone ?? "Europe/Madrid",
    });
}

[HttpPost("Event/{id:guid}/Occurrence/{originalStartUtc}/Edit")]
[ValidateAntiForgeryToken]
public async Task<IActionResult> EditOccurrence(Guid id, string originalStartUtc, OccurrenceOverrideFormViewModel form, CancellationToken ct)
{
    var ev = await _calendar.GetEventByIdAsync(id, ct);
    if (ev is null) return NotFound();
    var authz = await _authz.AuthorizeAsync(User, ev.OwningTeam, PolicyNames.CalendarEditor);
    if (!authz.Succeeded) return Forbid();

    var zone = DateTimeZoneProviders.Tzdb[form.RecurrenceTimezone];
    var original = OccurrenceOverrideFormViewModel.ParseOriginal(originalStartUtc);

    Instant? overrideStart = form.OverrideStartLocal is { } s
        ? LocalDateTime.FromDateTime(s).InZoneStrictly(zone).ToInstant()
        : null;
    Instant? overrideEnd = form.OverrideEndLocal is { } e
        ? LocalDateTime.FromDateTime(e).InZoneStrictly(zone).ToInstant()
        : null;

    await _calendar.OverrideOccurrenceAsync(id, original,
        new OverrideOccurrenceDto(overrideStart, overrideEnd,
            form.OverrideTitle, form.OverrideDescription,
            form.OverrideLocation, form.OverrideLocationUrl),
        GetCurrentUserId(), ct);

    return RedirectToAction(nameof(Event), new { id });
}
```

- [ ] **Step 3: View**

`Views/Calendar/OccurrenceEdit.cshtml` — simple form editing the six override fields. Clear hint that leaving a field blank inherits from the parent event.

- [ ] **Step 4: Commit**

```bash
git add src/Humans.Web/Controllers/CalendarController.cs src/Humans.Web/Models/Calendar/OccurrenceOverrideFormViewModel.cs src/Humans.Web/Views/Calendar/OccurrenceEdit.cshtml
git commit -m "Calendar: per-occurrence cancel/override"
```

---

## Task 19: Controller integration tests (authorization)

**Files:**
- Create: `tests/Humans.Integration.Tests/Controllers/CalendarControllerTests.cs`

- [ ] **Step 1: Write the tests**

Follow the existing controller-integration test pattern (look at `tests/Humans.Integration.Tests/Controllers/BudgetControllerTests.cs` or similar to match the fixture for authenticated requests).

```csharp
using System.Net;
using FluentAssertions;
using Xunit;

namespace Humans.Integration.Tests.Controllers;

public class CalendarControllerTests : IClassFixture<WebFixture>
{
    private readonly WebFixture _fx;
    public CalendarControllerTests(WebFixture fx) => _fx = fx;

    [Fact]
    public async Task GET_Calendar_returns_200_for_logged_in_human()
    {
        var client = await _fx.AuthenticatedClientAsync("human@example.com", roles: Array.Empty<string>());
        var resp = await client.GetAsync("/Calendar");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task GET_Calendar_redirects_to_login_for_anonymous()
    {
        using var client = _fx.AnonymousClient();
        var resp = await client.GetAsync("/Calendar");
        resp.StatusCode.Should().BeOneOf(HttpStatusCode.Redirect, HttpStatusCode.Found, HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task POST_Edit_returns_403_when_not_coordinator_of_owning_team()
    {
        var (client, eventId) = await _fx.SeedEventOwnedByOtherTeamAsync("human@example.com");
        var resp = await client.PostAsJsonAsync($"/Calendar/Event/{eventId}/Edit", new { Title = "Hack" });
        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
```

(If `WebFixture` or its helpers aren't already there, build on the existing integration harness. Adjust to the project's actual auth helper — dev login / cookie-based.)

- [ ] **Step 2: Run, implement fixture helpers as needed, make it pass**

```bash
dotnet test tests/Humans.Integration.Tests/Humans.Integration.Tests.csproj --filter "FullyQualifiedName~CalendarControllerTests"
```

- [ ] **Step 3: Commit**

```bash
git add tests/Humans.Integration.Tests/
git commit -m "Calendar: controller integration tests"
```

---

## Task 20: Nav links

**Files:**
- Modify: `src/Humans.Web/Views/Shared/_Layout.cshtml` (or whichever file hosts the top nav)
- Modify: the team details view (e.g. `src/Humans.Web/Views/Team/Details.cshtml`)

- [ ] **Step 1: Top nav link**

Locate the top-nav partial used by the site and add an item linking to `/Calendar` (label: "Calendar"). Place it near related items (Teams, Shifts).

- [ ] **Step 2: Team details link**

On the team detail view, add a "Team calendar" link to `/Calendar/Team/{team.Id}`.

- [ ] **Step 3: Smoke**

Reload the site — verify the Calendar link appears in the nav and clicking it loads the month view.

- [ ] **Step 4: Commit**

```bash
git add src/Humans.Web/Views/Shared/_Layout.cshtml src/Humans.Web/Views/Team/
git commit -m "Calendar: nav link + team calendar link"
```

---

## Task 21: Section invariants doc + feature spec

**Files:**
- Create: `docs/sections/Calendar.md`
- Create: `docs/features/39-community-calendar.md`

- [ ] **Step 1: Write `docs/sections/Calendar.md`**

Follow the shape of the existing section docs (e.g. `docs/sections/Shifts.md`). Required sections:

- **Actors & Roles** — Admin (full access), Coordinators of the owning team (CRUD on their own team's events), logged-in humans (view only).
- **Invariants** —
  - Every `CalendarEvent` has a non-null `OwningTeamId`.
  - Only users with Admin OR coordinator role on the owning team may create/edit/delete events or their occurrences.
  - `RecurrenceRule` and `RecurrenceTimezone` are set together or not at all.
  - `EndUtc` is required iff `IsAllDay = false`.
  - `StartUtc <= EndUtc` when `EndUtc` is present.
  - `CalendarEventException` entries cascade-delete with their parent event.
- **Triggers** — none in v1.
- **Cross-Section Dependencies** — Teams (owning team), Users (audit).

- [ ] **Step 2: Write `docs/features/39-community-calendar.md`**

Follow the shape of an existing feature doc (e.g. `docs/features/31-budget.md`). Cover the v1 surface and reference the post-v1 slice list from the spec.

- [ ] **Step 3: Commit**

```bash
git add docs/sections/Calendar.md docs/features/39-community-calendar.md
git commit -m "Calendar: section + feature docs"
```

---

## Task 22: About page + data model update

**Files:**
- Modify: `src/Humans.Web/Views/About/Index.cshtml`
- Modify: `docs/architecture/data-model.md`
- Modify: `docs/architecture/maintenance-log.md`

- [ ] **Step 1: Add Ical.Net to About page**

Open `src/Humans.Web/Views/About/Index.cshtml`, find the NuGet package list, and add a row for `Ical.Net` with its version and license (MIT).

- [ ] **Step 2: Add entities to data-model.md**

Add `CalendarEvent` and `CalendarEventException` to the entities section. Note the owning-team FK and the cascade-delete relation between event and exception.

- [ ] **Step 3: Append to maintenance-log.md**

Under the current date, note: "Added community calendar slice 1 — new entities `CalendarEvent`, `CalendarEventException`; Ical.Net package added."

- [ ] **Step 4: Commit**

```bash
git add src/Humans.Web/Views/About/Index.cshtml docs/architecture/
git commit -m "Calendar: About + data-model + maintenance-log updates"
```

---

## Task 23: E2E smoke test

**Files:**
- Create: `tests/e2e/tests/calendar.spec.ts`

- [ ] **Step 1: Write the spec**

Follow the existing Playwright suite pattern (`tests/e2e/tests/*.spec.ts`). A minimum smoke:

```typescript
import { test, expect } from '@playwright/test';
import { loginAsHuman } from './helpers/auth';

test('calendar month view renders for logged-in human', async ({ page }) => {
  await loginAsHuman(page);
  await page.goto('/Calendar');
  await expect(page.getByRole('heading', { level: 1 })).toBeVisible();
  await expect(page.locator('text=All times shown in')).toBeVisible();
});
```

(`loginAsHuman` — use whatever helper the existing suite provides.)

- [ ] **Step 2: Run the suite**

```bash
cd tests/e2e && npx playwright test calendar.spec.ts
```
Expected: pass.

- [ ] **Step 3: Commit**

```bash
git add tests/e2e/tests/calendar.spec.ts
git commit -m "Calendar: E2E smoke test"
```

---

## Task 24: Final verification + PR

- [ ] **Step 1: Full build + test**

```bash
dotnet build Humans.slnx
dotnet test Humans.slnx
```
Expected: green.

- [ ] **Step 2: Manual pass in browser**

- Log in as Admin → create a recurring event on any team → verify it shows on month view and detail → cancel one occurrence → verify it disappears → edit one occurrence → verify it moves/renames → delete the whole event → verify it's gone.
- Log in as a non-coordinator human → verify no create button visible, `/Calendar/Event/Create` returns 403, month view still renders.

- [ ] **Step 3: Push and update PR #238**

```bash
git push origin design/community-calendar-513
```

The existing PR #238 picks up the new commits. Add a comment summarising what shipped:

```bash
gh pr comment 238 --repo peterdrier/Humans --body "Slice 1 implementation complete (tasks 1–24). Build + tests green. See commits $(git log --oneline upstream/main..HEAD | head -30)."
```

- [ ] **Step 4: Open post-v1 follow-up issues**

Create skeleton issues for Slices 2–5 (module aggregation, audience scoping, iCal feed + public view, "my calendar" + notifications). Each references the design doc.

```bash
gh issue create --repo nobodies-collective/Humans \
  --title "Community calendar slice 2: module aggregation" \
  --label "section:calendar,enhancement,blocked:needs-design" \
  --body "Follow-up to #513 slice 1. Aggregate events from Shifts, Camps, Budget, Tickets, Governance, Onboarding, Campaigns, Legal into the calendar via ICalendarContributor interface. See docs/superpowers/specs/2026-04-21-community-calendar-design.md § Post-v1 Slicing."
# ... repeat for slices 3, 4, 5
```

---

## Self-Review

**Spec coverage check:**

| Spec section | Task(s) |
|---|---|
| CalendarEvent entity | 2 |
| CalendarEventException entity | 3 |
| EF configuration + invariants | 4 |
| Migration | 5 |
| ICalendarService interface + DTOs | 6, 7 |
| Create / get | 8 |
| Window query (non-recurring) | 9 |
| Window query (recurring, DST-safe) | 10 |
| Exception expansion | 11 |
| Update / soft delete | 12 |
| CalendarEditor authorization | 13 |
| Month view | 14 |
| Agenda + team views | 15 |
| Event detail | 16 |
| Create / edit / delete UI | 17 |
| Per-occurrence cancel / override UI | 18 |
| Controller authz tests | 19 |
| Nav link + team-calendar link | 20 |
| Section invariant doc + feature spec | 21 |
| About page + data-model + maintenance-log | 22 |
| E2E smoke | 23 |
| Final verification + PR | 24 |

All v1 spec sections have a task. Post-v1 slices are tracked as follow-up issues (Task 24 step 4), not implemented here.

**Placeholder scan:** The plan uses forward references ("a skilled dev can flesh it out" on Razor grid markup, "confirm the claim name by inspecting …") in places where the exact project-specific detail is ambiguous without further source reading. These are flagged to the implementer with specific files to check, not generic hand-waves.

**Type consistency:** Method names match between interface (Task 6), skeleton (Task 7), and implementation (Tasks 8–12). DTO field names match across tests and implementation.

---

## Execution Handoff

Plan complete and saved to `docs/superpowers/plans/2026-04-21-community-calendar-slice1.md`. Two execution options:

1. **Subagent-Driven (recommended)** — I dispatch a fresh subagent per task, review between tasks, fast iteration. Good fit for a 24-task plan with clear TDD boundaries.

2. **Inline Execution** — Execute tasks in this session using executing-plans, batch execution with checkpoints for review.

Which approach?
