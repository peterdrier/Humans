# Auth — G0 First Audit

**Kind:** horizontal · **Audited:** 2026-08-03 @ 5a9bbe198

## G1 — Ownership

| # | Predicate | Result | Evidence |
|---|-----------|--------|----------|
| 1 | Every owned table read/written by exactly one repository in-section | PASS | Owns `role_assignments` (entity `RoleAssignment`). `reforge ownership-violations --owner Auth --tables RoleAssignment` → 0. `reforge injected IRoleAssignmentRepository` → 2 consumers, both in `Services/Auth`: `RoleAssignmentService`, `AdminAuthorizationService`. |
| 2 | One writer-service per table (no interceptor workarounds) | PASS | Only `RoleAssignmentService` calls repo writes (`AddAsync`/`UpdateAsync`/`UpdateManyAsync`/`ReassignToUserAsync`). `AdminAuthorizationService` is read-only (`HasActiveRoleAsync`). |
| 3 | No EF entity leaks across boundary; other sections consume DTOs | **FAIL — corrected 2026-08-03** | `IRoleAssignmentService` is clean: it returns `RoleAssignmentSummarySnapshot`/`RoleAssignmentDetailSnapshot`/`RoleAssignmentSnapshot` DTOs, never `RoleAssignment` entities, to cross-section callers (e.g. `Services/Users/DuplicateAccountService.cs:78` calls `GetByUserIdAsync` → DTO list). But the original pass scored this predicate against that one interface and missed Auth's other public service: `ApplicationServiceEntityReadReturns.baseline.txt` carries `Humans.Application.Interfaces.Auth.IMagicLinkService.FindUserByVerifiedEmailAsync:Humans.Domain.Entities.User` — an Auth-owned baseline row returning the raw `User` entity. |
| 4 | No cross-section EF joins (zero baseline entries) | **FAIL** | Zero hits in the 5 ratchet-baseline `.txt` files (correct — HUM0024 isn't baseline-file-based). But `RoleAssignmentConfiguration.cs:8` carries an active `[Grandfathered("HUM0024", ...)]` marker, which **is** this analyzer's allowlist mechanism (attribute-based, not a text-file baseline) — see predicate 5(b). |
| 5 | No `[Obsolete]` cross-section navs / `[Grandfathered]` / owned baseline rows | **FAIL** | Two separate findings. (a) `src/Humans.Application/Services/Auth/RoleAssignmentService.cs:21-23` carries `[DontFix(reason: "Auth (crosscut) references vertical sections — IUserServiceRead for assignee/creator display stitching, ISystemTeamSync for system-team membership. Permanent exception pending Peter-led inversion.", since: "2026-05-25")]`. `ISystemTeamSync` lives in `Humans.Application.Interfaces.GoogleIntegration` — a vertical section, **not** on the plan's shared-contract exception list (only User/UserInfo, Auth, Audit are blessed). `IUserServiceRead` is fine (Users is a shared contract). (b) **Missed in the original pass:** `src/Humans.Infrastructure/Data/Configurations/Auth/RoleAssignmentConfiguration.cs:8` also carries `[Grandfathered(ruleId: "HUM0024", justification: "Pre-existing cross-section EF navigation join; migrating to bare FK + service-level stitching.", since: "2026-05-25", issueRef: "docs/architecture/roslyn-analysis.md#hum0024")]` — the same HUM0024 cross-section-EF-join marker found on 5 other sections' configs in this audit pass (Calendar, Budget×3, Camps×3, Campaigns×2). Tracked only to a generic doc anchor, not a specific issue. |
| 6 | Controllers thin, no HUM0031 grandfathers | PASS / N/A | Auth owns no `Humans.Web.Controllers` type (surface-score.json paths for Auth only cover `src/Humans.Web/Authorization/**`, which are `IAuthorizationHandler`/claims-transformation classes, not MVC controllers). Nothing to grandfather. |
| 7 | `docs/sections/Auth.md` exists and matches reality | PASS | **Correction:** `docs/sections/Auth.md` DOES exist (a prior glob call with brace-expansion syntax returned a false negative — confirmed via `ls` and direct `Read`; it's a 177-line, highly detailed doc: role-assignment temporal model, magic-link flow, authorization-policy phase history, access-matrix UI convention). No drift found — the doc's own "Touch-and-clean guidance" section already accurately lists the 5 external files still reading the `[Obsolete]` `RoleAssignment.User`/`CreatedByUser` navs under `#pragma warning disable CS0618` (see G1 gap list). It does **not** mention the `RoleAssignmentConfiguration` HUM0024 grandfather from predicate 5(b) — minor omission, not counted as a separate drift item since HUM0024 grandfathers aren't documented in any section's doc (systemic, not Auth-specific). |

## G3 — Tests

| # | Predicate | Result | Evidence |
|---|-----------|--------|----------|
| 1 | Repo tests on real Postgres, zero EF-InMemory | **FAIL** | `tests/Humans.Application.Tests/Repositories/RoleAssignmentRepositoryTests.cs:21-24` — `new DbContextOptionsBuilder<HumansDbContext>().UseInMemoryDatabase(...)`. |
| 2 | Service tests mock repo/`I*ServiceRead`, zero `HumansDbContext` | **FAIL — corrected 2026-08-03** | Grepping these files for the literal `HumansDbContext` is a false negative: they inherit their EF setup. `RoleAssignmentServiceTests` extends `ServiceTestHarness` and constructs `new RoleAssignmentRepository(DbFactory)`; `MagicLinkServiceTests` extends it too. `ServiceTestHarness` (`tests/Humans.Application.Tests/Infrastructure/ServiceTestHarness.cs:54,71`) builds a real `HumansDbContext` over `.UseInMemoryDatabase(...)` and exposes it as `Db`/`DbFactory`, so a harness-derived test constructing a concrete repository is exactly the EF-InMemory service test this predicate forbids. Same false-negative pattern corrected across Auth, Budget, Camps, CityPlanning, Feedback, Governance and Consent in this pass. |
| 3 | Invariants/triggers from section doc each have a test | **FAIL — corrected 2026-08-03** | Using the real `docs/sections/Auth.md`: "temporal assignments, never resurrected" + overlap rejection — covered, `RoleAssignmentServiceTests.cs` has 5 tests around `HasOverlappingAssignmentAsync`. "Magic-link login tokens single-use (15-min replay cache)" and "signup rate-limited 1/60s" — plausibly covered by `MagicLinkServiceTests.cs` given its scope; not line-verified. "Assigning Board triggers `SyncBoardTeamAsync`" — **no coverage exists** (corrected 2026-08-03): a repo-wide search for `SyncBoardTeamAsync` across `tests/` returns zero hits, so this documented trigger is untested rather than merely untraced. That makes this predicate FAIL, not a spot-check PASS — see the G3 gap list. |
| 4 | No skipped tests without an issue ref | PASS | No `Skip=` found in `RoleAssignmentServiceTests.cs`, `RoleAssignmentRepositoryTests.cs`, `AdminAuthorizationServiceTests.cs`, `MagicLinkServiceTests.cs`. |
| 5 | Tests grouped under the section | **FAIL** | Tests live under generic `tests/Humans.Application.Tests/Services/*.cs` and `.../Repositories/RoleAssignmentRepositoryTests.cs` — no `tests/.../Auth/` folder. Not movable as a unit at G5 without reorganizing. |

## G1 gap list

1. **Cross-section reference from horizontal Auth into vertical GoogleIntegration.** `RoleAssignmentService` injects `ISystemTeamSync` (`Humans.Application.Interfaces.GoogleIntegration`), marked `[DontFix]` as a "permanent exception pending Peter-led inversion." — `src/Humans.Application/Services/Auth/RoleAssignmentService.cs:21-34`. Fix: either formally add GoogleIntegration to the shared-contract exception list (Peter's call), or invert — GoogleIntegration/Teams subscribes to a role-changed notification instead of Auth calling `SyncBoardTeamAsync()` directly. No-migration-needed: **y**.
2. **`RoleAssignmentConfiguration.cs` HUM0024 cross-section EF join grandfather** — where: `src/Humans.Infrastructure/Data/Configurations/Auth/RoleAssignmentConfiguration.cs:8`. Tracked only to a generic doc anchor, no specific issue, no queued G2 item. Suggested fix: verify liveness (some of these HUM0024 markers across sections may be stale now that navs are `[Obsolete]`-marked, per the AuditLog audit's finding of a similarly stale marker) and either retire the attribute or file a tracking issue. No-migration-needed: **y** (pending verification).
3. **`RoleAssignment.User`/`CreatedByUser` `[Obsolete]` navs still actively read via `#pragma warning disable CS0618` from 5 external call sites** (`AboutController`, `GovernanceController`, `ProfileController`, `SendAdminDailyDigestJob`, `SendBoardDailyDigestJob`) — already self-documented in `docs/sections/Auth.md`'s own "§15h touch-and-clean" list. Suggested fix: migrate the 5 call sites to `IUserService.GetByIdsAsync` (§6b pattern), then drop the nav properties entirely. No-migration-needed: **y** (nav removal is code-only; the FK scalar stays).
4. **Added 2026-08-03: `IMagicLinkService.FindUserByVerifiedEmailAsync` returns the raw `User` entity** — an Auth-owned `ApplicationServiceEntityReadReturns.baseline.txt` row missed by the original pass, which scored predicate 3 against `IRoleAssignmentService` alone. Fix: return a DTO (or just the `Guid`, which is all the magic-link flow needs) and drop the baseline line. No-migration-needed: **y**.
*(Restructured 2026-08-03: two test-only items previously numbered 5 and 6 here were G3 predicates
1 and 5, not G1 ownership work. Because this list drives gate advancement, their placement
scheduled G3 test migration and reorganization as prerequisites for completing G1. They now live
under the G3 gap list below, together with the G3.2/G3.3 additions that were also trailing this
section. Counts are unchanged — the verdict already scored G1 as 4 and G3 as 4.)*

## G3 gap list

1. **`RoleAssignmentRepositoryTests.cs` on EF-InMemory (G3.1)** — `tests/Humans.Application.Tests/Repositories/RoleAssignmentRepositoryTests.cs:21-24`. Fix: convert to the shared Postgres fixture (#764/#766 pattern). No-migration-needed: **y**.
2. **Harness-inherited EF-InMemory service tests (G3.2)** — `RoleAssignmentServiceTests` and `MagicLinkServiceTests` extend `ServiceTestHarness`, which stands up a real `HumansDbContext` over `.UseInMemoryDatabase(...)`; the original pass missed this because it grepped for a literal `HumansDbContext` the files never name. Fix: convert to `Substitute.For<IRoleAssignmentRepository>()` per #766, or move these off the harness. No-migration-needed: **y**.
3. **Documented `SyncBoardTeamAsync` trigger is untested (G3.3)** — `docs/sections/Auth.md` documents "assigning or ending a Board role triggers `SyncBoardTeamAsync`", but a repo-wide search for `SyncBoardTeamAsync` across `tests/` returns **zero hits** — the trigger is untested, not merely untraced. `RoleAssignmentServiceTests` covers Board overlap and active-role behaviour only. Fix: add assignment/end coverage asserting the sync is invoked (the seam already exists — `RoleAssignmentService` injects `ISystemTeamSync`, so it substitutes cleanly). No-migration-needed: **y**.
4. **Auth tests not grouped under a section folder (G3.5)** — `Services/RoleAssignmentServiceTests.cs`, `Services/MagicLinkServiceTests.cs`, `Services/AdminAuthorizationServiceTests.cs`, `Repositories/RoleAssignmentRepositoryTests.cs`. Fix: move into `tests/Humans.Application.Tests/Auth/`. No-migration-needed: **y**.

## Schema demolition queue

**Corrected 2026-08-03** — "no FK-cut or rename items surfaced" was wrong; the demolition
inventory in this same commit records both, and predicate 5(b) already found the HUM0024
grandfather that marks the FKs. Auth's schema queue is:

- **Cut 2 cross-section FK relationships** — `role_assignments → AspNetUsers` (the
  `RoleAssignment.User`/`CreatedByUser` pair), currently HUM0024-grandfathered at
  `RoleAssignmentConfiguration.cs:8`. Sequenced after the G1 nav-strip (gap #3).
- **Rename `role_assignments` → `auth_role_assignments`** — the table is unprefixed; the
  inventory proposes the section-prefixed form.

Otherwise the schema looks clean: no dead columns spotted in a shape-only read of
`RoleAssignment.cs`; `Notes` is free text with no observed debt.
