---
name: design-page
description: "Design or reshape a Humans page (.cshtml) so the first draft follows the house design direction. Use before writing a new view or changing a page's layout. Argument: the page or feature."
argument-hint: "<page or feature>"
---

# Design a page

Decide the page before writing markup. Flat pages come from skipping steps 1 to 3.

1. **Read** `docs/design/DESIGN.md` in full, and look at `/Budget` and `/Finance` (4 on the look rubric, `tools/look-audit/rubric.md`) for hierarchy and calm only: they predate the shared components, so take markup from `/Debug/WidgetGallery`, not from them.
2. **Answer in three lines**, in your reply or the PR body:
   - Who is it for: a member on a phone, or an admin on a laptop?
   - What is the one thing that matters on it (a number, a state, a choice)?
   - What is the one primary action?
3. **Outline the page top to bottom** as a short list of blocks, each built from a shared component in the DESIGN.md table where one fits (`<page-header>`, `.stats` + `_StatTile`, `_Table`, `<empty-state>`, `<status-badge>`, `<vc:human>`) and from plain Bootstrap form, grid and card markup otherwise. Rank the blocks: one is primary, the rest recede. No clear first block: split or re-rank before going on.
4. **Check what is missing.** Forms, columns and cards need no component. If a block wants a new reusable widget, build the page with the nearest existing piece, and name the gap in your report: a new shared Base component needs Peter's approval, so don't add one unasked.
5. **Write the view** from the outline. No raw `badge bg-*`, no local money or date helpers, no inline `style=` (a component's own custom property such as `--stats-cols` excepted), no hex colours, no raw enum output. Every list has its empty state (`.Empty(...)` on a `_Table`, `<empty-state>` elsewhere). Member-facing strings go in the resx set in every culture.
6. **Look at it**: run the `look-check` skill (it looks once and reports), fix what you see, look again. Aim for 4 overall. Below 3, rework the outline rather than nudging spacing. Three looks in total, then report the score and what still bothers you.

Report: the three answers, the outline, the before and after scores, and anything DESIGN.md did not cover (so it can be added).
