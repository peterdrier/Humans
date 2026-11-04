# section-doctor — Onboarding — 2026-11-04

- Invocation: unattended (scheduled routine section-doctor-daily)
- Anchor commit: `6956f2105` (origin/main at branch point); branch `section-doctor/2026-11-04T003857Z`.
- Budget: 2.5h.
- PR: pending

## Assessment summary

A re-doctor against `3912d0511`. Since the last run the review queue moved out to Consent, the consent step started embedding Consent's signing component, and the follow-ups to a member's own writes came home to `IOnboardingIntake`. The code followed those moves cleanly; the prose did not. The section doc, both feature docs, both csproj comments and the guide still described the old shape: a dispatcher every step returns to, a dashboard checklist with a safety-check row, a status table, a coordinator review that gates something, a Base reference that doesn't exist, and board voting on an internal interface. The behavior walk found no defect.

Independence check: pass. Findings 1 and 2 came from the target (§1 and the §2 note that steps hand forward), finding 15 from its Guest seam.

Target diff: the previous target was right about the shape; its cites had drifted (four line numbers, the retired-notification cite pointed at Notifications instead of Consent's declaration) and its structure section still described several `Section*.cs` files and `_ConsentReviewBody`.

Session lines: 2026-11-04, cloud container, no app boot and no live render; InspectCode and reforge not run (not installed).

## Findings

1. The feature docs describe a dashboard "Getting Started" checklist with per-step state and a safety-check row, and a dashboard status flow with a Rejected status; neither exists (`MembershipCalculator.ComputeStatusAsync` is the real classifier).
2. `Onboarding.md` and the guide say every step page returns to the dispatcher; Names hands forward to Shifts and the Shifts actions to Consents.
3. The Contracts csproj comment claims Base references Users.Contracts (it is the other way), names `ConsentResource` and `_ConsentReviewBody`, lists the review queue as kept internal here, and carries lane and issue tags.
4. The Onboarding csproj comment says Consent is the one section project referenced and names `ConsentResource`; an empty `ItemGroup` sits at the end.
5. `Onboarding.md` routes board voting through `IGovernanceManagement` (internal; voting is on the concrete `ApplicationDecisionService`), names `OnboardingReview_*`/`OnboardingBanner_*` prefixes that no key carries, and says the threshold flips `Profile.ConsentCheckStatus` (it sets Consent's review `Status`).
6. `Onboarding.md` narrates its own history: issue tags, "moved", "came home", a migration Status line, and a long account of Users' lifecycle service.
7. Other sections repeat false claims about Onboarding: `Users.md` gives the threshold a coordinator notification, `Program.cs` says the membership filter bounces to Guest/Home, Governance's resource and view comments name `OnboardingReview_*` keys.
8. `OnboardingService.SetConsentCheckPendingIfEligibleAsync` is public and returns a bool only a test reads.
9. The test project carries a dead `ToUserInfo` stub with a false doc comment, a csproj comment counting "two controllers", and test comments that narrate past bugs.
10. Small code noise: a two-step initializer in `GuestController`, a builder default parameter no caller relies on, a restating controller comment, a cast where `UserClaimsPrincipal` exists.
11. Freshness triggers miss files the docs make claims about (the name filter, Shifts' signup service and controller, Governance views, Consent's section and controllers, Users' admin controller, `MembershipCalculator`).
12. The pipeline doc sends Stage 1 to "Profile Setup", says the banner is rendered from the layout, and keeps a one-time grandfather migration as if it were current.
13. The shifts step decides the viewer's early-entry privilege in its controller; the effective lock is a Shifts fact.
14. `Views/Welcome/Index.cshtml` carries one hardcoded English line pointing at the English-only Tour page.
15. peterdrier/Humans#2103 is shipped; its last section-doctor comment already says so and recommends close.

## Debt verified

- The section has no `Docs/debt.yml` and the central ledger has no Onboarding row; nothing to verify. The cap did not bind.

## Worked

- 1, 12 — sonnet executor rewrote the two feature docs from the code; its extra same-class strikes (the "displayed on the dashboard" line, the second grandfather section and the "safety checks" link label) were checked against the source and kept.
- 2, 5, 6, 11 — `Onboarding.md` and the guide corrected, narration struck, triggers added.
- 3, 4 — both csproj comments rewritten short and true; empty `ItemGroup` cut.
- 7 — the off-section claims fixed at each site.
- 8 — made private, returning `Task`; its theory now goes through `ConsentSignedAsync` and keeps the merged-alias case.
- 9, 10 — cut. Mechanical: visibility, initializer and comment changes, so no reviewer pack was built.
- 13 — ledgered as CENTRAL-107.
- `health.md` regenerated; trace gate clean apart from the deliberately-not-done name.

## Skipped

- 14 — Peter's call (Needs Peter).
- 15 — no issue comment: the state matches the last section-doctor comment on the issue.
- Feedback, Finance and Notifications were blocked by open section-doctor work.
- The Tests thread's coverage gaps (threshold negative branches, no coordinator notification on the threshold, reject cancellation) were not written this run; they test absences or paths already pinned elsewhere and none failed a reading.

## Retro

Selector: right pick. The section had changed shape under three other PRs and nobody had reread its prose; the code needed almost nothing.

Wasted motion: the first push went out before the full-suite run (the section test project was green); the full suite ran right after and is recorded in the PR.

Striking revealed: the feature docs were the worst drift, and only the Freshness thread's reading of the dashboard views caught it; no trace or grep would have, because every name in them still resolves.

The conformance root-file detector flags `OnboardingAuditActions.cs` and `OnboardingNotificationSources.cs`, which every section now carries at its root since the per-section declaration lists landed; the allow-regex predates that convention. Advisory only, the file is Peter's.

## Needs Peter

- [ ] 14 — localize the Welcome page's Tour line, or keep it English-only as a written exception?

## File coverage

| Path | Disposition |
|---|---|
| `docs/guide/Onboarding.md` | changed |
| `src/Sections/Humans.Onboarding.Contracts/Humans.Onboarding.Contracts.csproj` | changed |
| `src/Sections/Humans.Onboarding.Contracts/IOnboardingIntake.cs` | reviewed |
| `src/Sections/Humans.Onboarding.Contracts/OnboardingResult.cs` | reviewed |
| `src/Sections/Humans.Onboarding/Contracts/IOnboardingShiftsStep.cs` | reviewed |
| `src/Sections/Humans.Onboarding/Controllers/GuestController.cs` | changed |
| `src/Sections/Humans.Onboarding/Controllers/OnboardingWidgetController.cs` | changed |
| `src/Sections/Humans.Onboarding/Controllers/WelcomeController.cs` | reviewed |
| `src/Sections/Humans.Onboarding/Docs/Onboarding.md` | changed |
| `src/Sections/Humans.Onboarding/Docs/authorization.md` | reviewed |
| `src/Sections/Humans.Onboarding/Docs/data-access.md` | reviewed |
| `src/Sections/Humans.Onboarding/Docs/features/onboarding-pipeline.md` | changed |
| `src/Sections/Humans.Onboarding/Docs/features/volunteer-status.md` | changed |
| `src/Sections/Humans.Onboarding/Docs/health.md` | changed |
| `src/Sections/Humans.Onboarding/Humans.Onboarding.csproj` | changed |
| `src/Sections/Humans.Onboarding/Models/ConsentsStepViewModel.cs` | reviewed |
| `src/Sections/Humans.Onboarding/Models/GuestDashboardViewModel.cs` | reviewed |
| `src/Sections/Humans.Onboarding/Models/NamesViewModel.cs` | reviewed |
| `src/Sections/Humans.Onboarding/Models/OnboardingShiftsStepBuilder.cs` | changed |
| `src/Sections/Humans.Onboarding/Models/ShiftsStepViewModel.cs` | reviewed |
| `src/Sections/Humans.Onboarding/OnboardingAuditActions.cs` | reviewed |
| `src/Sections/Humans.Onboarding/OnboardingNotificationSources.cs` | reviewed |
| `src/Sections/Humans.Onboarding/OnboardingResource.ca.resx` | reviewed |
| `src/Sections/Humans.Onboarding/OnboardingResource.cs` | reviewed |
| `src/Sections/Humans.Onboarding/OnboardingResource.de.resx` | reviewed |
| `src/Sections/Humans.Onboarding/OnboardingResource.es.resx` | reviewed |
| `src/Sections/Humans.Onboarding/OnboardingResource.fr.resx` | reviewed |
| `src/Sections/Humans.Onboarding/OnboardingResource.it.resx` | reviewed |
| `src/Sections/Humans.Onboarding/OnboardingResource.resx` | reviewed |
| `src/Sections/Humans.Onboarding/Properties/AssemblyInfo.cs` | reviewed |
| `src/Sections/Humans.Onboarding/Section.cs` | reviewed |
| `src/Sections/Humans.Onboarding/Services/AuditEntityTypes.cs` | reviewed |
| `src/Sections/Humans.Onboarding/Services/HttpOnboardingWidgetSessionState.cs` | reviewed |
| `src/Sections/Humans.Onboarding/Services/IOnboardingService.cs` | reviewed |
| `src/Sections/Humans.Onboarding/Services/IOnboardingWidgetSessionState.cs` | reviewed |
| `src/Sections/Humans.Onboarding/Services/IOnboardingWidgetState.cs` | reviewed |
| `src/Sections/Humans.Onboarding/Services/OnboardingEmailPreviews.cs` | reviewed |
| `src/Sections/Humans.Onboarding/Services/OnboardingEmails.cs` | reviewed |
| `src/Sections/Humans.Onboarding/Services/OnboardingService.cs` | changed |
| `src/Sections/Humans.Onboarding/Services/OnboardingWidgetState.cs` | reviewed |
| `src/Sections/Humans.Onboarding/ViewComponents/OnboardingProgressBannerViewComponent.cs` | changed |
| `src/Sections/Humans.Onboarding/Views/Guest/Index.cshtml` | reviewed |
| `src/Sections/Humans.Onboarding/Views/OnboardingWidget/Consents.cshtml` | reviewed |
| `src/Sections/Humans.Onboarding/Views/OnboardingWidget/Names.cshtml` | reviewed |
| `src/Sections/Humans.Onboarding/Views/OnboardingWidget/Shifts.cshtml` | reviewed |
| `src/Sections/Humans.Onboarding/Views/Shared/Components/OnboardingProgressBanner/Default.cshtml` | reviewed |
| `src/Sections/Humans.Onboarding/Views/Welcome/Index.cshtml` | reviewed |
| `src/Sections/Humans.Onboarding/Views/_ViewImports.cshtml` | reviewed |
| `tests/Humans.Onboarding.Tests/Architecture/OnboardingArchitectureTests.cs` | reviewed |
| `tests/Humans.Onboarding.Tests/Architecture/OnboardingLocalizerBindingTests.cs` | reviewed |
| `tests/Humans.Onboarding.Tests/Controllers/GuestControllerTests.cs` | reviewed |
| `tests/Humans.Onboarding.Tests/Controllers/OnboardingWidgetControllerConsentsTests.cs` | changed |
| `tests/Humans.Onboarding.Tests/Controllers/OnboardingWidgetControllerDispatcherTests.cs` | reviewed |
| `tests/Humans.Onboarding.Tests/Controllers/OnboardingWidgetControllerNamesTests.cs` | changed |
| `tests/Humans.Onboarding.Tests/Controllers/OnboardingWidgetControllerShiftsTests.cs` | reviewed |
| `tests/Humans.Onboarding.Tests/Controllers/WelcomeControllerTests.cs` | reviewed |
| `tests/Humans.Onboarding.Tests/Humans.Onboarding.Tests.csproj` | changed |
| `tests/Humans.Onboarding.Tests/Infrastructure/TestOnboardingEmails.cs` | reviewed |
| `tests/Humans.Onboarding.Tests/Services/OnboardingEmailsTests.cs` | reviewed |
| `tests/Humans.Onboarding.Tests/Services/OnboardingServiceTests.cs` | changed |
| `tests/Humans.Onboarding.Tests/Services/OnboardingWidgetStateTests.cs` | reviewed |
| `tests/Humans.Onboarding.Tests/Services/UserInfoStubs.cs` | changed |
| `tests/Humans.Onboarding.Tests/ViewComponents/OnboardingProgressBannerViewComponentTests.cs` | reviewed |

## Threads

| Thread | How it ran | Model | Findings |
|---|---|---|---|
| Shape | main | main session | 8, 10 |
| Behavior & bugs | main | main session | none (merge resolution on the consent step and widget signups checked and found sound) |
| Controller intent | subagent (`pd:orch-opus-low`) | opus low | 13 |
| Freshness | subagent (`pd:orch-opus-low`) | opus low | 1, 2, 4, 5, 7, 11, 12 |
| Conformance | detectors on main, classified on main | main session | none (two advisory root-file hits, see Retro) |
| Tests | subagent (`pd:orch-opus-low`) | opus low | 8, 9 |
| Prose & surface | subagent (`pd:orch-haiku-medium`) | haiku medium | 7, 14 |
| History | subagent (`pd:orch-opus-low`) | opus low | 6, 12 |
| Comments | subagent (`pd:orch-opus-low`) | opus low | 3, 4, 9, 10 |
| Inbox | main | main session | 15 |

Inbox: peterdrier/Humans: covered; nobodies-collective/Humans: not covered: REST read of a known issue refused (repository not enabled for this session).
