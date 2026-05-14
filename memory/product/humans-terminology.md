---
name: UI terminology — "humans", not "members" or "volunteers"
description: Public-facing text uses "humans" — never "members" or "volunteers". Admin screens may say "users" when literally referring to the users table / IUserService. Branded org terminology. Applies across all locales (the word stays in English in es/de/fr/it). Internal code unaffected.
---

In **public-facing** text (views non-admin members see, localization strings, emails to humans, public copy), use **"humans"** — not "members" or "volunteers". This is the org's branded terminology.

It applies across all locales (the word "humans" is kept in English even in es/de/fr/it translations). Internal code (entity names, variable names) is unaffected.

**"Users" is allowed on admin/diagnostic screens** when it specifically refers to the `users` DB table or `IUserService` cache — e.g., admin dashboard stat tiles ("Total users", "Active (has profile)"), debug surfaces (`/Users/Admin/Debug`), nav labels for admin-only tools. Admins benefit from the precise distinction between "row in the users table" (~3 k, includes imported contacts) and "human with a complete profile" (~979). Don't extend this carve-out to anything a non-admin sees.

**Why:** Branding decision by Nobodies Collective. The word "humans" carries the org's identity; "members" and "volunteers" sound generic and miss the framing. "Users" is technically accurate vocabulary that's useful when an admin needs to talk about the underlying account/table state.

**How to apply:**

- Public Razor views, localization resources, emails, release notes, Discord posts — use "humans" (capitalize per sentence position).
- Admin Razor views, admin nav labels, admin-only diagnostics — "users" is fine when it refers to the users table / IUserService specifically. "Humans" is still the preferred word for member-shaped concepts on admin screens.
- Even in es/de/fr/it `.resx` files, "humans" stays in English; don't translate to "miembros", "freiwillige", etc.
- Internal code (`User`, `IUserService`, `humans` table — wait, the table IS named for users; entity is `User`) is fine as-is.
- "Volunteer" is OK only when specifically referring to the **Volunteer** role/team (the Spanish `Volunteers` team, the volunteer-vs-Colaborador-vs-Asociado tier distinction).
- "Member" / "Camp member" is OK in the **Camps** context — `CampMember` is a real domain concept (a person's active participation in a specific camp/year), and "Camp Members" reads accurately for the per-camp roster UI. Don't apply the humans-replacement here.
