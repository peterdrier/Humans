---
name: An img alt on a member-facing view is a user-facing string
description: An `<img alt>` on a member-facing view needs resx keys in all six cultures — the admin exemption doesn't cover an ungated modal opened from an admin page.
---

`alt` text is read aloud by a screen reader and shown when the image fails to load. On a member-facing view that makes it a **user-facing string**: it gets a resx key in the section's set, in all six supported cultures (en, es, de, it, fr, ca), like any other.

A hardcoded English `alt` is the same defect as a hardcoded English heading — it is just easier to miss, because it renders for nobody most of the time.

**The admin exemption does not stretch to cover it.** [`localization-admin-exempt`](localization-admin-exempt.md) exempts a view only when admin or operator roles alone can reach it. A partial or modal without that gate is not exempt merely because the page that opens it is an admin page — the gate on the rendered markup is what the rule is about.

**Decide by the gate on the markup, not by where the file lives.** If the rendered markup is reachable without an admin or operator role, the string is localized.
