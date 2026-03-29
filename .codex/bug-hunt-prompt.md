# Bug Hunt — Codex Autonomous Prompt

## Mission

You are scanning a ~70k-line ASP.NET Core 10 Clean Architecture codebase (membership management for a Spanish nonprofit) for bugs. Your job is to **find and fix real bugs** — not refactor, not improve style, not add features.

**Work autonomously.** Find a bug, fix it, verify the build passes, commit, push the branch to `origin`, move on. Keep going until you run out of bugs or hit the session limit.

If this session is resumed later, continue from the existing branch and prior progress instead of restarting completed investigation.

**What counts as a bug:**
- Code that will crash or behave incorrectly at runtime
- HTML that renders wrong due to incorrect attribute handling
- Queries that silently return incomplete data
- Stale cache served after mutations
- Forms that lose user input on validation failure
- Missing authorization on sensitive actions
- Exception handlers that swallow errors silently

**What does NOT count:**
- Style issues, naming conventions, code smell
- Performance concerns
- Missing features or enhancements
- Test coverage gaps

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

## HARD LIMITS — NEVER DO THESE

- **Never delete files.** Not even if you think they're unused. Dead code removal is not your job.
- **Never remove methods from interfaces or classes.** Even if they appear uncalled — they may be used via reflection, DI, or from views.
- **Never remove or rename public properties on entities.** Reflection and JSON deserialization depend on them.
- **Never remove controller actions.** Even if you can't find a link — the link may be generated dynamically or exist in JS.
- **Never modify `.csproj` files, `Program.cs`, or DI registration.**

If you're unsure whether something is "unused" — it isn't. Skip it.

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

## Phase 1: Razor Boolean Attributes *(highest frequency — 8+ historical fixes)*

HTML boolean attributes (`disabled`, `readonly`, `checked`, `selected`, `required`, `hidden`, `multiple`, `open`) are active when **present**, regardless of value. `disabled="False"` still disables the element.

**How to find them:**
1. Search all `.cshtml` files for patterns like `disabled="@...`, `readonly="@...`, `checked="@...`, `selected="@...`
2. Any boolean attribute set to a C# expression that can evaluate to `false`/`true` is a bug — the attribute will always be present in the DOM
3. Also check for `asp-for` tag helpers on checkboxes — verify the hidden field pattern is correct

**How to fix:**
- Use conditional rendering: `@if (condition) { @: disabled }` or `@(condition ? "disabled" : "")`
- For tag helpers, use the conditional attribute syntax: `disabled="@(condition ? "disabled" : null)"`  — null removes the attribute entirely
- Verify the fix renders correctly: attribute absent when false, present when true

## Phase 2: Missing .Include() on EF Core Queries *(6+ historical fixes)*

EF Core does NOT lazy-load. If a LINQ query materializes an entity and then accesses a navigation property that wasn't `.Include()`'d, it returns `null` — no exception, just silent missing data.

**How to find them:**
1. Search Infrastructure services and controllers for `.FirstOrDefaultAsync()`, `.SingleOrDefaultAsync()`, `.ToListAsync()`, `.FindAsync()`
2. For each query result, trace how it's used — does the code access `.RelatedEntity.Property` afterward?
3. Check if the query has `.Include(e => e.RelatedEntity)` for every navigation property accessed
4. Watch for `.ThenInclude()` needs: `entity.Parent.Children` requires `.Include(e => e.Parent).ThenInclude(p => p.Children)`

**Where to look:**
- `src/Humans.Infrastructure/Services/` — all service implementations
- `src/Humans.Web/Controllers/` — controllers that query directly via DbContext

**How to fix:**
- Add the missing `.Include()` to the query chain
- If the query uses `Select()` projection, `.Include()` is NOT needed — skip those

## Phase 3: Cache Invalidation *(5+ historical fixes)*

The app uses `IMemoryCache`. After any mutation (create/update/delete), affected cache entries must be evicted. Stale cache = users see old data.

**How to find them:**
1. Search for `_cache.Set(` or `_cache.GetOrCreate` to find all cache keys
2. For each cache key, identify what data it caches
3. Search for all methods that mutate that same data (create, update, delete operations)
4. Check whether those mutation methods call `_cache.Remove()` for the affected key

**How to fix:**
- Add `_cache.Remove("key")` in the same service method that performs the mutation
- The eviction must happen AFTER the database write succeeds
- If multiple cache keys could be affected, evict all of them

## Phase 4: Form Field Preservation *(4+ historical fixes)*

When a POST action fails validation and re-displays the form, all user input must be preserved. Common bug: dropdown/select lists populated via ViewBag in the GET action are not repopulated in the POST action path.

**How to find them:**
1. Search controllers for POST actions that have `return View(model)` (re-displaying form on error)
2. Check the corresponding GET action — does it set ViewBag/ViewData items (for dropdowns, lists, etc.)?
3. Does the POST action set those SAME ViewBag/ViewData items before `return View(model)`?
4. If not, the form will crash or render empty dropdowns when validation fails

**How to fix:**
- Extract shared ViewBag population into a private method (e.g., `PopulateViewData()`)
- Call it from both GET and POST actions before returning the view
- Verify the model is passed back to the view so text inputs retain their values

## Phase 5: EF Core Bool Sentinel Trap *(3+ historical fixes)*

EF Core uses `false` as the sentinel value for booleans. `HasDefaultValue(false)` in entity configuration means EF Core can't distinguish between "explicitly set to false" and "unset" — it treats both as the sentinel and sends the database default instead.

**How to find them:**
1. Search `src/Humans.Infrastructure/Data/EntityConfigurations/` for `HasDefaultValue(false)` — but do NOT modify these files (exclusion zone). Just note which properties are affected.
2. Search service code for where those boolean properties are set to `false` explicitly — these writes may be silently ignored by EF Core
3. Also check for `HasDefaultValue(0)` on ints and `HasDefaultValue("")` on strings — same trap

**How to fix:**
- Do NOT modify entity configurations (exclusion zone)
- Instead, fix the calling code: set the value on the tracked entity directly after creation, not relying on the default
- Or document the issue as a comment in the service code if no behavioral bug is currently manifesting

## Phase 6: Authorization Gaps *(8+ historical fixes, partially addressed)*

**How to find them:**
1. List all controllers in `src/Humans.Web/Controllers/`
2. For each POST/PUT/DELETE action, verify it has `[Authorize(Roles = "...")]` — bare `[Authorize]` only checks authentication, not authorization
3. For admin controllers (`AdminController`, `TeamAdminController`, `ShiftAdminController`, `CampAdminController`), verify EVERY action has role-based auth
4. Check all POST actions for `[ValidateAntiForgeryToken]`
5. Search for `User.IsInRole()` in method bodies — these should usually be `[Authorize(Roles)]` attributes instead

**How to fix:**
- Add `[Authorize(Roles = "Admin")]` (or appropriate role) to unprotected actions
- Add `[ValidateAntiForgeryToken]` to POST actions missing it
- Replace inline `User.IsInRole()` checks with attributes where possible

## Phase 7: Null Reference Risks

**How to find them:**
1. Search for `FirstOrDefault()` / `SingleOrDefault()` results used without null check
2. `GetUserAsync(User)` results used without null check — can return null if session expired
3. ViewBag/ViewData used in views without null check
4. Navigation property access on entities that might not have the related record

**How to fix:**
- Add null checks with appropriate handling (return NotFound, show error, use null-conditional `?.`)
- For view code, use `??` to provide defaults: `@(ViewBag.Title ?? "Default")`

## Phase 8: Logic Errors and String Handling

**How to find them:**
1. Search for bare string comparisons: `.Equals(` without `StringComparison`, `==` on strings where case matters
2. `.Contains(`, `.StartsWith(`, `.EndsWith(`, `.IndexOf(`, `.Replace(` without `StringComparison` parameter
3. `RedirectToAction` calls — verify the target action and controller actually exist
4. Enum comparisons using `>`, `<` in LINQ — enums are stored as strings in this DB, so comparison operators produce wrong results in SQL

**How to fix:**
- Add `StringComparison.OrdinalIgnoreCase` (for user-facing comparisons) or `StringComparison.Ordinal` (for identifiers)
- Fix wrong redirects to point to correct actions
- Replace enum range comparisons with explicit `.Contains()` lists

## Phase 9: Orphan Pages and Dead Ends *(5+ historical fixes)*

Scan all controllers for actions that return views. For each one, verify there is at least one link to it from another view, nav menu, or partial. Fix by adding appropriate navigation links.

**How to find them:**
1. List all controller actions that return `View()`, `View(model)`, or `PartialView()`
2. For each action, search views for links: `asp-action="ActionName"`, `asp-controller="ControllerName"`, `Url.Action("ActionName"`, `Html.ActionLink`
3. If no inbound link exists, it's an orphan — add a contextual link from the most logical related page

**Common locations for links:**
- Nav menu: `Views/Shared/_Layout.cshtml` or `Views/Shared/_NavMenu.cshtml`
- Admin sidebar: `Views/Shared/_AdminNav.cshtml` or similar
- Related detail pages: e.g., a team settings page should be linked from the team detail page

## Phase 10: Silent Exception Swallowing *(5+ historical fixes)*

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

## SAFETY CHECKS

Before every fix, verify:

1. **Am I touching an EF entity, migration, or DbContext configuration?** → STOP, skip this fix.
2. **Am I removing a property that looks unused?** → STOP, it's likely used via reflection.
3. **Am I removing a method, file, or controller action?** → STOP, that's not your job.
4. **Am I changing authorization level?** → Verify the new level matches the original intent exactly.
5. **Does `dotnet build Humans.slnx` still pass?** → Must pass after every fix.
6. **Does `dotnet test Humans.slnx --filter "FullyQualifiedName~Application"` still pass?** → Must pass.
7. **Is my fix minimal?** → Fix only the bug. Don't refactor surrounding code.
