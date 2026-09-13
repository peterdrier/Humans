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

Resource set: every key in `OnboardingResource` resolves from a live call site and all six
cultures are at parity, verified on the main thread after both haiku threads returned all-clean
with no per-key proof and disagreed with each other on the key total (finding 26).

## Ranked findings

The full ranked list, with sources and plays, is `$RUNDIR/assessment/ranked-list.md` for findings
1–24; each description below is the one prose description of findings raised after 3e.

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
- **Finding 21** (reject's success path has no positive assertion — dropping one of the three
  de-provisioned teams passes today) — same reason. This is the section's one action with real
  consequences and the test is worth writing; it needs a machine that can run it.
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
(finding 18). §4 had no line about notifications at all, which is precisely where the four-place
doc error hid (finding 4). A target that is never diffed against its predecessor launders last
run's mistakes into this run's authority.

## Needs Peter

- [ ] 1 — `/Guest` is unreachable by profileless accounts and nothing links to it; delete the page,
      exempt `Guest` from `NameRequiredFilter`, or leave it as a typed-URL relic? Docs now tell the
      truth either way; the fix changes business behaviour and touches the Shell.
- [ ] 18 — reviewer-approved surface reduction, deferred unverifiable; re-run it on a machine that
      can compile.
- [ ] 21 — reject's three-team de-provision and `ProfileRejected` notification still have no
      positive assertion.
- [ ] 25 — the cloud container cannot cold-build Razor views. Should Phase 7's gate detect this and
      declare itself unmeasured rather than a run discovering it by accident? Separately: is
      `rollForward: latestFeature` in `global.json` doing what you want, given CI resolves
      `10.0.x`?
- [ ] 26 — should `prose-gate` skip `tests/**`, or is an assertion literal genuinely in scope?
- [ ] 27 — should the Phase 4 executor brief say "fix these findings **and any instance of the same
      error class in the same files**, listing what you added"?

## Sweep queue

- debt: `docs/architecture/debt-ledger.yml` — the dead-glob row's per-page counts are stale beyond
  the Onboarding entry this run corrected; the whole row wants re-measuring before anyone runs the
  scripted pass it proposes.

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
