# Section Align - Camps
**Run started:** 2026-05-12 | **Mode:** existing-section | **Worktree:** `H:\source\humans\.worktrees\section-align-camps`
**Branch:** `align/camps` (off `origin/main` @ `14497864dd`)

## Axis 1 - Boundary integrity

### 1.1 Section name consistency
Canonical section name is `Camps` and is consistent across:

- `docs/sections/Camps.md`
- `CampController` / `CampAdminController` / `CampApiController`
- `src/Humans.Application.Services.Camps`, `src/Humans.Application.Interfaces.Camps`
- `src/Humans.Infrastructure.Repositories.Camps`, `src/Humans.Infrastructure.Data.Configurations.Camps`

### 1.2 Controller ownership and route surface

- `CampController` owns `/Camps/*` and `/Barrios/*`.
- `CampAdminController` owns `/Camps/Admin/*` and `/Barrios/Admin/*`.
- `CampApiController` owns `/api/camps/*` and `/api/barrios/*`.
- No `/Admin/Camps/*` ownership found.

### 1.3 Views + models

- `Views/Camp/*`, `Views/CampAdmin/*` exist and are section-owned.
- View models are in `Models/Camp/*`, `Models/CampAdmin/*`.
- `CampaignController` is a separate `Campaigns` section and is intentionally excluded.

### 1.4 Extensions + DI

- `CampsSectionExtensions` exists and is wired in infrastructure extension registration.
- Services/repositories are section-owned and live in appropriate Application/Infrastructure layers.

### 1.5 Cross-boundary calls

- Inbound write/read from Camps to other sections is mediated via `IUserService` / `IUserService`-shaped interfaces where needed (lead/member naming and user IDs).
- No `Camp` writes/read paths were found in foreign production services outside explicit merge or dev-seeding contexts.
- `DevPersonaSeeder` performs direct `HumansDbContext` writes for dev fixtures (including `CampSettings`/`Camp`/`CampSeason`/`CampLead`) but this is environment-local and documented as a dev-only boundary exception. It remains a follow-up candidate for full section-boundary hardening.

### 1.6 Controller/base leakage

- No Camps-specific helpers were found on `HumansControllerBase`.
- `HumansCampControllerBase` contains shared Camps concern helpers and is appropriate.

## Axis 2 - Internal cohesion

### 2.1 Repository pattern / EF shape

- `CampService` and `CampRoleService` inject repositories (`ICampRepository`, `ICampRoleRepository`) and do not inject EF context directly.
- Repositories are in Infrastructure and use `IDbContextFactory<HumansDbContext>`.
- Repositories are `sealed`.

### 2.2 Caching + composition

- `CampService` uses short-TTL cache for list/settings read paths.
- `CampContactService` uses cache for rate-limit storage.
- No unauthorized EF or caching anti-patterns surfaced in a cursory run.

### 2.3 Shared presentation

- Shared reusable Camps rendering is via `MyCampsViewComponent` (plus standard shared partials for composition).

### 2.4 Tests + architecture

- `CampsArchitectureTests` exists and verifies the key namespace/repository/cache/nav-shape invariants.
- Service test coverage exists in:
  - `tests/Humans.Application.Tests/Services/Camp*`
  - `tests/Humans.Application.Tests/Repositories/CampRepositoryTests.cs`
  - `tests/Humans.Application.Tests/Authorization/CampAuthorizationHandlerTests.cs`

## Axis 3 - Targeted fixes and gaps

### 3.1 Implemented alignment fix

- Relocated `CampMemberConfiguration.cs` from:
  - `src/Humans.Infrastructure/Data/Configurations/CampMemberConfiguration.cs`
  
  to:
  
  - `src/Humans.Infrastructure/Data/Configurations/Camps/CampMemberConfiguration.cs`
- Updated namespace to `Humans.Infrastructure.Data.Configurations.Camps`.
- Updated `docs/sections/Camps.md` freshness pointer to the new path.

### 3.2 Follow-up candidates

- `DevPersonaSeeder` has dev-only direct writes for camp fixtures.
- `CampMember.UserId` is still not folded during account merges; this remains a known section-design gap already called out in `Camps.md`.
- Confirm no lingering docs still refer to the legacy Camps config path after longer-term doc audits.

### 3.3 Full-run validation

- Searched Axis 1/2 surfaces end-to-end (controllers, views, models, services, repositories, DI extensions, and boundary points).
- Ran broader Camps-scoped test sweep:
  - `dotnet test tests/Humans.Application.Tests/Humans.Application.Tests.csproj --filter "FullyQualifiedName~Camp"`
  - Result: 165 passed, 0 failed.
- No additional in-scope Camps code edits were identified beyond the configuration placement fix above.

## Test-attribute delta

- Baseline gate target from repository is `2139`.
- This pass: `+0 / -0 = 0`.
- Focused test pass: `CampsArchitectureTests` (`dotnet test --filter "FullyQualifiedName~CampsArchitectureTests"`): pass (11/11).
- Supplemental Camps-scoped pass: `dotnet test ... --filter "FullyQualifiedName~Camp"`: pass (165/165).

## Decision

Axis 1/2 are clean for existing section ownership.  
Axis 3 has one implemented boundary-organization fix (configuration location/namespace) and two explicit follow-ups for dev-seeding and account-merge folding behavior.
