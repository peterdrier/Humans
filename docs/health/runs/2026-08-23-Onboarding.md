# Section Doctor — Onboarding — 2026-08-23

- **Invocation:** `/section-doctor` (no arguments), unattended scheduled run
- **Anchor:** `e191c4c9` (origin/main at branch point)
- **Branch:** `section-doctor/2026-08-23T070948Z`
- **Budget:** 2.5h default; started 07:09Z
- **PR:** [#1458](https://github.com/peterdrier/Humans/pull/1458)
- **Environment:** healthy. The `.Net` cloud environment ships the SDK, `dotnet-ef` and
  reforge, so everything built, ran and measured. Two absences, both known: **Stryker is not
  installed** and is out of scope by the schedule's own instruction, so the mutation half of
  the Tests thread is skipped-with-reason below; and **there is no Docker**, so
  `Humans.Integration.Tests` could not run — its files were read and edited, and CI is their
  gate. No `gh` CLI; GitHub work went through the GitHub MCP tools.

## Selection

Blocked set: 4 open `section-doctor/*` PRs, whose file lists were pulled before selection and
carry no `Humans.Onboarding` path.

- Pool: 42 section projects. Onboarding has never been doctored, so it sits in the
  never-doctored tier, which outranks re-doctors.
- Feature-active down-rank applied against the open PRs' file lists.
- Onboarding ranks mid-pool on the reforge surface score (94, `loc=1392` at the anchor) — a
  genuinely middle-out pick rather than the largest or smallest thing left.

## Assessment summary

Onboarding is a pure orchestrator: no tables, no `DbContext`, no repository, no cache, no
`IClock`. Its job is the intake funnel — sign up, name, shifts, consents — plus the Consent
Coordinator review queue. The orchestrator shape is intact and the funnel's routing is
deliberate and well-commented: every widget step ends at the dispatcher rather than deciding
what comes next, which is the section's single best structural idea.

What the section does badly is *tell the truth about itself*, and in two places that costs
real users something.

The target is now written at `src/Sections/Humans.Onboarding/Docs/health.md`. This is the
section's first doctor pass, so there is no previous target to diff against.

### Findings, ranked

1. **[bug — struck] The site-wide onboarding progress banner rendered its own key names to
   every mid-onboarding human, on every authenticated page, in all six languages.**
   `Views/Shared/Components/OnboardingProgressBanner/Default.cshtml` bound
   `IHtmlLocalizer<SharedResource>`; `Onboarding_BannerText` and `Onboarding_BannerCta` came
   home to `OnboardingResource` with the G5 resx carve. A localizer returns the key as its own
   value when it cannot find it, so this shipped with a green build, a 200, no log line and
   identical output in every language — which is also why a translation review would not have
   caught it. It is the first thing a new human sees above the page body.
2. **[bug — struck] The review detail page rendered `Admin_Joined` and
   `AdminHuman_MembershipTier` as literal key names to Consent Coordinators.** Same failure
   mode, opposite direction: the page bound the section's own set for two keys that are
   `SharedResource`'s.
3. **[bug — struck] An infinite redirect loop between `/OnboardingWidget` and
   `/OnboardingWidget/Consents` when a required document's detail will not load.**
   `GetNextUnsignedConsentAsync` returned `null` for two different things — "nothing left to
   sign" and "there is something to sign but I could not load it" — and the controller read
   both as the first, handing the user back to the dispatcher. The dispatcher's answer is
   still `Consents`, because the document is still unsigned, so the two bounce forever. The
   return is now a three-case outcome; the unloadable case logs at error and stops with a
   message instead of looping.
4. **[tests — struck] Nothing guarded the binding that findings 1 and 2 broke.**
   `OnboardingPageRenderTests` looks like the guard and is not: it asserts the absence of three
   *carved prefixes*, so a misbound `Admin_*` key passes it, and its banner case signs in a
   **fully onboarded** persona, for whom the banner renders nothing — the assertion could not
   have failed. `OnboardingLocalizerBindingTests` is the replacement: static, Docker-free,
   walks every `.cs` and `.cshtml` in the section, resolves each call site's binding through
   the `_ViewImports` chain, and checks the key exists in the set that call site actually
   binds. It fails the build on both shipped defects.
5. **[tests — struck] `OnboardingReviewController` — the section's only privileged surface —
   had no tests at all.** Clear, bulk-clear, flag and reject, including the stated negative
   access rule that a Volunteer Coordinator can read the queue and must not act on it. Now
   covered, with the access rule tested as what it is: an assertion over the actions' policy
   attributes, which fails when someone adds an action and inherits the class-level read
   policy by omission.
6. **[freshness — struck] `Onboarding.md` claimed `OnboardingArchitectureTests` enforces six
   rules. The file had one test.** Rather than downgrade the doc, the five missing assertions
   are now real: no repository parameter, no `DbContext` / `IDbContextFactory` / store anywhere
   in the section, every localizer bound to one of the section's three sets, and the exact
   export list for both projects.
7. **[freshness — struck] The section's prose named four deleted projects and one deleted
   file.** `Humans.Application`, `Humans.Interfaces`, `Humans.UI` and `Humans.Infrastructure`
   across nine files, and `docs/sections/Profiles.md` (merged into `Users.md`) twice. Also:
   `OnboardingService` "depends only on interfaces (plus `IClock`)" — it takes no clock;
   `INotificationService` where the code injects `INotificationEmitter`; `IAccountDeletionService`
   described as "future" when it is in `Humans.Users`; the cycle guard cited in a test project
   that no longer exists; `IRoleAssignmentService` credited with returning `OnboardingResult`
   when it returns `RoleAssignmentResult`.
8. **[freshness — struck] The routing and authorization tables omitted the flow a new human
   actually walks.** `Onboarding.md`'s route table listed the review queue and two global
   filters and none of `/Welcome`, `/Guest` or the eight `/OnboardingWidget/*` routes;
   `authorization.md` omitted both controllers, one of which (`WelcomeController`) is the
   section's only anonymous surface.
9. **[freshness — struck] The feature spec described the inverse of the shipped bulk-clear.**
   `onboarding-pipeline.md` said the server "only clears those that are still pending and
   still have a legal name". `BulkClearConsentChecksAsync` filters against
   `Pending.Concat(Flagged)`, so a **flagged** row is bulk-clearable, and there is **no
   server-side legal-name check** at all — the view merely withholds the checkbox. Two wrong
   claims about a privileged bulk write. Also two rules numbered `8.` and a `/Profile/Edit`
   that is `/Profile/Me/Edit`.
10. **[freshness — struck] The user guide describes a flow the app does not route people
    into.** `docs/guide/Onboarding.md` documents the legacy linear Profile → Consent path;
    `MembershipRequiredFilter` sends a `Bare` user to `/OnboardingWidget` and
    `NameRequiredFilter` to `/OnboardingWidget/Names`. The widget appears nowhere in "Key pages
    at a glance" or in steps 2 and 3. Its freshness triggers did not watch the widget either,
    which is how it drifted unnoticed — they now do.
11. **[shape — struck] `OnboardingProgressBannerViewComponent` was the section's only
    unnecessary public type.** Shell's `SectionViewComponentFeatureProvider` exists precisely
    so a section's view components can be internal, `SectionChrome` invokes it by name, and the
    integration test's own comment already called it internal. Making it internal is what let
    finding 6's export-list assertion be written as the doc states it.
12. **[dedup / prose — struck] Four small things in the review and guest views.** The
    bulk-clear button existed twice, identical but for `disabled`. `Detail.cshtml`'s
    `else if (status == Cleared)` is unreachable as anything but `else`. The queue's "Review"
    link, its checkbox `aria-label` and the Guest dashboard's deletion confirmation were
    hard-coded English on otherwise fully localized pages. Two resx keys
    (`OnboardingReview_DetailTitle`, `OnboardingReview_Consents`) were named nowhere in `src/`
    or `tests/`, in all six locales.
13. **[delete — struck] Dead test scaffolding.** The dispatcher tests built a
    `UserManager<User>` substitute per test and never handed it to the controller, which does
    not take one. The widget-state tests called their `IBurnSettingsService` substitute
    `_shiftMgmt`.
14. **[conformance — reported] `section-file-layout` flags `SectionAdminNav.cs` and
    `SectionChrome.cs` at the project root, and the finding is the detector's, not
    Onboarding's.** The same pattern is repo-wide and universal: `SectionAdminNav.cs` in 29
    sections, `SectionJobs.cs` in 17, `SectionPolicies.cs` in 10, and six more `Section*.cs`
    kinds besides. Proposal in Needs-Peter; `section-conformance.yml` is read-only to every
    run, so nothing was changed.
15. **[conformance — reported] `resource-key-prefix`: most of Onboarding's keys are not
    prefixed `Onboarding_`.** The set carries `OnboardingReview_`, `OnboardingBanner_` and
    `Guest_` prefixes too. Reported, not backfilled — renaming a resource key touches six
    locale files and every call site, and the run has no ruling on whether the rule means the
    section prefix or the literal string.
16. **[tests — queued] The remaining invariant-matrix gaps.** No test for
    `OnboardingWidgetController.Skip`, `SignUpRange` or the `Shifts` GET; none for
    `OnboardingProgressBannerViewComponent`; none for `BulkClearConsentChecksAsync`'s own
    eligibility filter at the service level (finding 5 covers the controller's reporting of
    its result, not the filter). `Skip` writes a session key that `OnboardingWidgetState` reads
    — a coupling by string constant across two files with nothing pinning it.
17. **[debt — queued, off-section] Three more misbound resource keys, in Users.**
    `Profile_EmailDeleted`, `Profile_EmailVisibilityUpdated` and
    `Profile_NotificationTargetUpdated` in `Humans.Users/Controllers/ProfileController.cs`
    (bound `UsersResource`), and `Admin_SortBy` in `Views/UsersAdmin/AdminList.cshtml`. Found
    by running the finding-4 sweep repo-wide for scoping. Users' run, not this one — which is
    why the new test is scoped to this section rather than to the repo.
18. **[debt — queued, off-section] The admin sidebar is untranslated by construction, and
    `SharedResource.Nav_OnboardingReview` is dead.** `AdminNavItem.Label` is a raw string
    rendered as-is by Shell's `AdminSidebarViewComponent`, so all 29 contributing sections
    hard-code English. Seven `Nav_*` keys in `SharedResource` are now named nowhere in `src/`
    or `tests/`. Base's, not Onboarding's.

**Independence check: pass.** Findings 1, 2 and 3 — the three real defects — came from reading
the section against the derived target, not from any tool, score or grep: two misbound
resource sets and one `null` carrying two meanings. Findings 6 through 10 came from checking
every claim in every doc against the tree. No tool produced any finding on this run; reforge
produced the selection input and the `## Size` snapshot and nothing else. **Honest caveat:**
the reforge and conformance tool threads ran concurrently with the 3b/3c reading rather than
strictly after 3c, so the reading was not blind to them — it was blind to their *findings*,
because they produced none.

## Worked

- Bound the progress banner's two keys and the review detail page's two `Admin_*` labels to
  the sets that carry them (findings 1, 2).
- Gave `GetNextUnsignedConsentAsync` a three-case outcome so an unloadable document stops with
  a logged error and a message instead of looping against the dispatcher (finding 3). New key
  `Onboarding_ConsentDocumentUnavailable` in all six locales.
- Wrote `OnboardingLocalizerBindingTests` — every rendered key in the section checked against
  the set its call site binds, with a broken-sweep guard so a scan that resolves nothing fails
  rather than passing (finding 4).
- Wrote `OnboardingReviewControllerTests` — the negative access rule and the four actions'
  outcome reporting (finding 5).
- Made `OnboardingArchitectureTests` enforce what the doc says it enforces: five new
  assertions (finding 6), which required making the banner view component internal (finding 11).
- Re-derived the section's docs and comments from the code across the files in
  `## File coverage` (findings 7–10), including the user guide and its freshness triggers.
- Collapsed the unreachable `else if`; localized the three
  hard-coded English strings; deleted two dead resx keys across six locales; removed the dead
  `UserManager` scaffolding and renamed the misnamed substitute (findings 12, 13).
- Applied the merged 2026-08-22 Cantina run's sweep queue: 21 lessons into this skill. Its
  three `memory:` items were **not** applied, correctly: two are marked superseded by
  peterdrier/Humans#1454, which landed `memory/process/cloud-run-dotnet-bootstrap.md` and was
  then deliberately retired by #1456 when the environment started shipping the toolchain; the
  third (`section-doctor-no-sdk`) was absorbed into the skill's Phase 0 by that same PR.
  Creating any of the three would re-add guidance Peter removed. No `debt:` items in the queue.

## Skipped, and why

- **Findings 14–18** — reported or queued, not struck. 14 and 15 are conformance findings and
  `section-conformance.yml` is read-only to every run; 17 and 18 are other sections' files and
  belong to their runs; 16 is test work the budget did not reach after the five strikes above.
- **A mid-onboarding persona for `HumansWebApplicationFactory`** — which is what would make
  `OnboardingPageRenderTests`' banner assertion non-vacuous. The factory offers only
  `SignInAsFullyOnboardedAsync`, there is no Docker here to run the result against, and new
  integration-test infrastructure written blind is worse than none. Queued for Peter.
- **The mutation-score half of the Tests thread** — Stryker is not installed and is out of
  scope by the schedule's instruction. The invariant matrix, which is the other half, ran and
  produced findings 5 and 16.
- **`Humans.Integration.Tests`** — no Docker in this container. Its one touched file is
  comment-only; CI is the gate.
- **Sections passed over as blocked:** none of the 4 open run PRs touches Onboarding.
- **Phase 8 inline round:** unattended run, per the schedule's instruction.
- **The Phase 4.4 reviewer gate:** not obtained. This session is instructed not to dispatch
  subagents, so all five commits are **self-reviewed**, not reviewed. Recorded here, in the
  PR body, and in the Needs-Peter queue.

## Retro

- **The guard that looked like a guard.** `OnboardingPageRenderTests` is a careful, well-commented
  file whose banner assertion could not fail, because it signs in the one persona for whom the
  banner renders nothing. Nothing in a run's normal reading catches that: the test is present,
  named for the thing, and green. What caught it was asking *what state must the fixture be in
  for this assertion to have anything to assert against* — a question worth asking of every
  `NotContain` in a render test, because the empty page passes them all.
- **Reading the localizer bindings beat every tool available.** The two shipped defects are
  invisible to the compiler, to the analyzers, to the tests, to the reforge score and to a
  translation review, and identical in all six languages. They were found by reading two views
  and asking which set each key lives in. This is the second run in a row (after Cantina) where
  the whole finding set came from reading and none from a tool.
- **Making the doc true beat making the doc honest.** `Onboarding.md` promised six architecture
  assertions against one real test. Deleting five claims would have been a legitimate freshness
  fix and would have left the section less guarded than its own documentation believed. Writing
  them cost about twenty minutes and found a public type that should not have been public.
- **`git fetch origin <branch>` does not populate `origin/<branch>`** the way the run's first
  `.prs.json` build assumed, so every blocked PR came back with an empty file list — a blocked
  set that silently reports "touches nothing" is worse than an error. Diff against
  `origin/main...origin/<branch>`, and sanity-check that a non-empty PR yields a non-empty list.

## Threads

| Thread | Ran | Notes |
|---|---|---|
| Shape | yes (main) | Finding 11. The orchestrator shape itself is clean — no repository, no context, no cache. |
| Behavior & bugs | yes (main) | Findings 1, 2, 3, by walking the funnel against the target's invariants. |
| Freshness | yes (main) | Findings 7–10. Every name in every doc and comment resolved against the tree. |
| Conformance | yes (main) | Findings 14, 15. Detectors read by hand against the section; the yml is read-only to a run. |
| Tests | partial (main) | Invariant matrix built by hand → findings 4, 5, 16. **Mutation score not measured:** Stryker is not installed and is out of scope by the schedule's instruction. |
| Prose & surface | yes (main) | Finding 12's dead keys and hard-coded English; six-locale parity re-verified after every resx edit (105 keys each). |
| History | yes (main) | The `GetPendingReviewCountAsync` note in `Onboarding.md` was true and incomplete — the badge came back as `PillCounts.ReviewQueue`. Corrected rather than cut. |
| Comments | yes (main) | Every comment in the 3a inventory read; the corrections are in finding 7. |
| Inbox | yes (main) | No `debt-ledger.yml` item is tagged Onboarding; no open GitHub issue names the section. |
| **Reforge (tool)** | **yes** | Produced the selection input and the `## Size` snapshot. No finding. |
| **Stryker (tool)** | **no** | Not installed; out of scope by the schedule's instruction. The mutation dimension is unmeasured. |
| **InspectCode (tool)** | **no** | Not installed in this environment. The analyzers that do run are in the build, which is green. |
| **Runtime verification** | **no** | No Docker, so no integration run and no rendered page. Findings 1 and 2 are render defects, which is exactly what this thread would have caught — the static binding test written at finding 4 is the substitute, and it does catch both. |

## File coverage

- `docs/guide/Onboarding.md` — changed
- `src/Sections/Humans.Onboarding.Contracts/Humans.Onboarding.Contracts.csproj` — changed
- `src/Sections/Humans.Onboarding.Contracts/IOnboardingIntake.cs` — changed
- `src/Sections/Humans.Onboarding.Contracts/IOnboardingWidgetState.cs` — reviewed
- `src/Sections/Humans.Onboarding.Contracts/OnboardingResult.cs` — changed
- `src/Sections/Humans.Onboarding/Controllers/GuestController.cs` — reviewed
- `src/Sections/Humans.Onboarding/Controllers/OnboardingReviewController.cs` — reviewed
- `src/Sections/Humans.Onboarding/Controllers/OnboardingWidgetController.cs` — changed
- `src/Sections/Humans.Onboarding/Controllers/WelcomeController.cs` — reviewed
- `src/Sections/Humans.Onboarding/Docs/Onboarding.md` — changed
- `src/Sections/Humans.Onboarding/Docs/authorization.md` — changed
- `src/Sections/Humans.Onboarding/Docs/data-access.md` — changed
- `src/Sections/Humans.Onboarding/Docs/features/onboarding-pipeline.md` — changed
- `src/Sections/Humans.Onboarding/Docs/features/volunteer-status.md` — reviewed
- `src/Sections/Humans.Onboarding/Docs/health.md` — changed (created)
- `src/Sections/Humans.Onboarding/Humans.Onboarding.csproj` — changed
- `src/Sections/Humans.Onboarding/Models/ConsentsStepViewModel.cs` — reviewed
- `src/Sections/Humans.Onboarding/Models/NamesViewModel.cs` — reviewed
- `src/Sections/Humans.Onboarding/Models/OnboardingReviewDetailViewModel.cs` — reviewed
- `src/Sections/Humans.Onboarding/Models/OnboardingReviewIndexViewModel.cs` — reviewed
- `src/Sections/Humans.Onboarding/Models/OnboardingShiftsStepBuilder.cs` — changed
- `src/Sections/Humans.Onboarding/Models/ShiftsStepViewModel.cs` — changed
- `src/Sections/Humans.Onboarding/OnboardingResource.ca.resx` — changed
- `src/Sections/Humans.Onboarding/OnboardingResource.cs` — changed
- `src/Sections/Humans.Onboarding/OnboardingResource.de.resx` — changed
- `src/Sections/Humans.Onboarding/OnboardingResource.es.resx` — changed
- `src/Sections/Humans.Onboarding/OnboardingResource.fr.resx` — changed
- `src/Sections/Humans.Onboarding/OnboardingResource.it.resx` — changed
- `src/Sections/Humans.Onboarding/OnboardingResource.resx` — changed
- `src/Sections/Humans.Onboarding/Section.cs` — changed
- `src/Sections/Humans.Onboarding/SectionAdminNav.cs` — reviewed
- `src/Sections/Humans.Onboarding/SectionChrome.cs` — reviewed
- `src/Sections/Humans.Onboarding/Services/AuditEntityTypes.cs` — changed
- `src/Sections/Humans.Onboarding/Services/HttpOnboardingWidgetSessionState.cs` — changed
- `src/Sections/Humans.Onboarding/Services/IOnboardingService.cs` — changed
- `src/Sections/Humans.Onboarding/Services/IOnboardingWidgetSessionState.cs` — changed
- `src/Sections/Humans.Onboarding/Services/OnboardingService.cs` — changed
- `src/Sections/Humans.Onboarding/Services/OnboardingWidgetState.cs` — reviewed
- `src/Sections/Humans.Onboarding/ViewComponents/OnboardingProgressBannerViewComponent.cs` — changed
- `src/Sections/Humans.Onboarding/Views/Guest/Index.cshtml` — changed
- `src/Sections/Humans.Onboarding/Views/OnboardingReview/Detail.cshtml` — changed
- `src/Sections/Humans.Onboarding/Views/OnboardingReview/Index.cshtml` — changed
- `src/Sections/Humans.Onboarding/Views/OnboardingWidget/Consents.cshtml` — reviewed
- `src/Sections/Humans.Onboarding/Views/OnboardingWidget/Names.cshtml` — reviewed
- `src/Sections/Humans.Onboarding/Views/OnboardingWidget/Shifts.cshtml` — changed
- `src/Sections/Humans.Onboarding/Views/Shared/Components/OnboardingProgressBanner/Default.cshtml` — changed
- `src/Sections/Humans.Onboarding/Views/Welcome/Index.cshtml` — reviewed
- `src/Sections/Humans.Onboarding/Views/_ViewImports.cshtml` — changed
- `src/Sections/Humans.Onboarding/Views/_ViewStart.cshtml` — reviewed
- `tests/Humans.Integration.Tests/Controllers/OnboardingPageRenderTests.cs` — changed
- `tests/Humans.Onboarding.Tests/Architecture/OnboardingArchitectureTests.cs` — changed
- `tests/Humans.Onboarding.Tests/Architecture/OnboardingLocalizerBindingTests.cs` — changed (created)
- `tests/Humans.Onboarding.Tests/Controllers/OnboardingReviewControllerTests.cs` — changed (created)
- `tests/Humans.Onboarding.Tests/Controllers/OnboardingWidgetControllerConsentsTests.cs` — changed
- `tests/Humans.Onboarding.Tests/Controllers/OnboardingWidgetControllerDispatcherTests.cs` — changed
- `tests/Humans.Onboarding.Tests/Humans.Onboarding.Tests.csproj` — reviewed
- `tests/Humans.Onboarding.Tests/Services/OnboardingServiceTests.cs` — changed
- `tests/Humans.Onboarding.Tests/Services/OnboardingWidgetStateTests.cs` — changed
- `tests/Humans.Onboarding.Tests/Services/UserInfoStubs.cs` — changed

## Size

Measured against the anchor `e191c4c9`, which is the PR's base.

| Measure | Against `e191c4c9` |
|---|---|
| Section (`Humans.Onboarding` + `.Contracts`) | +371 / −155 → net **+216** |
| Tests | +549 / −32 → net **+517** |
| `docs/guide/Onboarding.md` | +40 / −4 → net **+36** |
| Shared sweep file (this skill) | +66 / −0 |
| Sections touched other than Onboarding | none |

**Reconciliation, which stays true however long this file grows:** the branch's insertions
minus this file's own line count must equal **1026** (= 371 + 549 + 40 + 66), and its deletions
must equal **191** (= 155 + 32 + 4 + 0). This file's own line count and the whole-branch total
are deliberately not stated — they cannot be, since the commit writing the number is a commit
the number has to count. GitHub's `additions`/`deletions` on the PR settles the branch total
on demand and is never stale.

Reforge, Onboarding: score **94 → 94**; `loc` 1392 → 1441; `cogP95` 5 → 5; `cogMax` 7 → 7
(`OnboardingWidgetState.GetCurrentStepAsync`); `maxClassLoc` 253 → 261 (`OnboardingService`).
Solution-wide `combined` 19956, unchanged.

**Growth, and why.** This is the first run in recent memory that leaves its section *bigger*,
so the trade is stated rather than glossed. Of the section's +216: the three-case consent
outcome and its handling is about 40 lines of real code, the new resx key is 6 lines × 6
locales, and the rest is doc and comment text — which reforge counts as `locProd`, hence
`loc` +49 against a diff that added far more prose than code. The tests' +517 is four-fifths
of the whole branch and buys the two static guards plus the review queue's first coverage. The
section did not grow because behaviour was added; it grew because a section that could not
describe itself is now described, and two defects that nothing could catch are now catchable.

## Cost

| Component | Fresh in | Out | Cache write | Cache read | ~$ |
|---|---|---|---|---|---|
| main:phase2 | 28 | 4,973 | 46,181 | 1,326,153 | 1.08 |
| main:phase3 | 594 | 234,938 | 719,629 | 57,320,051 | 39.03 |
| **total** | 622 | 239,911 | 765,810 | 58,646,204 | **40.11** |

API-equivalent $ at list rates; the run itself is under subscription quota. Measured from
Phase 1 to PR creation. The cloud transcript layout the skill flags as unverified turned out
to work — see Needs Peter item 6. **Caveat:** the phase log carries no marker after `phase3`,
so phases 4–7 are all attributed to the `phase3` row; the total is right, the split is not.

## Review round

Phase 8 was skipped by the schedule's instruction, so the review that would have run inline ran
on the PR instead. Two bot findings and one the surface report caught; all three handled on the
branch.

1. **Upheld — `disabled="@(!hasSelectableReviews)"` is a documented hard reject.** Finding 12
   collapsed the bulk-clear button's two branches into one with an expression-valued `disabled`.
   `code-review-rules.md` §"Razor Boolean Attributes" (8+ historical fixes) bans exactly that,
   "regardless of context". The collapse *works* — Razor omits a false-valued attribute — and
   that is the point of the rule: the broken version is indistinguishable from this one, so the
   rule refuses the shape rather than adjudicating each instance. Reverted to the two-branch
   form, with a comment naming the rule so the next run does not re-collapse it. **The strike
   was wrong and the reviewer was right**: a dedup that trades a documented rule for four lines
   is not a dedup.
2. **Refuted — the `else if (Cleared)` → `else` collapse.** Codex read the `else` as a catch-all
   that would tell a coordinator a never-reviewed human had been cleared. It is not: the `if`
   above it tests `Model.ConsentCheckStatus != ConsentCheckStatus.Cleared`, and the property is
   `ConsentCheckStatus?`, so C#'s lifted `!=` sends **null down the actions branch**, not the
   `else`. The `else` is reachable only for `Cleared`, exactly as before. Replied on the thread
   rather than changing code. (The behaviour Codex describes for a null status — a coordinator
   seeing Clear/Flag/Reject on someone not in the queue — is real, pre-existing, and unchanged
   by this branch; it is Needs Peter item 2's question, which this run had already asked.)
3. **Self-caught — `.phase-log` and `.prs.json` were committed.** The PR surface report listed
   both under New Files. They are per-run scratch and the skill says never commit them; a
   strike's `git add -A` cannot tell them from the files it means to stage. Untracked and added
   to `.gitignore`, which is the fix that survives the next run.

## Needs Peter

1. [ ] **Widen the `section-file-layout` detector, or the template, to `Section[A-Za-z]*.cs`.**
   (Finding 14.) The detector's allow-regex is `Section\.cs`, so it flags `SectionAdminNav.cs`
   and `SectionChrome.cs` here — and the same two files, plus seven more kinds, in 29 other
   sections. Every section does this; either the template should say so or the detector should
   stop reporting it. **Answered 2026-08-23 — widen it: "yes, you're right, rule needs
   expanding."** Allow-regex is now `Section[A-Za-z]*\.cs`. Peter also gave the direction of
   travel: *"realistically though we're going to merge the other classes into Section.cs with
   multiple interface implementations"* — so the sibling files are transitional, not the target
   shape. Recorded in both the detector's notes and `G5-SECTION-TEMPLATE.md` step 2, which now
   says prefer folding into one `Section.cs` and do not add a new sibling kind.

   Widening drops this rule from ~30 noise hits to **12 real ones**, all pre-existing and none
   in Onboarding: `Health/` in Agent, Email, GoogleIntegration, Guide and Tickets; `Helpers/` in
   Events, Shifts and Users; `Extensions/` in Users; and three loose root files
   (`LogApiKeyAuthFilter.cs` in Debug, `HoldedSectionOptions.cs`, `StoreSectionOptions.cs`).
   **`Health/` in five sections looks like the next `Section*.cs`** — a convention the allow-list
   never learned — but that is a separate call from the one made here, so it is reported, not
   changed.
2. [x] **Should a cleared human be re-flaggable or rejectable from the review detail page?**
   **Answered 2026-08-23 — yes, allow it; enforce nothing.** "If need be we can reject a person
   later if there's cause. You don't need to enforce anything though, just allow the state
   change with the right permissions." So the view stops withholding **Reject** once `Cleared`
   — the verb the answer names — and only the clear form is replaced by the already-cleared
   note. The service keeps its permissive behaviour deliberately, and the gate is the
   `ConsentCoordinatorBoardOrAdmin` policy on the action. Recorded as an invariant in
   `health.md` so a later run does not "fix" it back.

   **Flag was pulled back out during review, and this half needs Peter.** The first pass
   exposed Flag too. Codex flagged it, and it was right: `RecordConsentCheck` sets
   `Profile.IsApproved = (status == Cleared)` (`Humans.Users/Services/UserService.cs:380`) and
   `SystemTeamSyncJob` gates Colaborador/Asociado team membership on that flag
   (`Humans.Teams/Services/SystemTeamSyncJob.cs:466`). So a `Cleared → Flagged` on a tier member
   silently drops them from their tier team on the next hourly sync — a kick-out dressed as an
   annotation, and not the "allow the state change" that was asked for. Volunteers admission was
   already carved out of `IsApproved` for exactly this reason
   (`SystemTeamSyncJob.cs:194` — "the Flagged consent-check is an audit annotation that no longer
   gates admission"); the tier path never got the same treatment.

   Flag is therefore still hidden for a cleared human, with a comment in the view naming the
   cascade. The real fix is cross-section and is Peter's call — either `Flagged` stops writing
   `IsApproved`, or tier eligibility stops reading it. Both live in `Humans.Users`/`Humans.Teams`,
   outside this run's section (cf. item 5), so nothing was changed there.
3. [x] **`resource-key-prefix` for Onboarding: section prefix or literal string?**
   (Finding 15.) **Answered 2026-08-23 — the literal `Onboarding_`, and rename.** "It's not for
   user benefit, but it's tech debt." 78 of the set's 105 keys moved:
   `OnboardingReview_X` → `Onboarding_ReviewX`, `OnboardingBanner_X` → `Onboarding_BannerX`,
   `Guest_X` → `Onboarding_GuestX`, `Welcome_X` → `Onboarding_WelcomeX`; the 27 already on
   `Onboarding_` are untouched. Applied across all six locales and every call site, including
   Governance's `BoardVoting/Detail.cshtml`, which binds this section's set through
   `OnboardingLocalizer` and is the one legitimate off-section consumer.
4. [x] **Add a mid-onboarding persona to `HumansWebApplicationFactory`.** Without one,
   `OnboardingPageRenderTests`' banner assertion is vacuous — which is how findings 1 and 2
   shipped past it. **Answered 2026-08-23 — there was no question in it; written.**
   `SignInAsMidOnboardingAsync` is on the factory and the banner now has a test that renders it.

   The part worth knowing: *skipping* the consent seed is not enough. A fresh integration DB has
   no legal documents, so `HasAllRequiredConsentsForTeamAsync` is vacuously true and the persona
   is `Complete` the moment dev-login seeds it — the incomplete state has to be **manufactured**.
   The helper seeds a required, active Volunteers document with an already-effective version,
   invalidates both startup-warmed caches, then dev-logs in without consenting to it. It then
   asserts the resulting step is not `Complete` and throws if it is, so the helper cannot quietly
   regress into the vacuum it exists to close.

   **And the first version of it always failed.** Codex caught it post-push: dev login runs
   `DevPersonaSeeder.EnsureActiveAsync` on every request, on both the create and already-exists
   paths, and that method walks `MissingConsentVersionIds` and submits every one. So seeding the
   required document *before* logging in guarantees the seeder signs it — the persona reaches
   `Complete` and the guard throws every time. The order is inverted now (sign in, then introduce
   a document that did not exist at login), and the document is created fresh per call rather
   than reused, because a reused one is signed by the next call's login for the same reason.

   **Correction — there is no gate.** This entry originally read "CI is the gate — no Docker
   here." That is false, and checking it was the run's job. `.github/workflows/build.yml:111`
   runs `dotnet test … --filter "FullyQualifiedName!~Humans.Integration.Tests"`, and the only
   workflow that does run that project (`localization-sweep.yml`, cron 1st & 15th) filters to
   `~LocalizationCoverageSweep`. So `OnboardingPageRenderTests` executes nowhere: not locally
   (no Docker in the doctor's environment), not in `build`, not in the sweep. The helper and
   the banner test are written and compile, and both are unproven. Whether
   `Humans.Integration.Tests` should get a CI job at all is a separate question — the project
   currently builds on every branch and is then filtered out, and `localization-sweep.yml`
   demonstrates that GitHub-hosted runners have the Docker the self-hosted ones lack. Filed as
   item 8 of the section-doctor retrospective.
5. [x] **Widen `OnboardingLocalizerBindingTests` repo-wide once Users and Governance are
   clean.** (Finding 17.) The sweep already runs repo-wide; it is scoped to this section only
   because four real hits remain in Users. **Answered 2026-08-23 — leave them: "those sound
   like they're in Users, outside the scope of this section-doctor run."** The four keys stay
   in the sweep queue for Users' own run, and the test stays section-scoped until then.
5b. [x] **Is the untranslated admin sidebar debt or a decision?** Raised because
   `AdminNavItem.Label` is a raw string rendered as-is, so all 29 contributing sections
   hard-code English. **Answered 2026-08-23 — a decision: "admin side stuff is English only
   until further notice."** So it is not debt and no run should re-report it; the sweep-queue
   entry is rewritten to say so. The eight dead `Nav_*` keys in `SharedResource` are a
   separate matter — still dead, still Base's, still queued.
6. [ ] **`cost-report.py` works in the cloud environment — the skill should stop warning that
   it might not, and should learn to emit a phase marker per phase.** The transcript layout the
   skill flags as unverified is the layout the script reads, so `## Cost` above is real. What is
   not real is the split: the phase log stops at `phase3`, so phases 4–7 all land in that row.
   A one-line marker write at the top of each phase would fix it.
7. [ ] **The reviewer gate was not obtained.** This session is instructed not to dispatch
   subagents, so all five commits are self-reviewed. Same standing item as the 2026-08-18 and
   2026-08-22 runs.

## Sweep queue

- `lesson: 2026-08-23 — "make the fixture incomplete" is usually harder than skipping a seed step. Onboarding's mid-onboarding persona could not be built by omitting the consent seed: with no legal documents in a fresh test DB the required-consent check is vacuously true and the user is Complete anyway. The incomplete state had to be manufactured (seed a required doc, then do NOT consent). Whenever a fixture is meant to be partway through something, assert the partway-ness inside the helper and throw — otherwise it silently becomes the complete case again.`
- `lesson: 2026-08-23 — a resource-key rename is verifiable in a way most refactors are not: after renaming, diff the set of keys referenced in source against the set the resx carries, in both directions. That check found the three [Display(Name=...)] keys in Onboarding live in SharedResource rather than the section's set (correct — Program.cs routes all DataAnnotations lookups there), which no amount of reading the diff would have shown.`
- `lesson: 2026-08-23 — a NotContain assertion in a render test is only as good as the fixture's state. Onboarding's banner assertion signed in a fully-onboarded persona, for whom the banner renders nothing, so it could not fail — and the two keys it was written to guard shipped misbound. Ask of every render assertion: what must the fixture be for this to have anything to assert against?`
- `lesson: 2026-08-23 — a key looked up against the wrong IStringLocalizer<T> is not an error. The localizer returns the key as its own value, so the page renders the raw key with a green build, a 200, no log line, and identically in every language — which is why translation review misses it too. Check every localizer binding in a section against the set that actually carries each key; it is cheap and no tool does it.`
- `lesson: 2026-08-23 — a resx carve leaves misbindings in both directions. Onboarding had keys that came home still bound to SharedResource, and SharedResource keys bound to the section's set. A guard that only asserts the absence of the carved prefixes catches the first kind and never the second.`
- `lesson: 2026-08-23 — when a doc claims a test enforces N rules, count the assertions before believing or deleting it. Onboarding.md listed six and the file had one. Writing the five missing ones was cheaper than the freshness fix looked and found a public type that should have been internal.`
- `lesson: 2026-08-23 — a null return that means two things is a redirect loop waiting to happen. GetNextUnsignedConsentAsync returned null for "nothing left to sign" and for "could not load the document"; the caller handed both back to the dispatcher, whose answer for the second is still the page the user just came from. When a resolver's null has two causes and the caller routes on it, make the cases explicit.`
- `lesson: 2026-08-23 — git fetch origin <branch> does not populate origin/<branch> the way the Phase 2 blocked-set build assumes. Diff origin/main...origin/<branch>, and assert the file list is non-empty for a PR known to have files; a blocked set that silently reports "touches nothing" un-blocks every section.`
- `lesson: 2026-08-23 — a sweep-queue item marked SUPERSEDED may have been superseded and then retired. Cantina's two memory items pointed at an atom that #1454 landed and #1456 deliberately deleted when the environment started shipping the toolchain. Check whether the successor still exists before applying or skipping; applying would have re-added guidance Peter removed.`
- `lesson: 2026-08-23 — a doc-and-comment re-derivation pass grows the section's reforge loc, because doc comments on production code are production LOC. Onboarding's loc went 1392 → 1441 on a run whose code changes were about 40 lines. State the growth and its cause in ## Size rather than letting a "docs only" framing hide it.`
- `lesson: 2026-08-23 — before a dedup strike collapses a two-branch view into one, grep docs/architecture/code-review-rules.md for the shape you are collapsing INTO. The bulk-clear collapse produced disabled="@boolExpr", which that file rejects regardless of context, and review blocked it. A dedup that costs a documented rule is not a win, and "but it works" is not the test — the rule exists because the broken shape looks identical to the working one.`
- `lesson: 2026-08-23 — a run's scratch files get committed unless .gitignore stops them. The skill says never commit .phase-log or .prs.json, and this run committed both, because a strike's git add -A cannot distinguish them from the files it means to stage. An instruction to a future self is not a control; the .gitignore line is. Check git ls-files for run scratch before the PR, and prefer ignoring over remembering.`
- `lesson: 2026-08-23 — when a bot review says a collapsed conditional broke a null case, check the operator that was actually lifted before believing it. Codex read else-after-"!= Cleared" as a catch-all reaching null; C#'s lifted != sends null down the OTHER branch, so the collapse was exact. Refuting takes one look at the property's nullability and the if above it — but so does confirming, and skipping the look either way is how a bot finding becomes a bad commit.`
- `lesson: 2026-08-23 — an unattended run that skips Phase 8 has not skipped review, it has deferred it onto the PR. Three findings landed there within minutes of opening, one of them a block. Plan for the post-push round: do not tear down the worktree assuming the run is over, and keep the check-in armed.`
- `debt: 2026-08-23 — four pre-existing violations of code-review-rules.md §"Razor Boolean Attributes", all the banned bool-valued form rather than the sanctioned ? "x" : null string form: Humans.Shifts/Views/VolunteerTracking/_ExportCard.cshtml line 50 (disabled="@subPeriodHidden") and lines 34 and 44 (selected="@(expr == expr)"), and Humans.MailerLite/Views/MailerLite/Admin/Debug.cshtml line 67 (selected="@(a.Key == Model.SelectedKey)"). Found by sweeping the repo for the pattern after review blocked the same shape in Onboarding; left to Shifts' and MailerLite's own runs (found by /section-doctor on Onboarding, 2026-08-23).`
- `lesson: 2026-08-23 — cost-report.py does read the cloud transcript layout; the skill's "may not work here" hedge is stale and a run should just measure. What it cannot do is split the phases: the phase log stops being written after phase3, so every later phase is attributed to that row. Write a phase marker at the top of each phase, not just the first three.`
- `debt: 2026-08-23 — four misbound resource keys in Users: Profile_EmailDeleted, Profile_EmailVisibilityUpdated and Profile_NotificationTargetUpdated in Humans.Users/Controllers/ProfileController.cs (bound UsersResource, keys are SharedResource's), and Admin_SortBy in Humans.Users/Views/UsersAdmin/AdminList.cshtml. Each renders as its own key name to the user in all six languages. Found by running Onboarding's new binding sweep repo-wide (found by /section-doctor on Onboarding, 2026-08-23).`
- `decision: 2026-08-23 — the admin sidebar being English-only is DELIBERATE, not debt. AdminNavItem.Label is a raw string rendered as-is by Shell's AdminSidebarViewComponent and all 29 sections contributing SectionAdminNav hard-code English; Peter: "admin side stuff is english only until further notice." Do not report it as a conformance or localization finding, and do not localize AdminNavItem without asking first.`
- `debt: 2026-08-23 — eight Nav_* keys in SharedResource are named nowhere in src/ or tests/: Nav_Home, Nav_BoardVoting, Nav_Review, Nav_Voting, Nav_Board, Nav_Scanner, Nav_Agent, Nav_OnboardingReview. Dead because the admin sidebar takes raw strings — but the English-only decision above is about the sidebar, not about keeping dead keys, so these are still deletable. Base's set, so Base's run (found by /section-doctor on Onboarding, 2026-08-23).`
- `debt: 2026-08-23 — HumansControllerBase.SetError resolves ILoggerFactory from HttpContext.RequestServices while SetSuccess and SetInfo resolve nothing, so a controller unit test passes for two of the three and throws ArgumentNullException on the third. Two Onboarding test files now carry the same six-line RequestServices/ActionDescriptor scaffolding to work around it; injecting the logger would delete both copies (found by /section-doctor on Onboarding, 2026-08-23).`
- `debt: 2026-08-23 — the two Humans.Onboarding.Docs data-access blocks for HumanLifecycleService and NonCompliantMemberSuspension describe Users-owned services and belong in src/Sections/Humans.Users/Docs/data-access.md, which does not carry them. Left in place with a pointer rather than moved, because the concurrency contract forbids a run writing another section's file (found by /section-doctor on Onboarding, 2026-08-23).`
- `debt: 2026-08-23 — /Profile/Edit is stale in three places outside Users: a comment in Humans.Consent/Controllers/ConsentController.cs, a doc comment in Humans.Consent.Tests/Controllers/ConsentControllerTests.cs, and step 2 of .claude/skills/test-site/SKILL.md. The live route is /Profile/Me/Edit (found by /section-doctor on Onboarding, 2026-08-23).`
