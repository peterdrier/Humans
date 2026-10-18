# Freshness sweep — 2026-10-18

**Mode:** diff
**Previous anchor:** `d6e4accec`
**New anchor:** `25a06a1c4` (upstream/main)
**Worktree base:** `origin/main` @ `25a06a1c4` (frozen at start; equal to upstream/main)
**Diff window:** 3046 changed files, 176 commits

**Counts:** 9 of 9 mechanical entries dirty. 158 marked editorial docs dirty, all reviewed;
76 editorial docs changed. All 9 verifiers in `docs/scripts/freshness-checks/run-all.sh` pass, and
`diff-mode.sh` passes 17/17.

## Updated automatically

- `reforge-history`: 22 rows appended (180 rows, 180 distinct days).
- `dev-stats`: 22 rows appended (192 rows). Class/interface counts come from reforge for all 22 days.
- `about-page-packages`: version syncs for Anthropic 12.55.0, MailKit 4.18.1, Admin.Directory,
  CloudIdentity, Drive.v3, Google.Apis.Auth 1.77.0, Magick.NET 15.0.0, HtmlSanitizer and AngleSharp
  1.8.4. The Pkcs/Xml security pins are transitive (no production csproj reference), so they get no card.
- `docs-readme-index`: added the `preapprovals` and `workgroup-budget` global feature specs.
- `authorization-inventory`: new Preapprovals map (both controllers, Manage/Access policies, two
  handlers). Teams has an anonymous `/Teams/Tree` row. The Store handler lists RecordPayment, Refund
  and DeletePayment. Expenses Withdraw goes through the handler. Global file: the view map was rebuilt
  for the admin nav redesign, `AuthorizeAsync` lines were refreshed, `PrivateUploadMiddleware` was
  added, and Preapprovals was added to §3–§6.
- `dependency-graph`: 350 eager + 19 lazy cross-section edges (was 300 + 19); `linkStyle` recounted.
  Added 10 nodes (six new services, plus AgentService, AgentSettingsService, CalendarFeedTokenService,
  SyncSettingsService and UserNameSyncService). EventBulkImporter is in the no-edge roster. Dropped
  stale edges: GDriveAccess→Audit, GSyncSvc→UEmail, GovIndex→User, UEmail lazy→TicketQ.
- `service-data-access-map`: new Preapprovals map. New headings: EventBulkImporter,
  ExpenseHoldedPublisher, StoreInvoiceIssuer, StoreOrderReader. Drift fixed in 13 more maps:
  `I*Write`/`IPreapproval*`/`IAuditLogService` deps, repository method names, real `store_*` table
  names. Rollup: Store/Finance contexts and the TrackedCache path.
- `guid-reservations`, `code-analysis-suppressions`: checked against source, no change.
- `AuditLog.md` `freshness:auto` block: 29 missing `AuditAction` values added (Preapprovals ×17,
  LegalDocument ×5, HoldedExpenseAccount ×3, four exports, SurveyResponsesExported).

## Verifier fixes (sweep-owned)

- `lib-service-classes.sh` now strips `//` comments before flattening a declaration. A `;` inside a
  comment in Finance's `Service` constructor parameter list cut the declaration short, so both the
  `dependency-graph` and `service-data-access-map` checks skipped it without saying so. The service
  population went from 109 to 110 and from 121 to 122. The graph node is now labelled
  `HoldedFinanceManagement`, matching the verifier's name.
- Known blind spot, not fixed: a class whose only contract carries no marker (e.g. `UserService` via
  `IUserService`) is not counted by either check.

## Fixed in place (editorial drift)

- **Tickets/Store:**
  - Tickets: participation writes go through `IUserWrite`; the roster role lookup is Auth;
    off-season the dashboard ticket card is hidden.
  - Merge fan-out is `AccountMergeService.MergeAsync` (was `FoldAsync`; same fix in Consent).
  - The accountant Orders/Attendees CSVs are audited.
  - `IStripeService` is no longer read-only.
  - Store: admin nav is Store › Payments / Summary, and RecordPayment/Refund/DeletePayment exist.
  - Guides: the off-season ticket card, the accountant income card, department orders, deposit
    return and refund, and the zero-balance invoice rule.
- **Governance:** real application form limits (motivation 50–2000, extra info 1000, Asociado
  answers 2000). `PartitionUsersAsync` no longer lists the deleted AdminDashboardService.
- **Finance/Expenses/Holded/Budget/Preapprovals:**
  - SEPA steps: the bank line is read via Holded and drives booking. Text-matched lines book
    unattended; amount-matched lines need a click.
  - `ISepaBankBooking` is in the Finance contract table.
  - Dependency lists trued: `IHoldedFinanceWrite`/`IHoldedFinanceService`,
    `IUserWrite.SetProfileIbanAsync`, `GetYearByIdAsync` consumers.
  - The preapprovals spec now says what is built: public `IPreapprovalService`/`IPreapprovalWrite`,
    consumed by Expenses.
  - guide/Expenses: subject is required, the account is picked on the form, reports file against
    pre-approvals, lines can be declined one at a time, and rejected drafts can be edited.
- **Users/Gdpr/Teams:**
  - Uploads accept HEIC/HEIF/AVIF; the coordinator popover links `/Teams/{slug}`.
  - Profile field limits fixed; the preferred Google address is the `UserEmail` row flagged `IsGoogle`.
  - Communication preferences are email-only.
  - The gdpr-export table matches the contributors.
  - Teams: the public `/Teams/Tree` page (US-6.14); no directory pagination.
- **Events/Camps/Shifts:**
  - Barrio events moved to `EventsController` (`/Events/Barrio/{slug}/*`); `GuideModerator` is now
    `EventsAdmin`.
  - Shifts uses `INotificationEmitter`.
  - ShiftDashboard: `ShiftDepartmentManager` at class level, `ShiftDashboardAccess` on three actions.
  - Dashboard cache keys carry a `:{period|all}` suffix. Off season the shift cards show "Season wrapped".
  - Post-event department sort is ordinal; a pie click is a GET.
- **Auth/Comms/Google:**
  - Auth: per-section policy registration; magic links use a SHA-256 replay key and a 60 s send
    reservation per user.
  - `OutboxRetentionDays` applies to sent messages only.
  - Notification sources gained Preapprovals and five missing inbox rows.
  - `ThingsToDoViewComponent` is in Base.
  - Provision Email is HumanAdmin or Admin.
  - The provisioning prefix is validated; removal emails fall back to `en`.
- **Small sections/global:**
  - Scanner has its own admin group; Gate/Debug read the active event from Settings.
  - Agent: `POST /Issues`.
  - Tour: `TourCardViewComponent`.
  - background-jobs gained `SepaBankBookingJob` and feedback-reply-emails.
  - global-search, active-user-metrics, section-activation and administration were trued to code.
  - admin-shell gained the Pre-approvals group and the sign-in method card.
  - guide/Admin: sync mode lives at `/Settings#google-sync`.
  - expires-on-deadline: `Users.Contracts` is referenced by Base, so it cannot reference Base back.
- **Architecture:**
  - AGENTS.md: the cross-section test gate is `tests/Humans.Web.Tests`;
    `Humans.Application.Tests` no longer exists.
  - design-rules §1: surface is published from `Contracts/`, with a `.Contracts` leaf only to break
    a cycle (11 remain). §8 Finance gained `holded_managed_accounts`.
  - conventions: pill counts come via `AdminNavItems`.
  - roslyn-analysis: live rules are HUM0030–31 and HUM0033–37; HUM0032 is retired; next free ID is
    HUM0038.
  - seed-data: dev seed nav entry.

Verified clean, no edits: 82 docs, including TicketTailor, Stripe, Surveys, most Governance, Rideshare,
Cantina, CityPlanning, Campaigns, MailerLite, Onboarding, Monitor, Issues, Feedback, Backdoor,
Settings, Search, EarlyEntry, Containers, code-review-rules and coding-rules.

## Dead trigger globs

### Repaired
None.

### Unresolved
None.

## Pruned

2992 lines removed. The target was 2487 (5% of 49750) and the cap 3482 (7%).

- `docs/plans/2026-08-03-g0-first-audit/` (1802): 38 per-section scorecards with gap lists, all
  chaff. Its durable notes are already in the section docs (Email ExtraHeaders, Debug DbVersion,
  Search GUID path).
- `docs/superpowers/plans/2026-08-12-burn-demo-pages.md` (894): an implementation task list. Tour's
  invariants live in Tour.md.
- `docs/superpowers/specs/2026-08-12-burn-demo-pages-design.md` (65): its decisions are already Tour.md
  invariants.
- `docs/plans/2026-09-16-production-readiness.md` (112): a release ledger. The R01–R14 invariants are
  already in the section docs.
- `docs/superpowers/specs/2026-07-30-faq-proposals-production.md` (119): a one-off triage output. Its
  "no GUID fast-path" claim is no longer true.

### Wheat migrated
- production-readiness §Promotion rehearsal → `Email.md`: transport selection keys on
  `Email:SmtpHost`, which `appsettings.json` defaults, so clearing credentials still sends real mail.
  Verified in `Humans.Email/Section.cs`. Email.md's "stub in dev/test" line was corrected to match.
- faq-proposals §How this run differed → `Agent.md`: `RefusalReason` is written only for rate-limit,
  abuse and error turns, so it is not a knowledge-gap signal. Verified in
  `AgentService.PersistRefusal` and `AppendFailureMessage`.

### Retargeted refs
- `Tour.md`: the spec citation was replaced with its inline rationale.
- `2026-08-07-g5-section-project-split-design.md` (×2) and `docs/health/runs/2026-08-28-Settings.md`:
  rewritten as historical, pointing at the living section docs.

### Not pruned
- `2026-08-10-pleo-connector-design.md`: an unimplemented draft awaiting sign-off, not a husk.
- `docs/plans/2026-08-07-fk-cut-inventory.md`: still cited by `CampService.cs:907`.
- Future candidates (budget): `2026-07-15-per-section-dbcontext-design.md` (666), the G5 specs
  (1441 + 236 + 89 + 131), and `2026-09-25-workgroup-budget.md` (2031, ages out on 2026-10-25).

## Unmarked editorial (add `freshness:triggers`)

- `Humans.Finance/Docs/features/sepa-payout.md`, `Humans.Notifications/Docs/features/notification-api.md`,
  `Humans.Backdoor/Docs/features/finance-read-api.md`, and six Surveys feature docs:
  grid-questions, ranked-choice-voting, survey-information-blocks, survey-intro-markdown,
  survey-invitation-email-copy, survey-preview.

## Flagged for human review

Out-of-scope code/memory drift found while reviewing (not edited by the sweep):
- `memory/process/scoped-inner-loop-tests.md` still names the removed `tests/Humans.Application.Tests`.
- `tests/e2e/tests/admin-shell.spec.ts`: `ALL_GROUP_LABELS` and the Board `sidebarMatrix` lack the
  Pre-approvals group.
- `Humans.Gdpr/Contracts/IGdprService.cs` XML doc: it says contributors get "the id asked with", but
  the orchestrator passes the resolved survivor id.
- `Camp/Index.cshtml:24` calls `RoleChecks.IsActiveMember` in a view. The authorization inventory
  lists it as the one exception.
- `docs/sections/G5-SECTION-TEMPLATE.md` (ignore-listed) still mentions a `*ServiceRead` interface.

## Proposed for review

None. All candidates were resolved this sweep.

## Questions

Unattended run; these await Peter.

1. design-rules §1 lists AccessMatrix/Human/HumanSearch/TempDataAlerts as Base view components.
   Should it also list AdminBreadcrumb, ChromeSlot, SectionNav, ThingsToDo and UserParts?
2. `expense-reimbursement-process.md` has a "Status (2026-09-06)" section. Re-date it or drop it?

## Skipped (errors)

None.
