# Dietary & Medical Nudge — Implementation Plan

> **For agentic workers:** REQUIRED: Use superpowers:subagent-driven-development (if subagents available) or superpowers:executing-plans to implement this plan. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Surface a dashboard nudge that prompts humans to record their dietary preference, allergies, intolerances, and medical conditions when (and only when) they have an active 6+ hour shift signup. Save the data to existing `VolunteerEventProfile` columns — no migration.

**Architecture:** Reuse the existing `IShiftManagementService` surface (`GetShiftProfileAsync` / `UpdateShiftProfileAsync` + new `HasQualifyingCantinaSignupAsync`). A new pure helper `Shift.QualifiesForCantinaMeal()` keeps the qualifying-shift rule co-located with the entity. `ProfileController` gains a new `DietaryMedical` GET/POST action pair backed by a standalone Razor view (full-page form — no HTMX, no modal). `ThingsToDoViewComponent` narrows its `IsShiftProfileEmpty` helper to skills/quirks/languages only and adds a separate gated branch for the dietary/medical item.

**Tech Stack:** .NET 10 (`Humans.slnx`), EF Core + PostgreSQL (`Npgsql`), NodaTime + `NodaTime.Testing`, ASP.NET Core MVC + Razor, xUnit v3 with `[HumansFact]`/`[HumansTheory]`, `AwesomeAssertions`, `NSubstitute`. Resource strings in 6 files: `SharedResource.resx` (English base) + `SharedResource.ca.resx`, `.de.resx`, `.es.resx`, `.fr.resx`, `.it.resx`.

**Spec:** [`docs/features/profiles/dietary-medical-nudge.md`](../../features/profiles/dietary-medical-nudge.md) (PR #606).

**Branch:** `feature/issue-279-dietary-nudge-impl` (worktree at `.worktrees/impl-279/`, branched off `feature/issue-279-dietary-nudge`).

---

## File Map

### Files created (new)
- `src/Humans.Web/Models/DietaryMedicalViewModel.cs` *(flat `Models/`, not a subdir — matches `ShiftInfoViewModel` placement)*
- `src/Humans.Web/Views/Profile/DietaryMedical.cshtml`

### Files modified
**Domain:**
- `src/Humans.Domain/Entities/Shift.cs` — add `QualifiesForCantinaMeal()` pure helper
- `tests/Humans.Domain.Tests/Entities/ShiftTests.cs` — helper tests

**Application:**
- `src/Humans.Application/Interfaces/Shifts/IShiftManagementService.cs` — add `HasQualifyingCantinaSignupAsync`
- `src/Humans.Application/Services/Shifts/ShiftManagementService.cs` — implement it
- `src/Humans.Application/Interfaces/Repositories/IShiftManagementRepository.cs` — add `GetUserActiveSignupsForCantinaGateAsync`
- `tests/Humans.Application.Tests/Services/Shifts/ShiftManagementServiceTests.cs` — service tests

**Infrastructure:**
- `src/Humans.Infrastructure/Repositories/Shifts/ShiftManagementRepository.cs` — implement the new repo method

**Web:**
- `src/Humans.Web/Controllers/ProfileController.cs` — new `DietaryMedical` GET/POST
- `src/Humans.Web/ViewComponents/ThingsToDoViewComponent.cs` — narrow `IsShiftProfileEmpty`, add dietary/medical branch
- `src/Humans.Web/Views/Profile/Index.cshtml` — add "Dietary & medical info" link to the profile-nav list (Shift Info / Emails / Communication Preferences / Governance / Privacy / My Emails)
- `src/Humans.Web/Resources/SharedResource.resx` (en) + the six locale variants — add `Todo_DietaryMedical_*` keys and form-label keys

**Docs:**
- `docs/features/profiles/dietary-medical-nudge.md` — fix route from `/Profile/DietaryMedical` to `/Profile/Me/DietaryMedical` to match the project's `[HttpGet("Me/<area>")]` convention
- `docs/sections/profile.md` *(if exists)* — note the new edit surface

---

## Task 1: Domain — `Shift.QualifiesForCantinaMeal()` helper

**Files:**
- Modify: `src/Humans.Domain/Entities/Shift.cs`
- Test: `tests/Humans.Domain.Tests/Entities/ShiftTests.cs`

- [ ] **Step 1: Write the failing tests**

Add to `ShiftTests.cs`:

```csharp
[HumansFact]
public void QualifiesForCantinaMeal_AllDayShift_ReturnsTrue()
{
    var shift = CreateShift();
    shift.IsAllDay = true;
    shift.Duration = Duration.FromHours(0);

    shift.QualifiesForCantinaMeal().Should().BeTrue();
}

[HumansFact]
public void QualifiesForCantinaMeal_SixHourShift_ReturnsTrue()
{
    var shift = CreateShift();
    shift.IsAllDay = false;
    shift.Duration = Duration.FromHours(6);

    shift.QualifiesForCantinaMeal().Should().BeTrue();
}

[HumansFact]
public void QualifiesForCantinaMeal_FiveHourFiftyNineMinuteShift_ReturnsFalse()
{
    var shift = CreateShift();
    shift.IsAllDay = false;
    shift.Duration = Duration.FromMinutes(359);

    shift.QualifiesForCantinaMeal().Should().BeFalse();
}

[HumansFact]
public void QualifiesForCantinaMeal_ShortAllDayShift_StillQualifies()
{
    var shift = CreateShift();
    shift.IsAllDay = true;
    shift.Duration = Duration.FromHours(2);

    shift.QualifiesForCantinaMeal().Should().BeTrue();
}
```

- [ ] **Step 2: Run tests, confirm they fail**

```bash
cd .worktrees/impl-279
dotnet test tests/Humans.Domain.Tests/Humans.Domain.Tests.csproj -v quiet --filter "FullyQualifiedName~QualifiesForCantinaMeal"
```

Expected: **build error** — `'Shift' does not contain a definition for 'QualifiesForCantinaMeal'`. `dotnet test` aborts on build failures before reporting test results, so this is normal.

- [ ] **Step 3: Implement the helper**

In `Shift.cs`, add immediately after `GetAbsoluteEnd(EventSettings)`:

```csharp
/// <summary>
/// True when this shift qualifies a volunteer for cantina meal planning.
/// All-day shifts always qualify (08:00–18:00 = 10h);
/// timed shifts qualify when <see cref="Duration"/> is at least 6 hours.
/// Pure helper — no DB hit, no clock, no <see cref="EventSettings"/> needed.
/// See: docs/features/profiles/dietary-medical-nudge.md
/// </summary>
public bool QualifiesForCantinaMeal() =>
    IsAllDay || Duration >= Duration.FromHours(6);
```

- [ ] **Step 4: Run tests, confirm they pass**

```bash
dotnet test tests/Humans.Domain.Tests/Humans.Domain.Tests.csproj -v quiet --filter "FullyQualifiedName~QualifiesForCantinaMeal"
```

Expected: PASS (4 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Humans.Domain/Entities/Shift.cs tests/Humans.Domain.Tests/Entities/ShiftTests.cs
git commit -m "feat(shifts): Shift.QualifiesForCantinaMeal helper (#279)"
```

---

## Task 2: Application — repository signup-list query

**Files:**
- Modify: `src/Humans.Application/Interfaces/Repositories/IShiftManagementRepository.cs`
- Modify: `src/Humans.Infrastructure/Repositories/Shifts/ShiftManagementRepository.cs`

**Why this layer separately:** the service-layer test for `HasQualifyingCantinaSignupAsync` (Task 3) will substitute the repository, so the interface needs to exist first. The repo impl is small and gets its own integration coverage via the service test running against an in-memory DB (see `ServiceTestHarness` pattern).

- [ ] **Step 1: Add the interface method**

In `IShiftManagementRepository.cs`, near the other `ShiftSignup` query methods:

```csharp
/// <summary>
/// Returns a user's active signups (Pending or Confirmed) with each
/// signup's <see cref="ShiftSignup.Shift"/> navigation eagerly loaded
/// so callers can inspect <see cref="Shift.Duration"/>, <see cref="Shift.IsAllDay"/>,
/// and call <see cref="Shift.GetAbsoluteEnd"/> without further DB hits.
/// Cross-section rule: does NOT include <see cref="ShiftSignup.User"/>.
/// </summary>
Task<IReadOnlyList<ShiftSignup>> GetUserActiveSignupsForCantinaGateAsync(
    Guid userId,
    CancellationToken ct = default);
```

- [ ] **Step 2: Implement in repository**

In `ShiftManagementRepository.cs`:

```csharp
public async Task<IReadOnlyList<ShiftSignup>> GetUserActiveSignupsForCantinaGateAsync(
    Guid userId,
    CancellationToken ct = default)
{
    await using var db = await factory.CreateDbContextAsync(ct);
    return await db.ShiftSignups
        .AsNoTracking()
        .Include(s => s.Shift)
        .Where(s => s.UserId == userId
            && (s.Status == SignupStatus.Pending || s.Status == SignupStatus.Confirmed))
        .ToListAsync(ct);
}
```

Notes:
- The primary-ctor parameter on `ShiftManagementRepository` is named `factory` (not `dbFactory`) — use the existing name.
- `.AsNoTracking()` is correct here — we only read.
- No `.Include(s => s.User)` per cross-section rule.
- Past-shift filtering happens in the service (needs clock + `EventSettings`), not in the query — keeps the repo pure.

- [ ] **Step 3: Build to verify the interface + impl compile together**

```bash
dotnet build src/Humans.Infrastructure/Humans.Infrastructure.csproj -v quiet
```

Expected: build succeeds.

- [ ] **Step 4: Commit**

```bash
git add src/Humans.Application/Interfaces/Repositories/IShiftManagementRepository.cs \
        src/Humans.Infrastructure/Repositories/Shifts/ShiftManagementRepository.cs
git commit -m "feat(shifts): repo query for user's active signups w/ Shift nav (#279)"
```

---

## Task 3: Application — `IShiftManagementService.HasQualifyingCantinaSignupAsync`

**Files:**
- Modify: `src/Humans.Application/Interfaces/Shifts/IShiftManagementService.cs`
- Modify: `src/Humans.Application/Services/Shifts/ShiftManagementService.cs`
- Test: `tests/Humans.Application.Tests/Services/Shifts/ShiftManagementServiceTests.cs`

- [ ] **Step 1: Read the existing test scaffolding**

```bash
grep -n "SeedUser\|SeedRotaScenario\|SeedShift\|SeedSignup\|TestNow\|protected.*Db\b" \
  tests/Humans.Application.Tests/Services/Shifts/ShiftManagementServiceTests.cs \
  tests/Humans.Application.Tests/_Harness/ServiceTestHarness.cs 2>&1 | head -40
```

What the harness gives you (verified on origin/main):
- `protected User SeedUser(string displayName = "...")` on `ServiceTestHarness` — creates a `User` row, returns the entity (so `.Id` is the userId).
- `protected static readonly Instant TestNow` — a fixed `Instant` used by `Clock` (a `FakeClock`).
- `protected HumansDbContext Db` (or `DbFactory.CreateDbContext()`) — direct EF access for arbitrary seed inserts.
- `SeedRotaScenario` exists but bundles too much (creates EventSettings + Rota + Shift with a 4-hour fixed Duration). For dietary-gate tests we need fine-grained control over `Duration` / `IsAllDay` / start, so we seed directly via `Db` rather than reuse it.

If your reading shows a different harness shape, prefer the harness methods over raw EF — they capture future invariants.

- [ ] **Step 2: Add seed helpers + tests at the end of `ShiftManagementServiceTests.cs`**

```csharp
// ---- Helpers for #279 dietary-gate tests ----

private async Task<EventSettings> SeedActiveEventSettingsAsync()
{
    await using var db = DbFactory.CreateDbContext();
    var es = new EventSettings
    {
        Id = Guid.NewGuid(),
        Name = "Test Event",
        EventTimeZoneId = "Europe/Madrid",
        StartDate = LocalDate.FromDateTime(TestNow.InUtc().Date.ToDateTimeUnspecified()).PlusDays(7),
        EndDate = LocalDate.FromDateTime(TestNow.InUtc().Date.ToDateTimeUnspecified()).PlusDays(14),
        IsActive = true,
    };
    db.EventSettings.Add(es);
    await db.SaveChangesAsync();
    return es;
}

private async Task<Shift> SeedShiftAsync(
    EventSettings es,
    Duration startOffsetFromNow,
    Duration duration,
    bool isAllDay)
{
    await using var db = DbFactory.CreateDbContext();
    var rota = new Rota { Id = Guid.NewGuid(), EventSettingsId = es.Id, Name = "TestRota" };
    db.Rotas.Add(rota);
    var startInstant = TestNow.Plus(startOffsetFromNow);
    var startLocal = startInstant.InZone(DateTimeZoneProviders.Tzdb[es.EventTimeZoneId]).LocalDateTime;
    var shift = new Shift
    {
        Id = Guid.NewGuid(),
        RotaId = rota.Id,
        Date = startLocal.Date,
        StartTime = startLocal.TimeOfDay,
        Duration = duration,
        IsAllDay = isAllDay,
    };
    db.Shifts.Add(shift);
    await db.SaveChangesAsync();
    return shift;
}

private async Task SeedSignupAsync(Guid userId, Shift shift, SignupStatus status)
{
    await using var db = DbFactory.CreateDbContext();
    db.ShiftSignups.Add(new ShiftSignup
    {
        Id = Guid.NewGuid(),
        UserId = userId,
        ShiftId = shift.Id,
        Status = status,
        Enrolled = false,
        CreatedAt = TestNow,
    });
    await db.SaveChangesAsync();
}

// ---- Tests ----

[HumansFact]
public async Task HasQualifyingCantinaSignup_NoSignups_ReturnsFalse()
{
    await SeedActiveEventSettingsAsync();
    var user = SeedUser();

    var result = await _service.HasQualifyingCantinaSignupAsync(user.Id);

    result.Should().BeFalse();
}

[HumansFact]
public async Task HasQualifyingCantinaSignup_SixHourFutureConfirmed_ReturnsTrue()
{
    var es = await SeedActiveEventSettingsAsync();
    var user = SeedUser();
    var shift = await SeedShiftAsync(es, Duration.FromHours(24), Duration.FromHours(6), isAllDay: false);
    await SeedSignupAsync(user.Id, shift, SignupStatus.Confirmed);

    var result = await _service.HasQualifyingCantinaSignupAsync(user.Id);

    result.Should().BeTrue();
}

[HumansFact]
public async Task HasQualifyingCantinaSignup_FourHourFutureConfirmed_ReturnsFalse()
{
    var es = await SeedActiveEventSettingsAsync();
    var user = SeedUser();
    var shift = await SeedShiftAsync(es, Duration.FromHours(24), Duration.FromHours(4), isAllDay: false);
    await SeedSignupAsync(user.Id, shift, SignupStatus.Confirmed);

    var result = await _service.HasQualifyingCantinaSignupAsync(user.Id);

    result.Should().BeFalse();
}

[HumansFact]
public async Task HasQualifyingCantinaSignup_AllDayShiftEvenIfShortDuration_ReturnsTrue()
{
    var es = await SeedActiveEventSettingsAsync();
    var user = SeedUser();
    var shift = await SeedShiftAsync(es, Duration.FromHours(24), Duration.FromHours(2), isAllDay: true);
    await SeedSignupAsync(user.Id, shift, SignupStatus.Confirmed);

    var result = await _service.HasQualifyingCantinaSignupAsync(user.Id);

    result.Should().BeTrue();
}

[HumansFact]
public async Task HasQualifyingCantinaSignup_PendingSignup_QualifiesSameAsConfirmed()
{
    var es = await SeedActiveEventSettingsAsync();
    var user = SeedUser();
    var shift = await SeedShiftAsync(es, Duration.FromHours(24), Duration.FromHours(8), isAllDay: false);
    await SeedSignupAsync(user.Id, shift, SignupStatus.Pending);

    var result = await _service.HasQualifyingCantinaSignupAsync(user.Id);

    result.Should().BeTrue();
}

[HumansFact]
public async Task HasQualifyingCantinaSignup_BailedSignup_DoesNotQualify()
{
    var es = await SeedActiveEventSettingsAsync();
    var user = SeedUser();
    var shift = await SeedShiftAsync(es, Duration.FromHours(24), Duration.FromHours(8), isAllDay: false);
    await SeedSignupAsync(user.Id, shift, SignupStatus.Bailed);

    var result = await _service.HasQualifyingCantinaSignupAsync(user.Id);

    result.Should().BeFalse();
}

[HumansFact]
public async Task HasQualifyingCantinaSignup_PastShift_DoesNotQualify()
{
    var es = await SeedActiveEventSettingsAsync();
    var user = SeedUser();
    // Shift started 8h before TestNow and lasted 7h → ended 1h ago.
    var shift = await SeedShiftAsync(es, Duration.FromHours(-8), Duration.FromHours(7), isAllDay: false);
    await SeedSignupAsync(user.Id, shift, SignupStatus.Confirmed);

    var result = await _service.HasQualifyingCantinaSignupAsync(user.Id);

    result.Should().BeFalse();
}

[HumansFact]
public async Task HasQualifyingCantinaSignup_NoActiveEventSettings_ReturnsFalse()
{
    // Deliberately do NOT seed EventSettings.
    var user = SeedUser();
    var result = await _service.HasQualifyingCantinaSignupAsync(user.Id);
    result.Should().BeFalse();
}
```

**If the seed helpers compile-fail** (entity property mismatch, wrong constructor pattern, missing required fields), open the actual entity file (`Shift.cs`, `Rota.cs`, `ShiftSignup.cs`, `EventSettings.cs`) and align the property assignments. Don't guess — entities use `init` for some properties and `set` for others; the existing seed code in the same test file is the ground truth.

- [ ] **Step 2: Run tests, confirm they fail**

```bash
dotnet test tests/Humans.Application.Tests/Humans.Application.Tests.csproj -v quiet \
    --filter "FullyQualifiedName~HasQualifyingCantinaSignup"
```

Expected: **build error** — `'IShiftManagementService' does not contain a definition for 'HasQualifyingCantinaSignupAsync'`. Add the interface method (Step 3) to unblock compilation.

- [ ] **Step 3: Add interface method**

In `IShiftManagementService.cs`, near `GetShiftProfileAsync`:

```csharp
/// <summary>
/// True when the user has at least one Pending or Confirmed signup on a
/// future-or-current qualifying shift (see <see cref="Shift.QualifiesForCantinaMeal"/>).
/// Used by the dashboard Things-to-do nudge for dietary/medical info.
/// Returns false when no active event settings exist (fail closed).
/// </summary>
Task<bool> HasQualifyingCantinaSignupAsync(
    Guid userId,
    CancellationToken ct = default);
```

- [ ] **Step 4: Implement the service method**

In `ShiftManagementService.cs`, near `GetShiftProfileAsync`:

```csharp
public async Task<bool> HasQualifyingCantinaSignupAsync(
    Guid userId,
    CancellationToken ct = default)
{
    var eventSettings = await repo.GetActiveEventSettingsAsync();
    if (eventSettings is null)
        return false;

    var now = clock.GetCurrentInstant();
    var signups = await repo.GetUserActiveSignupsForCantinaGateAsync(userId, ct);

    return signups.Any(s =>
        s.Shift is not null
        && s.Shift.QualifiesForCantinaMeal()
        && s.Shift.GetAbsoluteEnd(eventSettings) > now);
}
```

- [ ] **Step 5: Run tests, confirm they pass**

```bash
dotnet test tests/Humans.Application.Tests/Humans.Application.Tests.csproj -v quiet \
    --filter "FullyQualifiedName~HasQualifyingCantinaSignup"
```

Expected: PASS (8 tests).

- [ ] **Step 6: Run the whole test suite to catch regressions**

```bash
dotnet test Humans.slnx -v quiet
```

Expected: PASS. Investigate any new failures (especially in `ShiftManagementServiceTests`) before continuing.

- [ ] **Step 7: Commit**

```bash
git add src/Humans.Application/Interfaces/Shifts/IShiftManagementService.cs \
        src/Humans.Application/Services/Shifts/ShiftManagementService.cs \
        tests/Humans.Application.Tests/Services/Shifts/ShiftManagementServiceTests.cs
git commit -m "feat(shifts): HasQualifyingCantinaSignupAsync service method (#279)"
```

---

## Task 4: Resource strings — `Todo_DietaryMedical_*` + form labels

**Files:** `src/Humans.Web/Resources/SharedResource.resx` (English) + 5 locale variants: `.ca.resx`, `.de.resx`, `.es.resx`, `.fr.resx`, `.it.resx`. **6 files total.**

**Translation policy:** English values are required in the base file; Spanish translations are required (Spanish nonprofit, primary user language); other locales get the English text as placeholder (existing pattern — verify by checking one or two existing `Todo_*` keys in the non-Spanish .resx files).

- [ ] **Step 1: Locate all locale .resx files**

```bash
ls src/Humans.Web/Resources/SharedResource*.resx
```

Expected output: 6 files (base + 5 locales). If you see a different count, stop and reconcile before editing — a new locale may have been added.

- [ ] **Step 2: Add Things-to-do nudge keys + Spanish + form labels**

Add to each `SharedResource.resx`:

| Key | English value |
|---|---|
| `Todo_DietaryMedical_Title` | `Tell us about your food needs` |
| `Todo_DietaryMedical_Pending` | `We need this to plan cantina meals` |
| `Todo_DietaryMedical_Done` | `Thanks — we've got it` |
| `Todo_DietaryMedical_Action` | `Fill out` |
| `Profile_DietaryMedical_PageTitle` | `Dietary & medical info` |
| `Profile_DietaryMedical_DietaryPreference_Label` | `Dietary preference` |
| `Profile_DietaryMedical_DietaryPreference_Omnivore` | `Omnivore` |
| `Profile_DietaryMedical_DietaryPreference_Vegetarian` | `Vegetarian` |
| `Profile_DietaryMedical_DietaryPreference_Vegan` | `Vegan` |
| `Profile_DietaryMedical_DietaryPreference_Pescatarian` | `Pescatarian` |
| `Profile_DietaryMedical_Allergies_Label` | `Allergies` |
| `Profile_DietaryMedical_AllergyOther_Label` | `Other allergy — please describe` |
| `Profile_DietaryMedical_Intolerances_Label` | `Intolerances` |
| `Profile_DietaryMedical_IntoleranceOther_Label` | `Other intolerance — please describe` |
| `Profile_DietaryMedical_MedicalConditions_Label` | `Medical conditions` |
| `Profile_DietaryMedical_MedicalConditions_Hint` | `Only visible to you and the No-Info Admins. Anything coordinators should know — diabetes, epilepsy, severe injuries, etc.` |
| `Profile_DietaryMedical_Saved` | `Your dietary and medical info has been saved.` |

Spanish (`.es.resx`):

| Key | Spanish value |
|---|---|
| `Todo_DietaryMedical_Title` | `Cuéntanos sobre tu alimentación` |
| `Todo_DietaryMedical_Pending` | `Lo necesitamos para planificar las comidas de la cantina` |
| `Todo_DietaryMedical_Done` | `Gracias — ya tenemos lo necesario` |
| `Todo_DietaryMedical_Action` | `Rellenar` |
| `Profile_DietaryMedical_PageTitle` | `Información alimentaria y médica` |
| `Profile_DietaryMedical_DietaryPreference_Label` | `Preferencia alimentaria` |
| `Profile_DietaryMedical_DietaryPreference_Omnivore` | `Omnívoro/a` |
| `Profile_DietaryMedical_DietaryPreference_Vegetarian` | `Vegetariano/a` |
| `Profile_DietaryMedical_DietaryPreference_Vegan` | `Vegano/a` |
| `Profile_DietaryMedical_DietaryPreference_Pescatarian` | `Pescetariano/a` |
| `Profile_DietaryMedical_Allergies_Label` | `Alergias` |
| `Profile_DietaryMedical_AllergyOther_Label` | `Otra alergia — describe cuál` |
| `Profile_DietaryMedical_Intolerances_Label` | `Intolerancias` |
| `Profile_DietaryMedical_IntoleranceOther_Label` | `Otra intolerancia — describe cuál` |
| `Profile_DietaryMedical_MedicalConditions_Label` | `Condiciones médicas` |
| `Profile_DietaryMedical_MedicalConditions_Hint` | `Solo visible para ti y los No-Info Admins. Cualquier cosa que el equipo deba saber — diabetes, epilepsia, lesiones graves, etc.` |
| `Profile_DietaryMedical_Saved` | `Tu información alimentaria y médica se ha guardado.` |

Other locales (`.ca.resx`, `.de.resx`, `.fr.resx`, `.it.resx`): copy the **English** values into each, as the existing pattern for under-translated locales. The Catalan (`.ca.resx`) may already have full coverage — if so, translate properly; otherwise English placeholder is acceptable.

- [ ] **Step 3: Build to confirm resource keys resolve**

```bash
dotnet build src/Humans.Web/Humans.Web.csproj -v quiet
```

Expected: build succeeds (no broken refs from views yet — those come next).

- [ ] **Step 4: Commit**

```bash
git add src/Humans.Web/Resources/SharedResource*.resx
git commit -m "feat(profile): add Todo_DietaryMedical + form-label resource keys (#279)"
```

---

## Task 5: Web — `DietaryMedicalViewModel`

**Files:** Create `src/Humans.Web/Models/DietaryMedicalViewModel.cs`

The existing `ShiftInfoViewModel` lives at `src/Humans.Web/Models/ShiftViewModels.cs` with namespace `Humans.Web.Models` (flat models tree, not subdirectoried per-area). Match that placement so the views auto-resolve the type without extra `@using` directives.

- [ ] **Step 1: Confirm ShiftInfoViewModel's actual location and namespace**

```bash
grep -rn "class ShiftInfoViewModel\|^namespace.*Models" src/Humans.Web/Models/ | head -5
```

Expected: confirms `Humans.Web.Models` namespace, flat directory. If your reading shows it nested under `Models/Profile/` or similar, prefer that — match what's actually there.

- [ ] **Step 2: Create the view model**

```csharp
using System.ComponentModel.DataAnnotations;
using Humans.Domain.Entities;

namespace Humans.Web.Models;

public sealed class DietaryMedicalViewModel
{
    [Required]
    public string DietaryPreference { get; set; } = string.Empty;

    public List<string> Allergies { get; set; } = [];

    [StringLength(500)]
    public string? AllergyOtherText { get; set; }

    public List<string> Intolerances { get; set; } = [];

    [StringLength(500)]
    public string? IntoleranceOtherText { get; set; }

    [StringLength(4000)]
    public string? MedicalConditions { get; set; }

    public static readonly IReadOnlyList<string> DietaryPreferences =
        ["Omnivore", "Vegetarian", "Vegan", "Pescatarian"];

    public static readonly IReadOnlyList<string> AllergyOptions =
        ["Peanut", "Tree nut", "Dairy", "Egg", "Shellfish", "Wheat/Gluten", "Soy", "Sesame", "Other"];

    public static readonly IReadOnlyList<string> IntoleranceOptions =
        ["Lactose", "Gluten", "Histamine", "FODMAP", "Other"];

    public static DietaryMedicalViewModel FromProfile(VolunteerEventProfile profile) => new()
    {
        DietaryPreference = profile.DietaryPreference ?? string.Empty,
        Allergies = [.. profile.Allergies],
        AllergyOtherText = profile.AllergyOtherText,
        Intolerances = [.. profile.Intolerances],
        IntoleranceOtherText = profile.IntoleranceOtherText,
        MedicalConditions = profile.MedicalConditions,
    };

    public void ApplyTo(VolunteerEventProfile profile)
    {
        profile.DietaryPreference = string.IsNullOrWhiteSpace(DietaryPreference) ? null : DietaryPreference;
        profile.Allergies = [.. Allergies.Where(IsKnownAllergy)];
        profile.AllergyOtherText = Allergies.Contains("Other") ? AllergyOtherText?.Trim() : null;
        profile.Intolerances = [.. Intolerances.Where(IsKnownIntolerance)];
        profile.IntoleranceOtherText = Intolerances.Contains("Other") ? IntoleranceOtherText?.Trim() : null;
        profile.MedicalConditions = string.IsNullOrWhiteSpace(MedicalConditions) ? null : MedicalConditions.Trim();
    }

    private static bool IsKnownAllergy(string v) => AllergyOptions.Contains(v);
    private static bool IsKnownIntolerance(string v) => IntoleranceOptions.Contains(v);
}
```

Notes:
- `[Required]` on `DietaryPreference` enforces the spec's "required radio group".
- `[StringLength]` mirrors the DB column lengths (500 / 4000).
- `IsKnownAllergy` / `IsKnownIntolerance` filter the POSTed list against the allowed set — anything else is silently dropped (defensive against tampering).
- `Other` text is only persisted when `Other` is selected (per spec).

- [ ] **Step 3: Build**

```bash
dotnet build src/Humans.Web/Humans.Web.csproj -v quiet
```

- [ ] **Step 4: Commit**

```bash
git add src/Humans.Web/Models/DietaryMedicalViewModel.cs
git commit -m "feat(profile): DietaryMedicalViewModel with FromProfile/ApplyTo (#279)"
```

---

## Task 6: Web — `ProfileController` actions

**Files:** Modify `src/Humans.Web/Controllers/ProfileController.cs`

Mirror the `ShiftInfo` GET/POST pair. Route prefix is `Me/` per the controller's existing convention.

- [ ] **Step 1: Locate the ShiftInfo action pair in the controller**

```bash
grep -n "ShiftInfo" src/Humans.Web/Controllers/ProfileController.cs | head -10
```

Use as the reference site for the new pair. New methods go immediately after.

- [ ] **Step 2: Add the GET action**

```csharp
[HttpGet("Me/DietaryMedical")]
public async Task<IActionResult> DietaryMedical()
{
    try
    {
        var user = await GetCurrentUserInfoAsync();
        if (user is null) return NotFound();

        // includeMedical: true — the form must pre-populate the user's own medical info.
        var profile = await shiftMgmt.GetShiftProfileAsync(user.Id, includeMedical: true);
        var vm = profile is null
            ? new DietaryMedicalViewModel()
            : DietaryMedicalViewModel.FromProfile(profile);

        return View(vm);
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Failed to load dietary/medical info for user");
        SetError("Failed to load dietary and medical info.");
        return RedirectToAction(nameof(Me));
    }
}
```

- [ ] **Step 3: Add the POST action**

```csharp
[HttpPost("Me/DietaryMedical")]
[ValidateAntiForgeryToken]
public async Task<IActionResult> DietaryMedical(DietaryMedicalViewModel model)
{
    if (!ModelState.IsValid)
        return View(model);

    // Server-side validation for the "Other text required iff Other selected" rule
    // (DataAnnotations can't express the conditional requirement cleanly).
    if (model.Allergies.Contains("Other") && string.IsNullOrWhiteSpace(model.AllergyOtherText))
    {
        ModelState.AddModelError(nameof(model.AllergyOtherText), localizer["Profile_DietaryMedical_AllergyOther_Required"].Value);
        return View(model);
    }
    if (model.Intolerances.Contains("Other") && string.IsNullOrWhiteSpace(model.IntoleranceOtherText))
    {
        ModelState.AddModelError(nameof(model.IntoleranceOtherText), localizer["Profile_DietaryMedical_IntoleranceOther_Required"].Value);
        return View(model);
    }

    try
    {
        var user = await GetCurrentUserInfoAsync();
        if (user is null) return NotFound();

        var profile = await shiftMgmt.GetOrCreateShiftProfileAsync(user.Id);
        model.ApplyTo(profile);
        await shiftMgmt.UpdateShiftProfileAsync(profile);

        SetSuccess(localizer["Profile_DietaryMedical_Saved"].Value);
        return Redirect("/");
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Failed to save dietary/medical info");
        SetError("Failed to save dietary and medical info.");
        return View(model);
    }
}
```

Notes:
- `Redirect("/")` lands on the dashboard per spec US-35.2. (`ProfileController.Index()` redirects to `Me`, which lands on `/Profile/Me` — not the dashboard. Avoid `RedirectToAction(nameof(Index))`.)
- The two conditional-required validations use new resource keys `Profile_DietaryMedical_AllergyOther_Required` and `Profile_DietaryMedical_IntoleranceOther_Required` — added in Step 4 below. (Acceptable to fold these into Task 4's commit instead if you prefer one resx commit.)

- [ ] **Step 4: Add the two missing resource keys to the resx files**

Append to `SharedResource.resx`:

| Key | English value |
|---|---|
| `Profile_DietaryMedical_AllergyOther_Required` | `Please describe your "Other" allergy.` |
| `Profile_DietaryMedical_IntoleranceOther_Required` | `Please describe your "Other" intolerance.` |

And to `SharedResource.es.resx`:

| Key | Spanish value |
|---|---|
| `Profile_DietaryMedical_AllergyOther_Required` | `Describe la otra alergia.` |
| `Profile_DietaryMedical_IntoleranceOther_Required` | `Describe la otra intolerancia.` |

- [ ] **Step 5: Build**

```bash
dotnet build src/Humans.Web/Humans.Web.csproj -v quiet
```

Expected: build succeeds.

- [ ] **Step 6: Commit**

```bash
git add src/Humans.Web/Controllers/ProfileController.cs \
        src/Humans.Web/Resources/SharedResource.resx \
        src/Humans.Web/Resources/SharedResource.es.resx
git commit -m "feat(profile): DietaryMedical GET/POST actions (#279)"
```

---

## Task 7: Web — `Views/Profile/DietaryMedical.cshtml`

**Files:** Create `src/Humans.Web/Views/Profile/DietaryMedical.cshtml`

Mirror the structural pattern of an existing Profile form view — full-page form, validation summary, anti-forgery token, cancel/save buttons. Use the existing localized resource keys from Task 4.

- [ ] **Step 1: Read the existing Edit.cshtml for the form skeleton**

```bash
head -30 src/Humans.Web/Views/Profile/Edit.cshtml
tail -15 src/Humans.Web/Views/Profile/Edit.cshtml
```

Note the form-wrapper pattern (validation summary block, save/cancel button row).

- [ ] **Step 2: Create the view**

```cshtml
@model DietaryMedicalViewModel
@using Microsoft.Extensions.Localization
@inject IStringLocalizer<SharedResource> Localizer
@{
    ViewData["Title"] = Localizer["Profile_DietaryMedical_PageTitle"].Value;
}

<div class="container my-4">
    <h1>@Localizer["Profile_DietaryMedical_PageTitle"]</h1>

    <form asp-action="DietaryMedical" method="post" novalidate>
        @Html.AntiForgeryToken()

        @if (ViewData.ModelState!.ErrorCount > 0)
        {
            <div class="alert alert-danger mb-3">
                <div asp-validation-summary="All"></div>
            </div>
        }

        <fieldset class="mb-4">
            <legend class="h5">@Localizer["Profile_DietaryMedical_DietaryPreference_Label"]</legend>
            @foreach (var pref in DietaryMedicalViewModel.DietaryPreferences)
            {
                <div class="form-check">
                    <input class="form-check-input"
                           type="radio"
                           asp-for="DietaryPreference"
                           value="@pref"
                           id="dp-@pref" />
                    <label class="form-check-label" for="dp-@pref">
                        @Localizer[$"Profile_DietaryMedical_DietaryPreference_{pref}"]
                    </label>
                </div>
            }
            <span asp-validation-for="DietaryPreference" class="text-danger"></span>
        </fieldset>

        <fieldset class="mb-4">
            <legend class="h5">@Localizer["Profile_DietaryMedical_Allergies_Label"]</legend>
            @foreach (var opt in DietaryMedicalViewModel.AllergyOptions)
            {
                <div class="form-check form-check-inline">
                    <input class="form-check-input"
                           type="checkbox"
                           name="Allergies"
                           value="@opt"
                           id="al-@opt"
                           @(Model.Allergies.Contains(opt) ? "checked" : null) />
                    <label class="form-check-label" for="al-@opt">@opt</label>
                </div>
            }
            <div class="mt-2" id="allergy-other-wrapper" hidden="@(Model.Allergies.Contains("Other") ? null : "hidden")">
                <label asp-for="AllergyOtherText" class="form-label">@Localizer["Profile_DietaryMedical_AllergyOther_Label"]</label>
                <input asp-for="AllergyOtherText" class="form-control" maxlength="500" />
                <span asp-validation-for="AllergyOtherText" class="text-danger"></span>
            </div>
        </fieldset>

        <fieldset class="mb-4">
            <legend class="h5">@Localizer["Profile_DietaryMedical_Intolerances_Label"]</legend>
            @foreach (var opt in DietaryMedicalViewModel.IntoleranceOptions)
            {
                <div class="form-check form-check-inline">
                    <input class="form-check-input"
                           type="checkbox"
                           name="Intolerances"
                           value="@opt"
                           id="in-@opt"
                           @(Model.Intolerances.Contains(opt) ? "checked" : null) />
                    <label class="form-check-label" for="in-@opt">@opt</label>
                </div>
            }
            <div class="mt-2" id="intolerance-other-wrapper" hidden="@(Model.Intolerances.Contains("Other") ? null : "hidden")">
                <label asp-for="IntoleranceOtherText" class="form-label">@Localizer["Profile_DietaryMedical_IntoleranceOther_Label"]</label>
                <input asp-for="IntoleranceOtherText" class="form-control" maxlength="500" />
                <span asp-validation-for="IntoleranceOtherText" class="text-danger"></span>
            </div>
        </fieldset>

        <fieldset class="mb-4">
            <legend class="h5">@Localizer["Profile_DietaryMedical_MedicalConditions_Label"]</legend>
            <p class="text-muted small">@Localizer["Profile_DietaryMedical_MedicalConditions_Hint"]</p>
            <textarea asp-for="MedicalConditions" class="form-control" rows="4" maxlength="4000"></textarea>
            <span asp-validation-for="MedicalConditions" class="text-danger"></span>
        </fieldset>

        <div class="d-flex justify-content-between">
            <a asp-action="Index" class="btn btn-outline-secondary">@Localizer["Common_Cancel"]</a>
            <button type="submit" class="btn btn-primary">@Localizer["Common_Save"]</button>
        </div>
    </form>
</div>

@section Scripts {
    <partial name="_ValidationScriptsPartial" />
    <script>
        (function () {
            function wireOther(prefix) {
                var checkbox = document.getElementById(prefix + '-Other');
                var wrapper = document.getElementById(prefix === 'al' ? 'allergy-other-wrapper' : 'intolerance-other-wrapper');
                if (!checkbox || !wrapper) return;
                checkbox.addEventListener('change', function () {
                    wrapper.hidden = !checkbox.checked;
                });
            }
            wireOther('al');
            wireOther('in');
        })();
    </script>
}
```

Notes:
- The small JS toggles the "Other" text input visibility on the client. Server-side validation (Task 6 step 3) still enforces the conditional-required rule, so JS-disabled users get an error and resubmit.
- `Common_Cancel` / `Common_Save` are existing keys — confirm with `grep "Common_Cancel\b" src/Humans.Web/Resources/SharedResource.resx`. If absent, use literal strings or fall back to existing button text used in `Edit.cshtml`.
- The view must be picked up by the default Razor view-discovery (`Views/Profile/DietaryMedical.cshtml`) so no `Views.json` update needed.

- [ ] **Step 3: Run the app and manually test the view**

```bash
cd .worktrees/impl-279
dotnet run --project src/Humans.Web
```

Then in a browser, with a logged-in dev user (`DevAuth__Enabled=true`):
1. Navigate to `/Profile/Me/DietaryMedical` directly.
2. Confirm: form renders, the "Other" inputs are hidden by default, toggling "Other" reveals the input.
3. Submit with no dietary preference → validation error.
4. Submit with `Other` selected but empty text → validation error.
5. Submit valid → redirected to `/`, success flash present, returning to `/Profile/Me/DietaryMedical` shows the saved values.

Stop the dev server (`Ctrl+C`).

- [ ] **Step 4: Commit**

```bash
git add src/Humans.Web/Views/Profile/DietaryMedical.cshtml
git commit -m "feat(profile): DietaryMedical form view (#279)"
```

---

## Task 8: Web — `ThingsToDoViewComponent` narrow + new branch

**Files:**
- Modify: `src/Humans.Web/ViewComponents/ThingsToDoViewComponent.cs`
- *Optional:* create `tests/Humans.Web.Tests/ViewComponents/ThingsToDoViewComponentTests.cs` if the test project structure supports it.

Two changes: (a) narrow `IsShiftProfileEmpty` to skills/quirks/languages only; (b) add a new gated branch for the dietary/medical item.

**Key structural note:** the existing `InvokeAsync` only fetches `shiftProfile` *inside* the `if (hasShiftSignups)` block. We **cannot** reference `shiftProfile` from outside that block. The new branch loads its own view of the profile, gated cheaply by the qualifying-signup check first.

- [ ] **Step 1: Read the full file**

```bash
cat src/Humans.Web/ViewComponents/ThingsToDoViewComponent.cs
```

Confirm:
1. The `TodoItem` type uses property-initializer syntax (`new TodoItem { Key = "...", Title = "...", ... }`), NOT positional record-constructor syntax.
2. `Url.Action("ShiftInfo", "Profile")` is the existing URL pattern — match it.
3. `model.Items.Add(...)` is how items are appended.

- [ ] **Step 2: Narrow `IsShiftProfileEmpty`**

Replace the existing body of the private helper (currently at the bottom of the class) with:

```csharp
private static bool IsShiftProfileEmpty(VolunteerEventProfile profile)
{
    return profile.Skills.Count == 0
        && profile.Quirks.Count == 0
        && profile.Languages.Count == 0;
}
```

This removes the `Allergies`, `Intolerances`, `DietaryPreference`, and `MedicalConditions` checks — they no longer belong to the generic "shift info empty" notion (per spec).

- [ ] **Step 3: Add the dietary/medical branch**

Add this block inside `InvokeAsync`, **after** the `// 4. Set shift preferences (only when user has shift signups)` block but still inside the outer `try`. Use the existing `TodoItem` property-initializer shape:

```csharp
// 5. Dietary & medical nudge — fires when the user has an active qualifying
// signup (6h+ or all-day) AND DietaryPreference is not yet set.
// See feature #279 / 35 — docs/features/profiles/dietary-medical-nudge.md
try
{
    var hasQualifyingSignup = await shiftMgmt.HasQualifyingCantinaSignupAsync(userId);
    if (hasQualifyingSignup)
    {
        var dietaryProfile = await shiftMgmt.GetShiftProfileAsync(userId, includeMedical: false);
        var dietaryEmpty = string.IsNullOrEmpty(dietaryProfile?.DietaryPreference);
        if (dietaryEmpty)
        {
            model.Items.Add(new TodoItem
            {
                Key = "dietary-medical",
                Title = localizer["Todo_DietaryMedical_Title"].Value,
                Description = localizer["Todo_DietaryMedical_Pending"].Value,
                IsDone = false,
                ActionUrl = Url.Action("DietaryMedical", "Profile"),
                ActionText = localizer["Todo_DietaryMedical_Action"].Value,
                IconClass = "fa-solid fa-utensils",
            });
        }
    }
}
catch (Exception ex)
{
    logger.LogError(ex, "Failed to check dietary/medical nudge for user {UserId}", userId);
}
```

Notes:
- Wrapped in its own `try/catch` matching the pattern of the existing shift-profile fetch (item 4) — one failing fetch shouldn't kill the whole component.
- `GetShiftProfileAsync(includeMedical: false)` is correct here: we only need `DietaryPreference`, which is NOT nulled by the `includeMedical: false` filter (the service only nulls `MedicalConditions`).
- Cheap-check-first ordering: `HasQualifyingCantinaSignupAsync` returns early when there's no event settings or no active signups, avoiding the second DB call.
- We do not show a "done" version of this item (unlike shift-info, which shows done text). The spec says the item disappears when answered — implemented by only appending when `dietaryEmpty`.

- [ ] **Step 4: Build**

```bash
dotnet build src/Humans.Web/Humans.Web.csproj -v quiet
```

Expected: build succeeds. Investigate any errors; the most likely is a property name mismatch on `TodoItem`.

- [ ] **Step 5: Add a smoke unit test (optional but recommended)**

Search for any existing `ThingsToDoViewComponent` test file:

```bash
grep -rln ThingsToDoViewComponent tests/
```

If a test file exists, add coverage there. If not, the new branch is covered only by manual smoke (Step 6 below). Given the narrow surface (the gate logic is fully exercised by the service-layer tests in Task 3), manual verification of the dashboard rendering is acceptable per project scale.

- [ ] **Step 6: Manual test — verify the gate works end-to-end**

Restart `dotnet run --project src/Humans.Web` and:

1. As a dev user with no qualifying signup: dashboard's Things-to-do card should NOT show the dietary/medical item.
2. Sign the dev user up for a 7-hour shift (use existing signup flow). Refresh the dashboard: the dietary/medical item should appear.
3. Click "Fill out" → land on `/Profile/Me/DietaryMedical`. Fill `DietaryPreference` = Omnivore, save. Redirected to `/`. Dietary/medical item is gone from the card. Other todo items (e.g., shift-info if applicable) still render correctly.
4. Cancel (Bail) the signup. Refresh dashboard: the item stays absent (because `DietaryPreference` is now set).
5. Sign up for another qualifying shift. Item should remain absent (already answered). Then clear `DietaryPreference` in DB directly (`UPDATE volunteer_event_profiles SET dietary_preference = NULL WHERE user_id = '<uuid>';` — easiest reset path). Refresh dashboard: item reappears.

Stop the dev server.

- [ ] **Step 7: Commit**

```bash
git add src/Humans.Web/ViewComponents/ThingsToDoViewComponent.cs
git commit -m "feat(dashboard): dietary/medical nudge branch + narrow IsShiftProfileEmpty (#279)"
```

---

## Task 9: Web — Profile nav link for unconditional access (US-35.3)

**Files:** Modify `src/Humans.Web/Views/Profile/Index.cshtml`

The profile nav-link list (Shift Info / Emails / Communication Preferences / Governance / Privacy / My Emails) lives in `Views/Profile/Index.cshtml` (around lines 180–187 on origin/main), NOT in `Edit.cshtml`. Add a "Dietary & medical info" link there.

- [ ] **Step 1: Find the existing link list**

```bash
grep -n "ShiftInfo\|CommunicationPreferences" src/Humans.Web/Views/Profile/Index.cshtml | head -10
```

You should see a block of `<a asp-controller="Profile" asp-action="..." class="list-group-item list-group-item-action">` entries.

- [ ] **Step 2: Add the new link**

Insert into the same `<div class="list-group">` (immediately after the `ShiftInfo` line for visual proximity, since dietary/medical is shift-related):

```cshtml
<a asp-controller="Profile" asp-action="DietaryMedical" class="list-group-item list-group-item-action">@Localizer["Profile_DietaryMedical_PageTitle"]</a>
```

Match the existing entries' formatting — they don't currently include FontAwesome icons in this list, so don't add one here (keep it consistent).

- [ ] **Step 3: Build + manual test**

```bash
dotnet build src/Humans.Web/Humans.Web.csproj -v quiet
```

Run the app. Navigate to `/Profile/Me` as a user without a qualifying signup. The "Dietary & medical info" link should appear in the nav list; click it → lands on `/Profile/Me/DietaryMedical`. Confirm proactive access works.

- [ ] **Step 4: Commit**

```bash
git add src/Humans.Web/Views/Profile/Index.cshtml
git commit -m "feat(profile): nav link to DietaryMedical from /Profile/Me (#279)"
```

---

## Task 10: Spec — fix the route reference

**Files:** Modify `docs/features/profiles/dietary-medical-nudge.md`

The spec says `GET /Profile/DietaryMedical`. The implementation uses `/Profile/Me/DietaryMedical` to match the controller's `[HttpGet("Me/<area>")]` convention. Update the spec to match implementation.

- [ ] **Step 1: Replace the route references**

In `docs/features/profiles/dietary-medical-nudge.md`, find every instance of `/Profile/DietaryMedical` and replace with `/Profile/Me/DietaryMedical`. (US-35.2 acceptance criteria, US-35.3 acceptance criteria.)

- [ ] **Step 2: Commit**

```bash
git add docs/features/profiles/dietary-medical-nudge.md
git commit -m "docs(issue-279): align spec route w/ controller Me/ convention"
```

---

## Task 11: Section invariants doc (if exists)

**Files:** `docs/sections/profile.md` *(only if it exists; skip if not)*

Profile section gets a new user-facing surface (`DietaryMedical`). The section invariant doc should mention it.

- [ ] **Step 1: Check**

```bash
ls docs/sections/profile.md docs/sections/Profile.md 2>&1
```

If neither exists, skip the rest of this task.

- [ ] **Step 2: Add `DietaryMedical` to the section's user-facing surface list**

Open the file, find the "user-facing surface" / "routes" / "actions" list, add `GET/POST /Profile/Me/DietaryMedical`. Match the document's existing format.

- [ ] **Step 3: Commit**

```bash
git add docs/sections/profile.md
git commit -m "docs(sections): note DietaryMedical surface in profile section invariants (#279)"
```

---

## Task 12: Final verification

- [ ] **Step 1: Full build + test**

```bash
dotnet build Humans.slnx -v quiet
dotnet test Humans.slnx -v quiet
```

Expected: clean build, all tests pass. Investigate any failures before continuing.

- [ ] **Step 2: Manual end-to-end smoke**

Run `dotnet run --project src/Humans.Web`. Walk through every acceptance criterion in the spec (US-35.1 through US-35.4) one by one:

- [ ] US-35.1.1: Dashboard does NOT show the item without a qualifying signup.
- [ ] US-35.1.2: Signing up for a 6h+ shift (Pending or Confirmed) makes the item appear.
- [ ] US-35.1.3: Saving `DietaryPreference` makes the item disappear.
- [ ] US-35.1.4: Past shifts and Bailed/Refused/NoShow/Cancelled signups don't trigger the nudge.
- [ ] US-35.2: Form fields render, validate, persist correctly. Required dietary preference is enforced. "Other" text shows/hides on toggle and is required when "Other" selected.
- [ ] US-35.2.medical: `MedicalConditions` textarea accepts up to 4000 chars; persisted value visible only to owner / `NoInfoAdmin` / `Admin` (verify by viewing another user's profile as a non-`NoInfoAdmin`).
- [ ] US-35.3: `/Profile/Me` nav list shows "Dietary & medical info" link unconditionally; clicking lands on `/Profile/Me/DietaryMedical` pre-populated with current values.
- [ ] US-35.4: Coordinator voluntolds a user into a qualifying shift → the next time that user logs in, the nudge is visible on their dashboard.

Stop the dev server.

- [ ] **Step 3: Push branch + open the impl PR**

```bash
git push fork feature/issue-279-dietary-nudge-impl
gh pr create \
  --repo peterdrier/Humans \
  --base main \
  --head FrankFanteev:feature/issue-279-dietary-nudge-impl \
  --title "feat(profile): dietary & medical nudge (#279)" \
  --body-file - <<'EOF'
## Summary

Implements feature 35 / issue #279 — dashboard nudge surfaces dietary and medical info collection when a human has an active 6+ hour shift signup.

Specced in #606 (`docs/features/profiles/dietary-medical-nudge.md`).

## What landed

- `Shift.QualifiesForCantinaMeal()` pure helper (Domain).
- `IShiftManagementService.HasQualifyingCantinaSignupAsync` + repo query (Application + Infrastructure).
- `ProfileController.DietaryMedical` GET/POST + Razor view (Web).
- `ThingsToDoViewComponent`: narrowed `IsShiftProfileEmpty` to skills/quirks/languages; added gated dietary/medical branch.
- `/Profile/Me` nav link for proactive access (US-35.3).
- Resource strings (`Todo_DietaryMedical_*`, `Profile_DietaryMedical_*`) across all locales — English + Spanish translated, others placeholder.
- Tests: 4 entity tests, 8 service tests.

## What this PR is NOT

- No schema change (all 6 columns already exist on `VolunteerEventProfile`).
- No HTMX / modal infrastructure (spec dropped modal-or-page; this is full-page only).

## Depends on

- #606 (spec). This branch is based on the spec branch; merging the spec first removes the docs diff from this PR.

## Test plan

- [ ] Full suite passes (`dotnet test Humans.slnx -v quiet`).
- [ ] Manual: walk through US-35.1 through US-35.4 acceptance criteria.
EOF
```

If the spec PR (#606) hasn't merged yet, leave this impl PR as draft until it does.

- [ ] **Step 4: Final cleanup**

The `.worktrees/impl-279/` worktree can be removed once the impl PR is merged.

```bash
cd ../..
git worktree remove .worktrees/impl-279
```

---

## Out-of-scope reminders (from spec)

- No email reminders.
- No bulk cantina export.
- No per-event dietary override (these fields are global to the user).
- No "I have nothing to declare" flag — picking any dietary preference (including Omnivore with empty allergies) counts as answered.

## Change Enforcement note

Per project doctrine: if any of these fields/columns change in future PRs, the spec's `freshness:triggers` will flag it. No code-level "if X then Y" rule needed beyond what already exists.

## GDPR confirmation

`VolunteerEventProfile` is already covered by:
- `AccountDeletionService` step 5 (deletes VEP rows on account deletion).
- `GdprExportSections.VolunteerEventProfiles` (export contributor via `ShiftSignupService`).

No new GDPR plumbing required for this feature.
