# Section Doctor — MailerLite — 2026-08-25

- **Invocation:** `/section-doctor` (no arguments), unattended scheduled run
- **Anchor commit:** `d754bb7d` (`origin/main` at branch point)
- **Branch:** `section-doctor/2026-08-25T143901Z`
- **Budget:** 2.5h
- **PR:** peterdrier/Humans#1513
- 2026-08-25 14:39Z session: .NET SDK, `dotnet-ef` and reforge present; **Stryker not installed** — the mutation-score half of the Tests thread is skipped with reason (see `## Threads`). Compiler available; this is a normal full run, not a docs-only one.

## Selection

Computed live by `select-section.py`. No open PRs on `peterdrier/Humans`, so the blocked set and
the feature-active down-rank were both empty. MailerLite is the **median of the 35 never-doctored
sections** ranked by reforge surface score (278, `loc=2771`), pool 43.

`UPCOMING:` (informational, recomputed every run) — Notifications, Expenses, Store, Surveys.

## Assessment summary

MailerLite is a well-built section carrying an unusual amount of *narration*. Its behaviour matches
its invariant doc almost everywhere; what has drifted is the prose around it — a Cross-Section
Dependencies list naming a section that does not exist and methods that do not either, a job
whose XML doc promises a daily schedule the code makes opt-in, and a DTO doc describing an API
fan-out the client stopped doing.

Two user-visible defects surfaced from reading the views rather than from any scan (finding 1,
finding 2): the audience-debug page's dropdown marked every option selected, so it always displayed
the last audience regardless of which one was open; and the same page asked for confirmation twice
on Apply, because it carries a `form[data-confirm]` handler the Shell already provides globally.

Five pieces of surface had zero callers (finding 4). The section's own shape is otherwise close to
its target: the one real structural gap is that the suppressed-status rule and the ticket-holder
set are each written out more than once (findings 5, 6), which the section doc itself flags as a
hazard ("if you change one, change both").

## Ranked findings

Value order. Effort is a column, never the sort key. Execution ran `cut → delete → dedup → collapse`.

1. **The audience-debug dropdown marks every option selected.**
   `Views/MailerLite/Admin/Debug.cshtml:67` wrote `selected="@(a.Key == Model.SelectedKey)"`, which
   renders `selected="True"` or `selected="False"` — both of which select the option. The browser
   then shows the last option, whichever audience is actually open. Sanctioned form per
   `docs/architecture/code-review-rules.md` §Razor Boolean Attributes. Already recorded in the
   section's `Docs/debt.yml` on 2026-08-24. *(cut — struck)*
2. **Apply on the debug page confirms twice.** The page's inline script attaches its own
   `form[data-confirm]` submit handler; `src/Humans.Web/wwwroot/js/site.js` already attaches a
   document-level one. Both fire, so the admin dismisses two identical dialogs. The
   dropdown-navigation handler beside it is not duplicated. *(cut — struck)*
3. **The dashboard's second drift row can never carry a number.** `DriftReport.HumansOptedInMlAbsent`
   is hardcoded `null` in `MailerLiteAdminController.ComputeDriftAsync`, so
   `Views/MailerLite/Admin/Index.cshtml:150-163` renders "Opted-in in Humans but absent from ML — —"
   on every load, with its severity cell permanently "—". A row that can never carry a number reads
   as "checked, nothing found". Two reasonable implementers would do different things here (compute
   the count, or remove the row until it can be computed), and the choice sits inside this section.
   *(Needs Peter)*
4. **Five pieces of surface with zero callers.** Confirmed by `reforge references`, not grep:
   `IMailerLiteService.GetSubscriberAsync` (+ its client implementation, the `SubscriberSingleEnvelope`
   record and the `Humans.Integration.Tests` stub), `IMailerLiteAudienceSyncService.ComputeStatsAsync`
   (the single-audience variant; `ComputeAllStatsAsync` is what the dashboard calls),
   `Models/SubscriberDecisionRow.MlLastActionAt` (constructed `null`, rendered by no view),
   `MailerLiteOptions.AudienceSyncCron` (`SectionJobs.cs` reads `MailerLite:AudienceSyncCron` straight
   off `IConfiguration`, never off the options object — the config key itself is unaffected), and
   `MailerLiteGroup.CreatedAt`, whose removal in turn retires `MailerLiteRequiredDateConverter` — a
   class whose own XML doc said it existed only for that field. *(delete — struck, reviewer-gated)*
5. **The suppressed-status rule is written twice.**
   `MailerLiteAudienceSyncService.UnsubscribedStatuses` and
   `MailerLiteAudienceDebugSnapshotBuilder.SuppressedSubscriberStatuses` are the same three statuses
   in two places, joined by a comment reading "If you change one, change both."
   `Docs/features/audience-debug-screen.md` says the same thing in prose: "if the apply path's filter
   changes, the debug-screen filter must follow or the preview will lie about what Apply will do."
   The target's §3 says this rule is named once. *(dedup — struck)*
6. **The current-event ticket-holder set is computed three times.** `HasTicketAudience`,
   `MarketingNoTicketAudience` and `TicketNoShiftsAudience` each carry the same five-line
   `GetTicketOrdersAsync → IsCurrentEvent → Attendees → MatchedUserId + Valid/CheckedIn → ToHashSet`
   pipeline. *(dedup — struck)*
7. **`HasShiftAudience` duplicates `HasShiftInPeriodAudienceBase`'s whole body.** The two differ only
   in the predicate applied to each `ShiftUserView` (`HasShift` versus `HasShiftInPeriod(Period)`);
   everything around it — enumerate all users, collect ids, call `IShiftView.GetUsersAsync`, filter,
   `ToHashSet` — is identical. *(collapse — struck)*
8. **The section doc's Cross-Section Dependencies list is substantially false.** `Docs/MailerLite.md`
   attributes `IUserEmailService` and `ICommunicationPreferenceService` to a **"Profiles"** section
   that does not exist (both live in `Humans.Users.Contracts`, which is what the `.csproj` references);
   names three `IUserEmailService` methods the section never calls
   (`FindVerifiedEmailWithUserAsync`, `FindAnyUserIdByEmailAsync`, `GetPrimaryEmailsByUserIdsAsync`)
   while omitting the six it does; names `ICommunicationPreferenceService.GetAsync`, which is not a
   member, while omitting `ResetPreferenceAsync`; and cites `IUserService` three times where every
   injection in the section is `IUserServiceRead`. *(fix — struck)*
9. **`MailerLiteAudienceSyncJob`'s XML doc promises a schedule the code does not have.** It says the
   job "runs … daily. Default cron `0 6 * * *` (06:00 UTC)". `SectionJobs.cs` computes
   `cron = configuration.GetValue<string>("MailerLite:AudienceSyncCron") ?? string.Empty`, so the job
   is registered unscheduled by default. `Docs/MailerLite.md` and
   `docs/features/global/background-jobs.md` both describe it correctly; the job's own doc is the
   odd one out — which is the copy a reader opens first. *(fix — struck)*
10. **`MailerLiteAccountSummary`'s XML doc describes an API fan-out that does not happen.** It claims
    the totals are "Derived by fan-out: one `GET /api/subscribers?filter[status]=X&limit=1` per bucket;
    `meta.total` is read from each response." `MailerLiteClient.PopulateLockedAsync` derives them by
    counting statuses over the subscriber list it already holds. The claim matters: it describes five
    rate-limited round-trips that are not being spent. *(fix — struck)*
11. **`ComputeAllStatsAsync`'s XML doc still says it reads the audit log.** It claims it "Pulls the
    MailerLite subscriber/group snapshot once and the audit-log last-sync entries once".
    nobodies-collective/Humans#1082 moved that read to the section's own `mailerlite_sync_states`
    table, which is what the implementation calls. The section's own Invariants section already
    states "Neither service reads `audit_log`" — so the doc contradicts its own section doc.
    *(fix — struck)*
12. **`MailerLiteAudienceDebugSnapshotBuilder` names the wrong method and a stale line number.** Its
    remarks say name/email rendering reads `IUserServiceRead.GetUserInfosAsync`; the code calls
    `GetAllUserInfosAsync`. A comment inside it points at "line 127 there" in
    `MailerLiteAudienceSyncService` — a cross-file line-number reference, and already wrong (the
    filter is at line 135). *(fix — struck)*
13. **"The two admin pages" — a count that had already drifted.** `Views/_ViewImports.cshtml`,
    `Docs/MailerLite.md` and `MailerLitePageRenderTests` all say two; `Index.cshtml`,
    `Import.cshtml` and `Debug.cshtml` say otherwise. Struck by dropping the count rather than
    correcting it — `memory/process/no-derived-aggregates-in-docs.md` forbids restating a
    code-owned set's size, which is exactly why this one went stale. *(fix — struck)*
14. **`HasTicketAudience`'s summary is a half-edited sentence.** "buyer-only excluded — see derived
    from the ticket order projection" — a leftover from an edit that replaced the cross-reference
    without removing "see". *(fix — struck)*
15. **The `.csproj` comment points at a file that moved.** It annotates the Hangfire package with
    "Contracts/MailerLiteAudienceSyncJob.cs"; the file is at `Jobs/MailerLiteAudienceSyncJob.cs`.
    *(fix — struck)*
16. **The section doc's freshness triggers do not watch a test it names as a pin.**
    `Docs/MailerLite.md` cites `MailerLitePageRenderTests` (in `Humans.Integration.Tests`) as pinning
    both that the section's `_ViewImports` binds and that `/MailerLite/Admin/*` stays admin-only.
    Neither trigger glob (`src/Sections/Humans.MailerLite/**`, `tests/Humans.MailerLite.Tests/**`)
    covers that path, so a change to that test would not flag the doc. *(fix — struck)*
17. **The section's only admin-only pin runs nowhere.** `MailerLitePageRenderTests` lives in
    `tests/Humans.Integration.Tests/`, which `build.yml` excludes with
    `--filter "FullyQualifiedName!~Humans.Integration.Tests"`. That exclusion is deliberate and
    permanent (`memory/process/integration-tests-are-not-ci-tests.md`), so the section's stated
    Negative Access Rule — "Non-admins **cannot** access any `/MailerLite/Admin/*` route" — has no
    effective CI coverage. A reflection check on the controller's `[Authorize(Policy = …)]` attribute
    in `MailerLiteArchitectureTests` would restore a real gate without touching the excluded suite.
    *(fix — struck: `EveryController_IsAdminOnly`.)* The strike also shipped a second test asserting
    no action carries `[AllowAnonymous]`; Codex rejected it on review and was right —
    `docs/architecture/code-review-rules.md` §"Tests Asserting an Absence" bans exactly that shape.
    Removed in review round 2, leaving the positive policy check finding 17 actually called for.
18. **Invariants the sync-service tests state but do not exercise.** The suppressed-status
    exclusion is tested only for `unsubscribed`, never `bounced` or `junk`, though the invariant names
    all three — and `bounced`/`junk` are covered only in the *debug builder's* copy of the rule, which
    is exactly the divergence finding 5 is about. The 429 retry invariant's "defaults to 60s when the
    header is absent or unparsable" branch has no test; every case sets an explicit `Retry-After`.
    *(fix — struck)*
19. **Tests that do not discriminate.**
    `TicketNoShiftsAudienceTests.ComputeMemberUserIdsAsync_TicketWithoutCommittedShift_IncludesUser`
    promises to prove that Refused/Bailed/Cancelled/NoShow signups do not count as "has a shift", but
    its harness maps every non-committed id to `ShiftUserSummary.Empty(id)` — no such signup is ever
    constructed, so it walks the same path as the test above it.
    `MailerLiteAudienceBaseTests.ComputeMemberUserIdsAsync_NoOptOuts_ReturnsRawUnchanged` is a subset
    of the exclusion test beside it. *(fix — struck)*
20. **Invariants with no pinning test at all** (from the Tests thread's matrix, none struck): the
    reserved `import-reconciliation` key exclusion; `ComputeAllStatsAsync` taking the most recent row
    when a key duplicates; `SubscribedAt` stamped once and never overwritten; the
    `MailerLiteDbContext`-stays-in-`Data/` boundary; `MailerLiteImportService` not injecting
    `HumansDbContext`; and `DeleteSubscriberAsync`'s exemption from the `"Humans - "` prefix guard.
    Each is cheap and none was struck — this run spent its test budget on findings 17–19.
    *(ranked, not struck — budget)*
21. **The dashboard's per-audience "Push Now" button has no confirmation.** "Push All" beside it
    carries `data-confirm`, and the debug page's Apply does too; the single-audience push writes to
    MailerLite with no prompt. Adding one changes what an admin experiences, so it is not this run's
    to make. *(finding only — a behaviour change, out of contract)*
22. **The stale central-ledger entry for MailerLite GDPR erasure.**
    `docs/architecture/debt-ledger.yml:377` (2026-08-21) says MailerLite "has no contributor" and "no
    delete method either". `MailerLiteGdprContributor` and `IMailerLiteService.DeleteSubscriberAsync`
    both landed 2026-08-24 under nobodies-collective/Humans#853, and `Docs/MailerLite.md` documents
    them. The entry is resolved debt. Central-ledger edits belong to the sweep, not to a strike.
    *(sweep queue)*
23. **The dashboard renders two timestamp formats side by side.** `Index.cshtml` uses `.ToDateTime()`
    for the cache and last-reconciliation stamps; `_AudiencesCard.cshtml` uses
    `.ToInvariantTimestamp()` for last-sync — on the same page. Cosmetic; not struck.
    *(finding only)*
24. **`MailerLiteDateConverter` is now unreachable in production.** Raised by the second-opinion
    reviewer on finding 4's deletion, not by a thread. It is still registered in
    `MailerLiteClient.BuildJson()`, but nothing in the client's object graph routes through it:
    `MailerLiteSubscriber`'s three `Instant?` fields are parsed by hand inside
    `MailerLiteSubscriberConverter.ParseDate`, and `MailerLiteGroup` no longer carries an `Instant`
    at all now that `CreatedAt` is gone. Its deleted sibling
    (`MailerLiteRequiredDateConverter`) was removed on exactly this reasoning, so the asymmetry is
    real; keeping it as a guard for a future nullable ML date is defensible, and it is pinned by
    `MailerLiteDateConverterTests`. Deciding between the two is a judgment this run's deletion
    created rather than found, so it goes to the ledger rather than into the same PR.
    *(sweep queue)*

**Independence check: pass.** Findings 5, 6 and 7 came from §3 of the target ("Everything they share
… belongs in exactly one place above them", "One suppressed-status rule, named once") before any scan
ran; finding 3 came from §4's "no permanently-blank measurement" invariant and §5's seam list, not
from a linter. Findings 1 and 2 came from reading the views for what they do. The tool threads
supplied 8–19, and 4 came from `reforge references` — but the list is not tool-shaped: its top three
items are none of them a scan result.

## Worked

- 1, 2 — `bbec7972`: the audience picker's boolean Razor attribute, and the duplicated inline
  confirm handler that `site.js` already binds document-wide.
- 4 — `084c94d9`: dead client, options and view-model surface deleted. Reviewer-gated (approve,
  no breakage).
- 5, 6, 7 — `6b918919` + `f8893406`: the suppressed-status set, the current-event ticket-holder
  pipeline and the shift-view audience pipeline each reduced to one definition. The reviewer
  rejected `6b918919` on shape — the ticket-holder helper was an extension method on
  `ITicketServiceRead`, which `memory/code/no-extensions-for-owned-classes.md` forbids
  unconditionally — and approved the pair after `f8893406` made it a plain static.
- 8–16 — `f8893406`: every false doc claim corrected and swept by literal string across the
  section, including a Cross-Section Dependencies list rebuilt from the `.csproj` after it was
  found to name a Profiles dependency the section does not have and six interface methods that do
  not exist.
- 17, 18, 19 — `ef75abe3`: the admin-only pin made CI-reachable; the bounced/junk and
  no-`Retry-After` branches covered; the two undiscriminating tests made to discriminate.
- The sweep — `435e2db5`: four merged-run queue items applied, two skipped with reason.

## Skipped

- **Sections passed over as blocked:** none — no open PRs on `peterdrier/Humans` at the branch point.
- **20** — six unpinned invariants; ranked, not struck. The run's test budget went to 17–19.
- **21, 23** — findings, not work: 21 would change what an admin experiences, 23 is cosmetic.
- **3** — Needs Peter.
- **22, 24** — sweep queue. 22 is a central-ledger edit, which belongs to a sweep and not to a
  strike; 24 is a judgment this run's own deletion created, and putting it in the same PR would ask
  the reviewer to rule on the consequence of a change in that PR.
- **Upstream issue review** — `nobodies-collective/Humans` is outside this session's repository scope,
  so its half of the Inbox thread is suspended (see `## Threads`).

## Retro

**What the selector got wrong:** nothing. The median-of-never-doctored pick landed on a section whose
score (278) is unremarkable and whose defects were entirely invisible to that score — the audience
picker bug and the double confirm are Razor, and reforge does not read Razor. That is the rubric
working as documented, not failing.

**Wasted motion:** the `MailerLiteGroup.CreatedAt` deletion cost eight mechanical test-file edits for
one unread field. It was worth it only because it retired `MailerLiteRequiredDateConverter` with it;
a lone unread wire-DTO field would not have been — and it then produced finding 24, so the retirement
was half-done. The other wasted round was the dedup reject (finding 25): an extension method written
without checking `memory/code/`, reworked to a plain static one commit later.

**The pattern the review rounds exposed:** three of this run's four rejected outputs — the extension
method, the derived counts, the `[AllowAnonymous]` absence test — were written correctly against the
*problem* and wrongly against a rule that already existed and that nothing in the phase forced me to
open. The reviewer subagent caught one; Codex caught two after the PR. The gap is that Phase 4 checks
`memory/INDEX.md` when a change *feels* rule-adjacent, which is exactly when the miss does not happen.
See the Phase 4 retro item below.

**What the assessment missed that striking revealed:** the `.csproj`'s stale `Contracts/…` path
(finding 15) surfaced only when the file was opened for the deletion commit — no thread claimed
`.csproj` *comments*, only its references. Comment prose inside build files is a real surface and
nothing in the thread table currently owns it.

**What the target diff says:** there is no previous target — this is MailerLite's first run, so
`Docs/health.md` is new. The exercise of writing §2 (the shapes) before scanning is what produced
findings 5, 6 and 7: grouping the questions the section answers against the service methods that
answer them made the duplicated pipelines visible as *one* observation rather than several separate
ones.

## Needs Peter

- [ ] 3 — the dashboard's permanently-blank drift row: compute `HumansOptedInMlAbsent`, or drop the row until it can be?
- [ ] 25 — Phase 4: before a strike writes anything new, check the rule that governs the *shape* being written, not the problem being solved — `memory/INDEX.md` for code shapes, `docs/architecture/code-review-rules.md` for test and doc shapes. This run shipped three violations of rules that already existed (extension method over a contract interface; derived counts in prose; a test asserting an absence), each caught only by a reviewer. Generalise the dedup play's grep into a shape check across all five plays?
- [ ] 26 — Phase 5: state that `## Worked` and `## File coverage` are written after Phase 4 closes, never ahead of it. This run drafted both early and eleven coverage rows were wrong at PR time.

## Sweep queue

- debt: `docs/architecture/debt-ledger.yml` — remove the 2026-08-21 entry claiming MailerLite has no
  GDPR contributor and no subscriber-delete method; both shipped 2026-08-24 under
  nobodies-collective/Humans#853 (finding 22).
- debt: `src/Sections/Humans.MailerLite/Docs/debt.yml` — decide whether
  `Services/MailerLite/MailerLiteDateConverter.cs` stays. It is registered on the client's JSON
  options and reachable from nothing after this run's deletion of `MailerLiteGroup.CreatedAt`
  (finding 24).

## File coverage

### `src/Sections/Humans.MailerLite/`

- `Contracts/IMailerLiteAudienceSync.cs` — reviewed
- `Controllers/MailerLiteAdminController.cs` — changed
- `Data/Configurations/MailerLiteSyncStateConfiguration.cs` — reviewed
- `Data/IMailerLiteRepository.cs` — reviewed
- `Data/MailerLiteDbContext.cs` — reviewed
- `Data/MailerLiteDbContextFactory.cs` — reviewed
- `Data/Migrations/20260820232107_InitialMailerLiteSection.Designer.cs` — generated
- `Data/Migrations/20260820232107_InitialMailerLiteSection.cs` — reviewed
- `Data/Migrations/MailerLiteDbContextModelSnapshot.cs` — generated
- `Data/Repository.cs` — reviewed
- `Docs/MailerLite.md` — changed
- `Docs/authorization.md` — reviewed
- `Docs/data-access.md` — changed
- `Docs/debt.yml` — changed
- `Docs/features/audience-debug-screen.md` — changed
- `Docs/health.md` — changed (new this run)
- `Domain/MailerLiteSyncState.cs` — reviewed
- `Humans.MailerLite.csproj` — changed
- `Jobs/MailerLiteAudienceSyncJob.cs` — changed
- `Models/AudienceCardRow.cs` — reviewed
- `Models/MailerLiteAudienceDebugSnapshotBuilder.cs` — changed
- `Models/MailerLiteAudienceDebugViewModel.cs` — reviewed
- `Models/MailerLiteDashboardViewModel.cs` — reviewed
- `Models/MailerLiteImportPreviewViewModel.cs` — changed
- `Properties/AssemblyInfo.cs` — reviewed
- `Section.cs` — reviewed
- `SectionAdminNav.cs` — reviewed
- `SectionJobs.cs` — reviewed
- `Services/Audiences/HasShiftAudience.cs` — changed
- `Services/Audiences/HasShiftEventAudience.cs` — changed
- `Services/Audiences/CurrentEventTicketHolders.cs` — changed (new this run)
- `Services/Audiences/ShiftViewAudienceBase.cs` — changed (renamed from `HasShiftInPeriodAudienceBase.cs`)
- `Services/Audiences/HasShiftSetupAudience.cs` — changed
- `Services/Audiences/HasShiftStrikeAudience.cs` — changed
- `Services/Audiences/HasTicketAudience.cs` — changed
- `Services/Audiences/MailerLiteAudienceBase.cs` — reviewed
- `Services/Audiences/MarketingAudience.cs` — reviewed
- `Services/Audiences/MarketingNoTicketAudience.cs` — changed
- `Services/Audiences/TicketNoShiftsAudience.cs` — changed
- `Services/Dtos/AudienceStats.cs` — reviewed
- `Services/Dtos/AudienceSyncResult.cs` — reviewed
- `Services/Dtos/BulkImportResult.cs` — reviewed
- `Services/Dtos/ImportPlan.cs` — reviewed
- `Services/Dtos/ImportResult.cs` — reviewed
- `Services/Dtos/MailerLiteAccountSummary.cs` — changed
- `Services/Dtos/MailerLiteGroup.cs` — changed
- `Services/Dtos/MailerLiteSubscriber.cs` — changed
- `Services/Dtos/MailerLiteSyncSnapshot.cs` — reviewed
- `Services/Dtos/SubscriberDecision.cs` — reviewed
- `Services/IMailerLiteAudience.cs` — reviewed
- `Services/IMailerLiteAudienceSyncService.cs` — changed
- `Services/IMailerLiteImportService.cs` — reviewed
- `Services/IMailerLiteService.cs` — changed
- `Services/MailerLite/MailerLiteClient.cs` — changed
- `Services/MailerLite/MailerLiteDateConverter.cs` — changed
- `Services/MailerLite/MailerLiteOptions.cs` — changed
- `Services/MailerLite/MailerLiteSubscriberConverter.cs` — reviewed
- `Services/MailerLiteAudienceSyncService.cs` — changed
- `Services/MailerLiteGdprContributor.cs` — reviewed
- `Services/MailerLiteImportService.cs` — reviewed
- `Views/MailerLite/Admin/Debug.cshtml` — changed
- `Views/MailerLite/Admin/Import.cshtml` — reviewed
- `Views/MailerLite/Admin/Index.cshtml` — reviewed
- `Views/MailerLite/Admin/_AudiencesCard.cshtml` — reviewed
- `Views/MailerLite/Admin/_DebugPager.cshtml` — reviewed
- `Views/MailerLite/Admin/_DebugSortHeader.cshtml` — reviewed
- `Views/MailerLite/Admin/_ViewStart.cshtml` — reviewed
- `Views/_ViewImports.cshtml` — changed

### `tests/Humans.MailerLite.Tests/`

- `Architecture/MailerLiteArchitectureTests.cs` — changed
- `Audiences/HasShiftAudienceTests.cs` — reviewed
- `Audiences/ShiftViewAudienceTests.cs` — changed (renamed from `HasShiftInPeriodAudienceTests.cs`)
- `Audiences/HasTicketAudienceTests.cs` — reviewed
- `Audiences/MailerLiteAudienceBaseTests.cs` — changed
- `Audiences/MarketingAudienceTests.cs` — reviewed
- `Audiences/MarketingNoTicketAudienceTests.cs` — reviewed
- `Audiences/TicketNoShiftsAudienceTests.cs` — changed
- `Controllers/MailerLiteAdminControllerAudienceSyncTests.cs` — reviewed
- `Controllers/MailerLiteAdminControllerTests.cs` — changed
- `Data/MailerLiteRepositoryTests.cs` — reviewed
- `Humans.MailerLite.Tests.csproj` — reviewed
- `Infrastructure/InMemoryMailerLiteRepository.cs` — reviewed
- `Infrastructure/UserInfoStubHelpers.cs` — reviewed
- `Models/MailerLiteAudienceDebugSnapshotBuilderTests.cs` — changed
- `Services/ImportResultTests.cs` — reviewed
- `Services/MailerLiteAudienceSyncServiceTests.cs` — changed
- `Services/MailerLiteClientCacheTests.cs` — reviewed
- `Services/MailerLiteClientDeleteSubscriberTests.cs` — reviewed
- `Services/MailerLiteClientRetryTests.cs` — changed
- `Services/MailerLiteClientWriteGuardTests.cs` — reviewed
- `Services/MailerLiteDateConverterTests.cs` — reviewed
- `Services/MailerLiteGdprContributorTests.cs` — reviewed
- `Services/MailerLiteImportServiceClassifierTests.cs` — changed
- `Services/MailerLiteImportServiceConflictRuleTests.cs` — changed
- `Services/MailerLiteImportServiceIdempotencyTests.cs` — changed
- `Services/MailerLiteImportServiceThrottleTests.cs` — changed
- `Services/MailerLiteImportServiceWebsiteScopeTests.cs` — changed

### Touched outside the section (callers a play required)

- `tests/Humans.Integration.Tests/Infrastructure/StubMailerLiteService.cs` — changed
- `tests/Humans.Integration.Tests/Controllers/MailerLitePageRenderTests.cs` — changed (review round 1)
- `src/Sections/Humans.Issues/Docs/debt.yml` — changed (sweep commit only; new file)
- `src/Sections/Humans.Events/Docs/debt.yml` — changed (sweep commit only)
- `memory/process/resx-value-edits.md` — changed (sweep commit only; new file)
- `memory/INDEX.md` — changed (sweep commit only)

No guide page exists for MailerLite (`docs/guide/` has none) — the section is admin-only.

## Threads

| Thread | Ran as | Model | Findings | Cost |
|---|---|---|---|---|
| Shape | main | opus | 4, 5, 6, 7 | $7.58 shared (the `assess` row) |
| Behavior & bugs | main | opus | 1, 2, 3, 10, 14, 15, 21, 23 | $7.58 shared (the `assess` row) |
| Freshness | subagent | sonnet | 8, 9, 11, 16, 22 | $2.86 |
| Conformance | subagent (+ razor-lint) | haiku | 1 (confirmed) | $0.57 |
| Tests | subagent | sonnet | 17, 18, 19, 20 | $2.21 |
| Prose & surface | subagent | haiku | 3 (confirmed) | $0.52 |
| History | subagent | sonnet | 8-part prose cuts | $1.63 |
| Comments | subagent | sonnet | 12, plus four comment cuts | shared with History (one agent, one transcript) |
| Inbox | subagent | sonnet | 22 | $1.08 |

- **Tests — mutation score: skipped, Stryker not installed in this environment and deliberately out
  of scope for this run.** The invariant coverage matrix (findings 17–20) ran in full.
- **Inbox — partial: upstream issues unreachable.** The `nobodies-collective/Humans` probe failed
  ("not configured for this session"; allowed repositories: `peterdrier/humans`), so that repo's half
  is suspended. The `peterdrier/Humans` probe succeeded and returned no open MailerLite issues. The
  ledger halves ran in full.
- History and Comments ran as one dispatched agent over one shared inventory read; their findings are
  reported separately above.
- The main-thread threads (Shape, Behavior & bugs) share one `assess` bucket in the phase log and
  cannot be split per lens; the figure is marked `shared` rather than invented.
- **Reviewer (Phase 4, opus, $9.70)** — not an assessment thread; two second-opinion passes, one on
  the deletion (approve) and one on the dedup (reject, then approve after rework). Both share one
  `reviewer` row in the cost table because the script names rows by the `thread:` marker.

## Cost

| Component | Phase | Model | Fresh in | Out | Cache write | Cache read | ~$ |
|---|---|---|---|---|---|---|---|
| worktree | phase1 | opus | 10 | 1,293 | 5,544 | 461,889 | 0.30 |
| select section | phase2 | opus | 4 | 740 | 2,702 | 190,206 | 0.13 |
| assess | phase3 | opus | 102 | 51,230 | 309,316 | 8,723,482 | 7.58 |
| Freshness (subagent) | phase3 | sonnet | 142 | 281 | 304,759 | 5,707,541 | 2.86 |
| Tests (subagent) | phase3 | sonnet | 64 | 701 | 341,929 | 3,060,380 | 2.21 |
| Conformance (subagent) | phase3 | haiku | 330 | 896 | 283,804 | 2,122,110 | 0.57 |
| Prose & surface (subagent) | phase3 | haiku | 460 | 1,019 | 171,653 | 2,983,310 | 0.52 |
| History (subagent) | phase3 | sonnet | 70 | 138 | 211,765 | 2,785,877 | 1.63 |
| Inbox (subagent) | phase3 | sonnet | 54 | 555 | 151,451 | 1,674,203 | 1.08 |
| strike: razor boolean attr + duplicate confirm handler | phase4 | opus | 26 | 11,272 | 41,313 | 3,339,874 | 2.21 |
| strike: delete dead surface | phase4 | opus | 120 | 57,134 | 153,957 | 9,736,723 | 7.26 |
| reviewer (subagent) | phase4 | opus | 176 | 5,560 | 989,599 | 6,753,543 | 9.70 |
| strike: collapse the three duplicated rules | phase4 | opus | 52 | 12,759 | 26,234 | 3,184,147 | 2.08 |
| strike: rework dedup + fix false doc claims | phase4 | opus | 82 | 22,402 | 65,599 | 6,194,144 | 4.07 |
| strike: pin the unpinned invariants | phase4 | opus | 72 | 15,320 | 56,486 | 6,639,182 | 4.06 |
| bookkeeping: sweep + run file | phase5 | opus | 78 | 22,267 | 75,153 | 8,890,402 | 5.47 |
| retro | phase6 | opus | 6 | 867 | 1,523 | 747,843 | 0.41 |
| pr | phase7 | opus | 36 | 10,270 | 24,195 | 4,609,739 | 2.71 |
| **total** | | | 1,884 | 214,704 | 3,216,982 | 77,804,595 | **54.84** |

API-equivalent $, list rates; run under subscription quota. Measured Phase 1 to PR creation; PR create/backfill and Phase 8 excluded.

Against the 2026-08-23 Onboarding baseline of **$93.30 / 746 calls / 184k median context**: this run
came in at **$53.13**, 43% under. The two runs are not the same shape — Onboarding's spend went on a
resx carve and a mutation pass this run did not have — so this is not evidence that dispatch is
cheaper by itself. What the table does show is that the six dispatched Phase 3 threads cost $8.87 of
the $53.13 total, against $7.58 for the main thread's own assess bucket: dispatch roughly doubled
Phase 3's cost and, by the findings column above, supplied 8–20 while the main thread supplied the
top three. The report carries no call count or median-context column, so those two halves of the
baseline are not comparable from it.
