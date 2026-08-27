---
name: An img alt on a member-facing view is a user-facing string
description: Adding or editing an <img alt="..."> in a Razor view — alt text is a user-facing string and needs a resx key in all six cultures. The admin exemption covers /Admin/*, /TeamAdmin/* and /Shifts/Dashboard only, not a member-facing modal opened from an admin page. Triggers on any <img> in a .cshtml.
---

`alt` text is read aloud by a screen reader and shown when the image fails to load. On a member-facing view that makes it a **user-facing string**: it gets a resx key in the section's set, in all six supported cultures (en, es, de, it, fr, ca), like any other.

A hardcoded English `alt` is the same defect as a hardcoded English heading — it is just easier to miss, because it renders for nobody most of the time.

**The admin exemption does not stretch to cover it.** [`localization-admin-exempt`](localization-admin-exempt.md) names exactly these surfaces: `/Admin/*`, `/TeamAdmin/*` and `/Shifts/Dashboard`. A member-facing partial or modal is not exempt merely because the page that opens it is an admin page — the reader is a member either way, and the reader is what the rule is about.

**Decide by who reads it, not by where the file lives.** If a member can reach the rendered markup, the string is localized.
