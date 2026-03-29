# Coding Rules

## Critical: Do Not Remove "Unused" Properties

Properties/methods appearing "unused" may be used dynamically via reflection:
- Serialization/deserialization
- Change tracking
- Object cloning/merging
- Dynamic binding

**Rule:** Do not remove properties/methods that appear unused without verifying they're not used via reflection.

## Critical: Never Rename Fields in Serialized Objects

Classes that are JSON serialized (to databases, APIs, files) will break if properties are renamed. Existing JSON expects the current property names.

**Rule:** Never rename properties on serialized classes. Existing data expects the current property names.

**Example:**
```csharp
// WRONG - breaks existing data
public class User {
    public string UserName { get; set; }  // Renamed from "Name"
}

// CORRECT - keeps existing property name
public class User {
    public string Name { get; set; }  // Matches JSON in storage
}
```

**Exceptions:**
- Adding `[JsonIgnore]` computed properties is safe (they're not serialized)
- Adding new properties is safe (old records will use default values)

## JSON Serialization

Uses System.Text.Json.

**Required attributes:**
- Private setters: `[JsonInclude]`
- New data classes: `[JsonConstructor]` (private parameterless)
- Polymorphic types: `[JsonPolymorphic]` + `[JsonDerivedType]` on base class

**Example:**
```csharp
public class MyData {
    [JsonInclude]
    public string PrivateProp { get; private set; }

    [JsonConstructor]
    private MyData() { }
}
```

## Timezone Handling

**Prefer NodaTime for internal time handling:**
- Use NodaTime types (`Instant`, `LocalDate`, `ZonedDateTime`) instead of `DateTime`/`DateOnly`/`TimeOnly`

**Server-side ALWAYS uses UTC:**
- Use NodaTime `Instant` or `SystemClock.Instance.GetCurrentInstant()` for current time
- Store all dates/times in UTC (database, JSON, APIs)
- Never store or transmit local timezones from server
- All server-side calculations and comparisons in UTC

**Client-side translates to local time at display:**
- Convert UTC to user's local timezone only at final display step
- Never send local times back to server - convert to UTC first

**Web/view-model exception:**
- `DateTime` is allowed in web-layer view models and Razor views **only after** a NodaTime value has been explicitly converted for display (for example via `.ToDateTimeUtc()`)
- Do not introduce `DateTime` into domain logic, persistence models, APIs, or service boundaries when NodaTime can be used instead

**Rationale:** NodaTime provides safer time handling. Prevents timezone bugs, ensures consistent server behavior across deployments, simplifies testing.

## String Comparisons

Always use explicit `StringComparison` parameter.

**Rule:** Use `StringComparison.Ordinal` for exact matches, `StringComparison.OrdinalIgnoreCase` for case-insensitive.

**Example:**
```csharp
// WRONG
if (status == "submitted")

// CORRECT
if (string.Equals(status, "submitted", StringComparison.Ordinal))
```

**Search/input convention:**
- For user-entered search terms, prefer the shared helpers in `Humans.Web.Extensions` (`HasSearchTerm`, `ContainsOrdinalIgnoreCase`, `WhereAnyContainsInsensitive`) instead of open-coding whitespace/length guards or ad hoc case handling
- Keep EF-query helpers and in-memory string helpers separate so translation behavior stays explicit

## Critical: No Enum Comparison Operators in EF Core Queries

Enums stored with `HasConversion<string>()` are persisted as their string names in the database. Comparison operators (`>`, `>=`, `<`, `<=`) translate to **lexicographic string comparison** in SQL, which does NOT match the numeric enum ordering. For example, `'AllActiveProfiles' >= 'BoardOnly'` is FALSE in SQL (because `'A' < 'B'`), even though the enum value 3 >= 0.

**Rule:** Never use `>`, `>=`, `<`, `<=` on enum properties in EF Core LINQ queries. Use explicit `Contains()` checks with a list of allowed values instead.

**Example:**
```csharp
// WRONG — string comparison breaks enum ordering
.Where(e => e.Visibility >= accessLevel)

// CORRECT — explicit list of allowed values
var allowed = GetAllowedVisibilities(accessLevel);
.Where(e => allowed.Contains(e.Visibility.Value))

private static List<ContactFieldVisibility> GetAllowedVisibilities(ContactFieldVisibility accessLevel) =>
    accessLevel switch
    {
        ContactFieldVisibility.BoardOnly => [ContactFieldVisibility.BoardOnly, ContactFieldVisibility.CoordinatorsAndBoard, ContactFieldVisibility.MyTeams, ContactFieldVisibility.AllActiveProfiles],
        ContactFieldVisibility.CoordinatorsAndBoard => [ContactFieldVisibility.CoordinatorsAndBoard, ContactFieldVisibility.MyTeams, ContactFieldVisibility.AllActiveProfiles],
        ContactFieldVisibility.MyTeams => [ContactFieldVisibility.MyTeams, ContactFieldVisibility.AllActiveProfiles],
        ContactFieldVisibility.AllActiveProfiles => [ContactFieldVisibility.AllActiveProfiles],
        _ => [ContactFieldVisibility.AllActiveProfiles]
    };
```

**This applies to any enum with `HasConversion<string>()`**, not just `ContactFieldVisibility`. The `ContactFieldService` and `UserEmailService` both use the `GetAllowedVisibilities` helper pattern.

## Avoid Magic Strings

Use `nameof()`, constants, or enum references instead of string literals that refer to code identifiers. Magic strings are fragile — they silently break on rename and can't be caught by the compiler.

**Rule:** When a string literal refers to a code element (property, method, class, role, entity type), replace it with a compile-time reference.

**Examples:**
```csharp
// WRONG — breaks silently if method is renamed
return RedirectToAction("HumanDetail");

// CORRECT
return RedirectToAction(nameof(HumanDetail));

// WRONG — typo creates inconsistent audit data
await _auditLog.LogAsync("Teem", ...);

// CORRECT — constants catch typos at compile time
await _auditLog.LogAsync(AuditLogEntityTypes.Team, ...);
```

**Applies to:** `RedirectToAction`/`RedirectToPage` targets, `TempData`/`ViewData` keys, `IsInRole()` role names, audit log entity types, claim types, and any other string that mirrors a code identifier.

**Exceptions:** Localization resource keys, HTML/CSS class names, and configuration keys that don't map to code identifiers.

**Auth-specific rule:**
- Never hardcode role names in controllers, views, or authorization helpers
- Use `RoleNames`, `RoleGroups`, or shared `RoleChecks` helpers

## Controller Base Conventions

Controllers that resolve the current human or set TempData messages must use the shared base classes instead of duplicating those patterns.

**Rule:**
- Inherit from `HumansControllerBase` or the appropriate specialized base when authenticated-user resolution or shared controller helpers are needed
- Use shared helpers such as `GetCurrentUserAsync`, `ResolveCurrentUserAsync`, `FindUserByIdAsync`, `SetSuccess`, `SetError`, and `SetInfo`
- Do not write new direct `_userManager.GetUserAsync(User)` calls in controllers when a base helper already covers the case
- Do not write direct `TempData["SuccessMessage"]`, `TempData["ErrorMessage"]`, or `TempData["InfoMessage"]` assignments in controllers

**Rationale:** This keeps PRG messaging, not-found handling, and user lookup behavior consistent across controllers.

## Authorization Conventions

Prefer centralized authorization declarations and shared role-check helpers over hand-written combinations.

**Rule:**
- Use `[Authorize(Roles = ...)]` with `RoleGroups`/`RoleNames` for static route guards
- Use shared `RoleChecks` / `ShiftRoleChecks` helpers for runtime combinations that cannot be expressed cleanly as an attribute
- Avoid repeating the same multi-role checks inline across multiple files

**Examples:**
- Use `RoleGroups.BoardOrAdmin`, not `"Board,Admin"`
- Use `ShiftRoleChecks.CanAccessDashboard(User)`, not repeated `IsInRole` chains

### Claims-First Rule for Controller Authorization

Controllers must use `User.IsInRole()` or `RoleChecks`/`ShiftRoleChecks` helpers (which check Identity claims) for HTTP request authorization — **never** query `RoleAssignments` or call service methods that query the database for global role checks.

`RoleAssignmentClaimsTransformation` already converts active `RoleAssignment` records into claims on every request (cached 60s). Querying the DB again is redundant and can disagree with the claims the auth pipeline is using.

**Two kinds of role checks:**
- **Global roles** (Admin, Board, TeamsAdmin, etc.) → always use claims via `User.IsInRole()` or `RoleChecks.*`
- **Team-specific roles** (coordinator of a specific team) → require DB query via service, and that's OK

**Pattern:** Check claims first for global roles, fall back to a DB query only for team-specific checks:

```csharp
// CORRECT — claims-first, DB only for team-specific coordinator check
private async Task<bool> CanManageAsync(Guid userId, Guid teamId)
{
    return RoleChecks.IsAdmin(User) ||
           User.IsInRole(RoleNames.VolunteerCoordinator) ||
           await _shiftMgmt.IsDeptCoordinatorAsync(userId, teamId);
}

// WRONG — queries DB for roles already available as claims
private async Task<bool> CanManageAsync(Guid userId, Guid teamId)
{
    return await _shiftMgmt.CanManageShiftsAsync(userId, teamId);
}
```

**Service-level role checks** (e.g., `TeamService.IsUserAdminAsync`) are acceptable for business logic decisions (what data to show, what options to offer), but must not be the sole authorization gate for controller actions.

## Markdown Rendering

Markdown rendering in Razor must go through the shared sanitized rendering path.

**Rule:**
- Do not embed local `HtmlSanitizer`, `Markdig.Markdown.ToHtml`, or `ConvertMarkdownToHtml(...)` helpers in views
- Use `@Html.SanitizedMarkdown(...)` for one-off markdown rendering
- If multiple pages share tabbed markdown document UI, extract or reuse a shared partial/component rather than duplicating tabs + sanitizer logic

**Rationale:** This prevents inconsistent sanitization and removes duplicated Markdown boilerplate from views.

## Date/Time Display Formatting

Display formatting should be standardized through shared extensions instead of scattered inline format strings.

**Rule:**
- Prefer shared extensions such as `ToDisplayDate`, `ToDisplayLongDate`, `ToDisplayDateTime`, `ToDisplayCompactDate`, `ToDisplayCompactDateTime`, `ToDisplayTime`, `ToAuditTimestamp`, and `ToDisplayGeneralDateTime`
- Avoid introducing new inline Razor format strings like `ToString("d MMM yyyy")` unless the format is genuinely one-off and not part of an established display convention

**Rationale:** This keeps view formatting consistent and makes date/time policy easy to evolve.

For CLR date formatting used outside display-only views (for example, outbound email payload strings), use shared helper extensions in the layer owning the content. 
Email templates should use `Humans.Infrastructure.Helpers.EmailDateTimeExtensions` rather than repeating long-date literals.

## Time Parsing Standardization

Use shared parser helpers for converting time input strings into `TimeOnly`/`LocalTime`.

**Rule:**
- Use `TryParseInvariantTimeOnly` and `TryParseInvariantLocalTime` from `Humans.Web.Extensions.TimeParsingExtensions` for shift/admin time parsing.
- Keep parsing locale-stable (`CultureInfo.InvariantCulture`) to avoid culture-dependent acceptance differences.

**Rationale:** This removes repeated parsing logic and avoids subtle parse differences when the server default culture changes.

## Search Endpoint Response Shape

Autocomplete/search JSON endpoints must use stable typed response models.

**Rule:**
- Return typed DTOs/records for JSON search results instead of anonymous objects
- Reuse shared mapping helpers when converting service-layer search results into web response shapes
- Keep property names stable once JavaScript consumers depend on them

**Rationale:** Anonymous JSON payloads drift easily and make it harder to reuse search behavior safely.

## Culture and Language Display

Culture support and display names must be centralized.

**Rule:**
- Use `CultureCatalog` and `CultureCodeExtensions` for supported culture lists, ordering, default document language selection, and display labels
- Do not create per-view language dictionaries or ad hoc language ordering logic when the shared helpers already cover the case

**Rationale:** This prevents inconsistent language labels and tab ordering across views.

## CSV and Pagination Helpers

Small repeated mechanics should use the shared helpers once they exist.

**Rule:**
- Use `AppendCsvRow` / `ToCsvField` for CSV generation instead of inline escaping/string interpolation
- Use `ClampPageSize()` for repeated page-size clamping instead of scattering `Math.Clamp(pageSize, ...)`

**Rationale:** These helpers reduce noise and prevent small formatting/validation differences between endpoints.

## Icons: Font Awesome 6 Only

This project uses **Font Awesome 6** (loaded via CDN in `_Layout.cshtml`). Bootstrap Icons are **not** loaded and will render as invisible/missing.

**Rule:** Always use `fa-solid fa-*` (or `fa-regular fa-*`, `fa-brands fa-*`) classes for icons. Never use `bi bi-*` (Bootstrap Icons).

```html
<!-- WRONG — Bootstrap Icons not loaded, will be invisible -->
<i class="bi bi-gear"></i>

<!-- CORRECT — Font Awesome 6 -->
<i class="fa-solid fa-gear"></i>
```

## Critical: Never Edit EF Core Migration Files

Migration files (`Migrations/*.cs`) are **generated** by `dotnet ef migrations add`. Two categories:

**Schema migrations (EF generated content in Up/Down):** Never edit. No exceptions. Don't add SQL, don't tweak columns, don't insert data fixups. Commit exactly what EF generates.

**Data-only migrations (EF generates empty Up/Down):** Adding `migrationBuilder.Sql()` is the proper EF API for data migrations when there are no schema changes. However, this requires **explicit user permission** every time — it has been done once in 500+ commits. Never do this autonomously.

**What goes wrong with unauthorized edits:** Hand-edited schema migrations break Designer/snapshot consistency, cause integration test failures, and create migrations that can't be cleanly removed or regenerated.

**Rules:**
1. Schema migrations: commit exactly what EF generates, never touch
2. Data-only migrations: only with explicit user permission, never autonomously
3. Never edit the `Up`/`Down` methods of an existing committed migration
4. Never rename migration files (the timestamp IS the migration ID)

**If a migration fails because objects already exist**, the database is out of sync with migration history. Fix the root cause (usually a missing `__EFMigrationsHistory` entry or a previously deleted migration), don't patch the migration.

## View Components vs Partial Views

ASP.NET Core offers two reusable view mechanisms. Use the right one:

**Use a View Component** (`ViewComponents/FooViewComponent.cs` + `Views/Shared/Components/Foo/Default.cshtml`) when:
- The component **fetches its own data** via injected services — the parent controller shouldn't need to know about the component's data needs
- The component has **interactive behavior** with its own JavaScript (autocomplete, search, Chart.js)
- The component is used across **multiple unrelated pages** that would each need to duplicate data-loading logic

**Use a Partial View** (`Views/Shared/_Foo.cshtml`) when:
- The component is **pure presentation** — it renders a model the parent already has in hand
- No service injection or data fetching needed
- Examples: badge rendering, status labels, simple card layouts

**The rule:** If a parent controller has to fetch data *specifically* to pass to a partial, that partial should be a View Component.

**Additional reuse rule:**
- If two or more pages share the same markup structure with only minor model/context differences, prefer a shared partial or shared page over duplicating the Razor body
- Thin wrapper views that only exist to forward to the same page shape should usually be collapsed into a shared view

**Interactive search rule:**
- If two endpoints feed the same client-side interaction pattern, prefer a shared builder/helper for the response assembly instead of duplicating result-shaping logic in each controller

**Existing View Components:** `ProfileCardViewComponent`, `NavBadgesViewComponent`, `UserAvatarViewComponent`, `TempDataAlertsViewComponent`.

**Example — wrong:**
```csharp
// Controller fetches shift data just to pass through to a partial
var shifts = await _shiftService.GetUpcomingForUser(userId);
var urgent = await _urgencyService.GetTopUrgent(5);
ViewData["ShiftCards"] = new ShiftCardsViewModel { NextShifts = shifts, UrgentShifts = urgent };
// Then in the view: @await Html.PartialAsync("_ShiftCards", ViewData["ShiftCards"])
```

**Example — right:**
```csharp
// View Component fetches its own data — controller doesn't know about shifts
// In the view: @await Component.InvokeAsync("ShiftCards")
public class ShiftCardsViewComponent : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync()
    {
        var userId = /* resolve from UserClaimsPrincipal */;
        var shifts = await _shiftService.GetUpcomingForUser(userId);
        return View(new ShiftCardsViewModel { ... });
    }
}
```

## Localization (i18n)

**Admin pages do not require localization.** Existing localized strings in admin views can stay, but do not add new `@Localizer[...]` calls or resource keys for admin-side views (`/Admin/*`, `/TeamAdmin/*`) until further notice. Only public/user-facing views require localization.

## Namespace Alias

Due to namespace collision, use `MemberApplication` alias when referencing `Humans.Domain.Entities.Application`:

```csharp
using MemberApplication = Humans.Domain.Entities.Application;
```

## LSP Integration

The `csharp-ls` LSP is active via the `csharp-lsp` Claude Code plugin. It provides real-time C# compiler diagnostics (type errors, missing usings, nullable warnings, etc.) on `.cs` files when they are read.

**After editing any `.cs` file, re-read it before moving on.** Diagnostics appear on `Read`, not on `Edit`. This catches errors immediately without waiting for a full `dotnet build`. Always fix LSP-reported errors in the current file before editing the next one.

## Debugging: Check the Log File

When debugging runtime errors, **always check the log file first** before speculating about causes. Serilog console sink is always enabled via `WriteTo.Console()`.

Use `Grep` on log output filtering by entity ID, error keywords, or timestamp. Write diagnostic log messages (`_logger.LogWarning`/`LogError`) that include entity IDs, actual values, and expected values — not just "operation failed". When something goes wrong, the log should tell you *why*.
