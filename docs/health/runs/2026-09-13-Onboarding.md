# section-doctor — Onboarding — 2026-09-13

- Invocation: unattended daily run (cloud), no arguments; Phase 8 skipped per the stored prompt.
- Anchor commit: `4b43e6f2` (origin/main at branch point); branch `section-doctor/2026-09-13T011559Z`.
- Re-doctor tier; `BASE: 3ed83626a64ca9cdca6992795aacac2072202195` (commit that added the last run file).
- Budget: standard daily (~2.5 h).
- PR: peterdrier/Humans#1663

## Assessment summary

Onboarding is an orchestrator that owns no tables. Its C# did not move since run 1 — the
`BASE..HEAD` diff is docs, resx and test deletions — so every change to the target this run is
run 1's target having been wrong, not the section drifting. That framing is what produced the
headline: findings 1, 5, 17 and 18 came out of regenerating the target and diffing it, not out of
any scan, and no tool in the set reports finding 1.

The section's own prose was the worst thing about it. Claims across `Docs/Onboarding.md`, both
feature specs and the user guide were false against the code, and code comments were false in the
dangerous direction — one described a safe operation that is not safe (finding 2), another
promised a notification that is never sent (finding 4).

Headline: finding 1. `/Guest` cannot be reached by the audience it exists for.

Resource set: every key in `OnboardingResource` resolves from a live call site and every supported
culture is at parity, verified on the main thread after both haiku threads returned all-clean
with no per-key proof and disagreed with each other on the key total (finding 26).

## Ranked findings

Value = bug surface removed, concepts removed, reader cost removed. Effort is a column, not the
sort key. Findings 1–24 are 3e's ranked list; 25–27 were raised after it, each where it was raised.

| # | Finding | Source | Play | Effort |
|---|---|---|---|---|
| 1 | **`/Guest` is unreachable by its stated audience, and every path into it bounces that audience straight back out.** `NameRequiredFilter` exempts only {Account, Language} + (OnboardingWidget,Names)/(Home,Error)/(Home,Privacy); a profileless account has `Profile is null` so `HasRequiredNameFields` is false and it is redirected to the name form. `MembershipRequiredFilter` still exempts `Guest` for that audience, and `Program.cs:458-461` says the ordering is deliberate (#812: name gate before "MembershipRequiredFilter bounces it to Guest/Home"). So the page renders only for a named, fully-onboarded member — and shows them a "Create your profile" CTA. **Corrected in review (2026-09-13):** the original wrote "nothing links or redirects to `/Guest`", which is false — it grepped the literal route and missed tag helpers and `RedirectToAction`. `CommunicationPreferences.cshtml:12`/`:113` link there, and `GuestAccountController:51,103,108,114,132,136` and `GuestDataController:56` redirect there. That makes the defect worse, not smaller: `Guest/CommunicationPreferences` is `[AllowAnonymous]`, so a profileless account does reach it, and its breadcrumb and error path then bounce it off the name gate. Not an orphan page — a live loop. | **target** (shape 1's split authority) | docs tell the truth this run; the page's fate is **Needs Peter 1** | S (docs) |
| 2 | `OnboardingService.cs:144-146` — Flag comment says "the Flagged flag is a record nothing acts on". FALSE: `RecordConsentCheck` → `UserService.cs:367` `profile.IsApproved = cleared`, and `SystemTeamSyncJob` gates the tier teams on it. `health.md` §4/§5 and `Detail.cshtml:128-137` say the opposite. An editor trusting this comment would expose Flag and silently kick tier members out of their team. | Comments C1 | fix comment | S |
| 3 | `Onboarding.md:27,123` present `IAdminDashboardService` / `AdminDashboardService` / `GetAdminDashboardAsync` as current siblings. Repo-wide untruncated grep over `src`+`tests`: no code at all; `Users/Section.cs:32` says both are "gone entirely". | History H3 | cut | S |
| 4 | A Consent-Coordinator notification on the review threshold is claimed at `Onboarding.md:41`, `:101`, `:117`, `onboarding-pipeline.md:157` and in `IOnboardingIntake.cs`'s XML doc. `ConsentReviewNeeded` is a retired source; the only emitter call in the section is `ProfileRejected`. | main + Freshness F-5, F-8 | cut, and record the true rule in the target | S |
| 5 | **Target §4 "every step page redirects to the dispatcher, never to a named next step" is false against the code.** `:99` → Shifts, `:158`/`:172`/`:180` → Consents; only `SignConsent` → Index. The tests pin the code. Inherited unchallenged from run 1's target. | **target** (regenerated vs previous) | correct the target | S |
| 6 | `onboarding-pipeline.md:37` "Key Change" and `volunteer-status.md:165`'s diagram both still put CC clearance on the admission path, contradicting `:103`/`:166`/`:170`, `:168`/`:171` and §4. This is the Volunteer-vs-approval conflation AGENTS.md names as the first way to hurt yourself. | Freshness F-9, F-11 | cut | S |
| 7 | `onboarding-pipeline.md:193` ("signups auto-promoted on admission") contradicts `:208` ("no post-admission promotion step"). No promotion code exists. | Freshness F-7 | cut `:193` | S |
| 8 | `Onboarding.md` self-contradictions: `:102`/`:88` attribute the peer call to `OnboardingWidgetController.Consents` (the GET step page) — it is in `SignConsent` POST, and `:60` says so; `:88` vs `:139` disagree on `IOnboardingIntake` vs `IOnboardingService`. | Freshness F-1, F-6 | fix | S |
| 9 | `Onboarding.md:123` — "both ProfileController and GuestController deletion actions call through it". This `GuestController` has no deletion action; deletion is Users' `GuestAccountController.cs:98`/`:126`. | Freshness F-2 / History H4 | fix | S |
| 10 | `Onboarding.md:142` — "see each repository's XML docs for the onboarding-support block": no such block in `IUserRepository` or `IApplicationRepository`. | Freshness F-3 | cut | S |
| 11 | "legacy linear flow (Profile → Consents) **or** the widget" narrated as a live alternative at `Onboarding.md:89` and `onboarding-pipeline.md:147`. Run 1 struck the same claim from `docs/guide`; the in-section copies were missed. | History H5 | cut | S |
| 12 | `docs/guide/Onboarding.md:102`/`:107` understate the removal levers (suspension and grace-period expiry also remove); `:109` describes an access split that never holds. | Freshness F-14, F-15 | fix | S |
| 13 | `volunteer-status.md:113` "Rejected … by Admin" — reject is `ConsentCoordinatorBoardOrAdmin`. | Freshness F-12 | fix | XS |
| 14 | `OnboardingReviewController.cs:14-17` — "Review queue for Consent Coordinators and Volunteer Coordinators": every mutating action is `ConsentCoordinatorBoardOrAdmin`, so the summary names a role that cannot act and omits roles that can. | Comments C3 | fix | XS |
| 15 | `Detail.cshtml:138` points at "the run file's Needs Peter item 2" — no such artifact in the tree. Live home is `health.md` §5. | Comments C2 | repoint | XS |
| 16 | `_ViewImports.cshtml:14-18` names the review pages' `<vc:>` elements wrongly — the file binds `<vc:human>` and `<vc:access-matrix>`; ProfileCard is `Component.InvokeAsync("ProfileCard")`. And it cites `OnboardingPageRenderTests` as the guard — that lives in `Humans.Integration.Tests`, which self-skips in CI. | main + Comments | fix | XS |
| 17 | **`BulkClearConsentChecksAsync` calls `GetReviewQueueAsync`** — building the full render payload (a `GetMembershipSnapshotAsync` await per queued user, plus a Governance pending-application lookup) purely to `.Select(u => u.Id)` for an eligibility filter. Pending ∪ Flagged is exactly `NeedsConsentReview \|\| IsConsentCheckFlagged`, both predicates on `UserInfo`. | **target** (shape 5 leaking into shape 3) | collapse; reviewer-gated | M |
| 18 | **`IOnboardingWidgetState` + `OnboardingWidgetStep` are public leaf surface with no consumer outside the section.** All references are in `Humans.Onboarding` / `.Contracts`. The leaf's own csproj comment states the rule: "Everything with no consumer outside the section stays internal". Its remarks justify the placement by "Shell's GuestController", which moved into this section in #1091. | **target** (§2 note 3, §3) | move internal; reviewer-gated | M |
| 19 | `GuestController.cs:36-45` — try/catch wraps a pure property copy off an already-materialised `UserInfo` plus `View()`; nothing in it can throw, and `logger` is injected only for the catch. In the Shell it served other catches, all of which left in #1091/#1406. | History H1 | delete; reviewer-gated | S |
| 20 | Comment sediment: #1091 move-narration in `GuestController:12-13` and `WelcomeController:10-11`, `SectionChrome.cs:5`, the byte-identical clause at `csproj:15-18`, `OnboardingShiftsStepBuilder`'s whole `<remarks>` (narrates a type that no longer exists), `Detail.cshtml:95-101` "until this change", `Index.cshtml:141-148` trimmed to the rule, `GuestDashboardViewModel`'s name-restating summaries, `HttpOnboardingWidgetSessionState.cs:12`, `IOnboardingService.cs:42-45` duplicating `IOnboardingIntake.cs:4-7`. | Comments C4-C13 / History H6 | one sweep | M |
| 21 | **Reject's success path is untested.** The `SyncMembershipForUserAsync` calls (Volunteers, Colaboradors, Asociados) and the `ProfileRejected` notification have no positive assertion — dropping a team passes today. Only the storage-failure negative exists. This is the section's only action with real consequences. | Tests T2 | add test | M |
| 22 | Dead `UserManager<User>` fixture built in the ctors of the widget-controller test files; the controller ctor takes no `UserManager` and no assertion reads it. | Tests T9 | cut | S |
| 23 | Dead `CancellationToken` parameters on `Skip`, `SignUp`, `SignUpRange` — `IShiftSignups.SignUpAsync`/`SignUpRangeAsync` take no ct at all and `Skip` is synchronous. | History H2 | cut if budget | S |
| 24 | Debt ledger: the inbox rows naming Onboarding for Razor boolean attributes, dead `Nav_*` keys, and the misfiled `HumanLifecycleService`/`NonCompliantMemberSuspension` data-access blocks are already fixed → strike. Needing correction: the `SetError` row's symptom is no longer true, and the `docs/guide` dead-glob row's Onboarding portion is false (its `freshness:triggers` globs all resolve). | Inbox I6-I11 | sweep commit | S |

Not struck, recorded only: T1/T3/T4 (further untested invariants — queued, below the budget line),
T6-T8 (asserting-the-mock), I10 (`design-rules.md` omits `/Guest/DownloadData` — Gdpr's route, not
Onboarding's), C13, T11.

**Independence check: pass.** Findings 1, 5, 17 and 18 came from the target rather than from any
scan: 1 from §2's note that access and the funnel answer shape 1 separately, 5 from regenerating §4 and diffing it against run
1's, 17 from shape 5 leaking into shape 3, 18 from §2's new third note (only shapes 3 and 6 are
asked from outside). The headline is finding 1, which no tool in the set reports. Neither
failure symptom applies: the list is not all tool findings, and it does cite shape mismatches and
spec-vs-reality deltas.

**Ruling on finding 12** (2026-09-13, review round): its own reading of the gates was wrong in the
same way the prose it was fixing was. `NameRequiredFilter` runs first and exempts far less, and
`UserStateClassifier.Classify` returns `Active` the moment names land, so no onboarding state has a
partial access split at all. Corrected in the review round, not in the strike.

- **25** — The container cannot build this repo's Razor views from cold. A pristine worktree at
  the anchor commit `4b43e6f2` fails in `Humans.Base/Views/Shared/_Pager.cshtml` and
  `_Table.cshtml`; a clean solution build fails in `Humans.EarlyEntry`, a section this run never
  touched. Projects declare `<AddRazorSupportForMvc>true</AddRazorSupportForMvc>` and the
  generated `*_cshtml.g.cs` is nonetheless emitted as though the view were a Blazor component
  (`CS9348: A compilation unit cannot directly contain members`, `@model` type unresolved). The
  SDK here is 10.0.400; `global.json` pins `10.0.100` with `rollForward: latestFeature`, which
  permits the feature-band drift, so the pin does not protect the build. `dotnet restore` is clean
  and does not change it. Not caused by this run's changes, but this run made it visible by
  deleting a project's `obj/`: the image ships warm build artifacts, and every build before that
  point was reading them. Governs Phase 7's gate.
- **26** — `doctor.py prose-gate` fires on test assertion literals — `Received(1)`,
  `Should().Be(2)`, `Instant.FromUnixTimeSeconds(1)`. These are code, not prose that types a count
  over a list, so the gate's own remedy ("delete, list, or justify") does not apply to them.
  Governs Phase 5's prose gate.
- **27** — A mechanical-strike executor told to "fix exactly these" findings correctly refused to
  touch lines carrying the *same* error class as findings it was fixing, in the same files, and
  reported them instead. The instruction was right and the executor was right; the gap is that the
  main thread has to re-close them by hand every time. Governs Phase 4's executor brief.

## Worked

- **Findings 1, 3, 4, 6–13** — docs truth sweep. Commit
  `doctor(Onboarding): docs tell the truth about notifications, admission and /Guest`.
  Finding 1 is recorded in docs only; the page's fate is Needs Peter.
- **Finding 5** — the target's own step-redirect invariant was false against the code. Corrected
  the target, not the code; the tests pin the code's behaviour. Raised by the Tests thread against
  a target I had just regenerated.
- **Findings 2, 14, 15, 16, 20** and `IOnboardingIntake`'s XML doc — comment truth and the
  move-narration sweep. Commit `doctor(Onboarding): comments say what the code does`.
- **Findings 19, 22, 23** — deletes. Commit `doctor(Onboarding): delete dead scaffolding`.
  Finding 19 was reviewer-gated and approved.
- **Finding 17** — collapse, reviewer-gated and approved, with the shared partition the reviewer
  asked for so the eligibility predicates cannot drift. Carries the service-level test that
  was missing, confirmed non-vacuous by mutation.
- **Finding 24** — debt-ledger sweep, in the sweep commit. The Razor-boolean-attribute, dead-`Nav_*`-key and
  misfiled-data-access rows were each verified fixed and removed; the `SetError` row and the
  dead-glob row were verified stale and corrected rather than removed.

## Skipped

- **Finding 18** (move `IOnboardingWidgetState` + `OnboardingWidgetStep` internal) — reviewer
  **approved**, with conditions (`Docs/Onboarding.md:130`'s leaf description must be corrected in
  the same change; the test projects and the integration factory need a namespace swap). Not
  applied: it is a cross-project structural move, and finding 25 means this container cannot
  compile the result. Shipping an unverifiable move of a type between assemblies is not a risk
  worth taking for a surface reduction. Queued for the next run, which should re-confirm the
  sweep first — the reviewer found the original blast-radius grep had missed
  `tests/Humans.Onboarding.Tests` (harmless, IVT covers it) and mis-cited where the stale
  "Shell's `GuestController`" justification lives.
- **Finding 21** — same reason. The test is worth writing; it needs a machine that can run it.
- **T1/T3/T4, T6–T8, C13, T11** — recorded during assessment, below the budget line, not struck.
- No section was passed over as blocked.

## Retro

**What the selector/rubric got wrong:** nothing visible. Onboarding was selected by script on the
re-doctor tier and was a good pick — a section whose code is stable but whose prose had rotted
badly is exactly the case re-doctoring exists for.

**Wasted motion:** deleting a project's `obj/` while chasing a build error. It looked like
ordinary cleanup, it destroyed prebuilt state the container cannot regenerate (finding 25), and it
cost this run the ability to verify findings 18 and 21. Before that, time went into
bisecting my own commits for a breakage that was never mine — the honest tell was there early
(errors in `Humans.Base` views nobody had touched) and I chased my own diff first anyway.

**What the assessment missed that striking revealed:** both haiku threads (Conformance,
Prose-surface) returned all-clean with no per-key proof and disagreed on the key total, which
is a `threads/CONTRACT.md` violation — threads never count. Their one substantive area had to be
re-verified on the main thread, so those dispatches bought nothing (finding 26 is the adjacent
tooling lesson, not this one). The `opus-low` reader threads all produced findings that survived
verification.

**What the target diff says:** the earlier target was wrong, repeatedly, and the section did
not move. §4's step-redirect invariant was false when written (finding 5). §3 called
`IOnboardingWidgetState` "the step resolver other sections read" when no other section reads it
(finding 18). §4 had no line about notifications at all, which is precisely where the doc error
spread across docs and XML comments hid (finding 4). A target never diffed against its predecessor
launders last run's mistakes into this run's authority.

**Caught in review, not by me:** the ranked list was left in `$RUNDIR` with the run file merely
pointing at it, so the committed artifact cited finding numbers that nothing in the repo defined —
`phases/bookkeeping.md` says plainly that the ranked list is where a 3e finding's one description
lives. Scratch dies with the container; a run file that points into it ships empty. Transcribe the
list before opening the PR, not after a reviewer asks.

## Needs Peter

- [ ] 1 — a profileless account reaches `Guest/CommunicationPreferences` (it is `[AllowAnonymous]`)
      and every way out of that page points at `/Guest`, where the name gate bounces it. Exempt
      `Guest` from `NameRequiredFilter`, retarget those links and redirects, or delete the page?
      Docs now tell the truth either way; the fix changes business behaviour and touches the Shell.
- [ ] 18 — reviewer-approved surface reduction, deferred unverifiable; re-run it on a machine that
      can compile.
- [ ] 21 — worth a test on a machine that can compile, or leave the success path unasserted?
- [ ] 25 — the cloud container cannot cold-build Razor views. Should Phase 7's gate detect this and
      declare itself unmeasured rather than a run discovering it by accident? Separately: is
      `rollForward: latestFeature` in `global.json` doing what you want, given CI resolves
      `10.0.x`?
- [ ] 26 — should `prose-gate` skip `tests/**`, or is an assertion literal genuinely in scope?
- [ ] 27 — should the Phase 4 executor brief say "fix these findings **and any instance of the same
      error class in the same files**, listing what you added"?

## Sweep queue

- none. The dead-glob row this run touched carries its own scripted pass; a queue entry restating
  what that row already says would be a second copy of the same record.

## File coverage

changed:

- `docs/guide/Onboarding.md`
- `src/Sections/Humans.Onboarding.Contracts/IOnboardingIntake.cs`
- `src/Sections/Humans.Onboarding/Controllers/GuestController.cs`
- `src/Sections/Humans.Onboarding/Controllers/OnboardingReviewController.cs`
- `src/Sections/Humans.Onboarding/Controllers/OnboardingWidgetController.cs`
- `src/Sections/Humans.Onboarding/Controllers/WelcomeController.cs`
- `src/Sections/Humans.Onboarding/Docs/Onboarding.md`
- `src/Sections/Humans.Onboarding/Docs/features/onboarding-pipeline.md`
- `src/Sections/Humans.Onboarding/Docs/features/volunteer-status.md`
- `src/Sections/Humans.Onboarding/Docs/health.md`
- `src/Sections/Humans.Onboarding/Humans.Onboarding.csproj`
- `src/Sections/Humans.Onboarding/Models/GuestDashboardViewModel.cs`
- `src/Sections/Humans.Onboarding/Models/OnboardingShiftsStepBuilder.cs`
- `src/Sections/Humans.Onboarding/SectionChrome.cs`
- `src/Sections/Humans.Onboarding/Services/HttpOnboardingWidgetSessionState.cs`
- `src/Sections/Humans.Onboarding/Services/IOnboardingService.cs`
- `src/Sections/Humans.Onboarding/Services/OnboardingService.cs`
- `src/Sections/Humans.Onboarding/Views/OnboardingReview/Detail.cshtml`
- `src/Sections/Humans.Onboarding/Views/OnboardingReview/Index.cshtml`
- `src/Sections/Humans.Onboarding/Views/_ViewImports.cshtml`
- `tests/Humans.Onboarding.Tests/Controllers/GuestControllerTests.cs`
- `tests/Humans.Onboarding.Tests/Controllers/OnboardingWidgetControllerConsentsTests.cs`
- `tests/Humans.Onboarding.Tests/Controllers/OnboardingWidgetControllerNamesTests.cs`
- `tests/Humans.Onboarding.Tests/Controllers/OnboardingWidgetControllerShiftsTests.cs`
- `tests/Humans.Onboarding.Tests/Services/OnboardingServiceTests.cs`

reviewed:

- `src/Sections/Humans.Onboarding.Contracts/Humans.Onboarding.Contracts.csproj`
- `src/Sections/Humans.Onboarding.Contracts/IOnboardingWidgetState.cs`
- `src/Sections/Humans.Onboarding.Contracts/OnboardingResult.cs`
- `src/Sections/Humans.Onboarding/Docs/authorization.md`
- `src/Sections/Humans.Onboarding/Docs/data-access.md`
- `src/Sections/Humans.Onboarding/Models/ConsentsStepViewModel.cs`
- `src/Sections/Humans.Onboarding/Models/NamesViewModel.cs`
- `src/Sections/Humans.Onboarding/Models/OnboardingReviewViewModels.cs`
- `src/Sections/Humans.Onboarding/Models/ShiftsStepViewModel.cs`
- `src/Sections/Humans.Onboarding/OnboardingResource.ca.resx`
- `src/Sections/Humans.Onboarding/OnboardingResource.cs`
- `src/Sections/Humans.Onboarding/OnboardingResource.de.resx`
- `src/Sections/Humans.Onboarding/OnboardingResource.es.resx`
- `src/Sections/Humans.Onboarding/OnboardingResource.fr.resx`
- `src/Sections/Humans.Onboarding/OnboardingResource.it.resx`
- `src/Sections/Humans.Onboarding/OnboardingResource.resx`
- `src/Sections/Humans.Onboarding/Properties/AssemblyInfo.cs`
- `src/Sections/Humans.Onboarding/Section.cs`
- `src/Sections/Humans.Onboarding/SectionAdminNav.cs`
- `src/Sections/Humans.Onboarding/Services/AuditEntityTypes.cs`
- `src/Sections/Humans.Onboarding/Services/IOnboardingWidgetSessionState.cs`
- `src/Sections/Humans.Onboarding/Services/OnboardingWidgetState.cs`
- `src/Sections/Humans.Onboarding/Services/ReviewDetailData.cs`
- `src/Sections/Humans.Onboarding/Services/ReviewQueueData.cs`
- `src/Sections/Humans.Onboarding/ViewComponents/OnboardingProgressBannerViewComponent.cs`
- `src/Sections/Humans.Onboarding/Views/Guest/Index.cshtml`
- `src/Sections/Humans.Onboarding/Views/OnboardingReview/_ViewStart.cshtml`
- `src/Sections/Humans.Onboarding/Views/OnboardingWidget/Consents.cshtml`
- `src/Sections/Humans.Onboarding/Views/OnboardingWidget/Names.cshtml`
- `src/Sections/Humans.Onboarding/Views/OnboardingWidget/Shifts.cshtml`
- `src/Sections/Humans.Onboarding/Views/Shared/Components/OnboardingProgressBanner/Default.cshtml`
- `src/Sections/Humans.Onboarding/Views/Welcome/Index.cshtml`
- `tests/Humans.Onboarding.Tests/Architecture/OnboardingArchitectureTests.cs`
- `tests/Humans.Onboarding.Tests/Architecture/OnboardingLocalizerBindingTests.cs`
- `tests/Humans.Onboarding.Tests/Controllers/OnboardingReviewControllerTests.cs`
- `tests/Humans.Onboarding.Tests/Controllers/OnboardingWidgetControllerDispatcherTests.cs`
- `tests/Humans.Onboarding.Tests/Controllers/WelcomeControllerTests.cs`
- `tests/Humans.Onboarding.Tests/Humans.Onboarding.Tests.csproj`
- `tests/Humans.Onboarding.Tests/Services/OnboardingWidgetStateTests.cs`
- `tests/Humans.Onboarding.Tests/Services/UserInfoStubs.cs`

generated: none.

## Threads

| Thread | How it ran | Model | Findings |
|---|---|---|---|
| Freshness | subagent (`doctor-reader`) | opus-low | F-1 … F-15 |
| Conformance | subagent | haiku | none — all-clean, no per-key proof; re-verified on main |
| Tests | subagent (`doctor-reader`) | opus-low | T1–T11, incl. the correction to §4 that became finding 5 |
| Prose-surface | subagent | haiku | none — all-clean, no per-key proof; re-verified on main |
| History | subagent (`doctor-reader`) | opus-low | H1–H6; flagged the clone as shallow, so every pickaxe result is bounded by the graft |
| Comments | subagent (`doctor-reader`) | opus-low | C1–C13 |
| Inbox | subagent (`doctor-reader`) | opus-low | I6–I11 |
| strike docs-truth | subagent executor | sonnet | executed findings 1, 3, 4, 6–13; reported same-class lines it was not asked to touch (finding 27) |
| review: GuestController catch | subagent (`doctor-reviewer`) | inherited | APPROVE (finding 19) |
| review: bulk-clear collapse | subagent (`doctor-reviewer`) | inherited | APPROVE (finding 17), with the shared-partition condition |
| review: widget-state internalisation | subagent (`doctor-reviewer`) | inherited | APPROVE (finding 18), with conditions; deferred per finding 25 |

All threads ran; none was skipped.

## Verification

- `tests/Humans.Onboarding.Tests`: 59 passed, 0 failed, 0 skipped — run against every code change
  in this branch, and the new bulk-clear eligibility test was mutation-checked (filtering on the
  raw user list instead of the partition fails it).
- The full-solution gate (`dotnet test Humans.slnx`) **did not run**: finding 25. CI is the first
  green light this branch will get, and the PR says so.
