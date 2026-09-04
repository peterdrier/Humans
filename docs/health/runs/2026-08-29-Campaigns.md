# Section-doctor run — Campaigns — 2026-08-29

- Invocation: unattended daily (scheduled), no args
- Branch: `section-doctor/2026-08-29T191609Z`, anchored at `443fe3731f00` (fork main)
- Budget: 2.5h strike window from 19:16Z; cloud run (repo root, no worktree)
- PR: peterdrier/Humans#1564

## Assessment summary

First doctoring of Campaigns (never-doctored tier, reforge 176, median pick; Camps blocked
by open doctor PR peterdrier/Humans#1561). The section's code is in good shape — small,
layered correctly, nothing grandfathered — but its prose had drifted hard: docs and comments named services,
navigations, indexes and test guards that no longer exist, and the test project missed the
section's actual invariants (redemption matching, the merge fold, GDPR paths) while
double-covering wave email pass-through. Two real behavior findings: the import flash message
reported input line count instead of the import result, and Resend buttons rendered for
TicketAdmin users whose POST could only 403. One judgment call queued for Peter (finding 13).

## Ranked findings

Numbers are final finding numbers, assigned once. Sources: F=Freshness, T=Tests, H=History,
C=Comments, P=Prose, B=Behavior(main), S=Shape(main).

1. F5+C8+C9+C10 false ordering claims on ICampaignServiceRead + ICampaignRepository (GetAllAsync, both grant reads) — **done** (docs now say unordered, callers sort)
2. C2+H7 Reassign comment "no unique index" is false — **done**
3. C1 misplaced Reassign `<summary>` stacked on DeleteGrantsForUserAsync — **done**
4. C7+H5+H6 comments narrate nonexistent/obsolete User nav — **done**
5. F1 Campaigns.md names ITeamService.GetActiveTeamOptionsAsync/GetTeamMembersAsync (don't exist) — **done** (ITeamServiceRead.GetTeamsAsync/GetTeamAsync)
6. F2 Campaigns.md INotificationService → INotificationEmitter — **done**
7. F3 data-access.md lists ICommunicationPreferenceService (not injected) — **done**
8. F4 feature doc "manual or auto" complete — **done** (manual only)
9. F6 Campaigns.md "(controller enforces)" — **done** (service-enforced)
10. C17 _ViewImports claims GateArchitectureTests guards this section — **done** (deleted)
11. B3/P2 Resend buttons rendered for TicketAdmin who gets 403 — **done** (gated with isAdmin)
12. B1 ImportCodes flash "Imported {lines} codes" counts input lines not imported — **done** (service returns (imported, skipped); message reports both)
13. B2 ResendToGrantAsync flips grant to Queued (CampaignService.cs:514) before resolving user/email; a throw at either lookup strands the grant Queued, invisible to RetryAllFailed — **needs Peter**: reorder the flip after resolution, or catch-and-flip-to-Failed? Two defensible shapes.
14. H1–H4 Contracts csproj comment block rewrite (wrong projects/counts/lanes) — **done**
15. C14+C15+H13 "sixteen members"/"pre-G5" stale counts — **done**
16. P1 Detail.cshtml duplicates EnumBadgeMap badge colors — **done** (EnumBadgeMap.For; Queued gains text-dark from the shared map)
17. T11 MarkGrantsRedeemedAsync logic untested — **done** (repo tests: case-insensitivity, Draft exclusion, already-redeemed, newest-wins, N-same-code, blank codes)
18. T16 ReassignGrantsToUserAsync untested in CI — **done** (repo tests: clean move, target-wins, same-campaign dedup)
19. T12 grant-read status filtering untested — **done** (repo tests for both per-user reads)
20. T5+T6 per-grant failure isolation untested (wave+retry) — **done**
21. T1 SendWave Active guard untested — **done**
22. T18 enum stability test one-directional — **done** (BeEquivalentTo)
23. T20+T21 redundant wave email assertions — **done** (merged into one pass-through test)
24. T24 unused Humans.Shifts.Contracts ref in test csproj — **done** (verified unused, removed)
25. C3 line-number refs in CampaignGrantConfiguration comment — **done**
26. C4+C5+C6 DbContext: wrong migrations path, bare #750, cref UsersDbContext — **done**
27. C11+C12+H8 "after the Campaigns migration lands" stale tense — **done**
28. C13 IUserService → IUserServiceRead in comments — **done**
29. C16+H9 SectionAdminNav dead path docs/sections/Campaigns.md — **done** (Docs/Campaigns.md)
30. C18 "(step 12)" unresolvable — **done** (dropped)
31. C19+H19 bare issue refs #546/#750 — **done** (qualified)
32. C20+C21 comments restating next line (Domain navs, Detail.cshtml section banners) — **done** (deleted)
33. H10+H11 "has been removed"/"is gone" narration in Campaigns.md — **done** (present-state wording)
34. H14+H15 csproj prior-state clauses — **done** (trimmed)
35. F7 docs say DisplayName; code sends BurnerName — **done**
36. F8 feature-doc stats list wrong — **done**
37. F12 feature-doc diagram narrates removed navs — **done** (bare-FK annotations)
38. F9+F10 freshness triggers don't watch asserted-about files — **done** (globs added)
39. T3+T4+T7+T8+T13+T14+T15+T17+T2 remaining test gaps — **partial**: T3 (case-insensitive dedupe), T4 (ImportOrder sequence), T7 (no partial wave), T14 (outbox write-back), T15 (GDPR export/erase) done; T8 (wave skip paths — needs harness support for email-less users), T13 (GetCodeTrackingAsync — largest untested read, ~1h on its own), T17 (resend failure paths — entangled with finding 13's pending ruling), T2 (absence pin) skipped on budget/dependency.
40. H12/H16/H17 keep-or-ask — **kept** (dated design records pin live decisions)
41. Inbox: no Campaigns-tagged issues; open peterdrier/Humans#1562 / peterdrier/Humans#1554 are skill-infra, out of section (keep); ledger's deferred `GetCodeTrackingSummaries` db-sort item stands — no recommendations
42. T9 skipped: asserts an absence (`no-tests-for-absences`)
43. T19 optional arch test (concrete controller injection) — skipped, low value
44. UI verification gate: findings 11/16 changed rendered markup; a cloud run has no browser against a live instance, so verification is build + razor-lint + render tests. **Needs Peter**: eyeball Detail on the PR preview deploy.
45. Skill amendment (Phase 7): the push snippet assumes `origin` = peterdrier/Humans, but this cloud container cloned nobodies-collective as `origin`; the run added a `fork` remote and pushed there. **Needs Peter**: proposed edit — Phase 7 derives the push remote from the PR target instead of naming `origin`.

## Worked

Findings 1–12, 14–38, plus the done half of 39, committed by concern: section docs, code
comments, Detail view, import counts, test batches (section suite green; the PR's own
diff stats carry the totals).

## Skipped

- Camps: blocked by open doctor PR peterdrier/Humans#1561 (selection, not a finding).
- Findings 13, 42, 43, and the skipped half of 39 — reasons on each entry above.
- Reforge surface items (readServiceInterfaceMethod=12 on grant reads, missingWriteSurface=10,
  longMethod SendWaveAsync): structural; none met the strike bar for a first pass and none is a
  defect — left for a future run with the section's history in hand.

## Retro

- **Selector/rubric**: fair pick. Campaigns' 176 overweights Contracts surface that turned out
  justified (a small consumer set, narrow DTOs); the real sickness was prose drift, which no score sees.
- **Wasted motion**: the Freshness subagent misattributed a sort to GetAllAsync (read the
  neighboring method's line), costing a main-thread re-verify; InspectCode is absent in the cloud
  image so Prose ran haiku-only. Remote naming cost a detour (finding 45).
- **Missed by assessment, revealed by striking**: the unused Shifts.Contracts test reference —
  no thread lens looks at csproj ItemGroups against usings; the Tests thread caught it only
  because it read the csproj for context. A context compaction hit mid-run (during Phase 4);
  the ranked-list checkpoint in $RUNDIR carried state across it as designed.
- **Target diff**: first run on this section — no previous target; `Docs/health.md` created new.

## Needs Peter

- [ ] 13 — ResendToGrantAsync: reorder the Queued flip after user/email resolution, or flip to Failed on throw?
- [ ] 44 — eyeball Detail.cshtml (badges, admin-gated Resend) on the preview deploy
- [ ] 45 — Phase 7 skill edit: derive push remote from PR target, not `origin`

## Sweep queue

- debt: several sections' csproj comments still name the pre-G5 `Humans.Interfaces` project (off-section for this run) → `docs/architecture/debt-ledger.yml`

## File coverage

| Path | Disposition |
|---|---|
| docs/guide/Campaigns.md | changed |
| src/Sections/Humans.Campaigns.Contracts/CampaignCodeTrackingDtos.cs | reviewed |
| src/Sections/Humans.Campaigns.Contracts/Humans.Campaigns.Contracts.csproj | changed |
| src/Sections/Humans.Campaigns.Contracts/ICampaignService.cs | changed |
| src/Sections/Humans.Campaigns.Contracts/ICampaignServiceRead.cs | changed |
| src/Sections/Humans.Campaigns/Controllers/CampaignController.cs | changed |
| src/Sections/Humans.Campaigns/Data/CampaignRepository.cs | changed |
| src/Sections/Humans.Campaigns/Data/CampaignsDbContext.cs | changed |
| src/Sections/Humans.Campaigns/Data/CampaignsDbContextFactory.cs | reviewed |
| src/Sections/Humans.Campaigns/Data/Configurations/CampaignCodeConfiguration.cs | reviewed |
| src/Sections/Humans.Campaigns/Data/Configurations/CampaignConfiguration.cs | reviewed |
| src/Sections/Humans.Campaigns/Data/Configurations/CampaignGrantConfiguration.cs | changed |
| src/Sections/Humans.Campaigns/Data/ICampaignRepository.cs | changed |
| src/Sections/Humans.Campaigns/Data/Migrations/20260809125024_BaselineCampaigns.Designer.cs | generated |
| src/Sections/Humans.Campaigns/Data/Migrations/20260809125024_BaselineCampaigns.cs | generated |
| src/Sections/Humans.Campaigns/Data/Migrations/CampaignsDbContextModelSnapshot.cs | generated |
| src/Sections/Humans.Campaigns/Docs/Campaigns.md | changed |
| src/Sections/Humans.Campaigns/Docs/authorization.md | reviewed |
| src/Sections/Humans.Campaigns/Docs/data-access.md | changed |
| src/Sections/Humans.Campaigns/Docs/features/Campaigns-feature.md | changed |
| src/Sections/Humans.Campaigns/Docs/health.md | changed (created this run) |
| src/Sections/Humans.Campaigns/Domain/Campaign.cs | changed |
| src/Sections/Humans.Campaigns/Domain/CampaignCode.cs | changed |
| src/Sections/Humans.Campaigns/Domain/CampaignGrant.cs | reviewed |
| src/Sections/Humans.Campaigns/Domain/CampaignStatus.cs | reviewed |
| src/Sections/Humans.Campaigns/Humans.Campaigns.csproj | changed |
| src/Sections/Humans.Campaigns/Models/CampaignViewModels.cs | reviewed |
| src/Sections/Humans.Campaigns/Properties/AssemblyInfo.cs | reviewed |
| src/Sections/Humans.Campaigns/Section.cs | changed |
| src/Sections/Humans.Campaigns/SectionAdminNav.cs | changed |
| src/Sections/Humans.Campaigns/Services/CampaignService.cs | changed |
| src/Sections/Humans.Campaigns/Services/Dtos/CampaignDtos.cs | reviewed |
| src/Sections/Humans.Campaigns/Views/Campaign/Create.cshtml | reviewed |
| src/Sections/Humans.Campaigns/Views/Campaign/Detail.cshtml | changed |
| src/Sections/Humans.Campaigns/Views/Campaign/Edit.cshtml | reviewed |
| src/Sections/Humans.Campaigns/Views/Campaign/Index.cshtml | reviewed |
| src/Sections/Humans.Campaigns/Views/Campaign/SendWave.cshtml | reviewed |
| src/Sections/Humans.Campaigns/Views/Campaign/_ViewStart.cshtml | reviewed |
| src/Sections/Humans.Campaigns/Views/_ViewImports.cshtml | changed |
| tests/Humans.Campaigns.Tests/Architecture/CampaignsArchitectureTests.cs | changed |
| tests/Humans.Campaigns.Tests/Controllers/CampaignControllerTests.cs | reviewed |
| tests/Humans.Campaigns.Tests/Data/CampaignRepositoryTests.cs | changed (created this run) |
| tests/Humans.Campaigns.Tests/Enums/EnumStringStabilityTests.cs | changed |
| tests/Humans.Campaigns.Tests/Humans.Campaigns.Tests.csproj | changed |
| tests/Humans.Campaigns.Tests/Services/CampaignServiceTests.cs | changed |

## Threads

| Thread | How it ran | Model | Findings |
|---|---|---|---|
| Shape | main thread | fable | 0 standalone (folded into 3c target) |
| Behavior & bugs | main thread | fable | 3 |
| Freshness | subagent | doctor-reader (opus low) | 12 |
| Tests | subagent | doctor-reader (opus low) | 24 |
| History | subagent | doctor-reader (opus low) | 19 |
| Comments | subagent | doctor-reader (opus low) | 23 |
| Prose & surface | subagent | haiku | 2 — InspectCode absent in the cloud image, so haiku-only |
| Conformance | self-run, main thread | fable | 0 (both detectors clean) |
| Inbox | self-run, main thread | fable | 0 recommendations (2 open repo issues reviewed, both skill-infra; in-app issues unreachable from cloud) |
