# Freshness Sweep Report

**Date:** 2026-07-14
**Mode:** diff
**Previous anchor:** `c89e1514c` · **New anchor:** `upstream/main` `39a9fa32`
**Worktree base:** `origin/main` `60e089c0`
**Dirty set:** 10 mechanical entries (8 prompt + 2 script) + 55 editorial docs
**Outcome:** 21 editorial docs drift-fixed · 9 mechanical regens updated (+1 clean) · 18 husks pruned (−4,729 lines, within the 5,012 cap) · 13 wheat migrations into 8 living docs · 10 inbound-ref retargets · 0 errors

Range themes (upstream `c89e1514c..39a9fa32`, 50 commits): the new **Gate Admissions** section (#1066 + ~20 hardening commits: PINs disabled → no-PIN override #1075, kiosk "logout" #1084, vendor mirror ledger + 30-day backfill #1080–#1083, admit writes Attended #1081); **TicketTailor transfer automation** (void+reissue #1058, /check_ins sync #1059, gate-checked-in transfer block #1067); **cantina arrival-day feeding** (#1056); **manage past shifts** unified panel (#1055); **FODMAP removal** (#1054); event CSV **Host column** (#1085).

## Updated automatically (mechanical)

- `development-stats.md` — script: +8 daily rows (table now 130 data rows)
- `reforge-history.csv` — script: 119 rows, ok=8 fail=0
- `authorization-inventory.md` — added Gate section (GateController under ScannerAccess, write actions under new **GateAdmit** composite policy, TicketAdminOrAdmin settings/PIN admin, GateVendorBackfillAdminController AdminOnly, supervisor-override shared PIN + GatePinThrottle in §4); +GateAdmit in §3/§5 + §7 note; +ExpensesController FinanceAdminOrAdmin flag call site; removed deleted VolunteerTracking view check; corrected §2/§6 line numbers; +previously-undocumented GovernanceBoardVotingController.Detail RoleChecks.IsAdmin row
- `controller-architecture-audit.md` — full regen, 89 controllers: +GateController, +GateVendorBackfillAdminController, +Debug HttpErrors/Timings/Translations, +EventsModeration Edit/Update, +Finance creditor trio, +Google outbox requeue, +ShiftDashboard PostEventStats, +TeamAdmin LookupTicket; removed Events favourite toggles + Expenses SepaReopen/SepaGenerate; GateController.Decision → Decide? in Rename Summary; date → 2026-07-14
- `architecture/dependency-graph.md` — new Gate section node + 7 eager edges (Gate → TicketQ/EarlyEntry/BurnSettings/ShiftMgmt/Role/User/Audit); linkStyle recomputed (eager 0..284, lazy 285..303); fan-in table refreshed (User 59, Audit 36, Team 28, …)
- `architecture/service-data-access-map.md` — new Gate section (gate_scan_events/gate_settings/gate_staff_pins via `ctx.Set<T>`; **no caching decorator by design**; IUserMerge + GDPR GateScans contributor); Tickets check-in era (#1058/#1059/#1067); +3 Web-layer gate IMemoryCache keys; Cross-Section Analysis item 14: Gate = zero cross-section table access; date → 2026-07-14
- `architecture/data-model.md` — +Gate entity-index row (GateScanEvent/GateSettings/GateStaffPin) + Gate bare-Guid FK edges; **orchestrator fix:** stale FK edge `HoldedTransaction.BudgetCategory` (entity doesn't exist) → `HoldedCategoryMap.BudgetCategoryId` + `HoldedExpenseDoc.BudgetCategoryId`
- `About/Index.cshtml` — 3 version bumps (Anthropic 12.35.1, Google.Apis.Drive.v3 1.75.0.4192, OTel.AspNetCore 1.16.0); none added/removed
- `docs/README.md` — +rows for `features/gate-admissions.md`, `sections/Gate.md`; refreshed ticket-transfer + survey descriptions; all 131 links verified
- Verified clean (dirty but no drift): `guid-reservations.md` (Gate seeds no deterministic GUIDs — GateSettings singleton is created on first save)
- Not triggered: `code-analysis-suppressions`

## Updated (editorial drift fixes — 21 docs)

**Gate/Scanner (5):** `sections/Gate.md` — /Gate route has no claim step since #1075; +VendorCheckInBackfill routes; TryMarkSent ledger + 30-day backfill with original admit time; vendor dead-signal bullet rewritten (sync fixed in #1059; remaining gap is EvaluateAsync keying off Status==CheckedIn, tracked in debt-ledger 2026-07-02); stale End-shift/"Wrong person" affordances removed; +AdminEnrolled on gate_staff_pins; masked-email claim search (#1073) · `sections/Scanner.md` — kiosk account now route-locked to /Gate · `sections/AuditLog.md` — +Gate (GateStaffPinSet/Reset) and ticket-transfer (incl. TicketTransferAutoFailed) AuditActions · `features/gate-admissions.md` — DRAFT → SHIPPED (points to Gate.md as authoritative); gate_settings default corrected to Instant.MinValue fail-safe sentinel; 4 "open decisions" annotated resolved; **freshness markers added (was unmarked)** · `features/scanner/gate-terminal-login.md` — GateLogin redirects to /Gate (not /Scanner/Tickets); route-restriction middleware documented; typed-"logout" kiosk sign-out

**Tickets (6):** `sections/Tickets.md` — +TicketAttendee.CheckedInAt (model + projection); sync pulls+applies /check_ins (write-once); transfer requires CheckedInAt null; participation reads persisted CheckedInAt; cadence "5 min per shipped config (code default 15)"; +Gate (inbound) cross-section bullet · `features/tickets/ticket-transfer.md` — US-42.1: Valid AND not-gate-checked-in (three guard points) · `features/tickets/ticket-vendor-integration.md` — +CheckedInAt; TicketTailor GET+POST /check_ins with checkout netting, void-to-hold, issue-from-hold; job cadence corrected · `features/tickets/event-participation.md` — +CheckedInAt column; Ticketed→Attended trigger cites /check_ins; +GateService admit projection row (#1081) · `guide/Tickets.md` — scanned tickets non-transferable; 5-minutely sync; admin transfer-processing rewritten (Process/Retry/Mark successful/Cancel) · `guide/TicketTransfers.md` — **freshness markers added (was unmarked)**; gate-scanned non-transferable; "by hand" processing claim removed

**Auth/Admin (2):** `sections/Auth.md` — GateLogin redirect → /Gate · `sections/admin-shell.md` — +"Gate settings" sidebar item (TicketAdminOrAdmin). 9 other matched docs verified no-drift.

**Cantina/GDPR/jobs (3):** `features/cantina/daily-roster.md` — on-site = Confirmed-only; arrival-day rule (#1056) across weekly/mini-summary/drill-down/CSVs; feeds ArrivesOn; excluded from NoShift · `features/global/gdpr-export.md` — +GateScans row ({OccurredAt, Verdict, Role, LaneId}, contributor GateService, data-minimized); ProfileService's 6 rows reattributed to UserService per ExpectedContributorTypes (count stays 21); stale trigger paths fixed (Profiles/ProfileService, Users/AccountMergeService, +Surveys/SurveyService) · `features/global/background-jobs.md` — +GateRetentionJob (daily 03:45 UTC, Gate:RetentionDays default 365, ≤0 disables), +GateVendorCheckInJob (on-demand enqueued mirror, Gate:VendorMirrorEnabled default off). 11 other matched docs verified no-drift.

**Shifts (2):** `features/shifts/shift-signup-visibility.md` — past shifts use the unified Manage panel (#1055) · `guide/Shifts.md` — past-shift Manage panel; remove-confirm names the human (#1052). FODMAP already cleaned in the feature PRs; 5 other matched docs no-drift.

**Events/Survey/Budget/Expenses/Store/CityPlanning/Guide (0):** all 13 matched docs verified no-drift (Host CSV column contradicts nothing; survey.md shipped current with LoggedInSince #894; rest cosmetic).

**Architecture (3):** `design-rules.md` — +Gate row in §8 table-ownership map; §15i repo count 34→35 (+GateRepository) · `conventions.md` — +Gate/Index.cshtml in client-side fetch() exceptions table · `roslyn-analysis.md` — boundary-scan count 7→5 (pre-anchor drift, fixed as verified contradiction). `coding-rules.md`, `code-review-rules.md`: no contradictions.

## Pruned — 18 husks, −4,729 lines (oldest-first slice: all eligible specs 2026-04-26 → 2026-05-08)

### Wheat migrated (13 blocks, each code-verified)

| Source §section → Destination | Verified by |
|---|---|
| containers-design §Decisions → `sections/Containers.md` (org containers = real camp, no sentinel) | no SystemCampIds in src; Container.CampId non-nullable |
| container-placement-map §GeoJSON → `sections/Containers.md` (footprint+door-triangle contract, rotation in properties) | geometry.js TRIANGLE_TIP_INDEX=4; CityPlanningApiController checks |
| container-placement-map §Out of scope → `sections/CityPlanning.md` (no SignalR/MapboxDraw on container map) | signalr.js only under barrio-map/ |
| issue-489-camp-roles §D5 → `sections/Camps.md` (no SlotIndex; over-capacity rendered, never evicted) | CampRolesPanelData.OverCapacity |
| account-merge-fold §Conflict rules → `sections/Users.md` (EventParticipation status precedence on fold) | UserRepository.ReassignEventParticipationToUserAsync |
| account-merge-fold §Transaction model → `architecture/conventions.md` (ambient TransactionScope + Npgsql auto-enlist) | AccountMergeService.cs:226-229 |
| email-oauth-pr4 §Service changes → `sections/Profiles.md` (Unlink/Delete disjoint sets; login-removal-first; last-verified guard) | UserEmailService.cs:290, 844-900 |
| email-oauth-pr4 §Non-goals → `sections/Profiles.md` (no admin OAuth link — self-only) | ProfileController route inventory |
| email-oauth-pr4 §Architecture → `sections/Profiles.md` (no-"OAuth"-token naming rule + one exception) | UserArchitectureTests |
| issues-section §10 → `sections/Issues.md` (thread = comments + audit merged at read time; no event schema) | IssuesService.cs:253-298 |
| holded-read-integration §API findings → `sections/Finance.md` (full-pull forced: date filters key on null accountingDate) | HoldedClient.ListPurchaseDocumentsPageAsync |
| store-section §Spanish invoicing → `sections/Store.md` (VAT regardless of country; deposits VAT-free) | BalanceCalculator.Compute:67-68 |
| store-section §Invoice issuance → `sections/Store.md` (camp-name fallback; contact match key; thin-probe caveat still live) | StoreService Phase 5 throw; HoldedClient TODO(probe) |

### Husks deleted

2026-04-26: containers-design (202), container-placement-map (261), container-placement-phase (96), admin-shell-left-nav (310), camp-lead-retirement-followup (41), issue-489-camp-roles-reimpl (351), holded-read-integration (357) · 04-27: email-and-oauth-decoupling (419) · 04-29: issues-section (307) · 04-30: account-merge-fold (255), email-oauth-pr4-grid (298), store-section (351) · 05-04: shift-range-signup (319) · 05-05: email-problems-page (154), low-friction-shift-signup (270) · 05-07: persistent-multi-measurements (149), volunteer-tracking (422) · 05-08: container-placement-notes (167). Per-doc all-chaff/superseded evidence is in the prune manifests (drop reasons verified against Shifts.md/Onboarding.md/Profiles.md/Store.md/Finance.md/features/47-volunteer-tracking.md and current code).

### Inbound refs fixed (10)

budget.md V2c → sections/Finance.md · store.md ×2 spec links removed (Store.md already linked) · sections/Store.md status line → features/store/store.md · holded-finance-feature1-actuals plan ×3 → historical · holded-finance-integration spec ×2 → historical · store-summary-aggregates spec → historical

### Not pruned (budget decisions, not punts)

- `architecture/tech-debt-2026-04-23.md` — NOT eligible: ~16 items still OPEN
- `superpowers/specs/2026-04-25-freshness-sweep-design.md` — active spec (referenced from CLAUDE.md + skill), excluded
- Future-sweep slice: specs 2026-05-09 → 05-14 (~2,439 lines), docs/plans q3 files (06-11, 06-13), superpowers/plans ≤ 06-13 (~22 files)

## Flagged for human review (informational — no decision required)

- `tests/e2e/tests/admin-shell.spec.ts` — sidebarMatrix doesn't pin the new "Gate settings"/"Vendor check-in backfill" items though admin-shell.md says the per-role table is pinned by this spec (presence-style assertions keep it green).
- Stale **code comments** (out of doc-sweep scope): `IGateService.SetOwnPinAsync` XML doc contradicts #1074 (everyone may self-enrol a claim PIN); `AccountController.GateLogin` comment still describes the removed claim-screen redirect (#1075).
- `features/cantina/daily-roster.md` — pre-anchor internal contradictions left in place (access-path OR-composition vs "no team-name heuristic"; "out of scope: per-day drill-down" vs routes listing /Cantina/Roster/Day).
- `features/shifts/shift-signup-visibility.md` — pre-anchor "navigation to User" claim (navs stripped in #541) left as original-spec framing.
- `features/global/background-jobs.md` — pre-anchor catalog gaps: missing CleanupIssuesJob, AgentConversationRetentionJob, MailerAudienceSyncJob; SendAdminDailyDigestJob/SendBoardDailyDigestJob in table but not found in RecurringJobExtensions.cs (not verified).
- Gate.md documents the temp vendor check-in backfill page (marked "remove after use" in code) — when the page is deleted, its two route rows + the ledger/backfill paragraph should come out.

## Proposed for review

None — all candidates resolved this sweep (every wheat migration verified against code; no uncertain item queued).

## Questions (delivered inline, Phase 7.5 — all resolved by Peter 2026-07-14)

1. Gate.md disabled personal-PIN flow → **shrink**. Applied: bullet reduced to a tombstone (table/services remain; design retained in git history, peterdrier#1071/#1073/#1074).
2. background-jobs.md GateVendorCheckInJob "On demand (enqueued)" row → **keep**. No change.
3. Tickets sync cadence → **match actual**. Applied: sections/Tickets.md + ticket-vendor-integration.md now say "every 5 min via `TicketVendor:SyncIntervalMinutes`" (code-default hedge dropped).
4. Orphaned Gate actions vs "Orphaned Pages" rule → **plan to delete** the dead actions. Filed nobodies-collective/Humans#933; Gate.md unreachable-routes note now references it. Rule untouched.
5. Unmarked editorial docs → **add triggers**. Applied: freshness:triggers + flag-on-change markers added to all 11 docs.
6. AuditLog.md catalog → **yes, auto**. Applied: catalog regenerated to the full AuditAction enum and wrapped in a `freshness:auto id="auditaction-catalog"` block.

## Skipped (errors)

None — all 24 subagents returned; both scripts ran clean; `dotnet build` 0 errors.
