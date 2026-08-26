# Section Doctor — Notifications — 2026-08-26

- **Invocation:** `/section-doctor` (no arguments), unattended scheduled run
- **Anchor commit:** `3dcdae3c` (`origin/main` at branch point)
- **Branch:** `section-doctor/2026-08-26T101558Z`
- **PR:** peterdrier/Humans#1527
- 2026-08-26 10:15Z session: .NET SDK, `dotnet-ef` and reforge present; compiler available, so this is a normal full run. The clone is shallow, so git archaeology was unavailable — every "this used to be true" call below is inferred from the code, never from history.

## Selection

Computed live by `select-section.py`. Pool 43, blocked 2 (CityPlanning `#1525`, Store `#1520`), 10 feature-active sections set aside. Notifications is the lower-middle **median of the 22 remaining never-doctored sections** by reforge surface score (274, `loc=2421`).

`UPCOMING:` (informational, recomputed every run) — Expenses, Gdpr, AuditLog, Campaigns.

## Assessment summary

Notifications is a correct section with an unreliable narrator. Almost every behavioural invariant it claims holds; almost every *name* it uses to explain itself is out of date. Four interfaces named across three docs no longer exist under those names, two sections are cited that were renamed or never existed, and the `.csproj` justifying the contracts leaf argues from two projects (`Humans.Application`, `Humans.Interfaces`) that are gone from the solution. None of this breaks anything, which is exactly why it accumulated.

The one story worth reading closely was the DI cycle (finding 3). Six files describe `NotificationRecipientResolver` as the thing standing between `NotificationService` and a startup-time cycle. Grepping the graph rather than trusting the comments shows the cycle does not exist today: `RoleAssignmentService` injects the narrow `INotificationEmitter`, which is implemented by a separate type that has no resolver edge at all. Three of the six comments were simply false and are now corrected; the resolver itself is a one-method pass-through that is very likely deletable, but proving that needs a gate this repo does not have (finding 15), so it stands.

Two user-visible gaps were held rather than struck, both because striking them would be a decision rather than a repair: three POST routes that no view or script reaches (finding 1), and eight English meter titles rendered on `/Notifications`, a page the admin-localization exemption does not cover (finding 2).

## Ranked findings

**1 — Three POST routes have no caller.** `POST /Notifications/Resolve/{id}`, `POST /Notifications/Dismiss/{id}` and `POST /Notifications/MarkRead/{id}` are reachable from nothing. The inbox view and the bell popup between them invoke only `Index`, `Popup`, `MarkAllRead`, `BulkResolve`, `BulkDismiss` and `ClickThrough`; `src/Humans.Web/wwwroot/js/site.js` fetches only `/Notifications/Popup`. Checked every `asp-action`, `Url.Action` and `fetch(` in the section's four views and in the Shell's site-wide script. A human today resolves a single actionable notification by clicking through it, or by selecting it and using the bulk control. Deleting three endpoints, their service methods and the repository methods under them is a real cut, but the alternative reading — that the single-item buttons were dropped from the view by accident and the endpoints are what survived — is equally consistent with what is on disk, and the two readings have opposite fixes. Held. The one resx key tied to this fork (`Notification_Dismiss`) was held back from finding 8's deletion for the same reason.

**2 — Eight meter titles are hardcoded English on a non-exempt page.** `NotificationMeterProvider` builds every meter title as a C# literal: "Consent reviews pending", "Applications pending your vote", "Pending account deletions", "Failed Google sync events", "Onboarding profiles pending", "Team join requests pending", "Ticket sync error", and a pluralised "N humans want to join your camp". These render on `/Notifications` and in the bell popup, which `memory/code/localization-admin-exempt.md` does **not** exempt — the exemption covers `/Admin/*`, `/TeamAdmin/*` and `/Shifts/Dashboard` only. Fixing it is eight new keys across six cultures plus a plural form the resx set has no existing pattern for, and it puts an `IStringLocalizer` into a provider that currently has no view-layer dependency. Held: the size and the plural question both want a decision.

**3 — The DI-cycle story was told six ways, three of them false.** `INotificationEmitter.cs`, `INotificationService.cs`, `NotificationRecipientResolver.cs`, `INotificationRecipientResolver.cs`, `Section.cs` and `NotificationsArchitectureTests.cs` each explained the emitter/service split as a cycle break. Three of them described a cycle through `IRoleAssignmentService` that the current graph does not contain, and one asserted that a direct `NotificationService → IRoleAssignmentService` edge "would still close a cycle through the resolver" — which presumes the resolver survives the very deletion being discussed. Struck: the comments now say what is verifiable — which type injects which interface, and why the narrow one exists — and the architecture test's comment was narrowed to the claim its assertion actually makes.

**4 — `Dismiss` returned `StatusCode(403)` where every sibling returns `Forbid()`.** The cookie scheme sets `AccessDeniedPath = "/Account/AccessDenied"`, so `Forbid()` redirects a browser to a page and `StatusCode(403)` returns a bare status the view never handles. Struck: `Forbid()`, matching `Resolve` and the bulk paths.

**5 — Four sources fell through the default arm.** `NotificationSourceMapping` had no explicit case for `IssueComment`, `IssueStatusChanged`, `IssueAssigned` or `IssueSubmitted`; all four landed on `_ => MessageCategory.System` by accident rather than by decision. `System` is the right category for them, so the behaviour does not change — but "arrived at by falling through" and "chosen" are different states, and only one of them survives someone adding a category. Struck: four explicit arms.

**6 — The meter provider counted the same thing twice under two names.** `MeterCounts` carried both `OnboardingPending` and `ConsentReviewsPending`, computed from the same `UserInfo.NeedsConsentReview` predicate over the same snapshot. Two fields, one number, free to drift. Struck: one field, read by both meters, with the Board/VolunteerCoordinator meter's different *label* kept and its reason written down.

**7 — The meter provider's `<see cref>` list named interfaces it does not inject.** Struck alongside finding 9's sweep.

**8 — Four resx keys are named nowhere.** `Notification_Tag_Urgent`, `Notification_Tag_Action`, `Notification_Tag_Info` and `Notification_Pref_InboxEnabled` appear in no view, controller or service. Struck across all six cultures — 24 entries — by targeted exact-string replacement per `memory/process/resx-value-edits.md`, leaving each file's formatting, comments and trailing newline untouched (verified: the diffs are pure deletions). `Notification_Dismiss` was **not** deleted despite its only consumer being finding 1's unreachable route; it goes when that fork is decided.

**9 — Three docs named four interfaces the code does not use.** `ITicketSyncService` (now `ITicketSync`), `IApplicationDecisionService.GetUnvotedApplicationCountAsync` (now on `IApplicationServiceRead`), `IUserService` on the inbox service (`IUserServiceRead`), and an `ITeamServiceRead` on the recipient resolver that it has never injected. Also: Issues was said to inject `INotificationService` (it injects `INotificationEmitter`), and `AccountMergeService` was filed under a "Profiles section" that does not exist — it lives in `Humans.Users`. Struck, swept by literal string across the whole repo: `Docs/Notifications.md`, `Docs/data-access.md`, `docs/architecture/service-data-access-map.md`. Hits under `docs/plans/**` were left — those are dated plan documents describing what was true when they were written.

**10 — `notification-inbox.md` put the Camp sources in the wrong category.** All four `Camp*` rows listed Category `System`; `NotificationSourceMapping` maps all four to `TeamUpdates`. A recipient reading the doc would expect their System preference to suppress a camp notification, and it does not. Struck. Every other row in that 34-row table was checked against the mapping and is correct.

**11 — `Notifications.md` claimed resolve sets the per-recipient `ReadAt`.** It does not. The doc and the code disagreed and the code was right: every unread query also filters `ResolvedAt == null`, so resolving drops the row out of the badge without touching read state. Struck in the doc's favour of the code, with the reason written down — and pinned by a test (finding 14), because a doc claim with no test is how this drifted in the first place.

**12 — The contracts `.csproj` comment argued from two projects that no longer exist.** It justified the leaf by "eleven `Humans.Application` services", said it "References `Humans.Interfaces` only", and placed `CleanupNotificationsJob` in `Contracts/`. Neither project is in the solution; the job is in `Jobs/`; the leaf references `Humans.Base`. Struck: the comment now names the thirteen section projects that actually dispatch in.

**13 — The filter/tab rule was written twice.** `NotificationsController.Index` re-derived "the resolved filter forces the all tab" so the view's tab pill would render correctly, while `NotificationInboxService.ParseFilterAndTab` derived the same rule again for the query. Two copies, free to drift into a pill that disagrees with the rows underneath it. Struck: `NotificationInboxResult` now reports the tab actually queried, and the controller reflects it instead of deciding.

**14 — `NotificationServiceTests` re-tested the emitter through a pass-through.** `NotificationService.SendAsync` is a one-line delegation to `INotificationEmitter.SendAsync`, and the test class wires a *real* `NotificationEmitter` in — so six tests there (per-recipient rows, field persistence, empty list, preference suppression, actionable bypass, badge eviction) ran exactly the code `NotificationEmitterTests` already covers, one hop further from it. Struck: one narrow test that the delegation is live, five deleted. In their place, the two invariants nothing asserted — resolve leaves `ReadAt` alone yet still clears the badge (finding 11), and the resolved filter reports the all tab (finding 13).

**15 — The recipient resolver is probably deletable and there is no way to prove it offline.** `NotificationRecipientResolver` is a single method that forwards to `IRoleAssignmentService.GetActiveUserIdsInRoleAsync`. Its documented reason for existing — breaking a DI cycle — is not true of the current graph (finding 3). The gate that would catch a reintroduced cycle is `ValidateOnBuild = true` in `src/Humans.Web/Program.cs:72-73`, which only fires when the app actually starts; no test in the solution builds the full container. So a run can delete the resolver, watch every test pass, and ship a container that throws at startup. Held: this is a deletion whose only safety net is a manual `dotnet run`.

**16 — `tests/Humans.Integration.Tests` runs nowhere, and cannot run here either.** `.github/workflows/build.yml:112` passes `--filter "FullyQualifiedName!~Humans.Integration.Tests"`, so that project is excluded from every CI run; the comment above it says why — the assembly needs Docker/Testcontainers, which is noisy on `ubuntu-latest`. This run's own full `dotnet test Humans.slnx` confirmed the shape from the other side: all 318 of its tests fail in under a second with `DockerUnavailableException` at `unix:///var/run/docker.sock`, because this session's container has no Docker either. So the project is green nowhere and red everywhere it is actually invoked, which makes "excluded from CI" understate it — it is unrunnable in both environments the repo has. Repo-wide, not this section's. Queued.

**17 — The scheduling prompt and the skill disagree about Stryker.** The prompt instructed this run to "record it skipped-with-reason in the run file"; the skill at HEAD (`3dcdae3c`, "upstream issues and Stryker become opt-in flags") says a run without `--mutation` must never "attempt, probe for, mention, or record-as-skipped" Stryker. Followed the skill, so no run artifact mentions it — but the scheduled prompt still carries the older instruction and will re-issue this conflict every night until it is edited. Raised for Peter.

**18 — The default arm still hides the next missing case.** Finding 5 filled in four accidental fall-throughs, but `_ => MessageCategory.System` remains, so the *next* source added without a mapping repeats the same silent default. Removing the arm would make a missing case a compile error (CS8509) instead — the analyzer-shaped fix Peter's rules prefer over a test — at the cost of turning an unmapped runtime value into a throw. That trade is a decision, not a repair. Raised for Peter.

## Worked

- Findings 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14 — struck across four commits.
- Target shape written fresh as `src/Sections/Humans.Notifications/Docs/health.md`, before any scan.
- Gates: `dotnet format whitespace Humans.slnx --verify-no-changes` clean; full `dotnet test Humans.slnx -v quiet` green in every project **except** `Humans.Integration.Tests`, which fails 318/318 on Docker unavailability in this container and is the project CI excludes for the same reason (finding 16) — not a regression from this branch. `Humans.Notifications.Tests`: 65 passed.
- Sweep applied (its own commit): every queued item from merged run files was already present in its target except two from the MailerLite run — a stale `debt-ledger.yml` entry claiming MailerLite has no GDPR contributor (verified closed: `Section.cs:80` registers one, and `IMailerLiteService.DeleteSubscriberAsync` exists) and the open `MailerLiteDateConverter` question, recorded in that section's `debt.yml`.

## Skipped

- Findings 1, 2, 15, 18 — each is a decision rather than a repair; see the descriptions above and `## Needs Peter`.
- Finding 16 — repo-wide, not this section's; queued.
- Finding 17 — an instruction conflict, not a code defect.
- Sections passed over as blocked: CityPlanning (open PR `#1525`), Store (open PR `#1520`).

## Retro

**What the selector got wrong: nothing, and the rationale was legible.** Notifications and Expenses tied at score 274; the selector took the smaller `loc`, which was the right call for a first-ever run on a fan-in section — the width is in the callers, not the lines.

**Wasted motion: the sweep, mostly.** Four of the six queued items were already applied by earlier sweeps, and finding that out cost a read of four run files, four `debt.yml` files, a memory atom and the INDEX. That is the idempotence design working as intended, but the cost is paid every run and grows with the queue. Worth noting rather than fixing: the alternative — ticking swept items — is exactly what the skill forbids, and for good reason.

Also wasted: three of fourteen scripted literal replacements missed on the first pass because the on-disk text wrapped differently than the strike plan assumed. The recovery (print the real region, re-run against the true string) was cheap, but the lesson generalises — see the Needs-Peter item.

**What the assessment missed that striking revealed.** Two things, both found by *changing* code rather than reading it. Finding 13's duplicated rule read as harmless redundancy in the assessment; it only became a defect once the fix was written and the drift case was concrete — the pill and the rows can disagree. And finding 14 was invisible until finding 11 needed a test: looking for where to put the new assertion is what surfaced that six existing assertions were already covered elsewhere. Reading for coverage gaps found nothing; needing a home for one test found the duplication immediately.

**What the target diff says.** No previous target existed — this is run 1 for the section, and `health.md` is new. So there is nothing to diff, and the honest statement is that the next run's diff is the first one that will carry information. One thing worth recording for it: the target's §3 asks for "one controller carrying no rule of its own, including no copy of a rule the service already applies", and that clause was written *before* finding 13 was understood as a defect. It earned its place within the hour.

## Needs Peter

- [ ] 1 — Delete the three uncalled POST routes, or restore the single-item buttons that would call them?
- [ ] 2 — Localize the eight meter titles (8 keys × 6 cultures + a plural form), or exempt `/Notifications` meters?
- [ ] 15 — Delete the recipient resolver? Only a manual app start can prove it safe today.
- [ ] 17 — Edit the scheduled prompt to drop its Stryker instruction, which the skill now forbids.
- [ ] 18 — Drop `_ => MessageCategory.System` so a missing mapping is a compile error?
- [ ] Phase 4 — after a scripted literal replacement reports a miss, re-read the region before retrying; a second guess at the string is how a strike plan silently half-applies.

## Sweep queue

- debt: `tests/Humans.Integration.Tests` runs in no environment the repo has. `.github/workflows/build.yml:112` excludes it from CI via `--filter "FullyQualifiedName!~Humans.Integration.Tests"` because it needs Docker/Testcontainers, and in an agent session container all 318 tests fail in under a second with `DockerUnavailableException` at `unix:///var/run/docker.sock`. A test project that is skipped in CI and red locally is not a safety net; decide whether it is retired (delete it) or gated (a `services: postgres` job, or a Testcontainers-capable runner). Found by /section-doctor on Notifications 2026-08-26 (finding 16).

## File coverage

### `src/Sections/Humans.Notifications.Contracts/`

| Path | Disposition |
|---|---|
| `Humans.Notifications.Contracts.csproj` | changed |
| `INotificationAutoResolve.cs` | reviewed |
| `INotificationEmitter.cs` | changed |
| `INotificationRetention.cs` | changed |
| `INotificationService.cs` | changed |
| `NotificationClass.cs` | reviewed |
| `NotificationPriority.cs` | reviewed |
| `NotificationSource.cs` | reviewed |

### `src/Sections/Humans.Notifications/`

| Path | Disposition |
|---|---|
| `Controllers/NotificationsController.cs` | changed |
| `Data/Configurations/NotificationConfiguration.cs` | reviewed |
| `Data/Configurations/NotificationRecipientConfiguration.cs` | reviewed |
| `Data/INotificationRepository.cs` | changed |
| `Data/Migrations/20260809032723_BaselineNotifications.Designer.cs` | generated |
| `Data/Migrations/20260809032723_BaselineNotifications.cs` | generated |
| `Data/Migrations/NotificationsDbContextModelSnapshot.cs` | generated |
| `Data/NotificationRepository.cs` | changed |
| `Data/NotificationsDbContext.cs` | reviewed |
| `Data/NotificationsDbContextFactory.cs` | reviewed |
| `Docs/Notifications.md` | changed |
| `Docs/authorization.md` | reviewed |
| `Docs/data-access.md` | changed |
| `Docs/features/notification-inbox.md` | changed |
| `Docs/health.md` | changed (new this run) |
| `Domain/Notification.cs` | changed |
| `Domain/NotificationRecipient.cs` | changed |
| `Humans.Notifications.csproj` | reviewed |
| `Jobs/CleanupNotificationsJob.cs` | changed |
| `Models/NotificationsViewModels.cs` | reviewed |
| `NotificationsResource.ca.resx` | changed |
| `NotificationsResource.cs` | changed |
| `NotificationsResource.de.resx` | changed |
| `NotificationsResource.es.resx` | changed |
| `NotificationsResource.fr.resx` | changed |
| `NotificationsResource.it.resx` | changed |
| `NotificationsResource.resx` | changed |
| `Properties/AssemblyInfo.cs` | reviewed |
| `Section.cs` | changed |
| `SectionChrome.cs` | changed |
| `SectionJobs.cs` | reviewed |
| `Services/Dtos/NotificationMeter.cs` | reviewed |
| `Services/INotificationInboxService.cs` | changed |
| `Services/INotificationRecipientResolver.cs` | changed |
| `Services/NotificationEmitter.cs` | reviewed |
| `Services/NotificationInboxService.cs` | changed |
| `Services/NotificationMeterProvider.cs` | changed |
| `Services/NotificationRecipientResolver.cs` | changed |
| `Services/NotificationService.cs` | reviewed |
| `Services/NotificationSourceMapping.cs` | changed |
| `ViewComponents/NotificationBellViewComponent.cs` | changed |
| `Views/Notifications/Index.cshtml` | reviewed |
| `Views/Notifications/_NotificationPopup.cshtml` | reviewed |
| `Views/Notifications/_NotificationRow.cshtml` | reviewed |
| `Views/Shared/Components/NotificationBell/Default.cshtml` | reviewed |
| `Views/_ViewImports.cshtml` | changed |

### `tests/Humans.Notifications.Tests/`

| Path | Disposition |
|---|---|
| `Enums/EnumStringStabilityTests.cs` | reviewed |
| `Humans.Notifications.Tests.csproj` | reviewed |
| `NotificationsArchitectureTests.cs` | changed |
| `Services/NotificationEmitterTests.cs` | changed |
| `Services/NotificationInboxServiceTests.cs` | changed |
| `Services/NotificationMeterProviderTests.cs` | reviewed |
| `Services/NotificationRecipientResolverTests.cs` | reviewed |
| `Services/NotificationRepositoryTests.cs` | reviewed |
| `Services/NotificationRetentionTests.cs` | reviewed |
| `Services/NotificationServiceTests.cs` | changed |
| `TestInfrastructure.cs` | changed |

### Touched outside the section

| Path | Disposition | Why |
|---|---|---|
| `docs/architecture/service-data-access-map.md` | changed | finding 9's repo-wide literal sweep |
| `docs/architecture/debt-ledger.yml` | changed | sweep commit |
| `src/Sections/Humans.MailerLite/Docs/debt.yml` | changed | sweep commit |
| `docs/health/runs/2026-08-26-Notifications.md` | changed (new this run) | this file |

## Threads

| Thread | How it ran | Model | Findings | Cost |
|---|---|---|---|---|
| Spine | main | opus | 3, 15 | $9.58 (shared) |
| Shape | main | opus | 6, 13 | $9.58 (shared) |
| Behavior & bugs | main | opus | 1, 4, 5 | $9.58 (shared) |
| Freshness | subagent | sonnet | 9, 10, 11, 12 | $2.75 |
| Tests | subagent | sonnet | 14, 16 | $2.28 |
| Comments | subagent | sonnet | 3, 7 | $4.22 |
| Prose | subagent | haiku | 2 | $0.93 |
| Inbox | subagent | sonnet | 1 | $1.10 |

The three main-thread rows share one figure and are marked so: Phase 3 marks the phase log once, so every main-thread call in it lands in the single `assess` bucket. Splitting it per lens would be an invented number.

Every thread ran. Findings 17 and 18 were raised later — 17 at Phase 1 when the prompt was read against the skill, 18 at Phase 4 while striking finding 5.

One thread disagreement worth recording: the Comments subagent proposed an architecture-test comment asserting that a direct `NotificationService → IRoleAssignmentService` edge "would still close a cycle through the resolver". That presumes the resolver survives, which is the open question in finding 15, so it was rejected and replaced with a narrower claim the assertion can actually carry.

## Cost

| Component | Phase | Model | Fresh in | Out | Cache write | Cache read | ~$ |
|---|---|---|---|---|---|---|---|
| worktree | phase1 | opus | 20 | 3,712 | 20,329 | 955,958 | 0.70 |
| select section | phase2 | opus | 6 | 994 | 3,493 | 310,169 | 0.20 |
| assess | phase3 | opus | 134 | 76,768 | 286,042 | 11,743,359 | 9.58 |
| Freshness (subagent) | phase3 | sonnet | 122 | 4,010 | 271,708 | 5,554,644 | 2.75 |
| Tests (subagent) | phase3 | sonnet | 88 | 534 | 284,193 | 4,027,458 | 2.28 |
| Comments (subagent) | phase3 | sonnet | 148 | 1,166 | 490,518 | 7,884,636 | 4.22 |
| Prose (subagent) | phase3 | haiku | 560 | 5,984 | 382,677 | 4,196,336 | 0.93 |
| Inbox (subagent) | phase3 | sonnet | 48 | 287 | 171,596 | 1,494,021 | 1.10 |
| strike: baseline test run | phase4 | opus | 10 | 8,693 | 36,250 | 1,166,810 | 1.03 |
| strike: delete 4 dead resx keys x6 cultures | phase4 | opus | 78 | 36,560 | 86,111 | 10,184,676 | 6.54 |
| strike: false DI-cycle comments, history cuts, meter collapse, Forbid(), Issue* mapping | phase4 | opus | 164 | 54,153 | 247,681 | 9,931,096 | 7.87 |
| strike: doc sweep, filter/tab collapse, test dedup + 2 invariant tests | phase4 | opus | 76 | 16,815 | 68,768 | 6,132,444 | 3.92 |
| sweep | phase5 | opus | 24 | 23,402 | 30,281 | 2,181,528 | 1.87 |
| run file + retro | phase5 | opus | 38 | 7,878 | 18,550 | 3,767,281 | 2.20 |
| **total** | | | 1,516 | 240,956 | 2,398,197 | 69,530,416 | **45.17** |

API-equivalent $, list rates; run under subscription quota. Measured Phase 1 to PR creation; PR create/backfill and Phase 8 excluded.
