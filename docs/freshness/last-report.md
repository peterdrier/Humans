# Freshness sweep report

**Run:** 2026-06-09 (UTC)
**Mode:** diff
**Previous anchor:** `0f52d8a63`
**New anchor:** `989786372` (upstream/main HEAD at sweep start)
**Worktree base:** `b2a000d76` (origin/main HEAD at sweep start; includes #920 ahead of upstream)
**Window contents:** #916 scanner/ticket barcode, #915/#909 camp events card, #900 expense travel lines + IOU, #906 relevance-ranked cache-only search, #908 onboarding Names-save fix, 5 Dependabot bumps
**Entries:** 60 dirty (9 mechanical + 51 editorial docs); 29 updated, rest verified no-drift

## Updated automatically

### Mechanical

- `dev-stats` — +2 daily rows (2026-06-08, 2026-06-09)
- `reforge-history` — +2 daily rows
- `about-page-packages` — bumped Anthropic 12.27.0, Google.Apis.Auth 1.75.0, Magick.NET 14.14.0 in the license table; analyzer/test-only bumps not displayed
- `docs-readme-index` — 3 drifted descriptions refreshed (global-search Events bucket, ticket-transfer manual processing, guide/Events moderator wording)
- `authorization-inventory` — full re-scan: #900 Expenses guard surface (ExpenseReportOperation View/Endorse/CoordinatorReject/Approve/FinanceReject/IncludeInSepaPayout + submitter owner-checks), #916 Scanner barcode actions + ProfileApi search endpoints, UsersAdminController class-level policy + AdminOnly overrides, TeamController.EditTeam IsSensitive guard, ~15 drifted view line numbers
- `controller-architecture-audit` — added ScannerController.Tickets/Card, ExpensesController.AddMileage/AddPerDiem, EventsController.ToggleCampFavourite; removed stale ProfileController.ImportGooglePhoto (#745) and ProfileAdminController.EmailProblemsCompare/Merge (#899) missed by the prior regen
- `dependency-graph` — removed stale NotifResolver→Team edge and deleted GeneralAvailabilityService node, added missing OnboardingWidgetState node (5 edges), recomputed linkStyle indices
- `service-data-access-map` — #906 cache-only search, #916 barcode + transfer-cache invalidation, #915 IEventServiceRead read-split, #900 TravelReimbursementConfig; no new cross-section table violations
- `data-model-index` — no change needed (column-level changes only)
- `guid-reservations` — no change needed
- `code-analysis-suppressions` — not dirty (no trigger files changed)

### Editorial drift-fix (16 docs)

- **Tickets/Scanner (#916):** `sections/Tickets.md` (vendor-metadata claim removed; transfer cache invalidation documented), `features/tickets/ticket-transfer.md` (ti_ serial dropped from stub), `features/tickets/ticket-vendor-integration.md` (Barcode field + TicketTailor capture), `guide/Tickets.md` (attendee search by barcode; gate lookup at /Scanner/Tickets)
- **Expenses (#900):** `sections/Expenses.md` (travel lines non-editable invariant), `guide/Expenses.md` (travel items section, receipt claims scoped to purchases, IOU view)
- **Search (#906):** `features/profiles/profile-search-detail.md` (uncapped/relevance-ranked), `features/profiles/burner-name-collision-warning.md` (stale cap reference), `features/global/global-search.md` (relevance sort, cache-only Humans/Teams/Camps buckets)
- **Onboarding (#908):** `features/onboarding/onboarding-pipeline.md` (profile-prefill not OAuth claims; step-guard removal)
- **Events/Camps (#915/#909/#919):** `sections/Camps.md` (cache-only search; events card + Events cross-section dependency), `features/camps/camps.md` (US-20.2 events-card AC), `guide/Camps.md`, `guide/Events.md` (events card on camp page)
- **Architecture:** `design-rules.md` (§15a/§15c cache-only search carve-out; §8 missing Expenses row added; EventGuideService→EventService; Scanner row refresh), `conventions.md` (Scanner AJAX-partial exception; `<vc:…>` tag-helper form; §15 caching pointer)

### Orphan-ref cleanup (prune allowlist)

- `features/47-volunteer-tracking.md` — 3 dead links retargeted (25-shift-management → shifts/shift-management.md, 26-shift-signup-visibility → shifts/shift-signup-visibility.md, event-participation → tickets/event-participation.md)
- `features/global/background-jobs.md` — 02-profiles.md → profiles/profiles.md
- `features/profiles/communication-preferences.md` — notification-inbox.md → notifications/notification-inbox.md
- `authorization-inventory.md` — dead link to pruned 2026-04-03 transition plan edited out (2 spots)

## Pruned

| Husk | Lines | Evidence |
|---|---|---|
| `docs/superpowers/plans/2026-05-08-volunteer-tracking.md` | 2,040 | All chaff. Surviving rationale lives in the staying spec (2026-05-07-volunteer-tracking-design.md), `features/47-volunteer-tracking.md`, `sections/Shifts.md`, design-rules §8. Plan's blocked-days self-service, today-capped gap algorithm, and repo design were all superseded (day-off redesign 2026-05-09, #882). Zero inbound refs. |
| `docs/superpowers/plans/2026-05-10-expense-reports.md` | 3,354 | All chaff. Lifecycle/IBAN/SEPA/outbox rationale lives in the staying spec (2026-05-10-expense-reports-design.md); Holded vendor facts in `sections/Holded.md`; invariants in `sections/Expenses.md`; IBAN-mask rule in `memory/code/iban-mask-in-logs.md`. Plan's dedicated attachment storage and per-doc paid-polling were superseded in code (shared IFileStorage; creditor-balance reconciliation). One inbound ref retargeted. |

**Wheat migrated:** none — every candidate wheat item was verified against current code and found either already covered by a surviving doc or factually superseded. Verification details in the table above.

**Retargeted refs:** `sections/Expenses.md:23` plan → spec (`2026-05-10-expense-reports-design.md`).

**Budget:** total docs 83,065 lines; target ~4,153 (5%), hard cap 5,814 (7%); deleted 5,394. `docs/superpowers/plans/2026-05-10-early-entry-camps.md` (1,689 lines, 30 days old today) deferred to the next sweep — starting it would exceed the cap (budget decision, not a punt). `docs/architecture/tech-debt-2026-04-23.md` ineligible: multiple items still tagged `[OPEN]`.

## Flagged for human review

Presented to Peter inline in Phase 7.5 — **all 10 resolved same-session** (his dispositions in bold):

1. **`sections/Tickets.md` vs code — NotAttending overwrite:** doc said NotAttending rows "are never overwritten by ticket sync"; code flips NotAttending → Ticketed on a matched ticket. **Peter: code is correct — "a ticket is a physical thing, overriding any intentions."** Doc sentence rewritten to match code.
2. **`features/global/global-search.md`** `IProfileService.SearchProfilesAsync` → `IUserServiceRead.SearchUsersAsync`. **Fixed** (AC bullet, diagram, DTO row; verified against SearchService.cs).
3. **`features/tickets/ticket-vendor-integration.md`** `/Tickets/GateList — Stub for June implementation`. **Peter: still a stub — no change.**
4. **Onboarding docs naming** `ProfileService.SaveProfileAsync` → `IProfileEditorService.SaveProfileAsync`. **Fixed** (onboarding-pipeline.md + 2 spots in sections/Onboarding.md).
5. **`features/profiles/profile-pictures-birthdays.md`** dual-write claims. **Fixed**: storage approach, read path, flow diagram, computed property now FS-only with the `[Obsolete]` DB column pending #702 (citation corrected from #527). Additionally excised the removed US-14.5 Google-photo import (route row, user story → removal tombstone, localization table → orphaned-keys note) — verified `ImportGooglePhoto` no longer exists in src (#745).
6. **`design-rules.md` §15i repositories.** **Refreshed**: count corrected 26→33; added 11 missing repos; removed nonexistent UserEmailRepository/DriveActivityMonitorRepository; adjacent Shifts migration bullet's stale `IShiftSignupRepository`/`GeneralAvailabilityService` fragment corrected.
7. **`dependency-graph.md` prose.** **Fixed**: fan-in counts (UserService 55, TeamService 27, ShiftManagementService 16, MembershipCalculator 4 — OnboardingWidgetState consumers noted); GeneralAvailability annotated as deleted (#820).
8. **`sections/Shifts.md` §VolunteerBuildStatus.** **Fixed**: `DayOffs` (`List<DayOffEntry>`: DayOffset/Reason/MarkedByUserId/MarkedAt), `SetByUserId`/`SetAt` + Notes(500), rendering/write-path corrected; audit-action and service-method names verified already accurate.
9. **`sections/Events.md`.** **Fixed**: freshness markers added (29 verified trigger paths); #915/#919 surface documented (Favourite route, Camps consumer bullet, IEventServiceRead read-split). Follow-on naming drift also fixed: full rename pass to verified code symbols (GuideEvent→Event, EventGuideService→EventService, EventGuideRepository→EventRepository, GuideApiController→EventsApiController, etc., with real EF table names kept); phantom `IsAllDay`/`CreatedAt` rows removed, `AdminNotes` added, missing `Draft` status added, false cross-section-navs sentence corrected. The flagged "decorator writes via repository?" concern was verified FALSE — CachingEventService routes through the keyed inner `IEventService` (doc already correct).
10. **`sections/Scanner.md`.** **Fixed**: freshness:triggers + flag-on-change markers added (controller, views, scanner JS, view model).

**Informational (no decision needed):**
- No `docs/guide/Scanner.md` exists; the /Scanner/Tickets lookup is covered by a sentence in guide/Tickets.md. Consider a guide page if Scanner grows.
- `SearchService` injects full `IEventService` rather than `IEventServiceRead` (the new read interface is only consumed in the Web layer). Possible future read-split migration.
- Other unmarked editorial docs (no freshness markers): `sections/Agent.md`, `sections/Mailer.md`, `sections/_Index.md`.

## Proposed for review

None — all candidates resolved this sweep (prune candidates verified against code; no uncertain wheat queued).

## Questions

All asked and answered inline in Phase 7.5 — see resolutions above. Two carried Peter rulings worth remembering: a matched ticket overrides a NotAttending declaration (item 1), and `/Tickets/GateList` remains a stub (item 3).

## Skipped (errors)

None.
