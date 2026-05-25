---
name: Slug-keyed routes fall back to GUID
description: Any URL keyed by an entity slug (`/Section/Admin/Foo/{slug}`) must accept a GUID in the same position and look up by Id when the slug doesn't match. Empty/missing slugs are a normal state; the GUID path is the always-available identity. New routes use this pattern; pre-existing routes are migrated opportunistically when touched.
---

URLs keyed by a sluggable field (`{slug}` route parameter) must accept either the slug **or** the entity GUID in the same slot. Resolution order:

1. Try the literal value as a slug — `GetBySlugAsync(value, ct)`.
2. If no match, try `Guid.TryParse(value, out var id)` and `GetByIdAsync(id, ct)`.
3. If both fail, 404.

**Why:** slug columns are user-controlled (admin-edited), nullable-in-practice (`""` is the "not yet assigned" state — never backfilled), and rename-mutable. The GUID is the durable identity. Routes that hard-require a non-empty slug shut admins out of their own entities the moment a slug is blank, and break links across renames. The GUID fallback is also the link form everything else (audit log, dashboards, dev tools) already has at hand, so this is the form that "just works" when you paste an ID into the URL bar.

**How to apply:**

- New slug-keyed routes: build with both lookups from day one. The controller action signature stays `{slug}` (single parameter); the parsing fork lives inside the action or in a private helper.
- Existing slug-only routes: don't churn them in unrelated PRs. Migrate opportunistically when you touch a section. There's no system-wide sweep planned.
- Services: expose both `GetBy{Slug}Async` and `GetByIdAsync`. Don't add a `GetBySlugOrIdAsync` combo — the parse is a one-liner and belongs in the caller.
- Tests: new actions get a "GUID-instead-of-slug" path test alongside the slug happy-path.

**Out of scope of this rule:**

- Public-facing canonical URLs (e.g., shareable camp pages) where the slug is the SEO-stable identifier. Those routes can stay slug-only — the rule targets admin and internal navigation.
- Routes where the slug is part of a composite key the GUID can't reconstruct (e.g., `barrios-{year}-{slug}@domain` Google Group keys). Those are derived, not routed.

**Origin:** PR 631 (camps role drill-down), 2026-05-17 — empty `CampRoleDefinition.Slug` would have made `/Camps/Admin/Roles/{slug}` unreachable for any role not yet assigned a slug. Peter, 2026-05-17: *"drilldown can fallback to by guid if the slug is empty.. actually everything using slug in a url should fallback system wide."*
