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
        ContactFieldVisibility.BoardOnly => [ContactFieldVisibility.BoardOnly, ContactFieldVisibility.LeadsAndBoard, ContactFieldVisibility.MyTeams, ContactFieldVisibility.AllActiveProfiles],
        ContactFieldVisibility.LeadsAndBoard => [ContactFieldVisibility.LeadsAndBoard, ContactFieldVisibility.MyTeams, ContactFieldVisibility.AllActiveProfiles],
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
