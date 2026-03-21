# Bug Hunt — Codex Autonomous Prompt

## Mission

You are scanning a ~70k-line ASP.NET Core 10 Clean Architecture codebase (membership management for a Spanish nonprofit) for bugs, missing functionality, and quality gaps. Your job is to **find and fix real bugs** — not refactor, not improve style, not add features.

**Work autonomously.** Find a bug, fix it, verify the build passes, commit, move on. Keep going until you run out of bugs or hit the session limit.

**What counts as a bug:**
- Code that will crash at runtime (null reference, missing null check, wrong cast)
- Forms/pages with no inbound link (orphan pages — users can't reach them)
- Exception handlers that swallow errors silently (catch blocks with no logging)
- Controller actions that return views but have no route or nav link
- Missing try-catch on external service calls (Google API, email, etc.)
- Off-by-one errors, wrong enum comparisons, logic inversions
- Missing authorization checks on admin/sensitive actions
- Broken links or references to renamed/removed actions

**What does NOT count:**
- Style issues, naming conventions, code smell
- Performance concerns
- Missing features or enhancements
- Test coverage gaps (that's a different phase)

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
- `src/Humans.Web/Controllers/` — all controllers
- `src/Humans.Web/Views/` — all Razor views

## EXCLUSION ZONES — DO NOT TOUCH

```
src/Humans.Infrastructure/Data/HumansDbContext.cs
src/Humans.Infrastructure/Data/EntityConfigurations/**
src/Humans.Infrastructure/Migrations/**
```

Also do not modify:
- **Entity classes** in `src/Humans.Domain/Entities/` — properties that appear unused are accessed via reflection
- **Any `[JsonPropertyName]`, `[JsonInclude]`, `[JsonConstructor]`, `[JsonPolymorphic]`, or `[JsonDerivedType]` attributes**
- **Migration files**
- **ConsentRecord** — append-only table with database triggers preventing UPDATE/DELETE
- **Test files** in `tests/` — don't modify existing tests

## CODING RULES (must follow during all fixes)

1. **NodaTime for all dates/times** — Use `Instant`, `LocalDate`, `ZonedDateTime`. Never `DateTime`, `DateTimeOffset`, or `DateOnly`.
2. **Explicit StringComparison** — Every `.Equals()`, `.Contains()`, `.StartsWith()`, `.EndsWith()`, `.IndexOf()`, `.Replace()` on strings must specify `StringComparison.Ordinal` or `StringComparison.OrdinalIgnoreCase`.
3. **Font Awesome 6 only** — `fa-solid fa-*` syntax. Bootstrap Icons (`bi bi-*`) are NOT loaded.
4. **UI terminology** — User-facing text says "humans" not "members"/"volunteers"/"users". Use "birthday" not "date of birth".
5. **No concurrency tokens** — Do not add `IsConcurrencyToken()`, `[ConcurrencyCheck]`, or row versioning anywhere.
6. **Every new page needs a nav link** — If you find a controller action returning a view with no link to it, ADD the link.
7. **Admin pages don't need localization** — Views under `/Admin/*` and `/TeamAdmin/*` don't need `@Localizer[]` calls.

## Build Command

```
dotnet build Humans.slnx && dotnet test Humans.slnx --filter "FullyQualifiedName~Application"
```

## Phase 1: Orphan Pages and Dead Ends

Scan all controllers for actions that return views. For each one, verify there is at least one link to it from another view, nav menu, or partial. Fix by adding appropriate navigation links.

**How to find them:**
1. List all controller actions that return `View()`, `View(model)`, or `PartialView()`
2. For each action, search views for links: `asp-action="ActionName"`, `asp-controller="ControllerName"`, `Url.Action("ActionName"`, `Html.ActionLink`
3. If no inbound link exists, it's an orphan — add a contextual link from the most logical related page

**Common locations for links:**
- Nav menu: `Views/Shared/_Layout.cshtml` or `Views/Shared/_NavMenu.cshtml`
- Admin sidebar: `Views/Shared/_AdminNav.cshtml` or similar
- Related detail pages: e.g., a team settings page should be linked from the team detail page

## Phase 2: Silent Exception Swallowing

Find catch blocks that don't log the exception. Every catch block should either:
- Log with `_logger.LogError(ex, "context message")` or `_logger.LogWarning(ex, ...)`
- Re-throw (acceptable for catch-and-rethrow patterns)
- Have a clear comment explaining why the exception is intentionally ignored

**Where to look:**
- Controllers (especially ones handling form submissions)
- Infrastructure services (Google API calls, email sending, Hangfire jobs)
- Background jobs in `src/Humans.Infrastructure/Jobs/`

**How to fix:**
- Add `ILogger<ClassName>` to constructor if not present
- Add `_logger.LogError(ex, "Failed to {action} for {context}")` with structured logging
- Use descriptive messages that include enough context to debug

## Phase 3: Missing Authorization

Scan controller actions for missing or inconsistent authorization:

1. **Admin actions without `[Authorize]`** — any action in `AdminController`, `TeamAdminController`, `ShiftAdminController`, `CampAdminController` must have role-based authorization
2. **POST actions without `[ValidateAntiForgeryToken]`** — all POST actions that modify data should have anti-forgery validation
3. **Manual role checks that should be attributes** — `if (!User.IsInRole("Admin"))` in method bodies where `[Authorize(Roles = "Admin")]` would suffice

## Phase 4: Null Reference Risks

Scan for common null reference patterns:

1. **Unchecked `FirstOrDefault()` / `SingleOrDefault()` results** — if the result is used without null check, add one
2. **Navigation property access without null check** — `entity.RelatedEntity.Property` where `RelatedEntity` might be null
3. **`GetUserAsync(User)` results used without null check** — this can return null if the session expired
4. **ViewBag/ViewData used in views without null check** — especially for optional data

## Phase 5: Logic Errors

Look for:
1. **Enum comparisons using `>`, `<`, `>=`, `<=` in LINQ-to-SQL** — enums are stored as strings, comparison operators produce wrong results. Should use `.Contains()` with explicit value lists
2. **String comparisons without `StringComparison`** — bare `==` or `.Equals()` without specifying ordinal/case sensitivity
3. **Wrong redirect targets** — `RedirectToAction` pointing to non-existent actions or wrong controllers
4. **Incorrect date handling** — mixing `DateTime` with NodaTime, wrong timezone conversions

## SAFETY CHECKS

Before every fix, verify:

1. **Am I touching an EF entity, migration, or DbContext configuration?** → STOP, skip this fix.
2. **Am I removing a property that looks unused?** → STOP, it's likely used via reflection.
3. **Am I changing authorization level?** → Verify the new level matches the original intent exactly.
4. **Does `dotnet build Humans.slnx` still pass?** → Must pass after every fix.
5. **Does `dotnet test Humans.slnx --filter "FullyQualifiedName~Application"` still pass?** → Must pass.
6. **Is my fix minimal?** → Fix only the bug. Don't refactor surrounding code.
