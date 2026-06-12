# Freshness sweep report

**Run:** 2026-06-12 (UTC)
**Mode:** diff
**Previous anchor:** `523a44c3e`
**New anchor:** `upstream/main` @ `afa6ac5cc`
**Worktree base:** `origin/main` @ `5d0fa66ac`
**Dirty:** 11/11 mechanical entries, ~75 editorial docs (one promotion batch #863: Store async-payment state machine #949/#982, Events per-occurrence favourites #983, admin-shell nav regroup #960, ~12 section simplification passes, CSV→CsvHelper #959, post-event stats #952, scanner door context #940, Google-sync alerting/requeue #948/#944, buyer-fallback ownership retirement #953).
**Note:** upstream/main and origin/main had crossed at sweep start (upstream HEAD is the #863 promotion merge; origin carried 2 newer commits #985/#986). Expected post-promotion state; those 2 commits are the next sweep's input.
**Entries:** 43 updated (8 mechanical + 35 editorial docs) · 3 verified current · 8 husks pruned (−4,027 lines)

## Updated automatically

- `dev-stats` — +2 daily rows
- `reforge-history` — +2 daily rows (105 days)
- `code-analysis-suppressions` — removed stale xUnit1051 suppression (no longer in tests/Directory.Build.props)
- `authorization-inventory` — ExpensesController.SepaReopen (FinanceAdminOrAdmin + ExpenseReportOperation.ReopenSepa); ExpenseReportAuthorizationHandler operation list; ToggleCampFavourite→ToggleCardFavourite rename missed by the 2026-06-10 sweep
- `controller-architecture-audit` — ToggleCardFavourite row + corrected route; SepaReopen row; date bump
- `dependency-graph` — ICityPlanningServiceRead added to read-split list; CityPlanning read-boundary note; IGoogleSyncServiceRead fan-in clarification
- `service-data-access-map` — CampContactService stale cross-section claims removed; ICityPlanningServiceRead in read-split list; Store async-payment flow (SEPA transitions + pending-payment double-charge guard)
- `docs-readme-index` — post-event-stats.md indexed

Verified current, no edits: `data-model-index`, `guid-reservations`, `about-page-packages`.

## Updated (editorial drift-fix, 35 docs)

- **Store/Scanner:** sections/Store.md + features/store/store.md + guide/Store.md — deadline gate moved from service-throw to StoreOrderAuthorizationHandler/StoreOrderLineContext (admins exempt); pending-payment double-charge guard + RecordedPending status user-visible notes. sections/Scanner.md — door context (#940): EE grants, check-in, consents, provide list + new cross-section deps.
- **Events/system:** sections/Events.md — EventFavourite.DayOffset + unique-constraint prose; profile card personal-events-only. guide/Events.md — per-occurrence favouriting sentence. sections/AuditLog.md — StorePaymentSettled/Failed/Expired. sections/Debug.md — /Debug/Timings + minLevel filter on /Debug/Logs.
- **Shifts:** sections/Shifts.md — GetPostEventStatsAsync under ShiftDashboardAccess. features/shifts/post-event-stats.md + shift-management.md — new dashboard route/DTO/auth. features/shifts/workload-dashboard.md — inlined SortForDisplay reference removed.
- **Users/Admin/Campaigns:** guide/Admin.md — nav regroup (Money group, Campaigns→Tickets, system zone), googlerejected filter, sync-outbox requeue + per-user re-run. features/global/administration.md — 3 new GoogleController POST routes. features/campaigns/campaigns.md — stale unsubscribe description replaced with current two-path token behavior.
- **Google/Email:** sections/GoogleIntegration.md + features/google-integration/google-integration.md + features/global/background-jobs.md (recovered from died worker; verified). guide/GoogleIntegration.md — requeue/Re-run Sync admin actions.
- **Profiles:** sections/Profiles.md — coordinator Sent-Messages panel (AuditAction.FacilitatedMessageSent via IAuditViewerService, limit 50).
- **Governance/Teams:** sections/Governance.md, guide/Governance.md, features/governance/asociado-applications.md + board-voting.md (recovered from died worker; verified). guide/Teams.md + features/teams/teams.md — Early Entry search by ticket barcode (#936).
- **Misc sections:** sections/Auth.md — Phase-2 handler list (Container, ExpenseReport+ReopenSepa, IbanAccess, StoreOrder/OrderableUntil). sections/CityPlanning.md — ICityPlanningServiceRead read-split, CampPolygonSaveResult, upload pipeline in service, GetSettingsByYearAsync removal. features/city-planning/city-planning.md + sections/Containers.md — ICityPlanningServiceRead refs. sections/Expenses.md + guide/Expenses.md — ReopenSepa route/actor/trigger + user-facing description. sections/Notifications.md + features/notifications/notification-inbox.md — badge-count caching now inside NotificationInboxService (#954).
- **Architecture:** design-rules.md — §15i Notifications badge caching corrected to in-service (2-min TTL).

## Pruned (Phase 5.5) — 8 husks, 4,027 lines

Sizing: 82,242 total doc lines → 5% target 4,112, 7% cap 5,757. Pruned 4,027 — within target.

**Wheat migrated:** none — every wheat candidate across all 8 docs was verified already captured (and stated more richly) in the living docs. Verification spot-cites: AuditLog append-only triggers → `docs/sections/AuditLog.md` + `AuditLogRepository.cs`; Shifts Stryker MTP-runner bug → `docs/testing/mutation-testing.md:81`; Tickets `/Welcome` route exception → `docs/sections/Tickets.md:134`; ticket-transfer manual-processing model → `docs/features/tickets/ticket-transfer.md` (explicitly records the removed void+reissue engine; zero `TicketTransferVendorStepKind`/`RetryIssue`/`VoidIssuedTicket` refs remain in src/).

| Husk | Lines | Reason |
|---|---|---|
| docs/plans/2026-05-12-section-align-auditLog.md | 284 | Executed plan; all durable items in sections/AuditLog.md |
| docs/plans/2026-05-12-section-align-camps.md | 107 | Executed; CampMemberConfiguration move recorded in Camps.md |
| docs/plans/2026-05-12-section-align-google-integration.md | 369 | Executed in PR #500; follow-ups tracked in sections/GoogleIntegration.md |
| docs/plans/2026-05-12-section-align-governance.md | 80 | Executed; routing/DI-cycle rationale in sections/Governance.md |
| docs/plans/2026-05-12-section-align-scanner.md | 312 | Executed; client-only invariants in sections/Scanner.md |
| docs/plans/2026-05-12-section-align-shifts.md | 401 | Executed; run recorded in maintenance-log; Stryker gotcha in mutation-testing.md |
| docs/plans/2026-05-12-section-align-tickets.md | 160 | Executed; /Welcome exception in sections/Tickets.md |
| docs/superpowers/plans/2026-05-12-ticket-transfer-ui-history.md | 2,314 | Shipped-then-reversed: automated void+reissue engine deliberately removed; survivors documented in ticket-transfer.md + Tickets.md |

**Refs retargeted:**
- docs/plans/2026-05-13-section-align-teams.md:139 — auditLog plan link → "(historical — invariants live in docs/sections/AuditLog.md)"
- docs/plans/2026-05-13-section-align-teams.md:142 — scanner plan link → "(historical — invariants live in docs/sections/Scanner.md)"
- docs/architecture/maintenance-log.md:33 — left untouched by design (sweep never edits maintenance-log; repo convention keeps dead plan paths as git-historical provenance)

## Flagged for human review

Delivered inline at sweep end (Phase 7.5). Peter answered **"fix all"** — dispositions:

1. sections/AuditLog.md Expenses/IBAN AuditAction group — **FIXED**: added the 15-value Expenses/IBAN bullet (incl. ExpenseSepaReopened, IbanReveal note) sourced from AuditAction.cs:143–157.
2. features/cantina/daily-roster.md "no per-day route" — **FIXED**: replaced with /Cantina/Roster/Day + /Day/Csv rows (default = today's offset within the active event, per CantinaController.ComputeDefaultDayOffsetAsync).
3. features/budget/budget.md four-categories claim — **FIXED**: now "two auto-created categories: Ticket Revenue and Processing Fees" with a note that VAT Liability/Donations were never scaffolded.
4. design-rules.md §8 Event Guide table row — **FIXED**: replaced with the 7 real ToTable names verified against HumansDbContextModelSnapshot.cs.
5. code-review-rules.md timeout claim — **FIXED**: now "30s for HumansFact; 5s for HumansTheory, raised to 30s in Humans.Integration.Tests" (per HumansFactAttribute.DefaultTimeout=30000 and HumansTheoryAttribute.DefaultTimeoutFor). Note: the original flag had the Theory split backwards; fix written from code.
6. coordinator-roles.md sent-message panel — **NO EDIT (premise wrong)**: the panel gates on rota-team coordinatorship or PrivilegedSignupApprover (= Admin/NoInfoAdmin per AuthorizationPolicyExtensions.cs:134), NOT the VolunteerCoordinator governance role this doc covers. Already correctly documented in sections/Profiles.md; adding it here would have introduced drift.
7. features/profiles/preferred-email.md — **FIXED**: US-11.6 now notes admins can apply the change sooner via Re-run Google Sync or sync-outbox requeue.
8. features/profiles/profile-search-detail.md — **FIXED**: added "Extension: ticket-number lookup" subsection documenting HumanSearchPickerViewModel.TicketLookupUrl (Early Entry card, LookupTicket endpoint), cross-referenced to the spec.

Unmarked editorial (no freshness:triggers; add markers so future sweeps can scope them):
docs/sections/Agent.md, docs/sections/Mailer.md, docs/sections/_Index.md, docs/features/26-events.md, docs/features/27-guide-browser.md, docs/features/43-google-group-membership-sync.md, docs/features/test-system-reliability.md, docs/features/user-search-overhaul.md, docs/features/agent/agent-section.md, docs/features/scanner/gate-terminal-login.md, docs/features/scanner/scanner-barcode.md, docs/guide/AiHelper.md, docs/guide/EmailAccount.md, docs/guide/SigningIn.md, docs/guide/TicketTransfers.md, docs/guide/TwoStepVerification.md, docs/guide/YourData.md

Informational (no action needed):
- guide/CityPlanning.md says "/CityPlanning/Admin"; actual route /CityPlanning/BarrioMap/Admin — guide uses the page label, pre-existing, left as-is.
- HoldedClient ctor now takes ILogger — DI detail, not documented anywhere.
- admin-shell.md was already accurate for the #960 nav regroup (updated in the same PR).

## Proposed for review

None — all candidates resolved this sweep ("fix all", applied above).

## Skipped (errors)

None. (Three background workers died mid-run from a harness host restart; all were re-dispatched and completed — no entry was lost.)
