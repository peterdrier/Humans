# Debt Sweep Report — 2026-06-12

- **Branch:** `debt-sweep/2026-06-12T150757Z` (base `origin/main @ aaad87962`)
- **Budget:** 2h (default) — used ~1h35m, stopped between items
- **Theme worked:** `grandfathered-hum0031-controller-logic` (light review)
- **Rotation note:** rotation first picked `grandfathered-hum0024-nav-strip`; it proved schema-blocked (below) and the sweep moved to the next theme.

## Fixed — 8 items (HUM0031 grandfathers removed: 15 → 7)

| Item | Fix | Commit |
|---|---|---|
| `UsersAdminDebugController.ApplySort` (4 stmts / cc 16) | Switch → table-driven sort map | `f05b2378d` |
| `EventsController.MySubmissions` (21 / 19) | Row/block mapping → VM `From` factories | `3cdc6393d` |
| `EventsController.BulkUploadTemplate` (60 / 14) | Banner + CSV record builders → focused private helpers | `a7de55fb3` |
| `EventsController.Update` (42 / 17) | `ApplyIndividualFormToEvent` + `RedisplayIndividualFormAsync`, deduped error redisplay | `d60128120` |
| `EventsController.BarrioUpdate` (41 / 11) | Twin: `ApplyBarrioFormToEvent` + `RedisplayBarrioFormAsync` | `d60128120` |
| `EventsModerationController.ProcessActionAsync` (29 / 18) | Extracted `NotifySubmitterAsync` | `1a207cd18` |
| `TeamAdminController.Members` (23 / 19) | Local `MapResource` → `ResourceAccessViewModel.From` | `2c89d3ab2` |
| `ShiftsController.ToggleDay` (38 / 20) | `ToggleSignupAsync` + `SetToggleToastHeader` + `RenderRotaRow` | `9cb70ec2e` |

Verification per item: `dotnet build` clean (analyzer at Error with attribute removed), forbidden-move grep clean, full `dotnet test` green before each push (4,659 tests).

## Theme finding — `grandfathered-hum0024-nav-strip` is SCHEMA-BLOCKED

Verified empirically: removing the `HasOne` block from `IssueCommentConfiguration` fires `dotnet ef migrations has-pending-model-changes`. The config blocks own DB-level FK constraints + cascade behavior, so **every** HUM0024 fix is an FK-drop migration — out of sweep scope. Ledger marks the theme skipped pending Peter's decision (Phase 7 question).

## Skipped

- All remaining HUM0031 items (7) — budget; the tail is the hard set (AccountController 107/33, ProfileController.Edit 78/46, …), listed in the ledger notes.

## Forbidden-move reverts

None.

## Inbox additions

- Flaky integration test `UnprivilegedUser_AdminPostToOtherUserEmails_ReturnsForbid` — fails under full-suite parallel load (`TaskCanceledException` in cleanup), passes in isolation.

## Ledger changes

- `grandfathered-hum0024-nav-strip`: `last_swept: 2026-06-12`, schema-blocked note (remaining 34, untouched).
- `grandfathered-hum0031-controller-logic`: `last_swept: 2026-06-12`, remaining 26 → 7 (grandfather count 15 → 7; the warning-site count drops accordingly).
- `recent_sections: [Events]` (5 of 8 items were Events).
- Header: explicit staleness-check exclusion for `NoDestructiveMigrationOps.baseline.txt` (immutable history, guard not backlog).
- Inbox: +1 (flaky test), now 10.

## Questions for Peter

1. HUM0024 nav strips all require FK-drop migrations — dedicated migration PR(s) outside the sweep, or park the theme indefinitely?
   **Resolved 2026-06-12: fine — handled via dedicated migration PRs outside the sweep; theme stays marked schema-blocked in the ledger so rotation skips it.**
2. Confirm the `NoDestructiveMigrationOps` staleness exclusion is right (its 23 baseline entries are historical migrations, not fixable debt).
   **Resolved 2026-06-12: confirmed.**
