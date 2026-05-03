---
name: UI terminology — "humans", not "members" or "volunteers"
description: All user-facing text uses "humans" — never "members", "volunteers", or "users". Branded org terminology. Applies across all locales (the word stays in English in es/de/fr/it). Internal code unaffected.
---

In all user-facing text (views, localization strings, emails), use **"humans"** — not "members", "volunteers", or "users". This is the org's branded terminology.

It applies across all locales (the word "humans" is kept in English even in es/de/fr/it translations). Internal code (entity names, variable names) is unaffected.

**Why:** Branding decision by Nobodies Collective. The word "humans" carries the org's identity; "members" and "volunteers" sound generic and miss the framing.

**How to apply:**

- Razor views, localization resources, emails, release notes, Discord posts, public-facing copy — use "humans" (capitalize per sentence position).
- Even in es/de/fr/it `.resx` files, "humans" stays in English; don't translate to "miembros", "freiwillige", etc.
- Internal code (`User`, `IUserService`, `humans` table — wait, the table IS named for users; entity is `User`) is fine as-is.
- "Volunteer" is OK only when specifically referring to the **Volunteer** role/team (the Spanish `Volunteers` team, the volunteer-vs-Colaborador-vs-Asociado tier distinction).
