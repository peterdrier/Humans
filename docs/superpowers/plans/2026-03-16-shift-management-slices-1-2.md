# Shift Management (Slices 1+2) Implementation Plan

> **For agentic workers:** REQUIRED: Use superpowers:subagent-driven-development (if subagents available) or superpowers:executing-plans to implement this plan. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the core shift management entities, lead shift management UI, volunteer browsing/signup experience, and supporting infrastructure — Slices 1 (Core + Lead Management) and 2 (Volunteer Experience) of the shift management spec.

**Architecture:** Clean Architecture — Domain entities + enums → EF Core configurations + migration → Application interfaces → Infrastructure services → Web controllers + views. Shifts use relative day offsets from a gate opening date, resolved to absolute times via NodaTime. Authorization via a cached `ShiftAuthorizationService` that checks `TeamRoleDefinition.IsManagement` on parent teams.

**Tech Stack:** ASP.NET Core MVC, EF Core + PostgreSQL, NodaTime, Hangfire, xUnit + AwesomeAssertions, Bootstrap 5.3, Font Awesome 6

**Spec:** `docs/specs/2026-03-16-shift-management-design.md` (v1.2)

---

## File Structure

### Domain Layer (`src/Humans.Domain/`)
| File | Action | Purpose |
|------|--------|---------|
| `Entities/EventSettings.cs` | Create | Singleton event config — dates, timezone, EE capacity, caps |
| `Entities/Rota.cs` | Create | Shift container — belongs to department + event |
| `Entities/Shift.cs` | Create | Single work slot — DayOffset + StartTime + Duration |
| `Entities/DutySignup.cs` | Create | Links User to Shift with state machine |
| `Enums/DutyPriority.cs` | Create | Normal, Important, Essential |
| `Enums/SignupPolicy.cs` | Create | Public, RequireApproval |
| `Enums/SignupStatus.cs` | Create | Pending, Confirmed, Refused, Bailed, Cancelled, NoShow |
| `Enums/ShiftPeriod.cs` | Create | Build, Event, Strike |
| `Constants/RoleNames.cs` | Modify | Add `NoInfoAdmin` |
| `Constants/AuditLogEntityTypes.cs` | Create | New constant class for entity type strings (codebase currently uses raw strings — this follows CODING_RULES.md magic string guidance) |
| `Enums/AuditAction.cs` | Modify | Add 6 DutySignup actions |
| `Entities/User.cs` | Modify | Add `ICalToken Guid?` |
| `Entities/EmailOutboxMessage.cs` | Modify | Add `DutySignupId Guid?` |

### Infrastructure Layer (`src/Humans.Infrastructure/`)
| File | Action | Purpose |
|------|--------|---------|
| `Data/Configurations/EventSettingsConfiguration.cs` | Create | EF config — jsonb for EE capacity dictionaries |
| `Data/Configurations/RotaConfiguration.cs` | Create | EF config — FK to EventSettings + Team |
| `Data/Configurations/ShiftConfiguration.cs` | Create | EF config — Duration as bigint (seconds) |
| `Data/Configurations/DutySignupConfiguration.cs` | Create | EF config — enum conversions, indexes |
| `Data/Configurations/EmailOutboxMessageConfiguration.cs` | Modify | Add DutySignupId FK |
| `Data/HumansDbContext.cs` | Modify | Add DbSets |
| `Services/ShiftAuthorizationService.cs` | Create | Cached dept coordinator checks |
| `Services/EventSettingsService.cs` | Create | CRUD + active event resolution |
| `Services/RotaService.cs` | Create | CRUD with team validation |
| `Services/ShiftService.cs` | Create | CRUD with offset date resolution |
| `Services/DutySignupService.cs` | Create | State machine, invariants, bail, approve, voluntell |
| `Services/ShiftUrgencyService.cs` | Create | Urgency scoring, shared by homepage + NoInfo dashboard |
| `Jobs/SignupGarbageCollectionJob.cs` | Create | Cancel signups on deactivated shifts after 7 days |
| `Jobs/ProcessAccountDeletionsJob.cs` | Modify | Handle DutySignup, ICalToken, event profile cleanup |
| `Migrations/YYYYMMDDHHMMSS_AddShiftManagement.cs` | Create | Auto-generated migration |

### Application Layer (`src/Humans.Application/`)
| File | Action | Purpose |
|------|--------|---------|
| `Interfaces/IShiftAuthorizationService.cs` | Create | Authorization interface |
| `Interfaces/IEventSettingsService.cs` | Create | Event settings interface |
| `Interfaces/IRotaService.cs` | Create | Rota CRUD interface |
| `Interfaces/IShiftService.cs` | Create | Shift CRUD interface |
| `Interfaces/IDutySignupService.cs` | Create | Signup state machine interface |
| `Interfaces/IShiftUrgencyService.cs` | Create | Urgency scoring interface |
| `Interfaces/IVolunteerEventProfileService.cs` | Create | Event profile CRUD + visibility interface |

### Infrastructure Layer (additional for Slice 2)
| File | Action | Purpose |
|------|--------|---------|
| `Services/VolunteerEventProfileService.cs` | Create | Event profile CRUD with visibility enforcement |
| `Data/Configurations/VolunteerEventProfileConfiguration.cs` | Create | EF config for event profile entity |

### Web Layer (`src/Humans.Web/`)
| File | Action | Purpose |
|------|--------|---------|
| `Controllers/ShiftsController.cs` | Create | `/Shifts`, `/Shifts/Mine`, `/Shifts/Settings` |
| `Controllers/ShiftAdminController.cs` | Create | `/Teams/{slug}/Shifts` — dept coordinator management |
| `Models/ShiftViewModels.cs` | Create | ViewModels for all shift views |
| `Views/Shifts/Index.cshtml` | Create | Browse open shifts with filters |
| `Views/Shifts/Mine.cshtml` | Create | Personal signups + iCal URL |
| `Views/Shifts/Settings.cshtml` | Create | EventSettings admin form |
| `Views/ShiftAdmin/Index.cshtml` | Create | Dept coordinator shift management |
| `Views/Shared/_ShiftCards.cshtml` | Create | Homepage shift card partials |
| `Views/Home/Dashboard.cshtml` | Modify | Add shift cards when system open |
| `Views/Team/Details.cshtml` | Modify | Add shifts summary card for departments |
| `Views/Shared/_Layout.cshtml` | Modify | Add Shifts nav item |
| `Extensions/InfrastructureServiceCollectionExtensions.cs` | Modify | Register new services |
| `Extensions/RecurringJobExtensions.cs` | Modify | Register GC job |
| `Authorization/RoleAssignmentClaimsTransformation.cs` | Modify | Add NoInfoAdmin claim |

### Tests
| File | Action | Purpose |
|------|--------|---------|
| `tests/Humans.Domain.Tests/Entities/ShiftTests.cs` | Create | Computed properties, period classification |
| `tests/Humans.Domain.Tests/Entities/DutySignupTests.cs` | Create | State machine transitions |
| `tests/Humans.Domain.Tests/Enums/EnumStringStabilityTests.cs` | Modify | Add new enums to stability checks |
| `tests/Humans.Application.Tests/Services/DutySignupServiceTests.cs` | Create | Invariants, overlap, capacity, EE |
| `tests/Humans.Application.Tests/Services/ShiftAuthorizationServiceTests.cs` | Create | Coordinator resolution, caching |
| `tests/Humans.Application.Tests/Services/ShiftUrgencyServiceTests.cs` | Create | Urgency scoring |

---

## Chunk 1: Domain Model & Database

### Task 1: Create shift management enums

**Files:**
- Create: `src/Humans.Domain/Enums/DutyPriority.cs`
- Create: `src/Humans.Domain/Enums/SignupPolicy.cs`
- Create: `src/Humans.Domain/Enums/SignupStatus.cs`
- Create: `src/Humans.Domain/Enums/ShiftPeriod.cs`
- Modify: `src/Humans.Domain/Enums/AuditAction.cs`
- Test: `tests/Humans.Domain.Tests/Enums/EnumStringStabilityTests.cs`

- [ ] **Step 1: Create the four new enum files**

Each enum follows the existing pattern — file-scoped namespace, explicit int values:

```csharp
// DutyPriority.cs
namespace Humans.Domain.Enums;
public enum DutyPriority { Normal = 0, Important = 1, Essential = 2 }

// SignupPolicy.cs
namespace Humans.Domain.Enums;
public enum SignupPolicy { Public = 0, RequireApproval = 1 }

// SignupStatus.cs
namespace Humans.Domain.Enums;
public enum SignupStatus { Pending = 0, Confirmed = 1, Refused = 2, Bailed = 3, Cancelled = 4, NoShow = 5 }

// ShiftPeriod.cs
namespace Humans.Domain.Enums;
public enum ShiftPeriod { Build = 0, Event = 1, Strike = 2 }
```

- [ ] **Step 2: Add 6 audit actions to AuditAction enum**

Append to `AuditAction.cs`:
```csharp
DutySignupConfirmed,
DutySignupRefused,
DutySignupVoluntold,
DutySignupBailed,
DutySignupNoShow,
DutySignupCancelled
```

- [ ] **Step 3: Add new enums to EnumStringStabilityTests**

Add `TheoryData` entries for the 3 stored enums (`DutyPriority`, `SignupPolicy`, `SignupStatus`) with their exact member names. `ShiftPeriod` is NOT stored in the database (it's a computed result), so it does NOT go in string stability tests.

- [ ] **Step 4: Run tests**

Run: `dotnet test tests/Humans.Domain.Tests/ --filter EnumStringStability -v minimal`
Expected: All new enum stability tests pass.

- [ ] **Step 5: Commit**

```
feat: add shift management enums and audit actions
```

---

### Task 2: Create EventSettings entity

**Files:**
- Create: `src/Humans.Domain/Entities/EventSettings.cs`
- Create: `src/Humans.Infrastructure/Data/Configurations/EventSettingsConfiguration.cs`

- [ ] **Step 1: Create EventSettings entity**

Follow the Team.cs pattern. Key fields per spec §2.1:
- `Id` (Guid, init), `EventName` (string), `TimeZoneId` (string)
- `GateOpeningDate` (LocalDate), `BuildStartOffset` (int), `EventEndOffset` (int), `StrikeEndOffset` (int)
- `EarlyEntryCapacity` (Dictionary<int, int>), `BarriosEarlyEntryAllocation` (Dictionary<int, int>?)
- `EarlyEntryClose` (Instant), `IsShiftBrowsingOpen` (bool), `GlobalVolunteerCap` (int?)
- `ReminderLeadTimeHours` (int), `IsActive` (bool), `CreatedAt`/`UpdatedAt` (Instant)

Add a helper method: `GetEarlyEntryCapacityForDay(int dayOffset)` that implements the step function lookup.

- [ ] **Step 2: Create EF configuration**

Table name: `event_settings`. Use the CampSeasonConfiguration.cs jsonb pattern for `EarlyEntryCapacity` and `BarriosEarlyEntryAllocation` — `JsonSerializer.Serialize/Deserialize` with `ValueComparer`. `LocalDate` and `Instant` types are handled natively by Npgsql.NodaTime. All string fields need `HasMaxLength()`.

- [ ] **Step 3: Build to verify compilation**

Run: `dotnet build Humans.slnx`
Expected: Clean build.

- [ ] **Step 4: Commit**

```
feat: add EventSettings entity and EF configuration
```

---

### Task 3: Create Rota entity

**Files:**
- Create: `src/Humans.Domain/Entities/Rota.cs`
- Create: `src/Humans.Infrastructure/Data/Configurations/RotaConfiguration.cs`

- [ ] **Step 1: Create Rota entity**

Per spec §2.2:
- `Id` (Guid, init), `EventSettingsId` (Guid), `TeamId` (Guid)
- `Name` (string), `Description` (string?)
- `Priority` (DutyPriority), `Policy` (SignupPolicy), `IsActive` (bool, default true)
- `CreatedAt`/`UpdatedAt` (Instant)
- Navigation: `EventSettings`, `Team`, `Shifts` (ICollection)

- [ ] **Step 2: Create EF configuration**

Table name: `rotas`. Enum conversions with `HasConversion<string>().HasMaxLength(50)`. FK to `event_settings` with `DeleteBehavior.Restrict`. FK to `teams` with `DeleteBehavior.Restrict`. Index on `(EventSettingsId, TeamId)`.

- [ ] **Step 3: Build**

Run: `dotnet build Humans.slnx`

- [ ] **Step 4: Commit**

```
feat: add Rota entity and EF configuration
```

---

### Task 4: Create Shift entity with computed properties

**Files:**
- Create: `src/Humans.Domain/Entities/Shift.cs`
- Create: `src/Humans.Infrastructure/Data/Configurations/ShiftConfiguration.cs`
- Create: `tests/Humans.Domain.Tests/Entities/ShiftTests.cs`

- [ ] **Step 1: Write tests for Shift computed properties**

Test the key computed behaviors:
- `GetAbsoluteStart(eventSettings)` resolves correctly from DayOffset + StartTime + timezone
- `GetAbsoluteEnd(eventSettings)` = AbsoluteStart + Duration
- `GetShiftPeriod(eventSettings)` returns Build/Event/Strike correctly based on DayOffset vs EventEndOffset — test boundary cases: DayOffset=-1 (Build), DayOffset=0 (Event), DayOffset=EventEndOffset (Event), DayOffset=EventEndOffset+1 (Strike)
- Overnight shift (22:00 + 8h) ends next day correctly
- `IsEarlyEntry` returns true when DayOffset < 0

Use `InZoneLeniently` in the implementation (DST-safe per spec §3.1).

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Humans.Domain.Tests/ --filter ShiftTests -v minimal`
Expected: Compilation errors (Shift class doesn't exist yet).

- [ ] **Step 3: Create Shift entity**

Per spec §2.3:
- `Id` (Guid, init), `RotaId` (Guid), `Title` (string), `Description` (string?)
- `DayOffset` (int), `StartTime` (LocalTime), `Duration` (Duration)
- `MinVolunteers` (int), `MaxVolunteers` (int), `AdminOnly` (bool), `IsActive` (bool, default true)
- `CreatedAt`/`UpdatedAt` (Instant)
- Navigation: `Rota`, `DutySignups` (ICollection)

Computed methods (take EventSettings parameter — not stored):
```csharp
public Instant GetAbsoluteStart(EventSettings eventSettings)
{
    var tz = DateTimeZoneProviders.Tzdb[eventSettings.TimeZoneId];
    var date = eventSettings.GateOpeningDate.PlusDays(DayOffset);
    return date.At(StartTime).InZoneLeniently(tz).ToInstant();
}

public Instant GetAbsoluteEnd(EventSettings eventSettings) =>
    GetAbsoluteStart(eventSettings).Plus(Duration);

public bool IsEarlyEntry => DayOffset < 0;

public ShiftPeriod GetShiftPeriod(EventSettings eventSettings) =>
    DayOffset < 0 ? ShiftPeriod.Build :
    DayOffset <= eventSettings.EventEndOffset ? ShiftPeriod.Event :
    ShiftPeriod.Strike;
```

- [ ] **Step 4: Create EF configuration**

Table name: `shifts`. Duration stored as `bigint` (total seconds) with custom ValueConverter:
```csharp
builder.Property(s => s.Duration)
    .HasConversion(
        d => (long)d.TotalSeconds,
        s => Duration.FromSeconds(s));
```
`LocalTime` handled natively by Npgsql.NodaTime. FK to `rotas` with cascade delete. Index on `RotaId`.

- [ ] **Step 5: Run tests**

Run: `dotnet test tests/Humans.Domain.Tests/ --filter ShiftTests -v minimal`
Expected: All pass.

- [ ] **Step 6: Commit**

```
feat: add Shift entity with computed properties and tests
```

---

### Task 5: Create DutySignup entity with state machine

**Files:**
- Create: `src/Humans.Domain/Entities/DutySignup.cs`
- Create: `src/Humans.Infrastructure/Data/Configurations/DutySignupConfiguration.cs`
- Create: `tests/Humans.Domain.Tests/Entities/DutySignupTests.cs`

- [ ] **Step 1: Write tests for DutySignup state transitions**

Test the state machine per spec §4.1:
- `Confirm()` from Pending → Confirmed (sets ReviewedByUserId, ReviewedAt)
- `Refuse()` from Pending → Refused
- `Bail()` from Confirmed → Bailed (sets ReviewedByUserId if lead-initiated)
- `Bail()` from Pending → Bailed (volunteer withdrawal)
- `MarkNoShow()` from Confirmed → NoShow (only after shift end time)
- `Cancel()` → Cancelled (system-only)
- Invalid transitions throw `InvalidOperationException` (e.g., Bail from Refused)

- [ ] **Step 2: Run tests to verify they fail**

- [ ] **Step 3: Create DutySignup entity**

Per spec §2.4:
- `Id` (Guid, init), `UserId` (Guid), `ShiftId` (Guid)
- `Status` (SignupStatus), `Enrolled` (bool), `EnrolledByUserId` (Guid?), `ReviewedByUserId` (Guid?), `ReviewedAt` (Instant?), `StatusReason` (string?)
- `CreatedAt`/`UpdatedAt` (Instant)
- Navigation: `User`, `Shift`, `EnrolledByUser`, `ReviewedByUser`

State transition methods on the entity (manual guard clauses — do NOT follow Application.cs's Stateless pattern which uses `SystemClock.Instance` directly):
```csharp
public void Confirm(Guid reviewerUserId, IClock clock)
{
    if (Status is not SignupStatus.Pending)
        throw new InvalidOperationException($"Cannot confirm signup in {Status} state");
    Status = SignupStatus.Confirmed;
    ReviewedByUserId = reviewerUserId;
    ReviewedAt = clock.GetCurrentInstant();
    UpdatedAt = clock.GetCurrentInstant();
}
```

- [ ] **Step 4: Create EF configuration**

Table name: `duty_signups`. Enum conversion for Status. FKs to `users` (Restrict — GDPR uses anonymized user, not null), `shifts` (Restrict — application layer handles Pending→Cancelled before shift deletion, not DB cascade). Indexes on `UserId`, `ShiftId`, `(ShiftId, Status)` for capacity queries.

- [ ] **Step 5: Run tests**

Run: `dotnet test tests/Humans.Domain.Tests/ --filter DutySignupTests -v minimal`
Expected: All pass.

- [ ] **Step 6: Commit**

```
feat: add DutySignup entity with state machine and tests
```

---

### Task 6: Modify existing entities and add DbSets

**Files:**
- Modify: `src/Humans.Domain/Constants/RoleNames.cs`
- Modify: `src/Humans.Domain/Entities/User.cs`
- Modify: `src/Humans.Domain/Entities/EmailOutboxMessage.cs`
- Modify: `src/Humans.Infrastructure/Data/Configurations/EmailOutboxMessageConfiguration.cs`
- Modify: `src/Humans.Infrastructure/Data/HumansDbContext.cs`

- [ ] **Step 1: Add NoInfoAdmin to RoleNames**

```csharp
public const string NoInfoAdmin = "NoInfoAdmin";
```

- [ ] **Step 2: Add ICalToken to User entity**

```csharp
/// <summary>Token for personal iCal feed URL. Regeneratable.</summary>
public Guid? ICalToken { get; set; }
```

- [ ] **Step 3: Add DutySignupId to EmailOutboxMessage**

```csharp
/// <summary>FK to DutySignup for notification deduplication.</summary>
public Guid? DutySignupId { get; set; }
public DutySignup? DutySignup { get; set; }
```

Update `EmailOutboxMessageConfiguration.cs` to add the FK with `DeleteBehavior.SetNull`.

- [ ] **Step 4: Add DbSets to HumansDbContext**

```csharp
public DbSet<EventSettings> EventSettings => Set<EventSettings>();
public DbSet<Rota> Rotas => Set<Rota>();
public DbSet<Shift> Shifts => Set<Shift>();
public DbSet<DutySignup> DutySignups => Set<DutySignup>();
```

- [ ] **Step 5: Build**

Run: `dotnet build Humans.slnx`

- [ ] **Step 6: Create migration**

Run: `dotnet ef migrations add AddShiftManagement --project src/Humans.Infrastructure --startup-project src/Humans.Web`

Review the generated migration — verify it creates 4 tables (`event_settings`, `rotas`, `shifts`, `duty_signups`), adds `ical_token` to `users`, adds `duty_signup_id` to `email_outbox_messages`.

- [ ] **Step 7: Apply migration to dev database**

Run: `dotnet ef database update --project src/Humans.Infrastructure --startup-project src/Humans.Web`

- [ ] **Step 8: Run all tests**

Run: `dotnet test Humans.slnx -v minimal`
Expected: All existing tests still pass.

- [ ] **Step 9: Commit**

```
feat: add shift management entities, migration, and NoInfoAdmin role
```

---

## Chunk 2: Authorization & Core Services

### Task 7: ShiftAuthorizationService

**Files:**
- Create: `src/Humans.Application/Interfaces/IShiftAuthorizationService.cs`
- Create: `src/Humans.Infrastructure/Services/ShiftAuthorizationService.cs`
- Create: `tests/Humans.Application.Tests/Services/ShiftAuthorizationServiceTests.cs`

- [ ] **Step 1: Write tests for authorization scenarios**

Test cases:
- `IsDeptCoordinator_UserWithManagementRoleOnParentTeam_ReturnsTrue`
- `IsDeptCoordinator_UserWithManagementRoleOnSubTeam_ReturnsFalse`
- `IsDeptCoordinator_UserWithNonManagementRole_ReturnsFalse`
- `IsDeptCoordinator_CacheInvalidatedOnRoleChange_ReturnsUpdatedResult`
- `CanManageShifts_Admin_ReturnsTrue`
- `CanManageShifts_NoInfoAdmin_ReturnsFalse` (NoInfoAdmin can approve/voluntell but NOT create/edit)
- `GetCoordinatorDepartmentIds_ReturnsAllDepartmentsForUser`

- [ ] **Step 2: Create interface**

```csharp
public interface IShiftAuthorizationService
{
    Task<bool> IsDeptCoordinatorAsync(Guid userId, Guid departmentTeamId);
    Task<bool> CanManageShiftsAsync(Guid userId, Guid departmentTeamId);
    Task<bool> CanApproveSignupsAsync(Guid userId, Guid departmentTeamId);
    Task<IReadOnlyList<Guid>> GetCoordinatorDepartmentIdsAsync(Guid userId);
}
```

- [ ] **Step 3: Implement with IMemoryCache**

Use `IMemoryCache` with 60-second TTL (matches `RoleAssignmentClaimsTransformation` pattern). Cache key: `$"shift-auth:{userId}"`. Query: join `TeamMembers` → `TeamRoleAssignments` → `TeamRoleDefinitions` where `IsManagement = true` and team `ParentTeamId IS NULL` and team `SystemTeamType = None`.

60-second TTL staleness is acceptable at this scale — remove `InvalidateCache()` from the interface to avoid dead code. The cache naturally refreshes within a minute of any role change.

- [ ] **Step 4: Register in DI**

Add to `InfrastructureServiceCollectionExtensions.cs`:
```csharp
services.AddScoped<IShiftAuthorizationService, ShiftAuthorizationService>();
```

- [ ] **Step 5: Run tests**

Expected: All pass.

- [ ] **Step 6: Commit**

```
feat: add ShiftAuthorizationService with cached dept coordinator checks
```

---

### Task 8: EventSettingsService

**Files:**
- Create: `src/Humans.Application/Interfaces/IEventSettingsService.cs`
- Create: `src/Humans.Infrastructure/Services/EventSettingsService.cs`

- [ ] **Step 1: Create interface and service**

Methods:
- `GetActiveAsync()` — returns the single active EventSettings or null
- `GetByIdAsync(Guid id)` — by PK
- `CreateAsync(EventSettings entity)` — validates only one IsActive=true
- `UpdateAsync(EventSettings entity)` — standard update
- `GetEarlyEntryCapacityForDay(EventSettings settings, int dayOffset)` — step function lookup
- `GetAvailableEeSlots(EventSettings settings, int dayOffset)` — capacity minus barrios

The step function lookup: find the largest key in `EarlyEntryCapacity` that is ≤ dayOffset. If no key found (day is before first defined level), return 0.

- [ ] **Step 2: Register in DI**

- [ ] **Step 3: Build**

- [ ] **Step 4: Commit**

```
feat: add EventSettingsService with EE capacity step function
```

---

### Task 9: RotaService and ShiftService

**Files:**
- Create: `src/Humans.Application/Interfaces/IRotaService.cs`
- Create: `src/Humans.Application/Interfaces/IShiftService.cs`
- Create: `src/Humans.Infrastructure/Services/RotaService.cs`
- Create: `src/Humans.Infrastructure/Services/ShiftService.cs`

- [ ] **Step 1: Create RotaService**

Methods: `CreateAsync`, `UpdateAsync`, `DeactivateAsync`, `DeleteAsync`, `GetByIdAsync`, `GetByDepartmentAsync(Guid teamId, Guid eventSettingsId)`.

`CreateAsync` validates:
- Team exists, `ParentTeamId IS NULL`, `SystemTeamType = None`
- EventSettingsId references an active EventSettings

`DeleteAsync` validates:
- No child shifts with Confirmed signups → throw if blocked

- [ ] **Step 2: Create ShiftService**

Methods: `CreateAsync`, `UpdateAsync`, `DeactivateAsync`, `DeleteAsync`, `GetByIdAsync`, `GetByRotaAsync`.

`CreateAsync` validates:
- DayOffset within `BuildStartOffset..StrikeEndOffset`
- `MinVolunteers <= MaxVolunteers`

`DeleteAsync` validates:
- No Confirmed signups → throw if blocked
- Pending signups cascaded to Cancelled

Provides resolution helper: `ResolveShiftTimes(Shift shift, EventSettings es)` returning `(Instant start, Instant end, ShiftPeriod period)`.

- [ ] **Step 3: Register in DI**

- [ ] **Step 4: Build and run tests**

- [ ] **Step 5: Commit**

```
feat: add RotaService and ShiftService with validation
```

---

### Task 10: DutySignupService with invariants

**Files:**
- Create: `src/Humans.Application/Interfaces/IDutySignupService.cs`
- Create: `src/Humans.Infrastructure/Services/DutySignupService.cs`
- Create: `tests/Humans.Application.Tests/Services/DutySignupServiceTests.cs`

- [ ] **Step 1: Write tests for signup invariants**

Test cases per spec §4.2:
- `SignUp_PublicPolicy_CreatesConfirmed`
- `SignUp_RequireApprovalPolicy_CreatesPending`
- `SignUp_RequireApproval_DeptCoordinatorOwnDept_CreatesConfirmed`
- `SignUp_OverlappingShift_ReturnsOverlapError`
- `SignUp_CapacityReached_ReturnsCapacityWarning` (warning, not block)
- `SignUp_EeCapReached_ReturnsEeWarning`
- `SignUp_AdminOnly_RegularVolunteer_ReturnsError`
- `SignUp_SystemClosed_RegularVolunteer_ReturnsError`
- `SignUp_AfterEeClose_BuildShift_RegularVolunteer_ReturnsError`
- `Approve_RevalidatesInvariants_OverlapCreatedSinceRequest_ReturnsWarning`
- `Bail_OwnSignup_SetsStatusBailed`
- `Bail_PendingSignup_SetsStatusBailed` (volunteer withdrawal)
- `Bail_BuildShift_AfterEeClose_RegularVolunteer_ReturnsError`
- `Voluntell_CreatesConfirmedWithEnrolledFlag`

- [ ] **Step 2: Create interface**

```csharp
public interface IDutySignupService
{
    Task<SignupResult> SignUpAsync(Guid userId, Guid shiftId, Guid? actorUserId = null);
    Task<SignupResult> ApproveAsync(Guid signupId, Guid reviewerUserId);
    Task<SignupResult> RefuseAsync(Guid signupId, Guid reviewerUserId, string? reason);
    Task<SignupResult> BailAsync(Guid signupId, Guid actorUserId, string? reason);
    Task<SignupResult> VoluntellAsync(Guid userId, Guid shiftId, Guid enrollerUserId);
    Task<SignupResult> MarkNoShowAsync(Guid signupId, Guid reviewerUserId);
    Task<IReadOnlyList<DutySignup>> GetByUserAsync(Guid userId, Guid? eventSettingsId = null);
    Task<IReadOnlyList<DutySignup>> GetByShiftAsync(Guid shiftId);
}
```

`SignupResult` is a simple result type: `Success bool`, `Warning string?`, `Error string?`, `Signup DutySignup?`.

- [ ] **Step 3: Implement DutySignupService**

Key implementation details:
- **Overlap check:** Query user's Confirmed signups, resolve absolute times for each, check for time range overlap with the target shift.
- **Capacity:** Count Confirmed signups for the shift, compare to MaxVolunteers. Return warning (not error) if exceeded.
- **EE cap:** If shift is build-period, count unique users with Confirmed build signups, compare to `GetAvailableEeSlots()`. Return warning if exceeded.
- **EE freeze:** After `EarlyEntryClose`, block both bails AND new build signups for non-privileged users.
- **Approval revalidation:** On `ApproveAsync`, re-run overlap check and capacity check. Return warning to approver if issues found.
- **Audit logging:** Call `IAuditLogService` for every state transition.

- [ ] **Step 4: Run tests**

Expected: All pass.

- [ ] **Step 5: Commit**

```
feat: add DutySignupService with state machine and invariant enforcement
```

---

### Task 11: ShiftUrgencyService

**Files:**
- Create: `src/Humans.Application/Interfaces/IShiftUrgencyService.cs`
- Create: `src/Humans.Infrastructure/Services/ShiftUrgencyService.cs`
- Create: `tests/Humans.Application.Tests/Services/ShiftUrgencyServiceTests.cs`

- [ ] **Step 1: Write tests for urgency scoring**

Per spec §6.4:
```
remainingSlots = MaxVolunteers - ConfirmedCount
understaffedMultiplier = (ConfirmedCount < MinVolunteers) ? 2 : 1
score = remainingSlots × priorityWeight × durationHours × understaffedMultiplier
```
Priority weights: Normal=1, Important=3, Essential=6.

Test cases:
- Normal priority, 5 remaining slots, 4h shift → score = 5×1×4×1 = 20
- Essential priority, 2 remaining, 8h, understaffed → score = 2×6×8×2 = 192
- Fully staffed shift (remaining=0) → score = 0

- [ ] **Step 2: Implement**

Methods:
- `GetUrgentShiftsAsync(Guid eventSettingsId, int? limit, Guid? departmentId, LocalDate? date)` — returns shifts ranked by urgency, with filtering
- `CalculateScore(Shift shift, int confirmedCount)` — pure calculation

- [ ] **Step 3: Run tests**

- [ ] **Step 4: Register in DI and commit**

```
feat: add ShiftUrgencyService with urgency scoring
```

---

### Task 12: Extend ProcessAccountDeletionsJob and add GC job

**Files:**
- Modify: `src/Humans.Infrastructure/Jobs/ProcessAccountDeletionsJob.cs`
- Create: `src/Humans.Infrastructure/Jobs/SignupGarbageCollectionJob.cs`
- Modify: `src/Humans.Web/Extensions/RecurringJobExtensions.cs`

- [ ] **Step 1: Extend ProcessAccountDeletionsJob**

Add to the deletion flow (after existing cleanup):
- Cancel active DutySignups (Confirmed/Pending → Cancelled)
- Set `ICalToken = null` on User
- Delete volunteer event profile data (when entity exists)
- Audit log the cleanup

- [ ] **Step 2: Create SignupGarbageCollectionJob**

Hangfire daily job. Finds DutySignups where:
- Status is Confirmed or Pending
- Related Shift.IsActive = false
- Shift.UpdatedAt < now - 7 days

Sets status to Cancelled. Logs to audit.

- [ ] **Step 3: Register GC job in DI and RecurringJobExtensions**

Add `services.AddScoped<SignupGarbageCollectionJob>()` to `InfrastructureServiceCollectionExtensions.cs` (Hangfire resolves from DI). Then register in `RecurringJobExtensions.cs` — daily at 04:00 UTC.

- [ ] **Step 4: Build and run tests**

- [ ] **Step 5: Commit**

```
feat: add signup GC job and extend account deletion for shift data
```

---

## Chunk 3: Slice 1 Web Layer — Lead Management

### Task 13: EventSettings admin page (`/Shifts/Settings`)

**Files:**
- Create: `src/Humans.Web/Controllers/ShiftsController.cs` (initial — Settings action only)
- Create: `src/Humans.Web/Models/ShiftViewModels.cs` (initial — EventSettingsViewModel)
- Create: `src/Humans.Web/Views/Shifts/Settings.cshtml`

- [ ] **Step 1: Create ShiftsController with Settings action**

`[Authorize]` on class. Settings action requires Admin role check. GET displays form, POST saves. Use the existing controller pattern from AdminController.

Route: `[Route("Shifts")]`, action: `[HttpGet("Settings")]` / `[HttpPost("Settings")]`.

ViewModel includes all EventSettings fields. The EE capacity and barrios allocation dictionaries render as editable key-value pair lists in the form (day offset → capacity). Admin can add/remove rows.

- [ ] **Step 2: Create Settings view**

Bootstrap form. Date fields use `<input type="date">` (LocalDate). The jsonb dictionaries render as a dynamic table with "Add Row" / "Remove" buttons (simple JS, no framework needed). Toggle for `IsShiftBrowsingOpen`.

No localization needed (admin page per CLAUDE.md rules).

- [ ] **Step 3: Test manually**

Run the app, navigate to `/Shifts/Settings` as Admin. Create an EventSettings record. Verify it saves and loads correctly.

- [ ] **Step 4: Commit**

```
feat: add EventSettings admin page at /Shifts/Settings
```

---

### Task 14: Department Coordinator shift management (`/Teams/{slug}/Shifts`)

**Files:**
- Create: `src/Humans.Web/Controllers/ShiftAdminController.cs`
- Create: `src/Humans.Web/Views/ShiftAdmin/Index.cshtml`
- Extend: `src/Humans.Web/Models/ShiftViewModels.cs`

- [ ] **Step 1: Create ShiftAdminController**

Route: `[Route("Teams/{slug}/Shifts")]`. All actions check `IShiftAuthorizationService.CanManageShiftsAsync` or `CanApproveSignupsAsync` for the team. Return `Forbid()` if not authorized. Also allow Admin role.

Actions:
- `Index(string slug)` — GET, main management view
- `CreateRota(string slug, CreateRotaModel model)` — POST
- `EditRota(string slug, Guid rotaId, EditRotaModel model)` — POST
- `CreateShift(string slug, Guid rotaId, CreateShiftModel model)` — POST
- `EditShift(string slug, Guid shiftId, EditShiftModel model)` — POST
- `DeactivateShift(string slug, Guid shiftId)` — POST
- `ApproveSignup(string slug, Guid signupId)` — POST
- `RefuseSignup(string slug, Guid signupId, string? reason)` — POST
- `MarkNoShow(string slug, Guid signupId)` — POST (only for past shifts)

Validate the team slug resolves to a parent team (department).

- [ ] **Step 2: Create the management view**

The Index view shows:
- **Header:** Department name, fill rate, pending count
- **Pending approvals panel:** Table of Pending signups with Approve/Refuse buttons
- **Rotas section:** Each rota as a collapsible card with its shifts listed. Per-shift: title, date (resolved), time, fill bar, edit/deactivate buttons
- **Create Rota form:** Name, priority dropdown, policy dropdown
- **Create Shift form** (within each rota): Date picker (converts to DayOffset), start time, duration, min/max volunteers

Date picker: The form shows a calendar. On submit, JS computes `DayOffset = selectedDate - GateOpeningDate` (days between). The EventSettings GateOpeningDate is passed to the view for this calculation.

- [ ] **Step 3: Test manually**

Create rotas and shifts for a department. Verify they appear, fill rates display, date resolution works.

- [ ] **Step 4: Commit**

```
feat: add department coordinator shift management at /Teams/{slug}/Shifts
```

---

### Task 15: Team page shifts summary card and nav updates

**Files:**
- Modify: `src/Humans.Web/Views/Team/Details.cshtml`
- Modify: `src/Humans.Web/Views/Shared/_Layout.cshtml`
- Modify: `src/Humans.Web/Authorization/RoleAssignmentClaimsTransformation.cs`

- [ ] **Step 1: Add NoInfoAdmin to claims transformation**

In `RoleAssignmentClaimsTransformation.cs`, `NoInfoAdmin` is already handled by the generic role assignment query (it queries all active `RoleAssignment.RoleName` values). Verify this — no code change needed unless the query has a whitelist. If it queries generically, `NoInfoAdmin` will automatically be added as a role claim.

- [ ] **Step 2: Add Shifts nav item to _Layout.cshtml**

Add between existing nav items (after Teams, before Consents):
```razor
@if (isShiftBrowsingOpen || hasShiftSignups || User.IsInRole(RoleNames.NoInfoAdmin) || User.IsInRole(RoleNames.Admin) || isDeptCoordinator)
{
    <li class="nav-item">
        <a class="nav-link" asp-controller="Shifts" asp-action="Index">@Localizer["Nav_Shifts"]</a>
    </li>
}
```

The `isShiftBrowsingOpen` and `hasShiftSignups` flags need to be computed. Add them to the layout's ViewData or use a ViewComponent. Follow the existing pattern for how the Review badge count is computed (check existing _Layout.cshtml).

- [ ] **Step 3: Add shifts summary card to Team Details**

For parent teams (departments) only — check `Model.Team.ParentTeamId == null && Model.Team.SystemTeamType == SystemTeamType.None`. Show a card with:
- "Shifts" heading
- Fill rate (confirmed / total slots)
- Pending approvals count
- Link to `/Teams/{slug}/Shifts`

- [ ] **Step 4: Test manually**

Verify nav appears for Admin. Verify department team pages show shifts card.

- [ ] **Step 5: Commit**

```
feat: add Shifts nav item and team page shifts summary card
```

---

## Chunk 4: Slice 2 Web Layer — Volunteer Experience

### Task 16: Shift browsing page (`/Shifts`)

**Files:**
- Extend: `src/Humans.Web/Controllers/ShiftsController.cs`
- Create: `src/Humans.Web/Views/Shifts/Index.cshtml`
- Extend: `src/Humans.Web/Models/ShiftViewModels.cs`

- [ ] **Step 1: Add Index action to ShiftsController**

GET `/Shifts`. Requires `IsShiftBrowsingOpen = true` for regular volunteers (or user has existing signups). Dept Coordinators/NoInfoAdmin/Admin always see it.

Query all active shifts from active EventSettings, grouped by department. Apply filters: date range, department dropdown. Resolve absolute times for display. Include fill status and the user's existing signups (to show "already signed up" badges).

ViewModel: `ShiftBrowseViewModel` with `List<DepartmentShiftGroup>` where each group has department name, rotas, shifts, and fill counts.

- [ ] **Step 2: Create the browsing view**

- Filter bar: date picker, department dropdown, "Show full shifts" toggle
- Shifts listed by department → rota → shift, showing: title, date/time (resolved), duration, slots remaining, priority badge, Sign Up button
- Sign Up button: POST form to signup action
- If user already has a Confirmed/Pending signup for a shift, show status badge instead of button

- [ ] **Step 3: Test manually**

Browse shifts, verify filters work, dates display correctly in event timezone.

- [ ] **Step 4: Commit**

```
feat: add shift browsing page at /Shifts with filters
```

---

### Task 17: Signup and bail flows

**Files:**
- Extend: `src/Humans.Web/Controllers/ShiftsController.cs`

- [ ] **Step 1: Add SignUp action**

POST `/Shifts/SignUp`. Takes `shiftId`. Gets current user. Calls `IDutySignupService.SignUpAsync`. On success: redirect back with success TempData. On overlap error: redirect with error TempData showing conflict details. On warning (capacity/EE): show confirmation page or proceed with warning message.

- [ ] **Step 2: Add Bail action**

POST `/Shifts/Bail`. Takes `signupId`. Calls `IDutySignupService.BailAsync`. Handles EE freeze error.

- [ ] **Step 3: Test manually**

Sign up for a shift (public policy → instant confirm). Sign up for RequireApproval → pending. Bail a confirmed signup. Try overlapping signup → see error. Try build signup after EE close → see error.

- [ ] **Step 4: Commit**

```
feat: add shift signup and bail flows
```

---

### Task 18: Personal shifts page (`/Shifts/Mine`)

**Files:**
- Extend: `src/Humans.Web/Controllers/ShiftsController.cs`
- Create: `src/Humans.Web/Views/Shifts/Mine.cshtml`

- [ ] **Step 1: Add Mine action**

GET `/Shifts/Mine`. Query all DutySignups for current user in the active event. Group by status: Confirmed (upcoming), Pending, Past (completed + bailed + noshow). Resolve absolute times.

Include iCal feed URL: if `User.ICalToken` is null, generate one (Guid.NewGuid()) and save. Display the URL with a "Copy" button and a "Regenerate" button.

- [ ] **Step 2: Create the view**

- **Upcoming Shifts** section: Confirmed signups sorted by date. Each row: title, department, date/time, Bail button
- **Pending** section: Pending signups. Each row: title, department, submitted date, Withdraw (Bail) button
- **Past** section: Completed, Bailed, NoShow signups. Read-only.
- **iCal Feed** section: URL display, Copy button (JS clipboard), Regenerate button (POST)

- [ ] **Step 3: Test manually**

- [ ] **Step 4: Commit**

```
feat: add personal shifts page at /Shifts/Mine with iCal URL
```

---

### Task 19: Homepage shift cards

**Files:**
- Create: `src/Humans.Web/Views/Shared/_ShiftCards.cshtml`
- Modify: `src/Humans.Web/Views/Home/Dashboard.cshtml`
- Modify: `src/Humans.Web/Controllers/HomeController.cs`

- [ ] **Step 1: Create shift cards partial**

Two cards rendered as a partial:
- **"My Shifts":** Next 3 confirmed shifts (title, dept, date/time). Pending count badge. Link to `/Shifts/Mine`.
- **"Shifts Need Help":** Top 3 urgent shifts from `IShiftUrgencyService`. Each: title, dept, date/time, slots remaining. Link to `/Shifts`.

- [ ] **Step 2: Update Dashboard.cshtml**

Conditionally include shift cards when `IsShiftBrowsingOpen = true` OR user has signups. Add after the existing profile/consents cards. Hide completed onboarding cards (profile complete, consents done) when the user is already a volunteer member.

- [ ] **Step 3: Update HomeController**

Add shift data to the DashboardViewModel (or create a ViewComponent). Query `IShiftUrgencyService` and `IDutySignupService.GetByUserAsync` for the current user.

- [ ] **Step 4: Test manually**

- [ ] **Step 5: Commit**

```
feat: add shift cards to volunteer homepage
```

---

### Task 20: Volunteer event profile card

**Files:**
- This depends on the storage mechanism decision (deferred in spec). For now, create the minimum viable approach.

- [ ] **Step 1: Decide storage mechanism**

Given the ~500 user scale and the jsonb pattern already used for CampSeason.Vibes, use **individual jsonb columns on a new `VolunteerEventProfile` entity** (1:1 with User, scoped to EventSettings). This is the cleanest — separate entity, separate read path, event-scoped.

Create: `src/Humans.Domain/Entities/VolunteerEventProfile.cs`
```csharp
public class VolunteerEventProfile
{
    public Guid Id { get; init; }
    public Guid UserId { get; init; }
    public Guid EventSettingsId { get; init; }
    public List<string> Skills { get; set; } = new();
    public List<string> Quirks { get; set; } = new();
    public List<string> Languages { get; set; } = new();
    public string? DietaryPreference { get; set; }
    public List<string> Allergies { get; set; } = new();
    public List<string> Intolerances { get; set; } = new();
    public string? MedicalConditions { get; set; }
    public bool SuppressScheduleChangeEmails { get; set; }
    public Instant CreatedAt { get; init; }
    public Instant UpdatedAt { get; set; }
}
```

- [ ] **Step 2: Create EF configuration, add DbSet, create migration**

Create `VolunteerEventProfileConfiguration.cs`. Table: `volunteer_event_profiles`. Unique index on `(UserId, EventSettingsId)`. Jsonb columns follow CampSeason.Vibes pattern. `MedicalConditions` uses `HasMaxLength(4000)`.

Add `DbSet<VolunteerEventProfile>` to `HumansDbContext.cs`.

Run: `dotnet ef migrations add AddVolunteerEventProfile --project src/Humans.Infrastructure --startup-project src/Humans.Web`
Then: `dotnet ef database update --project src/Humans.Infrastructure --startup-project src/Humans.Web`

- [ ] **Step 3: Add "Event Volunteer" card to profile edit page**

Show only when `IsShiftBrowsingOpen = true` or user has signups. Multi-select dropdowns for skills/quirks/languages/allergies/intolerances. Single-select for dietary. Free text for medical. Checkbox for `SuppressScheduleChangeEmails`.

This is a new section on the existing Profile Edit page, not a separate page.

- [ ] **Step 4: Create IVolunteerEventProfileService interface and VolunteerEventProfileService implementation**

Create `src/Humans.Application/Interfaces/IVolunteerEventProfileService.cs` and `src/Humans.Infrastructure/Services/VolunteerEventProfileService.cs`.

Methods: `GetOrCreateAsync(Guid userId, Guid eventSettingsId)`, `UpdateAsync(VolunteerEventProfile profile)`, `GetByUserAsync(Guid userId, Guid eventSettingsId, bool includeMedical)`.

Visibility enforcement: general fields visible to owner/dept coordinators/NoInfoAdmin/Admin. Medical conditions visible only to owner/NoInfoAdmin/Admin — the `includeMedical` parameter controls this, and the caller (controller) checks roles before passing `true`.

- [ ] **Step 5: Test manually**

Fill out the event profile. Verify data saves and loads. Verify medical conditions are hidden from dept coordinators.

- [ ] **Step 6: Commit**

```
feat: add volunteer event profile entity and profile edit card
```

---

## Chunk 5: Final Integration & Polish

### Task 21: Wire up DI, update Program.cs, smoke test

**Files:**
- Modify: `src/Humans.Web/Extensions/InfrastructureServiceCollectionExtensions.cs`
- Modify: `src/Humans.Web/Program.cs` (if needed)

- [ ] **Step 1: Register all new services in DI**

Ensure all interfaces → implementations are registered:
- `IShiftAuthorizationService` → `ShiftAuthorizationService`
- `IEventSettingsService` → `EventSettingsService`
- `IRotaService` → `RotaService`
- `IShiftService` → `ShiftService`
- `IDutySignupService` → `DutySignupService`
- `IShiftUrgencyService` → `ShiftUrgencyService`
- `IVolunteerEventProfileService` → `VolunteerEventProfileService`

All as `Scoped`.

- [ ] **Step 2: Run the full test suite**

Run: `dotnet test Humans.slnx -v minimal`
Expected: All tests pass (existing + new).

- [ ] **Step 3: Run the app and smoke test the full flow**

1. Admin: create EventSettings at `/Shifts/Settings`
2. Dept Coordinator: create a rota and shifts at `/Teams/{dept-slug}/Shifts`
3. Volunteer: browse shifts at `/Shifts`, sign up
4. Volunteer: view signups at `/Shifts/Mine`, see iCal URL
5. Dept Coordinator: approve pending signups
6. Volunteer: bail a confirmed signup
7. Verify homepage shows shift cards
8. Verify department team page shows shifts summary

- [ ] **Step 4: Commit**

```
feat: wire up shift management DI and complete Slices 1+2 integration
```

---

## Post-Completion Checklist

After all tasks are complete:

- [ ] Run `dotnet build Humans.slnx` — clean build
- [ ] Run `dotnet test Humans.slnx` — all tests pass
- [ ] Verify no LSP errors in new `.cs` files
- [ ] Update `docs/features/` — create `25-shift-management.md` feature doc
- [ ] Update `.claude/DATA_MODEL.md` with new entities
- [ ] Update `CLAUDE.md` if any project-wide conventions changed
- [ ] Deploy to QA via `bash /opt/docker/human/deploy-qa.sh`
