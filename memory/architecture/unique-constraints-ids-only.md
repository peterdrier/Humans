---
name: Unique constraints on IDs only — never Name, never slug
description: HARD RULE (Peter, 2026-08-19, nobodies-collective/Humans#1027). Row identity and uniqueness rest on Id columns only, 100% of the time. Never a unique index on a name, slug, or any user-visible/editable string. Seed and system code reference rows by Id, never `WHERE Name = ...`.
---

Row identity and uniqueness are **Id columns only, 100% of the time**. Never put a unique constraint on a name, a slug, or any other user-visible or editable string.

**Why:** A name is display data, not a key. Unique-on-name turns renames into DB constraint violations, forces seed/maintenance code into `WHERE "Name" = 'Lead'` matching, and breeds case-sensitivity flip-flops (the `team_role_definitions` history, nobodies-collective/Humans#1027). A slug is just a second name — same failure, one step removed.

**How to apply:**

- New tables: uniqueness = PK Id (plus Id-composed unique indexes like `(TeamId, RoleId)` where both sides are Ids).
- Never propose a unique index on `(FkId, Name)` or a slug column as an identity fix — the fix is dropping the name-based index, not replacing the string.
- Duplicate display names are at most a domain-level nicety (soft check in the service), never a DB constraint.
- Seed/system code that must find a well-known row references its Id.

**Related:** [`db-enforcement-minimal`](db-enforcement-minimal.md), [`slug-routes-fallback-to-guid`](slug-routes-fallback-to-guid.md) (slugs are fine as *routes*, just never as identity).
