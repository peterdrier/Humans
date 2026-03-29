# Tech Debt Reduction Loop — Codex Deep Autonomy Prompt

## Mission

You are working on a ~50k-line ASP.NET Core 10 Clean Architecture codebase (membership management for a Spanish nonprofit). It grew from 10k to 70k lines rapidly, and patterns have diverged — multiple ways of doing the same thing, duplicated logic, inconsistent conventions. Your job is to **consolidate, deduplicate, and standardize** so the codebase becomes more predictable and maintainable.

**Work autonomously.** Do not stop for milestone reports. Find the next opportunity, fix it, verify the build passes, commit, push the branch to `origin`, and move to the next one. Keep going until you run out of clear improvements or hit the session limit.

If this session is resumed later, continue from the existing branch and prior progress instead of restarting completed investigation.

---

## Architecture (4 layers)

```
src/Humans.Domain/          — Entities, enums, value objects (pure, no dependencies)
src/Humans.Application/     — Interfaces, DTOs, constants (business rules)
src/Humans.Infrastructure/  — EF Core, external services, Hangfire jobs
src/Humans.Web/             — Controllers, Razor views, authorization, tag helpers
tests/Humans.Application.Tests/ — Unit tests
```

Key entry points:
- `src/Humans.Web/Program.cs` — DI, middleware, startup
- `src/Humans.Web/Extensions/InfrastructureServiceCollectionExtensions.cs` — service registration
- `src/Humans.Infrastructure/Extensions/RecurringJobExtensions.cs` — Hangfire job setup

---

## EXCLUSION ZONES — DO NOT TOUCH

These files/directories are **completely off-limits**. Changes here risk data loss, broken migrations, or silent runtime failures:

```
src/Humans.Infrastructure/Data/HumansDbContext.cs
src/Humans.Infrastructure/Data/EntityConfigurations/**
src/Humans.Infrastructure/Migrations/**
```

Also do not modify:
- **Entity classes** in `src/Humans.Domain/Entities/` — do not rename properties, remove properties, change types, or restructure. Properties that appear unused are accessed via reflection (serialization, change tracking, cloning). Removing them silently breaks functionality.
- **Any `[JsonPropertyName]`, `[JsonInclude]`, `[JsonConstructor]`, `[JsonPolymorphic]`, or `[JsonDerivedType]` attributes** — these control serialization of persisted data.
- **Migration files** — never edit, never create manually.
- **ConsentRecord** — append-only table with database triggers preventing UPDATE/DELETE.
- **Test files** in `tests/` — don't modify existing tests. You may add new tests if useful but don't change existing ones.

---

## CODING RULES (must follow during all changes)

1. **NodaTime for all dates/times** — Use `Instant`, `LocalDate`, `ZonedDateTime`. Never `DateTime`, `DateTimeOffset`, or `DateOnly`.

2. **Explicit StringComparison** — Every `.Equals()`, `.Contains()`, `.StartsWith()`, `.EndsWith()`, `.IndexOf()`, `.Replace()` on strings must specify `StringComparison.Ordinal` or `StringComparison.OrdinalIgnoreCase`. Never use bare `==` for string comparison where culture matters.

3. **No enum comparison operators in LINQ-to-SQL** — Enums are stored as strings. `>`, `<`, `>=`, `<=` produce wrong results. Use `.Contains()` with explicit value lists.

4. **Magic string avoidance** — Use `nameof()` for action/controller names, `RoleNames.*` constants for role checks, constants or enums for repeated strings.

5. **Font Awesome 6 only** — `fa-solid fa-*` syntax. Bootstrap Icons (`bi bi-*`) are NOT loaded.

6. **UI terminology** — User-facing text says "humans" not "members"/"volunteers"/"users". The word "humans" stays in English across all locales. Use "birthday" not "date of birth".

7. **No concurrency tokens** — Do not add `IsConcurrencyToken()`, `[ConcurrencyCheck]`, or row versioning anywhere.

8. **Every new page needs a nav link** — If you create a controller action returning a view, add a navigation link to it.

9. **Admin pages don't need localization** — Views under `/Admin/*` and `/TeamAdmin/*` don't need `@Localizer[]` calls.

---

## Build Command

```
dotnet build Humans.slnx && dotnet test Humans.slnx --filter "FullyQualifiedName~Application"
```

---

## Phase 1: Controller Pattern Consolidation

### Extract Repeated Controller Patterns

**Problem:** 108 occurrences across 19 controllers of this exact pattern:
```csharp
var user = await _userManager.GetUserAsync(User);
if (user == null) return NotFound();
```

**Fix:** Create a `HumansControllerBase : Controller` base class with:
```csharp
protected async Task<User?> GetCurrentUserAsync()
    => await _userManager.GetUserAsync(User);

protected async Task<IActionResult?> RequireCurrentUserAsync([NotNullWhen(false)] out User? user)
{
    user = await _userManager.GetUserAsync(User);
    return user == null ? NotFound() : null;
}
```
Migrate controllers incrementally. The base class goes in `src/Humans.Web/Controllers/`. Register `UserManager<User>` in the base constructor.

**Similar pattern — "resolve team + validate access"** (TeamAdminController, ShiftAdminController, CampAdminController):
```csharp
var user = await _userManager.GetUserAsync(User);
if (user == null) return NotFound();
var team = await _teamService.GetTeamBySlugAsync(slug);
if (team == null) return NotFound();
var canManage = await _teamService.CanUserApproveRequestsForTeamAsync(team.Id, user.Id);
if (!canManage) return Forbid();
```
Extract to a shared helper method on the base class or a team-specific base class.

### Consolidate Error Handling Patterns

**Problem:** Three different patterns used across controllers:

1. Try-catch → `TempData["ErrorMessage"]`
2. Try-catch → `ModelState.AddModelError`
3. No error handling at all (just returns NotFound)

**Fix:** Standardize on the `TempData` pattern since it works across redirects (PRG pattern). Create helper methods:
```csharp
protected void SetSuccess(string message) => TempData["SuccessMessage"] = message;
protected void SetError(string message) => TempData["ErrorMessage"] = message;
```
These can go in `HumansControllerBase`. Then consolidate existing usages.

### Consolidate Authorization Patterns

**Problem:** Three authorization mechanisms used inconsistently:
1. `[Authorize(Roles = "Admin")]` attributes (best for static checks)
2. `User.IsInRole()` manual checks inside action methods
3. `await _teamService.IsUserBoardMemberAsync(user.Id)` service calls

**Fix:** Use `[Authorize(Roles = "...")]` for controller/action-level gates. Use policy-based authorization for complex checks (team membership, management rights). Reduce manual `IsInRole` checks in method bodies where an attribute would suffice.

---

## Phase 2: Service & Caching Consolidation

### Consolidate Caching Patterns

**Problem:** 16 files use `IMemoryCache` with three different patterns:
1. `GetOrCreateAsync` (best)
2. Direct `Set`/`Get`
3. `TryGetValue` + manual set

**Fix:** Standardize on `GetOrCreateAsync` everywhere. If a service uses `TryGetValue` + `Set` separately, refactor to `GetOrCreateAsync`. Cache key strings should be constants, not inline strings.

**Also:** Cache invalidation is scattered. Consider a `CacheKeys` static class with all cache key templates and a helper for invalidation, so it's harder to forget to invalidate.

### Break Up God Classes

**TeamService.cs (1,639 lines, 40+ methods)** handles:
- Team CRUD
- Team membership (join, leave, approve/reject requests)
- Team role definitions (create, update, delete)
- Team role assignments
- Caching
- Admin operations

**Fix:** Extract into focused services:
- `TeamRoleService` — role definitions + role assignments
- Keep `TeamService` for team CRUD + membership operations
- Move the interface split to `ITeamRoleService` in Application layer

**TeamAdminController.cs (888 lines)** handles:
- Member management
- Resource management (Google Drive/Groups)
- Role definitions
- Role assignments

**Fix:** Extract `TeamRoleAdminController` for role definition and role assignment endpoints.

### Move Business Logic Out of Controllers

**Problem:** Several controllers do LINQ transformations, ViewModel construction from raw entities, and business decisions that belong in services:
- `TeamController` lines 70-101: LINQ transforms to build team list
- `ProfileController`: inline mapping from entities to ViewModels
- `CampController`: complex query composition

**Fix:** For each controller, if it does more than "call service → map to ViewModel → return View", extract the logic to the appropriate service and return a DTO/ViewModel-ready result.

---

## Phase 3: View & Email Consolidation

### Standardize Email Rendering

**Problem:** `EmailRenderer` (401 lines) has repetitive render methods — `RenderApplicationSubmitted`, `RenderApplicationApproved`, `RenderApplicationRejected` etc. all follow the same structure: build model → pick template → render.

**Fix:** If there's a clear template pattern, extract a generic `RenderEmailAsync<TModel>(string templateName, TModel model)` method and reduce the boilerplate per email type.

### Consolidate View Patterns

**Problem:**
- List/table views repeat the same pattern (loop, render row, sort links) without a shared partial
- Badge rendering scattered across `_RoleBadge.cshtml`, `_VolunteerProfileBadges.cshtml`, inline HTML
- UserAvatar ViewComponent exists but some views use inline `<img>` tags instead

**Fix:**
- Find inline avatar `<img>` tags and replace with `@await Component.InvokeAsync("UserAvatar", ...)` calls
- Consolidate badge rendering where feasible

---

## Phase 4: Code Quality Sweep

### Standardize Background Job Patterns

**Problem:** 14 jobs with:
- No shared base class or interface
- Inconsistent method names (`ExecuteAsync` vs `RunAsync`)
- No common error handling or logging wrapper

**Fix:** Create a minimal interface or convention:
```csharp
public interface IRecurringJob
{
    Task ExecuteAsync(CancellationToken cancellationToken = default);
}
```
Rename all job entry points to `ExecuteAsync`. Add consistent structured logging at job start/end.

### General Code Quality

Look for and fix:
- Methods longer than 60 lines that can be decomposed
- Classes with more than 15 public methods that can be split
- Copy-pasted LINQ queries that appear in 2+ places
- Inconsistent naming (e.g., `GetXAsync` vs `FetchXAsync` vs `LoadXAsync`)
- `string` parameters that should be `enum` or constants
- Missing `ConfigureAwait(false)` consistency (pick one convention and apply it)
- Nullable reference type warnings that can be resolved

---

## Phase 5: Test Coverage

Write tests for any new services, helpers, or base classes extracted in earlier phases. Focus on:
- Unit tests for extracted base class methods (HumansControllerBase helpers)
- Unit tests for extracted services (TeamRoleService, etc.)
- Integration tests for cache invalidation patterns

Use the existing test project at `tests/Humans.Application.Tests/`.

---

## Phase 6: Documentation Review

Review and update feature docs in `docs/features/` that were affected by refactoring. Focus on:
- Any BRDs that reference moved or renamed services/controllers
- Authorization rule changes documented in feature specs
- Updated file paths in architectural references

---

## SAFETY CHECKS

Before every change, verify:

1. **Am I touching an EF entity, migration, or DbContext configuration?** → STOP, skip this change.
2. **Am I renaming a property on a class that might be serialized to JSON?** → STOP, skip this change.
3. **Am I removing a property that looks unused?** → STOP, it's likely used via reflection.
4. **Am I changing a method signature on an interface in `Application/`?** → Check all implementations AND all callers.
5. **Am I changing authorization?** → Verify the attribute/policy matches the original access level exactly.
6. **Does `dotnet build Humans.slnx` still pass?** → Must pass after every change.
7. **Does `dotnet test Humans.slnx` still pass?** → Must pass after every change.

---

### Replay Checklist for This Refactor Wave (for QA re-alignment)

- [ ] Controller ownership now routes through shared auth/user helpers (`GetCurrentUserAsync`, `ResolveCurrentUserOrUnauthorizedAsync`, `FindUserByIdAsync`) and avoids raw `UserManager` lookups in each action.
- [ ] Authorization checks in controllers use `RoleChecks`, `RoleGroups`, and `RoleNames` consistently instead of scattered string literals.
- [ ] TempData status messaging has a single convention: `SuccessMessage` + `ErrorMessage` via helper methods, and controllers stop mixing response-style error patterns.
- [ ] Team and campaign access paths use shared authorization helpers (team access/capability checks) instead of duplicated in-controller logic.
- [ ] View/partial reuse is preferred over repeated Razor markup, especially for shared badges/sections, tables, avatars, and admin wrapper forms.
- [ ] Team/shift/admin search and filtering UIs reuse shared scripts/markup and do not carry duplicated markup or JS.
- [ ] Email code paths consistently share body composition/rendering and transport wrappers instead of per-call boilerplate.
- [ ] Date/time formatting and parsing use shared helper methods with explicit culture/invariant handling; no duplicated application/date display logic remains.
- [ ] String comparison hardening is consistent (`StringComparison` on all sensitive string operations, explicit replacement/contains/equals variants).
- [ ] Role and claim marker constants are centralized (`auth claim marker constants`, `role groups`, `role names`) and reused by controllers and views.
- [ ] Caching and simple in-memory lookups follow a consistent helper pattern where practical, avoiding mixed ad-hoc `TryGetValue`/manual set shapes.
- [ ] HTML escaping, anti-forgery helpers, and html-to-text helpers are used from shared extension points and not duplicated inline.
- [ ] Team resource persistence/validation and access-rule checks are centralized and reused (including shared scripts/messages and helper methods).
- [ ] Shift-role checks and access rules for coordinator/admin flows are standardized across controller and view layers.
- [ ] Audit log display sections and scripts are consolidated to shared partials/components.
- [ ] Input validation and mapping helpers for complex forms (applications, tickets, resources, team settings) are shared instead of copy-pasted.
- [ ] Controllers that only add role-filtering/redirect behavior now inherit from `HumansControllerBase`.
- [ ] Admin/TeamAdmin surface is pruned of unused wrapper views; shared pages are preferred where behavior is unchanged.
- [ ] Route/access entrypoints are consistent (for example role actions through canonical controllers) and not duplicated.
- [ ] PR/rebase conflicts from other branches are treated by replaying these categories against newly changed files, not just old ones.
