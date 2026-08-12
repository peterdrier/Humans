---
name: No /Admin/ URL section — legacy, never extend
description: HARD RULE. Top-level `/Admin/*` is legacy; never add new `/Admin/foo` routes. New admin pages always live at `/<Section>/Admin/*`.
---

**HARD RULE — `/Admin/*` is legacy and frozen.** No new top-level `/Admin/foo` routes, controllers, or links can be added to the application going forwards. There is no "Admin" section. New admin pages live at `/<Section>/Admin/*` (e.g. `/Store/Admin/Catalog`, `/Camps/Admin/Settings`, `/Tickets/Admin/Transfers`).

**Why:** Section ownership is sharper when admin pages live inside the section's own URL tree — it matches the "services own their data" rule at the URL level. Peter (PR #421 review, 2026-05-05): *"/Admin is not allowed to be added to, it's legacy and must go away. We do NOT have an 'Admin' section, thus no top level /Admin/foo links can be added to the application going forwards."* Earlier framing (/Store brainstorm 2026-04-30): *"there is NO /Admin/ Section going forwards.. /Store/Admin is the right url."*

**How to apply:**

- For new sections: admin pages under `/<Section>/Admin/...` from the start.
- For new admin functionality on existing sections: `/<Section>/Admin/...` only. Do not add to `/Admin/...`.
- Do **NOT** propose blanket migration of the existing legacy `/Admin/*` pages as part of unrelated work — that's a separate refactor Peter will scope when he wants it.
- An admin controller for a section is a separate file from the buyer/user-facing controller — split, don't mix `[Authorize(Policy = ...)]`-gated actions into a single controller. e.g. `TicketTransferController` (buyer) at `/Tickets/Transfers`, `TicketTransferAdminController` at `/Tickets/Admin/Transfers`.
- `CLAUDE.md`'s note ("`/Admin/*` is a nav holder, not a section") describes today's legacy state, not the desired future state.
