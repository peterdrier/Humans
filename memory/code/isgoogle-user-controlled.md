---
name: isgoogle-user-controlled
description: UserEmail.IsGoogle is a user-set preference — never auto-set it during merges/syncs/duplicate resolution; the sanctioned auto-pick only applies when adding a brand-new row.
---

`UserEmail.IsGoogle` is user-controlled whenever there's a user choice to preserve — the user toggles it themselves to indicate "this is my Google identity email." The rule has nuance: it applies to merges/syncs, not to add-row flows.

- **Never auto-set during merges, syncs, account folds, or duplicate resolution** when the user already has an `IsGoogle=true` row. Don't propagate `IsGoogle` from a source account during a merge; don't flip it during sync. The user's choice is sticky.
- **The sanctioned auto-pick is the invariant helper that runs after adding a new `UserEmail` row.** It picks a winner (prefer the org email domain, else the current `IsGoogle` row, else most-recent) — but if there's already exactly one `IsGoogle` row matching the winner, it's a no-op. The user's existing choice is preserved unless an obviously better winner exists (a newly-verified org-domain row).
- **The OAuth callback's email-update upsert path** must also route through that invariant helper after the write — it's an add-row flow, not a sync.

**Why:** an earlier "never automatically" framing was overbroad. The original concern was specifically about merges/syncs overriding user-set state, not about the add-row invariant helper that resolves a genuine vacuum (no existing `IsGoogle` row at all).

**How to apply:** designing a merge/sync/repair flow → never touch `IsGoogle`. Designing an add-row flow (insert/upsert) → route through the invariant helper. If unsure which category applies, ask.
