# Freshness sweep — 2026-09-22

**Mode:** diff
**Previous anchor:** `78ee98869`
**New anchor:** `d6e4accec` (upstream/main)
**Worktree base:** `origin/main` @ `ffe89e233` (frozen at start; upstream/main is an ancestor)
**Diff window:** 3029 changed files, 166 commits

**Counts:** 9 of 9 mechanical entries dirty. 153 of 154 marked editorial docs dirty (only
`docs/features/global/active-user-metrics.md` clean), every one with code-file hits. All reviewed;
50 editorial docs changed. All 9 verifiers in `docs/scripts/freshness-checks/run-all.sh` pass, and
`diff-mode.sh` passes 8/8.

## Updated automatically

- `reforge-history`: 13 rows appended (158 rows, 158 distinct days).
- `dev-stats`: 14 rows appended (170 rows). Class/interface counts come from reforge for 13 days;
  1 day fell back to the regex count.
- `about-page-packages`: version syncs for Anthropic, Hangfire ×2, MailKit, three Google APIs,
  Magick.NET and Markdig; added the missing AngleSharp card.
- `docs-readme-index`: added the Issues guide row; feature specs sepa-payout, assembly-votes,
  notification-api, notification-board, rideshare-board and ranked-choice-voting; section rows for
  Settings and Workgroups.
- `authorization-inventory`: added `CityPlanningMapAdminHandler`; Surveys now shows `AppAccess` plus
  a per-survey handler table; fixed stale rows (Expenses Review, Tickets ExportAccountantReport,
  Shifts Settings POST-only, Google SyncSettings, AuditLog Resource/Human → GoogleController).
  Global file: per-section `SectionPolicies` registration; composite handlers moved to their owning
  sections; `AuthorizeAsync` call-site table rebuilt; `MembershipRequiredFilter`/`NameRequiredFilter`
  rows cover the Profile exemptions and the Deleted/Merged wall.
- `dependency-graph`: regenerated (300 eager + 19 lazy cross-section edges; `linkStyle` recounted).
  Removed the Dashboard/AdminDashboard nodes (those services are gone). Added Workgroup, TeamMsgOpts,
  GDriveAccess, ComposerSelfSend, Gdpr, Stripe, TicketTailor and MailerLiteGdpr. The Email hub is now
  OutboxEmailService and Store is now StoreAccountingRead. The no-edge roster, cycles and fan-in were
  rebuilt.
- `service-data-access-map`: 12 per-section maps plus the rollup. New headings: StoreAccountingRead,
  TeamMessageOptionsProvider, GuideRoleResolver, ShiftsEmails/Previews. Constructor drift fixed
  (AccountDeletionService → `IGdprService`, AssemblyVoteService, TeamService, CampService,
  ConsentService, EventService, EmailProvisioningService, TeamResourceService,
  RoleAssignmentService). DbContext table updated: calendar feed tokens, email daily counts, SEPA,
  GoogleSyncLog, assembly tables, and the MailerLite and Workgroups contexts.
- `guid-reservations`: checked against source; no change (no new seed GUIDs).
- `code-analysis-suppressions`: removed `HUM_USER_NORMALIZEDEMAIL`, which moved from `NoWarn` to
  `WarningsNotAsErrors`.
- `AuditLog.md` `freshness:auto` block: added `DuplicateAccountFlagged` and `EventSettingsUpdated`.

## Verifier fixes (sweep-owned)

- `lib-service-classes.sh` used the gawk-only `asorti`. Under mawk (the Debian/Ubuntu default) the
  awk pass aborted, so the `dependency-graph` and `service-data-access-map` checks enumerated
  **zero** services and passed vacuously. It now uses a portable sort, and both callers fail when
  zero services are found. Once fixed, the checks exposed 5 missing graph nodes and 2 missing
  data-access headings, all now fixed.
- `generate-stats.sh` referenced the Windows-only `$USERNAME` under `set -u`, so on a machine
  without cloc it crashed instead of printing its "cloc not found" hint. It is now guarded.

## Fixed in place (editorial drift)

- **Governance:** the tie-break actor is the President of the Board (Art. 10.2), not "the chair".
  asociado-applications, board-voting and membership-tiers now describe the Volunteer gate as name +
  required consents (`SystemTeamSyncJob`), with Consent Coordinator review as an audit annotation.
- **Users:** Users.md had `IUserService`/`IUserServiceInternal` misattributions, the
  `FindByAddressAsync` rename, admin purge not yet described as Gdpr's `EraseForUserAsync` fan-out,
  and `Reassign*Async` listed on the wrong type (they are repository methods). All fixed.
  contact-fields: the emails page sorts alphabetically. dietary-medical-nudge: now uses the
  `ISectionThingsToDo` contributors.
- **Shifts:** email-a-rota now describes the shared Markdown `_EmailComposer`. shift-management and
  guide/Shifts: event calendar config is at `/Settings#event` and the Shifts knobs are at
  `/Settings#shifts` (`/Shifts/Settings` retired). 47-volunteer-tracking: clearing a missing day-off
  now shows an info toast.
- **Google/Monitor:** `/Google/SyncSettings` → `/Settings#google-sync`; added the
  `CreateSubfolderAsync`/`RequestSyncAsync` entry points; the credentials email also exists in `ca`.
  The drive-activity lookback is keyed off `DriveActivityMonitor:LastRunAt`.
- **Auth/Onboarding:** Profile is not wholesale-exempt from `MembershipRequiredFilter`; only
  specific Profile/ProfileEmails actions are. Added the Deleted/Merged wall.
- **Email/Notifications/Campaigns:**
  - email-outbox: fixed the backfill route and dropped three OTel counters that no longer exist.
  - Notifications: added the Workgroup/Rideshare/Assembly sources and the new inbound dependencies.
  - Campaigns: "Profiles section" → Users; `CreatedByUserId`/`UserId` are bare Guids with no FK.
- **Finance/Expenses/Holded:**
  - Finance: owed-only Creditors filter.
  - Expenses: contacts are created through Finance's `EnsureCreditorContactAsync`, which never PUTs
    to a linked contact.
  - guide/Expenses: Finance Admin can file, edit and set an IBAN on a member's behalf.
  - Holded-connector: ledger-entries `end_date` is exclusive.
- **Camps/CityPlanning:** removed "set public year" (the year now resolves from Settings); placement
  toggle, dates and registration info live on `/Settings#city-planning`.
- **Agent:** US-40.3 now points at the `/Settings#agent` tab.
- **Feedback/Issues:** message and comment content is Markdown, rendered sanitized; Feedback's
  `ErasureDeclaration` added. Development.md: the seeder's `IEventSettingsSeeding` /
  `SetShiftBrowsingOpenAsync` calls. AuditLog: its own `ErasureDeclaration`.
- **Global:**
  - administration: workgroups tile; city-planning tab policy is `CityPlanningMapAdmin`.
  - background-jobs: added `AssemblyVoteLapseJob` and `WorkgroupRhythmJob`; deletion job now
    described through the Article 17 fan-out.
  - global-search: camp season-status 404 gate.
  - section-activation: now cites `IUserServiceRead`.
  - admin-shell: sidebar items updated.
- **Architecture:**
  - design-rules: wrong counts removed; added Workgroups, the new fanout/seam rows, auth handlers,
    caching decorators and new tables; fixed the `settings_event` readers/writes paragraph.
  - conventions: two `fetch()` exceptions added.
  - roslyn-analysis: wrong counts and one retired architecture test removed.

Verified clean, no edits: Tickets/TicketTailor/Store/Stripe (11), Events/Calendar/Rideshare/Cantina
(11), Gate/Scanner/EarlyEntry/Guide/Tour/Search, Teams/Workgroups/Containers, Consent, Budget,
Gdpr/gdpr-export, Debug/Backdoor/Settings/Surveys, AGENTS.md, code-review-rules, coding-rules,
seed-data. Much of this window's drift had already been fixed by the in-window `doctor(<Section>)`
runs.

## Dead trigger globs

### Repaired
- `docs/guide/Email.md` — `…/Views/Profile/Emails.cshtml` → `…/Views/ProfileEmails/Emails.cshtml`
- `Humans.Users/Docs/features/email-flag-violations-remediation.md` — same
- `Humans.Users/Docs/features/preferred-email.md` — same
- `Humans.Users/Docs/features/contact-fields.md` — same

### Unresolved
None.

## Pruned

1996 lines removed; the cap was 3057 (7% of 43684).

- `docs/plans/2026-08-03-demolition-inventory.md` (645): all chaff. It was a file:line work list into
  the pre-G5 layout; its items are either done (#992 FK cut, config moves) or tracked by `[Obsolete]`
  markers.
- `docs/plans/2026-08-03-proposed-frozen-section-inventory.md` (101): all chaff. It is realised in
  `src/Sections/*`; its principles already live in atoms, design-rules §8 and Development.md; the
  Holded "API client only" line is no longer true.
- `docs/plans/2026-08-03-section-dependency-dag.md` (585): all chaff. It was a snapshot. Fan-out
  classification is in design-rules §8b, and "fan-out contracts must not live in Gdpr" is no longer
  true (`IUserDataContributor` is in `Humans.Gdpr.Contracts`).
- `docs/superpowers/plans/2026-08-10-holded-v2-migration.md` (490): all chaff. The durable signal is
  already in Holded.md/Holded-connector.md (verified: `Service.MaxTargetedRepullsPerRun`,
  `TrailingWindow`, `LedgerSyncGate`), and the design record stays in `Humans.Holded/Docs/`.
- `docs/superpowers/specs/2026-07-13-faq-proposals-production.md` (175): a one-off triage output.
  Its gotcha is no longer true (`BackdoorAgentController` computes refusal/handoff counts). The
  unapplied FAQ proposals are tracked in peterdrier/Humans#1805.

### Wheat migrated
None this sweep. See the deferral below.

### Retargeted refs
- 10 refs across `docs/plans/2026-08-03-g0-first-audit/{Gate,GoogleIntegration,Gdpr,Settings,Development,Search,Cantina}.md`
  were rewritten as "(historical, since deleted) — current invariants live in `src/Sections/*/Docs/`
  and `docs/architecture/design-rules.md`".

### Deferred
- `docs/plans/2026-08-07-fk-cut-inventory.md` (678) was not mined:
  `src/Sections/Humans.Camps/Services/CampService.cs:827` cites it, and prune may not touch `src/`.
  Analysis found three verified wheat items:
  - users are never hard-deleted, and a future delete needs an `IDeleteUser` fan-out → Users.md;
  - permanent team delete is dev-only, and `RequeueAllFailedAsync` needs a full outbox cleanup →
    Teams.md;
  - a service-layer pre-check is weaker than an FK constraint → conventions.md.

  Once a code PR retargets that comment, the next sweep can mine and delete it.
- Future candidates (budget): `docs/plans/2026-08-03-g0-first-audit/` (1802; dated subfolders are
  in the allowlist from the next sweep), `docs/superpowers/plans/2026-08-12-burn-demo-pages.md`
  (894), `docs/superpowers/specs/2026-07-15-per-section-dbcontext-design.md` (666). The
  freshness-sweep and debt-sweep design specs stay: live skills cite them.

## Unmarked editorial (add `freshness:triggers`)

- `Humans.Finance/Docs/features/sepa-payout.md`, `Humans.Notifications/Docs/features/notification-api.md`,
  and six Surveys feature docs: grid-questions, ranked-choice-voting, survey-information-blocks,
  survey-intro-markdown, survey-invitation-email-copy, survey-preview.

## Flagged for human review

- `Humans.Settings/Docs/authorization.md` carries issue references (#1628, #1104/#1631), which may
  count as history (mechanical output; left as is).

## Proposed for review

None. All candidates were resolved this sweep.

## Questions

Answered by Peter; answers applied in this PR.

1. AGENTS.md says every section has `Docs/features/` and `Docs/data-access.md`, but 12 sections lack
   `features/` and Debug/Development/Tour lack `data-access.md`. Change it to "where present"?
   **Answer:** Yes — now "where present".
2. design-rules §8 says `system_settings` keys "belong in their own sections eventually", yet
   `Workgroups:RootDriveFolderId` was just added there. Keep, soften or drop the sentence?
   **Answer:** Keep; moving the key recorded as `WORKGROUPS-2`.
3. design-rules §8b: mention `IIssueQueueOwner` (on `Humans.Issues.Contracts`) beside `ISectionSettings`?
   **Answer:** Rewritten: `.Contracts` seams exist only to break an assembly dependency loop.
4. design-rules §15i: list `CachingTicketVendorService` (a TTL cache, not TrackedCache)?
   **Answer:** Yes; replacing it with `TrackedCache` recorded as `TICKETS-7`.
5. seed-data.md: add `DevPersonaSeeder` to the seeder table?
   **Answer:** Yes — added.
6. conventions.md: cut the MinVer paragraph down to the reason?
   **Answer:** Yes — cut to the reason.
7. Users `profiles.md`: the deletion diagram shows the job anonymizing inline, but it now routes
   through `IGdprService.EraseForUserAsync`. Rewrite the diagram or leave it?
   **Answer:** Rewritten around the Gdpr fan-out.
8. `notification-board.md` (unimplemented design): assign the 5 new Workgroup sources to its
   migration tables now, or leave that to the implementer?
   **Answer:** Leave it.
9. Section help files (`Docs/help/*.guide.md`, `*.glossary.md`): add `freshness:triggers` to them,
   `ignore:` them in the catalog, or index them in docs/README?
   **Answer:** `ignore:`d in the catalog.
10. `Humans.Finance/Docs/expense-reimbursement-process.md`: give it a docs/README row?
   **Answer:** Yes — added.

## Skipped (errors)

None.
