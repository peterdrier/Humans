# Freshness Sweep Report

**Date:** 2026-06-24
**Mode:** diff
**Previous anchor:** `c10d07400` · **New anchor:** `upstream/main` `c89e1514c`
**Dirty set:** 10 mechanical entries (8 prompt + 2 script) + 61 editorial docs
**Outcome:** 22 editorial docs drift-fixed · 6 mechanical regens updated (+2 clean) · 12 husks pruned (−2,836 lines, within the 5,119 cap) · wheat migrated to `sections/Shifts.md` + `sections/Agent.md` · inbound-ref retargets · **Phase 7.5: all 6 review items approved by Peter and applied** · 0 errors

Range themes (upstream `c10d07400..c89e1514c`, ~9 days): Holded **ledger single-source** redesign (creditor-balances/payments tables → ledger-lines/creditor-contacts); **Expenses SEPA removal** (SEPA builder/config/statuses deleted, payment now external/Holded); **Notifications SourceKey** + inbox cleanup; **Google group membership sync** + UserEmail per-address Google status (#687); **voluntell past shifts** + per-day signup; **Events favourite → JSON API** (no reload); **Camps** shift-signup-count badges + account-merge re-point.

## Updated automatically (mechanical)
- `development-stats.md` — script: +3 daily rows (06-16, 06-17, 06-23); reforge-sourced class/interface counts (0 regex fallback)
- `reforge-history.csv` — script: +4 day rows (111 distinct days)
- `authorization-inventory.md` — removed SEPA auth ops (`IncludeInSepaPayout`/`ReopenSepa`, SepaReopen/SepaGenerate, #1030); corrected `ExpensesController` `AuthorizeAsync` call-site line numbers
- `controller-architecture-audit.md` — date → 2026-06-24; added `MailerAdminController.SyncAll` (POST `/Mailer/Admin/SyncAll`)
- `service-data-access-map.md` — Finance ledger tables (`HoldedLedgerLines`/`HoldedCreditorContacts` replace Balances/Payments); `SepaPaymentFileBuilder` removed; `HoldedFinanceService` added as `IUserDataContributor`; `GeneralAvailabilityService` removed from `ShiftView` invalidators; date → 2026-06-24
- `architecture/dependency-graph.md` — +3 eager edges (Issues→NotifInbox, Onboarding→Consent, Onboarding→HumanLifecycle); fan-in hotspot table + lazy `linkStyle` indices refreshed
- `About/Index.cshtml` — package version bumps (Anthropic 12.30.0, ASP.NET Google auth 10.0.9, Google.Apis Admin.Directory 1.75/CloudIdentity 1.75, Markdig 1.3.2); none added/removed
- `docs/README.md` — Holded index description now reflects Expenses+Finance shared use; all 66 feature / 35 section / 25 guide docs verified linked
- Verified clean (dirty but no drift): `data-model.md` (entity index current), `guid-reservations.md` (7 GUID blocks unchanged)
- Not dirty this sweep: `code-analysis-suppressions`

## Updated automatically (editorial drift-fix)
- `sections/Finance.md` — +creditor routes (`GET /Finance/Creditors`, `…/{accountNum:int}`, `POST …/Bind`); Evolution section (HoldedSyncJob/Finance shipped)
- `sections/Holded.md` — `ListChartOfAccountsAsync`+`ListPaymentsAsync` → `ListDailyLedgerAsync`+`ListContactsAsync`; stale Future-evolution → Evolution
- `sections/Expenses.md` — +bind-creditor-account (FinanceAdmin); `/Expenses` GET +AccountLedger
- `guide/Expenses.md` — removed deleted SEPA statuses + whole SEPA batch section (verified: `ISepaPaymentFileBuilder`/`SepaConfig`/`SepaPaymentFileBuilder` deleted; enum now Draft/Submitted/CoordinatorEndorsed/Approved/Withdrawn)
- `sections/Notifications.md` — +SourceKey row; cleanup invariant (retired sources purged); +`ResolveBySourceKeyAsync`; `DeleteUnresolvedBySourcesAsync` bullet
- `features/notifications/notification-inbox.md` — +SourceKey (nullable, 128); retired-source purge in Cleanup
- `guide/Admin.md` — notification triage replaced stale source enumeration with current meter-based description
- `features/global/background-jobs.md` — `CleanupNotificationsJob` now three passes
- `features/google-integration/google-integration.md` — removed SyncError Admin-notification claims (permanent-failure + dead-letter) → meter + `/Google/SyncOutbox` surface
- `features/profiles/preferred-email.md` — +`IsGoogle`/`GoogleEmailStatus` on UserEmail; per-address (#687)
- `features/profiles/contact-fields.md` — +`IsGoogle`/`GoogleEmailStatus` on UserEmail entity block
- `sections/Auth.md` — removed deleted SEPA auth operations `IncludeInSepaPayout`/`ReopenSepa`
- `features/global/gdpr-export.md` — +`HoldedCreditorAccount` row (HoldedFinanceService contributor)
- `sections/Events.md` — removed deleted route `ToggleCardFavourite`; favourite now JSON API (`POST /api/events/favourites/{eventId}`, no reload)
- `sections/Camps.md` — account-merge re-point bullet rewritten (CampMember re-point, role fold, HasEarlyEntry OR, EE-cache evict); `CampEventsViewComponent` → `EventsCardViewComponent` (concrete name fix)
- `features/camps/camps.md` — US-20.2 favourite → JSON API; US-20.13 +shift-signup count badge
- `guide/Camps.md` — +shift-signup count badge sentence
- `architecture/design-rules.md` — Finance §8 table-ownership map: `holded_creditor_balances`/`holded_payments` (dropped by migration `HoldedLedgerSingleSource`) → `holded_ledger_lines`/`holded_creditor_contacts`
- `sections/Calendar.md`, `sections/Users.md`, `features/calendar/community-calendar.md`, `features/guide/in-app-guide.md` — inbound-ref retargets from pruned husks (see Pruned)
- `sections/Shifts.md` — wheat migration (see Pruned → Wheat migrated)

Clean (dirty by broad trigger, no drift): all 5 E5a profiles-core docs, 4 E4 google docs (minus the one fixed), 12 E6 tangential docs, 7 E7a shifts docs, `guide/Events.md`, `features/tickets/event-participation.md`, and 5 of 6 arch-meta docs (`conventions`, `code-review-rules`, `coding-rules`, `roslyn-analysis`, `seed-data`).

## Pruned (−2,836 lines; cap 5,119)
Deleted 12 aged husks (10 in the main pass + 2 agent-section husks in Phase 7.5) — verified shipped / superseded, wheat absent or already in living docs (or migrated):
- `docs/plans/2026-05-16-cache-migration.md` (308) — all tasks shipped; wheat in design-rules §15i + Events.md
- `docs/plans/2026-05-20-events-bugs-recurrence-and-withdrawn-queue.md` (63) — both bugs fixed (`Event.GetOccurrenceInstants` gate-offset; `ModerationQueueViewModel.WithdrawnCount`)
- `docs/plans/2026-05-20-events-bulk-upload.md` (123) — feature in Events.md routing/invariants
- `docs/plans/2026-05-20-events-host-and-unified-submissions.md` (106) — feature in Events.md + features/26-events.md
- `docs/superpowers/specs/2026-04-20-pr235-cache-collapse-design.md` (261) — self-declared historical; design-rules §15
- `docs/superpowers/specs/2026-04-21-issue-511-user-migration.md` (253) — decision reversed #703, recorded in Users.md
- `docs/superpowers/specs/2026-04-21-community-calendar-design.md` (317) — Calendar.md + features/calendar own it
- `docs/superpowers/specs/2026-04-19-volunteer-coordinator-dashboard-design.md` (309) — mined (see Wheat migrated)
- `docs/superpowers/specs/2026-04-20-user-guide-design.md` (274) — pure impl-plan; guide exists (28 files)
- `docs/superpowers/specs/2026-04-21-in-app-guide-design.md` (349) — role-filtering already in Guide.md

**Wheat migrated:**
- `2026-04-19-volunteer-coordinator-dashboard-design.md §4` → `sections/Shifts.md` — coordinator dashboard metric definitions (filled / ticket-holder / engaged / stale-pending / coordinator-activity-row); verified against `ShiftManagementService.ComputeDashboardOverviewAsync` + `GetStalePendingSignupCountAsync` + `GetEngagedUserIdsForShiftsAsync`

**Inbound-ref retargets:**
- `sections/Calendar.md` — dropped cache-migration plan citation (T-08)
- `sections/Users.md` — dropped issue-511 spec pointer (reversal fact + #703 stay inline)
- `features/calendar/community-calendar.md` — removed "See the design document …" pointer (Out-of-Scope list already covers v2+)
- `features/guide/in-app-guide.md` — retargeted role-filtering authority to `sections/Guide.md` §Invariants/§Actors

**Also pruned (Phase 7.5, after Peter's go):**
- `docs/superpowers/specs/2026-04-20-agent-section-design.md` (339) + `2026-04-20-agent-section-prototype-notes.md` (134) — wheat migrated to `sections/Agent.md` as 5 provenance-tagged note blocks (model+cost rationale, ITPM/Tier1-Tier2 gotcha, route_to_feedback→route_to_issue supersession, GDPR data-sent/NOT-sent boundary, prototype quality baseline) + a community-KB supersession note; `tools/agent-spike/README.md` links retargeted to Agent.md.

**Deferred husks (future sweeps):**
- Budget: 4 `docs/superpowers/plans/*` (2026-05-18 store-summary-aggregates, 05-20 camp-roster-roles, 05-23 dietary-medical-nudge-impl, 05-23 volunteer-tracking-export; 5,872 lines) — would blow the 7% cap.

## Flagged for human review
None — every concrete factual contradiction caused by this changeset was fixed inline.

## Proposed for review
None — all prune candidates analysed this sweep were resolved (migrate / drop / defer-with-reason).

## Questions (delivered to Peter inline — Phase 7.5) — ALL RESOLVED
Peter answered "fix them all"; every item applied this sweep (code-verified):
1. **Holded GDPR note** — RESOLVED: `sections/Holded.md` note split — the Holded client + sync tables own no per-user data, but `HoldedFinanceService` (registered `IUserDataContributor` via `AddHoldedSection`, #1021) contributes the user's `holded_creditor_contacts` binding.
2. **background-jobs.md** — RESOLVED: added `HoldedExpenseOutboxJob` (every minute) + `HoldedSyncJob` (daily 03:00); schedules confirmed in `RecurringJobExtensions.cs`.
3. **gdpr-export.md** — RESOLVED: full re-enumeration of `IUserDataContributor` — count `16 → 21`; added rows Events, Issues, AgentConversations, ExpenseReports, ExpenseAuditLog; ASCII diagram + trigger paths refreshed (TeamEarlyEntry/HoldedCreditorAccount already present).
4. **Issues notification lifecycle** — RESOLVED: `sections/Issues.md` + `features/issues/issues-system.md` now note `IssueSubmitted` notifications use `sourceKey=issue.Id` and are resolved by `ResolveSubmittedNotificationsAsync` on terminal status (verified `IssuesService.cs`).
5. **preferred-email.md** — RESOLVED: `IsNotificationTarget`→`IsPrimary` (DB column note retained), `IsOAuth`/`DisplayOrder` annotated as EF shadow properties; `GoogleEmail` was already correct.
6. **Agent-section wheat** — RESOLVED: 5 wheat note blocks + a community-KB supersession note migrated into `sections/Agent.md` (provenance-tagged); both husks deleted; `agent-spike/README.md` links retargeted. (Notes are HTML comments — preserve rationale without adding rendered narrative to a section doc.)

## Skipped (errors)
None.
