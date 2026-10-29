---
name: A section's widgets are cataloged by the section's own gallery contribution
description: Adding/removing a public TagHelper, ViewComponent, or shared partial → add or update its card in the owning section's gallery view component (contributed through `ChromeSlots.WidgetGallery`); Debug's own gallery view catalogs Base and Shell widgets only.
---

`/Debug/WidgetGallery` is the one place to see every reusable widget rendered against sample data. **The cards live with their owners**: Debug's `Views/WidgetGallery/Index.cshtml` hand-writes cards only for Base and Shell widgets, and every section's cards come from that section's own gallery view component (`<Section>GalleryViewComponent` + `Views/Shared/Components/<Section>Gallery/Default.cshtml`), contributed from its `SectionChrome` (`ISectionChrome`) under `ChromeSlots.WidgetGallery`. The page names no section, and Debug references no section for it.

When you **add** a new TagHelper, ViewComponent, or user-facing shared partial, register it in the same PR:

- **A section widget** → a card in the section's gallery view component. Create the component and the `SectionChrome` contribution if the section has none yet (Users, Shifts, Teams, Tickets, Camps, Calendar, Events, AuditLog, Governance, Email and Agent already have one). The component resolves its own sample data from the section's own services; a chrome-slot contribution takes no arguments, so read the signed-in user from claims.
- **A Base or Shell widget** → a card in Debug's `Index.cshtml` (extend `WidgetGalleryController` when it needs a sample record).
- **A widget that cannot render in isolation** (needs a live record, a flag, a parent form) → a text-only card in the same place, saying why and where to see it. Debug's "Skipped (with reason)" list is for Base and Shell items only.
- **A component contributed through a slot** (`IUserPart`, `ISectionChrome`, `ISectionMemberDashboard`, `ISectionSettings`) is already shown by the gallery's slot renders and needs no card.
- **A partial that is a piece of one page** (a form fragment, a table row, a script-only partial) is not a widget and gets no entry anywhere.

Each card has: name, type badge (`kind-th` / `kind-vc` / `kind-pa`), source path, note, `@ParamsBlock`, example; add `@AuthLine` whenever the widget self-gates. The `wg-*` classes are the gallery page's, so a section view uses them as-is. A section rendering its own `<vc:…>` in its gallery view needs `@addTagHelper *, Humans.<Section>` in its `Views/_ViewImports.cshtml`; `ViewComponentTagHelperBindingTests` catches the miss.

When you **remove** or **rename** a widget, also remove or rename its card.

**Why:** the catalog rots if new widgets aren't registered, and the next person reinvents what already exists. Cards used to be hand-written in Debug for nine other sections, which made Debug reference those assemblies and made every new section edit Debug's view (PR 2118 did); moving each card to its owner (Peter, 2026-10-29) keeps sections self-contained and keeps the catalog accurate without a central file. We deliberately did not add an automated ratchet test — pieces of larger pages would make it noisy. Discipline + PR review catches drift.

**Related:** [`section-contribution-seams`](../architecture/section-contribution-seams.md) (the `ISectionChrome` seam), [`docs/architecture/code-review-rules.md`](../../docs/architecture/code-review-rules.md) §Orphaned Pages.
