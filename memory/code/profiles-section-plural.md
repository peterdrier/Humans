---
name: Profile section namespace is "Profiles" (plural)
description: `Humans.*.Services.Profiles` (plural). Singular `Profile` collides with the `Profile` entity class and triggers C# CS0135 ambiguity errors.
---

The section folder and namespace for Profile is **`Profiles`** (plural), not `Profile`.

**Why:** The `Profile` entity class exists in `Humans.Domain.Entities`. If the section namespace were also `Profile`, C# emits `CS0135`/similar ambiguity errors wherever both need to be referenced ("Profile is both a type and a namespace in this scope"). The fix is the plural form.

**How to apply:**

- When referencing paths or namespaces, use `Humans.Application.Services.Profiles`, `Humans.Application.Interfaces.Profiles`, `Humans.Infrastructure.Repositories.Profiles`, etc. **Not `Profile`.**
- All other potentially-colliding sections (User → Users, Team → Teams, Camp → Camps, Campaign → Campaigns, Shift → Shifts, Ticket → Tickets, Notification → Notifications) were already plural by convention; this is the one that needed the rename.
- When drafting tickets, audit doc entries, or migration plans, write `Profiles` not `Profile`. Don't regress to singular.
