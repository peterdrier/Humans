---
name: Register new widgets in /WidgetGallery when adding them
description: When adding or removing a TagHelper, ViewComponent, or user-facing shared partial under `src/Humans.Web/`, update `Views/WidgetGallery/Index.cshtml` (and the controller if real sample data is needed) so the admin Widget Gallery stays a complete catalog. The Skipped section is the legitimate exception list.
---

When you **add** a new TagHelper, ViewComponent, or user-facing shared partial under `src/Humans.Web/`, also register it in the Widget Gallery in the same PR:

- Add a card in the appropriate section of `src/Humans.Web/Views/WidgetGallery/Index.cshtml` — header (name, type badge, source path), one-line note, parameters table via `@ParamsBlock(...)`, optional `@AuthLine(...)` for role/policy gating, and an example rendered against real data.
- If the widget needs a real sample record that isn't already on `WidgetGalleryViewModel`, extend `WidgetGalleryController` to resolve it and add a property to the view model.
- If the widget is genuinely not catalog-able (layout chrome, script-only partial, deeply context-bound rota / dashboard view model that can't be synthesized), add it to the **Skipped (with reason)** section instead — that section is the explicit allowlist for non-rendered widgets.

When you **remove** or **rename** a widget, also remove or rename its card in the gallery.

**Why:** `/WidgetGallery` exists so designers and developers can see every reusable widget in one place. If new widgets aren't registered, the catalog rots — stale entries linger and new ones go undiscovered, and the next person reinvents what already exists. Tying registration to the same PR as the widget change keeps the catalog perpetually accurate without a separate maintenance loop. We deliberately did not add an automated ratchet test for this — the legitimate Skipped list, plus partials that are pieces of larger pages, would make the test noisy. Discipline + PR review catches drift.

**How to apply:**

- New TagHelper / ViewComponent / shared partial → add a gallery card before pushing the PR.
- Each card MUST have: name, type badge (`kind-th` / `kind-vc` / `kind-pa`), source path, note, `@ParamsBlock`, example.
- Add `@AuthLine` whenever the widget self-gates by role/policy or hides itself for unauthenticated viewers — it's how viewers learn the access rules without reading the source.
- For widgets that legitimately don't render in isolation (chrome, script-only, complex view models), add to the Skipped section with a one-line reason.

**Related:** [`docs/architecture/code-review-rules.md`](../../docs/architecture/code-review-rules.md) §Orphaned Pages — same family of "added something, didn't wire it up" defect, but for nav links rather than the widget catalog.
