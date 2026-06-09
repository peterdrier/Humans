# Section Align - Campaigns
**Run started:** 2026-05-13 | **Mode:** existing-section | **Worktree:** `H:\source\humans\.worktrees\section-align-campaigns`  
**Branch:** `align/campaigns` (off `origin/main`)  
**Canonical section name proposal:** Campaigns

## Axis 1 - Boundary integrity

### 1.1 Section name consistency - clean
Canonical name `Campaigns` is consistent across:
- `docs/sections/Campaigns.md`
- Controller: `src/Humans.Web/Controllers/CampaignController.cs`
- Views: `src/Humans.Web/Views/Campaign/`
- ViewModels: `src/Humans.Web/Models/CampaignViewModels.cs`
- DI extension: `src/Humans.Web/Extensions/Sections/CampaignsSectionExtensions.cs`

### 1.2 Controller existence/placement - clean
Campaign UI/admin flow is owned by `CampaignController` only.

### 1.3 URL surface - clean
Routes are under one canonical surface:
- `GET/POST /Campaigns/Admin`
- `/Campaigns/Admin/{id:guid}` (detail)
- `/Campaigns/Admin/{id:guid}/ImportCodes`
- `/Campaigns/Admin/{id:guid}/GenerateCodes`
- `/Campaigns/Admin/{id:guid}/Activate`
- `/Campaigns/Admin/{id:guid}/Complete`
- `/Campaigns/Admin/{id:guid}/SendWave`
- `/Campaigns/Admin/{id:guid}/RetryAllFailed`
- `/Campaigns/Admin/Grants/{grantId:guid}/Resend`

No `Team*`-style route aliasing or undocumented controller split was found.

### 1.4 Views folder - clean
`src/Humans.Web/Views/Campaign/` contains the section views (`Index`, `Detail`, `Create`, `Edit`, `SendWave`, `_ViewStart`) and no campaign view is parked in unrelated folders.

### 1.5 ViewModel placement - clean
Campaign view models are section-local in `CampaignViewModels.cs`.

### 1.6 Controller-base leak - clean
`CampaignController` does not expose campaign-only helper models or actions via `HumansControllerBase`.

### 1.7 Extensions placement - clean
`CampaignsSectionExtensions` exists and registers all campaign DI points.

### 1.8 Role surface - mostly clean
- `Index`, `Create`, `Edit`, `SendWave` (GET/POST), `Activate`, `Complete`, `ImportCodes`, `Resend`, `RetryAllFailed` require `PolicyNames.AdminOnly`.
- `Detail` and `GenerateCodes` require `PolicyNames.TicketAdminOrAdmin`.

No obvious role anomaly was found versus `docs/sections/Campaigns.md`.

### 1.9 Inbound cross-section EF access - clean
No non-campaign section code path reading `Campaigns` tables was identified in the section pass:
- `Campaigns` owns `Campaign`, `CampaignCode`, `CampaignGrant`.
- No `db.Campaign*` access outside `CampaignRepository` and its test implementations was found.

### 1.10 Cross-section inbound EF navigations - clean with exception tracking
Obsolete inbound campaign navs are intentionally retained:
- `Campaign.CreatedByUser` (`[Obsolete]`)
- `CampaignGrant.User` (`[Obsolete]`)

There is no service-layer or controller-layer usage of these nav properties; display names are resolved through `IUserService`.

### 1.11 Outbound cross-section access - clean
Campaign behavior uses section interfaces rather than direct repository/db dependencies:
- `ITeamService` for active team options and team members
- `IUserEmailService`, `IUserService` for recipient identity resolution
- `ICommunicationPreferenceService` for campaign opt-out
- `IEmailService` and `INotificationService` for message dispatch

### 1.12 Controller -> DbContext - clean
No controller directly injects `HumansDbContext`.

### 1.13 Migrations - clean
No campaign migration changes are owned by the controller or other campaign surface in this section pass.

### 1.14 Section doc shape - clean
`docs/sections/Campaigns.md` exists and defines concepts, invariants, negative rules, triggers, dependencies, and ownership.

### 1.15 Operational docs/routing gaps - none identified
No extra non-canonical admin-route aliases or route registrations were identified for Campaigns.

## Axis 2 - Internal cohesion

### 2.1 EF leakage from service layer - clean
`CampaignService` does not reference EF directly:
- It depends on `ICampaignRepository`
- and cross-section interfaces for users/teams/email/preferences

### 2.2 Caching placement - clean
No caching decorator exists for Campaigns; this matches current ownership size and current comment in `CampaignsSectionExtensions`.

### 2.3 DI lifetimes - clean
`CampaignRepository` is registered as a singleton with `IDbContextFactory<HumansDbContext>`, enabling per-call context creation in repository methods.
`CampaignsCampaignService` is scoped and surfaced through `ICampaignService`, `IUserDataContributor`, and `IUserMerge`.

### 2.4 Repository pattern - clean
Campaign data access is section-owned:
- `ICampaignRepository` interface exists at `src/Humans.Application/Interfaces/Repositories/ICampaignRepository.cs`
- `CampaignRepository` is `sealed` at `src/Humans.Infrastructure/Repositories/Campaigns/CampaignRepository.cs`

### 2.5 Shared visual components - review needed
No Campaign-specific shared `ViewComponent`/`TagHelper` was identified.

### 2.6 Interface budget + segregation - action required
`ICampaignService` currently has no `[SurfaceBudget]`.

This requires follow-up to align with other section contracts and ratchet rules in `Humans.Application.Architecture`.

### 2.7 Architecture tests - partial coverage
Campaign-owned service/repo surfaces have good generic and adjacent coverage, but there is **no dedicated**:
- `tests/Humans.Application.Tests/Architecture/CampaignsArchitectureTests.cs`

Cross-boundary usage in `Tickets` (`ITicketSyncService`, `TicketQueryService`) appears compliant via `ICampaignService`.

## Axis 3 - Test focus

### 3.1 Test folder placement
Section-relevant tests found:
- `tests/Humans.Application.Tests/Services/CampaignServiceTests.cs`

Missing section-aligned test categories:
- Campaign controller route/auth policy tests
- Campaign-facing architecture tests
- Campaign e2e scenario coverage (create/detail/send wave)

### 3.2 Coverage map
Service-layer behavior is covered by dedicated unit/integration-style tests.
No focused web or end-to-end coverage exists for admin campaign flows outside incidental shared coverage.

### 3.3 Redundancy
No duplicate Campaign coverage patterns are obvious in current files.

### 3.4 Mutation testing
No `local/stryker-runs/campaigns` report was identified.
Baseline from `docs/testing/mutation-testing.md` is `2139` attributes.

### Test-attribute gate
- Baseline from `docs/testing/mutation-testing.md`: `2139` test attributes.
- Phase 0 net delta: `+0 / -0 = 0`.

## Stop conditions tripped
None.

## Follow-up /section-align targets

1. `ICampaignService` surface ratchet
   - Add an explicit `[SurfaceBudget]` to `ICampaignService`.
   - Verify caller impact (`TicketSyncService`, `TicketQueryService`, `ProfileController`) and adjust only if API over/under exposed.

2. Campaign architecture tests
   - Add `tests/Humans.Application.Tests/Architecture/CampaignsArchitectureTests.cs`.
   - Assert:
     - controllers/services obey `NoCrossSectionEfJoins` expectations for campaign tables,
     - only campaign-owned services own `campaign*` mutation paths,
     - `CampaignController` route/policy placement remains section-local.

3. Controller + authorization guardrails
   - Add focused tests for mixed-admin/TicketAdmin policy behavior (`Detail` and `GenerateCodes` vs. Admin-only actions).

4. Section-facing coverage
   - Add an app test or e2e scenario for the admin campaign lifecycle (create → import/generate → preview/send wave).

## Phase plan

### Phase 1 - Surface alignment
1. [ ] Reconfirm route surface in `CampaignController` after latest mainline merges.
2. [ ] Reconfirm `Campaign` route naming/paths in `docs/sections/Campaigns.md` and runtime links.

### Phase 2 - Interface and cohesion cleanup
1. [ ] Add `[SurfaceBudget]` to `ICampaignService` with a method-count ratchet.
2. [ ] Add `CampaignsArchitectureTests` and wire section boundary checks.

### Phase 3 - Test hardening
1. [ ] Add controller-auth tests for `/Campaigns/Admin/*` policies.
2. [ ] Add targeted campaign admin e2e coverage for core workflow if not already implied by existing suite.

### Phase 4 - Docs
1. [ ] Mark campaigns follow-up status in `docs/sections/Campaigns.md` once follow-ups are run.
2. [ ] Add a dedicated plan link from any parent section plan (`section-align-budget`/`section-align-*` if scope expands to cross-section coupling).
