---
name: design-page
description: "Design or reshape a Humans page (.cshtml) so the first draft follows the house design direction. Use before writing a new view or changing a page's layout. Argument: the page or feature."
argument-hint: "<page or feature>"
---

# Design a page

Decide the page before writing markup. Flat pages come from skipping steps 1 to 3.

1. **Read** `docs/design/DESIGN.md` in full, and open the nearest good page for reference: `/Budget` and `/Finance` score 4 on the look rubric (`tools/look-audit/rubric.md`).
2. **Answer in three lines**, in your reply or the PR body:
   - Who is it for: a member on a phone, or an admin on a laptop?
   - What is the one thing that matters on it (a number, a state, a choice)?
   - What is the one primary action?
3. **Outline the page top to bottom** as a short list of blocks, each mapped to a shared component from the DESIGN.md table (`<page-header>`, `.stats` + `_StatTile`, `_Table`, `<empty-state>`, `<status-badge>`, `<vc:human>`). Rank the blocks: one is primary, the rest recede. More than four cards, or no clear first block: split or re-rank before going on.
4. **Check what is missing.** If a block needs something no shared component gives, stop and say so: a new Base component needs Peter's approval. Don't hand-build it in the section view.
5. **Write the view** from the outline. No raw `badge bg-*`, no local money or date helpers, no inline `style=`, no hex colours, no raw enum output. Every list has its empty state. Member-facing strings go in the resx set in every culture.
6. **Look at it**: run the `look-check` skill at both widths, fix what you see, look again. Aim for 4 overall. Below 3, rework the outline rather than nudging spacing. Stop after three rounds and report the score and what still bothers you.

Report: the three answers, the outline, the before and after scores, and anything DESIGN.md did not cover (so it can be added).
