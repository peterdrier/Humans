# section-doctor — Campaigns — 2026-10-14

- Invocation: unattended daily routine, no arguments (Phase 8 skipped per routine prompt)
- Anchor commit: `89c128e35` (origin/main at branch point); branch `section-doctor/2026-10-14T012017Z`.
- Budget: 2.5h.
- PR: pending

## Assessment summary

Campaigns was in good shape structurally: one controller, service, repository and context, a
`Contracts/` folder carrying only the outward surface. Its weight was prose. The invariant doc
described a Wave concept the code doesn't have, understated the subject placeholders and named
stale cross-section calls; the guide said the email carries the campaign description; the previous
target and several comments claimed the section has no resx set. One structural delta: vendor code
generation re-implemented the CSV import body. Tests pinned allocation in insertion order, not
`ImportOrder`, and duplicated the merge-fold coverage at repository and service level.

Independence check: pass — findings 2 and 3 came from walking the code against the target, not from a tool.

## Findings

1. "No resource set" claims are false (`CampaignsEmails.cs`, `_ViewImports.cshtml`, `Campaigns.md`, previous target) — the section ships a resx set for the member-facing notice.
2. `Campaigns.md` Wave concept says a wave targets humans already granted codes but not yet notified; a wave targets team members without a grant and creates the grants.
3. Vendor code generation (`ImportGeneratedCodesAsync`) re-implemented the CSV import's append-after-max-`ImportOrder`, skip-duplicates and audit body.
4. `Campaigns.md` named stale cross-section calls (`ITeamService.GetActiveTeamOptionsAsync`/`GetTeamMembersAsync`), the recipient as `DisplayName` (code passes `BurnerName`), and omitted the Gdpr dependency.
5. `Campaigns.md` said the subject supports `{{Name}}` only; the builder substitutes `{{Code}}` too.
6. The user guide said the email carries the campaign description; it carries the campaign's own subject and body.
7. False comments: the DbContext's migrations path, `GetByIdAsync` "Tracked", and `IUserService` where the code calls `IUserServiceRead`.
8. Issue/PR references and design-section pointers narrating past moves across comments and docs, plus "// Navigation" labels and comments restating the code.
9. Wave allocation by `ImportOrder` was untested in effect: seeded insertion order equalled `ImportOrder`.
10. `PreviewWaveSendAsync` reloaded the campaign only to throw on absence; its one caller had already loaded it.
11. Redundant tests: two CSV duplicate-import tests covering one path; the merge fold pinned at both repository and service level.
12. Thin test rows: import while Active untested; the profile read's user filter and the email-status update's single-grant scope unpinned.
13. Inbox: nobodies-collective/Humans#254 and nobodies-collective/Humans#110 carry stale text (recommendation only).
14. Remaining test gaps, unworked: the export test has no other-user grant; blank-field tests could fold into the theory; controller tests sit in the service test file; positional `ReceivedCalls` assertions; wrong-state toast untested; unique indexes unpinned.
15. Surface nits, unworked: possibly unused `@using` lines in `_ViewImports.cshtml` (unverified); `ViewBag.Title2` naming; Create has no breadcrumbs.

## Debt verified

`Docs/debt.yml` carries no rows; nothing to verify.

## Worked

- 1, 4, 5, 6 — invariant doc, feature spec, guide and `_ViewImports.cshtml` rewritten to match the code.
- 2 — Wave concept rewritten in `Campaigns.md`.
- 3 — `ImportGeneratedCodesAsync` deleted; vendor generation now calls `ImportCodesAsync`, propagates its failure and reports the imported count; new test for a generated code already in the campaign (reviewer: rejected once, approved on rework).
- 7, 8 — false comments corrected; history references and restating comments cut.
- 9 — allocation test seeds codes out of `ImportOrder` (mutation-checked against removing the `OrderBy`).
- 10 — redundant reload removed (reviewer approved).
- 11 — repository merge-fold tests deleted; the two import tests merged into one that also covers import while Active (reviewer approved both).
- 12 — profile-read test gains an other-user grant; email-status test proves other grants stay untouched.

## Skipped

- Blocked sections passed over by the selector: Camps (peterdrier/Humans#1968), Events (peterdrier/Humans#1931), Expenses (peterdrier/Humans#1973), Issues (peterdrier/Humans#1954) — open section-doctor PRs.
- 13 — existing issues are read-only; recommendations: nobodies-collective/Humans#484 keep; nobodies-collective/Humans#254 edit (six locales now, including ca); nobodies-collective/Humans#110 edit (dead spec path; SurveyInvitation also needs bounce status; add `section:surveys`).
- 14 — budget; test gaps that don't guard a current defect.
- 15 — unverified or cosmetic on admin-only pages.
- Detail page localization (prose thread) — dropped: Detail is gated `TicketAdminOrAdmin`, an operator role, so `memory/code/localization-admin-exempt.md` applies.

## Retro

**Selector.** Campaigns was a fair pick: low structural debt, but the prose drift was real and member-visible in the guide.

**Wasted motion.** Reading threads were dispatched before the target and behavior walk; harmless here because the threads ran on the previous target, but the order inverts 3b/3c's intent. The first collapse attempt discarded the import result and cost a review round.

**What striking revealed.** The collapse exposed that vendor generation had never reported duplicates it skipped; the reworked path counts only what was imported.

**Target diff.** The previous target claimed no resx set, a contracts leaf and an audit seam — all false. This target drops all three and records no structural delta remaining.

Commits on this branch lack the session attribution trailer; pushed history was left as is.

## Needs Peter

none

## File coverage

| Path | Disposition |
|---|---|
| `docs/guide/Campaigns.md` | changed |
| `src/Sections/Humans.Campaigns/CampaignsResource.ca.resx` | reviewed |
| `src/Sections/Humans.Campaigns/CampaignsResource.cs` | reviewed |
| `src/Sections/Humans.Campaigns/CampaignsResource.de.resx` | reviewed |
| `src/Sections/Humans.Campaigns/CampaignsResource.es.resx` | reviewed |
| `src/Sections/Humans.Campaigns/CampaignsResource.fr.resx` | reviewed |
| `src/Sections/Humans.Campaigns/CampaignsResource.it.resx` | reviewed |
| `src/Sections/Humans.Campaigns/CampaignsResource.resx` | reviewed |
| `src/Sections/Humans.Campaigns/Contracts/CampaignCodeTrackingDtos.cs` | reviewed |
| `src/Sections/Humans.Campaigns/Contracts/ICampaignService.cs` | changed |
| `src/Sections/Humans.Campaigns/Contracts/ICampaignServiceRead.cs` | reviewed |
| `src/Sections/Humans.Campaigns/Controllers/CampaignController.cs` | reviewed |
| `src/Sections/Humans.Campaigns/Data/CampaignRepository.cs` | changed |
| `src/Sections/Humans.Campaigns/Data/CampaignsDbContext.cs` | changed |
| `src/Sections/Humans.Campaigns/Data/CampaignsDbContextFactory.cs` | reviewed |
| `src/Sections/Humans.Campaigns/Data/Configurations/CampaignCodeConfiguration.cs` | reviewed |
| `src/Sections/Humans.Campaigns/Data/Configurations/CampaignConfiguration.cs` | reviewed |
| `src/Sections/Humans.Campaigns/Data/Configurations/CampaignGrantConfiguration.cs` | reviewed |
| `src/Sections/Humans.Campaigns/Data/ICampaignRepository.cs` | changed |
| `src/Sections/Humans.Campaigns/Data/Migrations/20260809125024_BaselineCampaigns.Designer.cs` | generated |
| `src/Sections/Humans.Campaigns/Data/Migrations/20260809125024_BaselineCampaigns.cs` | reviewed |
| `src/Sections/Humans.Campaigns/Data/Migrations/CampaignsDbContextModelSnapshot.cs` | generated |
| `src/Sections/Humans.Campaigns/Docs/Campaigns.md` | changed |
| `src/Sections/Humans.Campaigns/Docs/authorization.md` | reviewed |
| `src/Sections/Humans.Campaigns/Docs/data-access.md` | reviewed |
| `src/Sections/Humans.Campaigns/Docs/debt.yml` | reviewed |
| `src/Sections/Humans.Campaigns/Docs/features/Campaigns-feature.md` | changed |
| `src/Sections/Humans.Campaigns/Docs/health.md` | changed |
| `src/Sections/Humans.Campaigns/Domain/Campaign.cs` | changed |
| `src/Sections/Humans.Campaigns/Domain/CampaignCode.cs` | changed |
| `src/Sections/Humans.Campaigns/Domain/CampaignGrant.cs` | reviewed |
| `src/Sections/Humans.Campaigns/Domain/CampaignStatus.cs` | reviewed |
| `src/Sections/Humans.Campaigns/Humans.Campaigns.csproj` | reviewed |
| `src/Sections/Humans.Campaigns/Models/CampaignViewModels.cs` | reviewed |
| `src/Sections/Humans.Campaigns/Properties/AssemblyInfo.cs` | reviewed |
| `src/Sections/Humans.Campaigns/Section.cs` | changed |
| `src/Sections/Humans.Campaigns/SectionAdminNav.cs` | changed |
| `src/Sections/Humans.Campaigns/Services/CampaignCodeEmailRequest.cs` | changed |
| `src/Sections/Humans.Campaigns/Services/CampaignService.cs` | changed |
| `src/Sections/Humans.Campaigns/Services/CampaignsEmailPreviews.cs` | changed |
| `src/Sections/Humans.Campaigns/Services/CampaignsEmails.cs` | changed |
| `src/Sections/Humans.Campaigns/Services/Dtos/CampaignDtos.cs` | reviewed |
| `src/Sections/Humans.Campaigns/Views/Campaign/Create.cshtml` | reviewed |
| `src/Sections/Humans.Campaigns/Views/Campaign/Detail.cshtml` | reviewed |
| `src/Sections/Humans.Campaigns/Views/Campaign/Edit.cshtml` | reviewed |
| `src/Sections/Humans.Campaigns/Views/Campaign/Index.cshtml` | reviewed |
| `src/Sections/Humans.Campaigns/Views/Campaign/SendWave.cshtml` | reviewed |
| `src/Sections/Humans.Campaigns/Views/Campaign/_CampaignFormFields.cshtml` | reviewed |
| `src/Sections/Humans.Campaigns/Views/_ViewImports.cshtml` | changed |
| `tests/Humans.Campaigns.Tests/Architecture/CampaignsArchitectureTests.cs` | changed |
| `tests/Humans.Campaigns.Tests/Controllers/CampaignControllerTests.cs` | reviewed |
| `tests/Humans.Campaigns.Tests/Data/CampaignRepositoryTests.cs` | changed |
| `tests/Humans.Campaigns.Tests/Enums/EnumStringStabilityTests.cs` | changed |
| `tests/Humans.Campaigns.Tests/Humans.Campaigns.Tests.csproj` | reviewed |
| `tests/Humans.Campaigns.Tests/Services/CampaignServiceTests.cs` | changed |
| `tests/Humans.Campaigns.Tests/Services/CampaignsEmailsTests.cs` | changed |

## Threads

| Thread | How it ran | Model | Findings |
|---|---|---|---|
| Shape | main | — | 2 |
| Behavior & bugs | main | — | 3 |
| Freshness | subagent (`doctor-reader`) | opus-low | 8 |
| Conformance | main (detector scripts) | — | 0 |
| Tests | subagent (`doctor-reader`) | opus-low | 14 |
| Prose & surface | subagent (`general-purpose`) | haiku | 4 |
| History | subagent (`doctor-reader`) | opus-low | 16 |
| Comments | subagent (`doctor-reader`) | opus-low | 14 |
| Inbox | subagent (`doctor-reader`) | opus-low | 2 |

