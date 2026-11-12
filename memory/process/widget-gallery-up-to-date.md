---
name: widget-gallery-up-to-date
description: "Adding or removing a public TagHelper, ViewComponent or shared partial: update its card in the owning section's widget gallery component (`ChromeSlots.WidgetGallery`)."
---

`/Debug/WidgetGallery` shows every reusable widget against sample data, and **the cards live with their
owners**. Add, rename or remove the card in the same PR as the widget.

| Widget | Where its card goes |
|---|---|
| A section's TagHelper, ViewComponent or shared partial | The section's `<Section>GalleryViewComponent`, contributed from its `Section` (`ISectionChrome`) under `ChromeSlots.WidgetGallery`. Create both if the section has none. |
| A Base or Shell widget | Debug's `Views/WidgetGallery/Index.cshtml` |
| Can't render in isolation (needs a live record, flag or parent form) | A text-only card in the same place saying why and where to see it |
| Contributed through a slot (`IUserPart`, `ISectionChrome`, `ISectionMemberDashboard`, `ISectionSettings`) | No card; the gallery's slot renders already show it |
| A piece of one page (form fragment, table row, script partial) | Not a widget; no entry |

- A section's gallery component resolves its own sample data from its own services and reads the
  signed-in user from claims (a chrome-slot contribution takes no arguments).
- A card has: name, type badge (`kind-th` / `kind-vc` / `kind-pa`), source path, note, `@ParamsBlock`,
  example, and `@AuthLine` when the widget gates itself. Use the page's `wg-*` classes as they are.
- A section rendering its own `<vc:…>` in its gallery view needs `@addTagHelper *, Humans.<Section>` in
  its `_ViewImports.cshtml`.

**Why:** an incomplete catalog means the next person reinvents a widget that exists. Cards sit with
their owners so Debug references no section. There is deliberately no ratchet test; page fragments
would make it noisy.

**Related:** [`section-contribution-seams`](../architecture/section-contribution-seams.md)
