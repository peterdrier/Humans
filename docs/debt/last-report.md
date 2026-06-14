# Debt Sweep Report — 2026-06-13

- **Started:** 2026-06-13T20:29:52Z
- **Budget:** 2h (default); finished well within budget
- **Branch:** `debt-sweep/2026-06-13T203001Z` (off `origin/main` @ `18c308a52`)
- **Themes touched:** 3 (rotation walked the `never`-swept front; two were non-actionable, one delivered burndown)
- **Verification:** full `dotnet test` green — Web 340/340, Application 3698/3698, Integration 115 passed/1 skipped

## Rotation walk

The `never`-swept themes at the front of rotation were audited in order. The
first two turned out non-actionable for an autonomous sweep; the rotation
advanced (nav-strip precedent) to the first theme with real, safe burndown.

| # | Theme | Outcome |
|---|-------|---------|
| 1 | `grandfathered-hum0028-invalidators` | **Design-blocked** — verified, no code change |
| 2 | `obsolete-user-displayname` | **Actionable = 0** — all 20 sites legit, no code change |
| 3 | `cs0618-pragma-nav-reads` | Assessed **blocked** (schema configs + interface-stitching) — left at `never`, not marked swept |
| 4 | `baseline-entity-read-returns` | Deferred — every fix changes a public interface return type (approval-gated) |
| 5 | `baseline-display-sort` | **Worked** — 16 false-positive exceptions marked, ~55 genuine sorts characterized |

## Fixed (baseline-display-sort)

16 ratchet entries removed by marking genuine non-display sorts
`// arch:db-sort-ok` (exception categories per the rule doc). These were never
display debt — selectors, FIFO batches, deterministic identity picks,
chronological streams, pagination ordering:

**Commit `2adb25a6e`** — 6 sorts:
- ConsentRepository — latest-consent selector (`FirstOrDefault`) + consent-records chronological stream
- EmailOutboxRepository — outbox FIFO claim batch (`OrderBy CreatedAt` + `Take`)
- RoleAssignmentRepository — `ThenByDescending` tie-breaker completing the already-marked paged admin window
- FeedbackRepository — append-only message-thread chronological order
- ApplicationRepository — mandatory SQL ordering for `Skip/Take` pagination

**Commit `2b5e5e958`** — 4 sorts:
- GoogleSyncOutboxRepository — top-N recent-events selector + outbox FIFO batch
- ShiftRepository.Management — deterministic active-settings selector by identity
- ShiftRepository.Signups — maintenance orphan-scan order (no UI)

**Commit `505992d90`** — 6 sorts:
- CampRepository — `CampSettings.OrderBy(Id).First*` singleton-config selectors (×4)
- CampaignRepository — top-N available-code allocation selector + newest-campaign grant selector

`DisplaySortInControllers` arch test green after each cluster; full suite green at the end.

## Skipped / deferred (recorded, not chased)

- **HUM0028 invalidators (all 17):** folding is correctness-breaking, not mechanical.
  Proof on the best candidate (`IRoleAssignmentCacheInvalidator`): inner service
  invalidates after the write but before `SyncBoardTeamAsync` reads (read-after-write
  regression if folded into the decorator), and `ReassignAsync` defers invalidation
  to a post-`TransactionScope` cross-section caller. 16 of 17 are deliberate
  ordering-sensitive cross-section/sibling seams that must stay; 1
  (`IEventViewInvalidator`) is a speculative hook for open issue #719.
- **HUM_USER_DISPLAYNAME (all 20):** every firing site is a legit consumer
  (creation-time writes, BurnerName-fallback derivation, GDPR export, purge labels,
  write-side sync, GDPR sentinel, dev seeders). Rendering-caller debt already migrated
  (#691). The `build:` detect overcounts — legit consumers can't be silenced without a
  forbidden pragma, so the count floors at 20.
- **~55 genuine display sorts (baseline-display-sort):** UserRepository
  emails/profiles/contact-fields, Team/Camp departments+roles by `SortOrder`/`Name`,
  Camp images by `SortOrder`, Event/Ticket/Campaign lists, and ambiguous "user's list
  newest-first". These need caller-side moves + UI verification — out of safe scope for
  a `review: light` autonomous sweep (moving a repo `OrderBy` up changes order for all
  consumers). Recommend a focused reviewed PR.
- **TicketRepository `sortBy`/`sortDesc` parameterized helper (~12 sites):** the doc's
  named anti-pattern, but genuinely required at the SQL boundary for paged sortable
  admin grids — pagination-vs-rule architectural tension, Peter's call.

## Forbidden-move reverts

None. Diff scanned each cluster for `#pragma warning disable HUM`, `[SuppressMessage`,
new `[Grandfathered]`, `// ReSharper disable` — clean. All edits are
`// arch:db-sort-ok` markers on genuine exception-category sorts (not display-debt
dodges). No EF drift gate needed — comment-only repository edits, no model change.

## Ledger changes

- `recent_sections`: `[] -> [Camps, Campaigns, Shifts]`
- `grandfathered-hum0028-invalidators`: `last_swept -> 2026-06-13`; note = DESIGN-BLOCKED
  with per-item classification of all 17; recommend rotation skip pending Peter on #719.
- `obsolete-user-displayname`: `last_swept -> 2026-06-13`; `remaining 12 -> 20` (matches
  detect); note = ACTIONABLE=0, detect overcounts the legit floor; parked.
- `baseline-display-sort`: `last_swept -> 2026-06-13`; `remaining 71 -> 55`; note =
  16 false-positives removed, ~55 genuine sorts + Ticket tension characterized.
- No themes retired (no enforcer-guarded theme hit 0); no new themes (staleness check clean:
  grandfathered ids HUM0024/0028/0031 and baselines 21/71 all matched the ledger).

## Resolved with Peter (2026-06-13)

1. `IEventViewInvalidator` — **KEEP** as the #719 placeholder.
2. HUM0028 invalidators — **LEAVE in rotation** (re-check periodically; do not flag as skip).
3. HUM_USER_DISPLAYNAME — **LEAVE parked** in the ledger; the `[Obsolete]` warning guards new misuse.
4. TicketRepository `sortBy`/`sortDesc` — **REAL DEBT**, not a pagination exception: stays in the baseline, needs the paged-grid sort redesigned (separate effort).

## Follow-up — interactive display-sort moves (Peter-directed, 2026-06-13)

Peter corrected the initial framing: a flagged repo sort whose consumers don't
render an order-sensitive user-facing list is **wasted work — delete it**, not
relocate it; only genuinely user-facing lists get sorted **in the rendering
control** (view / VM assembly). Worked the whole theme down with reforge
caller-tracing per method. **`baseline-display-sort`: 71 → 25.**

- **Deleted** (~17 — no consumer renders the order): UserEmails/ContactFields/
  ProfileLanguages reads, GoogleResource sync lists, GDPR-export paths
  (AccountMerge/Campaign/CampRoles/Team), and the 4 camp-image `Include` sorts
  that `CampService` + the view already re-sort.
- **Marked db-sort-ok** (non-display): Camp latest-season selector, Event
  category/venue reorder-neighbor lookups, plus the earlier 16.
- **Moved repo → view**: campaign admin list + user/admin grant tables + camp
  role-definition tables (client-sortable `TableModel`, default order set in the
  view), team role-definition lists (`@foreach` by SortOrder), account-merge
  review queue (controller VM assembly, oldest-first).
- **Remaining 25** = 18 Ticket `sortBy`/`sortDesc` real-debt (stays, needs the
  paged-grid sort redesigned) + 7 harder view-moves deferred (tracking view not
  located; multi-consumer camps list; camp-role-assignment view; team roster —
  its slot DTO lacks `SortOrder` so the view can't re-sort without a DTO field).
  Each verified green per cluster; full suite was green before the moves began.
