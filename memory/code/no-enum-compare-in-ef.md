---
name: No enum comparison operators in EF Core queries
description: Enums stored with `HasConversion<string>()` translate to lexicographic SQL string comparison — `>=` doesn't match enum ordering. Use `Contains()` with explicit allowed-values lists.
---

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
    accessLevel switch {
        ContactFieldVisibility.BoardOnly => [ContactFieldVisibility.BoardOnly, ContactFieldVisibility.CoordinatorsAndBoard, ContactFieldVisibility.MyTeams, ContactFieldVisibility.AllActiveProfiles],
        ContactFieldVisibility.CoordinatorsAndBoard => [ContactFieldVisibility.CoordinatorsAndBoard, ContactFieldVisibility.MyTeams, ContactFieldVisibility.AllActiveProfiles],
        ContactFieldVisibility.MyTeams => [ContactFieldVisibility.MyTeams, ContactFieldVisibility.AllActiveProfiles],
        ContactFieldVisibility.AllActiveProfiles => [ContactFieldVisibility.AllActiveProfiles],
        _ => [ContactFieldVisibility.AllActiveProfiles]
    };
```

**This applies to any enum with `HasConversion<string>()`** — not just `ContactFieldVisibility`. The `ContactFieldService` and `UserEmailService` both use the `GetAllowedVisibilities` helper pattern.
