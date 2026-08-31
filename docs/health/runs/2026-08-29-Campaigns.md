# section-doctor — Campaigns — 2026-08-29

Invocation: unattended daily run (cloud, `CLAUDE_CODE_REMOTE=true`, repo root, no worktree).
Branch: `section-doctor/2026-08-29T071612Z`. Anchor commit: `c6e5fb67` (origin/main).
Budget: standard daily. PR: pending.

Caveat: the container was suspended mid-assessment — 2026-08-29 07:30Z through
2026-08-31 21:03Z — so assessment ran on the 29th and striking/bookkeeping on the 31st,
in one session with one auto-compaction between them. Two reading threads' final messages
(Comments, Prose) were recovered from their transcripts after the resume.

## Assessment summary

First doctoring of Campaigns (never-doctored tier, selected as median by reforge score;
selection details in Phase 2's output). Target shape written fresh at
`src/Sections/Humans.Campaigns/Docs/health.md`. Eight lenses ran (see `## Threads`);
conformance and razor-lint came back clean, and reforge confirmed no structural strike is
warranted — the section's shape is right, and its residual weight was prose: false doc and
comment claims, history narration, dead record fields, and a handful of view/test gaps.
All five cross-assembly contract members were verified live at their external call sites
(Tickets, Email, Users).

## Ranked findings (numbers are permanent)

Independence check: pass — items 1–16 trace to target-vs-reality reading, 17 to the shape
read, 19 to the target's structure statement, 22–27 to the invariant matrix. No tool
contributed a ranked item.

1. Phantom `CampaignGrant.User` nav narrated in 2 repo comments — false; rewritten. **done**
2. False "no unique index" comment in `ReassignGrantsToUserAsync` — unique `(CampaignId, UserId)` exists. **done**
3. Malformed doc block in `ICampaignRepository` — merge-fold summary sat on `DeleteGrantsForUserAsync`. **done**
4. Four false ordering promises on contract/repo surface — views sort by design; claims dropped. **done**
5. `Campaigns.md` cross-section names wrong (`ITeamServiceRead.GetTeamsAsync`/`GetTeamAsync`, `INotificationEmitter`, `IUserServiceRead`, `GuestAccountController`). **done**
6. `Campaigns.md` "controller enforces" Draft-only vendor generation — service enforces. **done**
7. `Campaigns.md` overbroad HTML-encoding claim — subject substitutes unencoded (plain text, deliberate); narrowed to body. **done**
8. `data-access.md`: uninjected `ICommunicationPreferenceService` listed; `ITicketDiscountCodes` mislabeled; Singleton lifetime missing. **done**
9. `Campaigns-feature.md`: no auto-complete path; "Completed = all codes assigned" false; stale grant→outbox diagram edge. **done**
10. Contracts csproj header: nonexistent projects, consumers misplaced in Base, wrong counts. **done**
11. Wrong "sixteen members" counts in `ICampaignService` remarks + `CampaignService` class doc — rewritten countless. **done**
12. `SectionAdminNav` cited dead `docs/sections/Campaigns.md` path. **done**
13. History-rewrites cluster ("after the Campaigns migration lands", bare issue refs, byte-identical-move narration, "(step 12)", "Issues' fold", duplicate nav-removed claim, …). **done**
14. `_ViewImports` named `GateArchitectureTests` (guards Gate, not Campaigns) as backstop. **done**
15. `ICampaignServiceRead` "display names sourced from the Campaigns section" — resolved via Users. **done**
16. `CampaignGrantConfiguration` comment cited drifted line numbers — rewritten without numbers. **done**
17. Dead fields: `GrantWithSendContext.CampaignTitle`+`CampaignId`; `CampaignCodeTrackingSummaryRow.CampaignCreatedAt`. **done (deleted)**
18. Dead `ProjectReference Humans.Shifts.Contracts` in tests csproj. **done (deleted)**
19. `Detail.cshtml` badge switches duplicated `EnumBadgeMap` (local Queued had drifted from Base's `bg-warning text-dark`). Collapsed into `EnumBadgeMap.For`; reviewer-approved. **done**
20. Create/Edit ~48-line duplicated form body — shared `_CampaignFormFields` partial (Calendar precedent); reviewer-approved. **done**
21. Comment cuts: region banners (repo + tests), restating markers, migration narration. **done**
22. `EnumStringStabilityTests` asserted subset (`Contain`) not set — now `BeEquivalentTo`. **done**
23. SendWave non-Active guard had no negative test. **done**
24. Per-grant failure isolation never exercised — added a test that throws from `SendAsync`. **done**
25. SendWave code-order test asserted a disjunction — seeder now sets `ImportOrder`; exact assert. **done**
26. Cross-assembly surface untested — added merge fold (target-wins), erasure, export tests. Remainder (`UpdateGrantEmailStatusAsync`, `GetCodeTrackingAsync`, grant-read tests) queued as in-section debt. **partially done**
27. Overlapping template-forwarding email tests asserted the factory, not the send — merged into one asserting `SendAsync` receives the factory's message. **done**
28. Admin POSTs surface raw 500s on wrong state: double-click Activate/Complete, SendWave insufficient-codes race, ImportCodes bad id, SendWave GET unguarded by status (page renders for Draft/Completed; POST 500s). Fixing changes behavior (error toasts/redirects instead of 500s) → Needs-Peter. **queued**
29. `Detail.cshtml` Resend column rendered for TicketAdmin who gets 403 on click — hidden behind `isAdmin`; reviewer-approved. **done**
30. Detail/SendWave lack breadcrumbs (Index/Create/Edit have them). **skipped** — low value; both pages have working back buttons, and a nav-pattern change is more than this run's budget wanted to spend against item 28's pending ruling on those same pages.
31. Freshness trigger gaps — section docs assert about Email/Users/Tickets files their triggers didn't watch; trigger lists extended. **done**

## Worked

Strike commits on the branch, in ranked order: dead deletions (17, 18); data-layer
comment truth (1, 2, 3, 4-repo, 16, 21-part); contracts surface (4-contracts, 10, 11, 15);
section-file references (12, 13, 14, 21-part); section docs (5–9, 13, 31); views
(19, 29, 21-part, 20 — all four reviewer-gated, all approved); tests (22–25, 27, 26-part);
plus the target shape, this file, and the sweep commit.

## Skipped

- 28 — Needs-Peter (behavior change).
- 30 — see its entry above.
- 26 remainder — queued to the section's `debt.yml` (sweep queue below).
- Sections passed over in selection: Camps (blocked — open doctor PR peterdrier/Humans#1561),
  Surveys (feature-active).

## Retro

- **What did the selector/rubric get wrong?** Nothing observed — Campaigns was a good
  median pick: enough defects to be worth a run, no structural work needed.
- **Wasted motion.** The container suspension cost a session resume, one auto-compaction,
  and two thread finals that had to be dug out of transcripts. The cost report's footer
  will show the compaction; per Phase 5's rule the phases were re-read before bookkeeping.
  Also: `git add src/Sections/Humans.Campaigns/Docs/` in the first strike commit
  unintentionally swept the untracked `health.md` into that commit — harmless (it belongs
  on the branch) but sloppy staging.
- **What did the assessment miss that striking revealed?** The test seeder never set
  `ImportOrder`, which is *why* the old code-order test asserted a disjunction — the
  assessment read the disjunction as timidity when it was actually masking a fixture gap.
  Also `MA0006` (string.Equals analyzer) fired on a new test helper; trivial, but the
  assessment's "tests are clean" read didn't predict analyzer friction on new test code.
- **What does the target diff say?** First run on this section — no previous target to
  diff. The target's own two corrections during the run (subject-line encoding claim,
  view count after the partial) were both places where the 3c draft repeated the section
  docs' overclaims instead of the code; a target written before the scans inherits the
  docs' errors until the threads catch them.

## Needs Peter

- [ ] 28 — admin POSTs 500 on wrong state (double-click Activate/Complete, SendWave races/GET): fix to toast+redirect, or leave as-is?
- [ ] 32 — Phase 4 lesson: when a strike stages a section's `Docs/` directory wholesale, an untracked working file (here the 3c target shape) rides into the wrong commit; propose Phase 4 name files explicitly in `git add` rather than staging directories.
- [ ] 33 — Phase 3c lesson: the target shape draft inherited two overclaims from the section docs it summarized (subject-line encoding; view count); propose Phase 3c require each invariant line in the target to cite code, not docs, the first time a section is doctored.

## Sweep queue

- `debt: Humans.Campaigns — cross-assembly members still without direct tests after finding 26's additions: UpdateGrantEmailStatusAsync, GetCodeTrackingAsync, and the two grant reads (GetActiveOrCompletedGrantsForUserAsync / GetAllGrantsForUserAsync). The service-level wave/resend tests exercise them indirectly; direct pins are the gap (finding 26, /section-doctor on Campaigns 2026-08-29).`

## File coverage

| Path | Disposition |
|---|---|
| docs/guide/Campaigns.md | changed |
| src/Sections/Humans.Campaigns.Contracts/CampaignCodeTrackingDtos.cs | reviewed |
| src/Sections/Humans.Campaigns.Contracts/Humans.Campaigns.Contracts.csproj | changed |
| src/Sections/Humans.Campaigns.Contracts/ICampaignService.cs | changed |
| src/Sections/Humans.Campaigns.Contracts/ICampaignServiceRead.cs | changed |
| src/Sections/Humans.Campaigns/Controllers/CampaignController.cs | reviewed |
| src/Sections/Humans.Campaigns/Data/CampaignRepository.cs | changed |
| src/Sections/Humans.Campaigns/Data/CampaignsDbContext.cs | changed |
| src/Sections/Humans.Campaigns/Data/CampaignsDbContextFactory.cs | changed |
| src/Sections/Humans.Campaigns/Data/Configurations/CampaignCodeConfiguration.cs | reviewed |
| src/Sections/Humans.Campaigns/Data/Configurations/CampaignConfiguration.cs | reviewed |
| src/Sections/Humans.Campaigns/Data/Configurations/CampaignGrantConfiguration.cs | changed |
| src/Sections/Humans.Campaigns/Data/ICampaignRepository.cs | changed |
| src/Sections/Humans.Campaigns/Data/Migrations/20260809125024_BaselineCampaigns.Designer.cs | reviewed (immutable) |
| src/Sections/Humans.Campaigns/Data/Migrations/20260809125024_BaselineCampaigns.cs | reviewed (immutable) |
| src/Sections/Humans.Campaigns/Data/Migrations/CampaignsDbContextModelSnapshot.cs | reviewed (immutable) |
| src/Sections/Humans.Campaigns/Docs/Campaigns.md | changed |
| src/Sections/Humans.Campaigns/Docs/authorization.md | reviewed |
| src/Sections/Humans.Campaigns/Docs/data-access.md | changed |
| src/Sections/Humans.Campaigns/Docs/features/Campaigns-feature.md | changed |
| src/Sections/Humans.Campaigns/Docs/health.md | generated (this run's target shape) |
| src/Sections/Humans.Campaigns/Domain/Campaign.cs | reviewed |
| src/Sections/Humans.Campaigns/Domain/CampaignCode.cs | reviewed |
| src/Sections/Humans.Campaigns/Domain/CampaignGrant.cs | reviewed |
| src/Sections/Humans.Campaigns/Domain/CampaignStatus.cs | reviewed |
| src/Sections/Humans.Campaigns/Humans.Campaigns.csproj | changed |
| src/Sections/Humans.Campaigns/Models/CampaignViewModels.cs | changed |
| src/Sections/Humans.Campaigns/Properties/AssemblyInfo.cs | reviewed |
| src/Sections/Humans.Campaigns/Section.cs | changed |
| src/Sections/Humans.Campaigns/SectionAdminNav.cs | changed |
| src/Sections/Humans.Campaigns/Services/CampaignService.cs | changed |
| src/Sections/Humans.Campaigns/Services/Dtos/CampaignDtos.cs | reviewed |
| src/Sections/Humans.Campaigns/Views/Campaign/Create.cshtml | changed |
| src/Sections/Humans.Campaigns/Views/Campaign/Detail.cshtml | changed |
| src/Sections/Humans.Campaigns/Views/Campaign/Edit.cshtml | changed |
| src/Sections/Humans.Campaigns/Views/Campaign/Index.cshtml | reviewed |
| src/Sections/Humans.Campaigns/Views/Campaign/SendWave.cshtml | reviewed |
| src/Sections/Humans.Campaigns/Views/Campaign/_CampaignFormFields.cshtml | generated (new, finding 20) |
| src/Sections/Humans.Campaigns/Views/Campaign/_ViewStart.cshtml | reviewed |
| src/Sections/Humans.Campaigns/Views/_ViewImports.cshtml | changed |
| tests/Humans.Campaigns.Tests/Architecture/CampaignsArchitectureTests.cs | reviewed |
| tests/Humans.Campaigns.Tests/Controllers/CampaignControllerTests.cs | reviewed |
| tests/Humans.Campaigns.Tests/Enums/EnumStringStabilityTests.cs | changed |
| tests/Humans.Campaigns.Tests/Humans.Campaigns.Tests.csproj | changed |
| tests/Humans.Campaigns.Tests/Services/CampaignServiceTests.cs | changed |

## Threads

| Thread | How it ran | Model | Findings |
|---|---|---|---|
| Shape | main thread | fable | 6 |
| Behavior & bugs | main thread | fable | 12 |
| Inbox | self-run on main thread | fable | 0 actionable — fork/upstream issue halves complete (no Campaigns-tagged backlog); in-app issues half skipped: no live instance reachable from a cloud run |
| Freshness | doctor-reader subagent | opus (effort low) | fed ranked 5–9, 31 |
| Tests | doctor-reader subagent | opus (effort low) | fed ranked 18, 22–27 (invariant matrix) |
| History | doctor-reader subagent | opus (effort low) | fed ranked 10, 13 |
| Comments | doctor-reader subagent | opus (effort low) | 42 verdicts (final recovered from transcript after suspension) |
| Prose & surface | subagent | haiku | 6 + lint-clean note (final recovered from transcript after suspension) |
| Conformance | subagent | haiku | 0 — ran clean |

The strike-gate reviewer (doctor-reviewer, fable, effort high) ran once, over the three
non-mechanical view strikes (19, 20, 29): all approved.
