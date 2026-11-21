---
name: design-direction-for-views
description: Adding or changing a `.cshtml` page: read `docs/design/DESIGN.md` first, build from the shared components, then look at the rendered page (`look-check`) before calling it done.
---

Before writing or reshaping a view, read [`docs/design/DESIGN.md`](../../docs/design/DESIGN.md) and build the page from the shared components it lists. Before calling the work done, look at the rendered page at desktop and phone width and fix what you see.

**Why:** Peter, 2026-11-21: the new Tickets pages came out "kind of junky" because no direction was given; they scored 2.2 of 5 against the look rubric while the best pages score 4. Each page was valid markup in the house colours, but flat: equal-weight blocks, hand-built badges and stat cards, figures in sentences. Nobody had looked at them.

**How to apply:** New page or a layout change: use the `design-page` skill (it walks DESIGN.md), then `look-check` on the PR preview or a local run. A text or bug fix inside an existing layout needs neither. A low look score is advice to try again while there is time; it never blocks a merge. Don't copy the layout of a neighbouring view without checking it against DESIGN.md: existing flat pages are debt, not precedent.

**Related:** [[burnername-is-the-display-name]], [[datetime-format-single-home]], [[base-ui-registries-are-section-populated]], [[localization-admin-exempt]]
