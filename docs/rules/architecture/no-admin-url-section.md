---
name: No /Admin/ URL section going forward
description: New admin pages live at `/<Section>/Admin/*`, never `/Admin/<Section>/*`. The global `/Admin/*` URL section is being phased out.
---

The `/Admin/*` URL section is being phased out. New admin pages live at `/<Section>/Admin/*` (e.g. `/Store/Admin/Catalog`, `/Camps/Admin/Settings`), not at `/Admin/Store`, `/Admin/Camps`, etc.

**Why:** Section ownership is sharper when admin pages live inside the section's own URL tree — it matches the "services own their data" rule at the URL level. Peter, /Store brainstorm 2026-04-30: "there is NO /Admin/ Section going forwards.. /Store/Admin is the right url."

**How to apply:**

- For new sections: put admin pages under `/<Section>/Admin/...` from the start.
- For new admin functionality on existing sections: prefer `/<Section>/Admin/...` over adding to `/Admin/...`.
- Do **NOT** propose blanket migration of the existing `/Admin/*` pages as part of unrelated work — that's a separate refactor Peter will scope when he wants it.
- Note: `CLAUDE.md` currently says "`/Admin/*` is a nav holder, not a section." That note describes today's state, not the desired future state.
