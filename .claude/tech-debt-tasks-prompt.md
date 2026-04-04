# Tech Debt Task Backlog — Claude Autonomous Prompt

## Mission

You are executing specific, pre-identified tech debt tasks from the backlog below. Work through them in order of priority. After completing each task, mark it done in this section of your handoff notes.

**Work autonomously.** Pick the next uncompleted task, implement it, verify the build passes, commit, push, move on.

---

## Architecture (4 layers)

```
src/Humans.Domain/          — Entities, enums, value objects (pure, no dependencies)
src/Humans.Application/     — Interfaces, DTOs, constants (business rules)
src/Humans.Infrastructure/  — EF Core, external services, Hangfire jobs
src/Humans.Web/             — Controllers, Razor views, authorization, tag helpers
tests/Humans.Application.Tests/ — Unit tests
```

---

## EXCLUSION ZONES — DO NOT TOUCH

```
src/Humans.Infrastructure/Data/HumansDbContext.cs
src/Humans.Infrastructure/Data/EntityConfigurations/**
src/Humans.Infrastructure/Migrations/**
```

Also do not modify:
- **Entity classes** in `src/Humans.Domain/Entities/`
- **JSON serialization attributes**
- **Migration files**
- **ConsentRecord** — append-only table
- **Existing test files** in `tests/` — don't modify, may add new ones

---

## CODING RULES

1. **NodaTime for all dates/times** — `Instant`, `LocalDate`, `ZonedDateTime`. Never `DateTime`.
2. **Explicit StringComparison** — on all string methods.
3. **No enum comparison operators in LINQ-to-SQL** — use `.Contains()`.
4. **Magic string avoidance** — `nameof()`, `RoleNames.*`, constants.
5. **Font Awesome 6 only** — `fa-solid fa-*`.
6. **UI terminology** — "humans" not "members"/"volunteers".
7. **No concurrency tokens.**
8. **Every new page needs a nav link.**
9. **Admin pages don't need localization.**

## Build Command

```
dotnet build Humans.slnx -v q && dotnet test Humans.slnx -v q --filter "FullyQualifiedName~Application"
```

---

## Task Backlog

Tasks are ordered by priority. Check git log and handoff notes to see which are already done. Mark completed tasks in your handoff notes.

### HIGH PRIORITY

#### T1: Consolidate caching patterns
**Status:** Not started
**What:** 16 files use `IMemoryCache` with three different patterns: `GetOrCreateAsync` (best), direct `Set`/`Get`, and `TryGetValue` + manual set. Standardize on `GetOrCreateAsync` everywhere. Extract cache key strings to a `CacheKeys` static class.
**Where:** Search for `IMemoryCache` usage across `src/Humans.Infrastructure/Services/`

#### T2: Consolidate controller error handling
**Status:** Not started
**What:** Three patterns: try-catch → `TempData["ErrorMessage"]`, try-catch → `ModelState.AddModelError`, no error handling. Standardize on TempData pattern with `SetSuccess`/`SetError` helper methods on `HumansControllerBase`.
**Where:** `src/Humans.Web/Controllers/`

#### T3: Move business logic out of controllers
**Status:** Not started
**What:** Several controllers do LINQ transforms, ViewModel construction, and multi-step mutations inline. These belong in services. Fix the worst offenders first.
**Known offenders:**
- `TicketController` injects `HumansDbContext` directly — all analytics logic is inline
- `HumanController.ProvisionEmail` — 4-step workspace provisioning with ordering constraint
- `ProfileController.Edit` POST — phone validation, burner CV check, tier field validation, image type sniffing
- `CampController.Edit` — name-lock date check is a domain rule bypassing `ICampService`
- Effective-members rollup duplicated in `VolController.DepartmentDetail` and `TeamController.Details`

#### T4: Consolidate authorization patterns
**Status:** Not started
**What:** Three mechanisms used inconsistently: `[Authorize(Roles)]` attributes (best for static), `User.IsInRole()` inline checks, service-level `IsUserBoardMemberAsync` calls. Replace inline checks with attributes where feasible.
**Where:** `src/Humans.Web/Controllers/`

### MEDIUM PRIORITY

#### T5: Standardize background job patterns
**Status:** Not started
**What:** 14 jobs with no shared interface, inconsistent method names (`ExecuteAsync` vs `RunAsync`), no common logging. Create `IRecurringJob` interface, rename entry points to `ExecuteAsync`, add structured logging at start/end.
**Where:** `src/Humans.Infrastructure/Jobs/`

#### T6: Replace inline avatar HTML with ViewComponent
**Status:** Partially done (some replaced in earlier runs)
**What:** `UserAvatar` ViewComponent exists but some views use inline `<img>` tags. Find and replace remaining instances.
**Where:** `src/Humans.Web/Views/`

#### T7: Consolidate badge rendering
**Status:** Not started
**What:** Badge rendering scattered across `_RoleBadge.cshtml`, `_VolunteerProfileBadges.cshtml`, and inline HTML. Consolidate where feasible.
**Where:** `src/Humans.Web/Views/Shared/`

#### T8: Standardize email renderer
**Status:** Partially done (some consolidation in earlier runs)
**What:** `EmailRenderer` has repetitive render methods all following the same pattern. Extract a generic `RenderEmailAsync<TModel>` if a clear template pattern exists.
**Where:** `src/Humans.Infrastructure/Services/EmailRenderer.cs`

### LOWER PRIORITY

#### T9: Move shift profile methods from ProfileService
**Status:** Not started (identified in super tier handoff)
**What:** `ProfileService.cs` lines 786-827 has `GetOrCreateShiftProfileAsync`, `UpdateShiftProfileAsync`, `GetShiftProfileAsync` — these operate on `VolunteerEventProfile`, not `Profile`. They belong in `IShiftManagementService` or `IShiftSignupService`.
**Where:** `src/Humans.Infrastructure/Services/ProfileService.cs`

#### T10: Move shift-count query from TeamService
**Status:** Not started (identified in super tier handoff)
**What:** `TeamService.cs` `GetAdminTeamListAsync` queries `EventSettings`, `Rotas`, `Shifts`, `ShiftSignups` for pending shift counts — this is shift domain logic in a team service.
**Where:** `src/Humans.Infrastructure/Services/TeamService.cs`

#### T11: Remove delegating role-check wrappers from ITeamService
**Status:** Not started (identified in super tier handoff)
**What:** `IsUserAdminAsync`, `IsUserBoardMemberAsync`, `IsUserTeamsAdminAsync` still on `ITeamService` as wrappers after the role-check move to `IRoleAssignmentService`. Long-term: remove from ITeamService and update callers/tests.
**Where:** `src/Humans.Application/Interfaces/ITeamService.cs`

---

## SAFETY CHECKS

Before every change, verify:

1. **Am I touching an EF entity, migration, or DbContext configuration?** → STOP.
2. **Am I renaming a serialized property?** → STOP.
3. **Am I removing something that looks unused?** → STOP, likely used via reflection.
4. **Am I changing an interface in `Application/`?** → Check all implementations AND callers.
5. **Am I changing authorization?** → Verify exact match.
6. **Does build pass?** → Must pass after every change.
7. **Do tests pass?** → Must pass after every change.
