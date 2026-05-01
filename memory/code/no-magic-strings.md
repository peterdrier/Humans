---
name: Avoid magic strings — use nameof, constants, or enum references
description: When a string literal refers to a code identifier (property, method, role, entity type), replace it with a compile-time reference. Magic strings break silently on rename.
---

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

**Exceptions:** Localization resource keys, HTML/CSS class names, configuration keys that don't map to code identifiers.

**Auth-specific:**
- Never hardcode role names in controllers, views, or authorization helpers
- Use `RoleNames`, `RoleGroups`, or shared `RoleChecks` helpers
