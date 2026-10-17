---
name: Every section shows its own current state and config
description: A section holding config, mappings, sync state or external-account maps owns an admin page that shows the current state — what maps to what, what is disabled, orphaned or stale. Hidden state that can break a user path is a gap to fill in the same PR.
---

Each section exposes, on its own admin surface, the current state of everything it configures or mirrors: integration mappings, account maps, flags, sync status, with the degraded rows (disabled, orphaned, stale, unmatched) called out as such. Nobody should need the database or a log to answer "what is this section configured as right now?".

**Why:** Peter, 2026-10-17, on orphan Holded accounts breaking the expense form: "we need visibility into our current state/config basically always, each section for their own bits." A failure that depends on state only the database can show is undebuggable by the Board and invisible to the admin who could fix it. Finance already does this (`/Finance/Holded` connector overview, `/Finance/HoldedAccounts` Mapped / ToAdd / Orphan plan, managed accounts with their Active / Retired flag); that is the bar, not an exception.

**How to apply:**

- Adding a mapping, flag, sync cursor or external id to a section: the same PR makes it visible on that section's admin page, including the broken shapes (orphan, retired, unmatched, never-synced).
- Fixing a bug caused by state (an orphan row, a stale mapping): check the state was visible somewhere an admin would look; if not, that page is part of the fix.
- Read-only is enough. The page shows; it need not act. `/Debug/*` is for developer diagnostics, not for this (see `debug-section`).
- Each section shows its own bits only; a section never renders another section's state.

**Related:** [[debug-section]], [[section-contribution-seams]].
