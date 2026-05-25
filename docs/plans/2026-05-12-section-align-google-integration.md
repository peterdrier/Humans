# Section Align — GoogleIntegration

**Run started:** 2026-05-12 | **Mode:** Existing-section | **Worktree:** `.worktrees/section-align-google-integration/`
**Branch:** `align/google-integration` (off `origin/main` @ `2d1aa0f0`)
**Canonical section name proposal:** `GoogleIntegration` (already canonical — namespace, folder, doc, controller all use it; URL prefix is `/Google`)

Reference doc: [`docs/sections/GoogleIntegration.md`](../sections/GoogleIntegration.md). Status declared (A) Fully migrated; this run confirms axes 1+2 are largely clean and focuses on tail-end drift, plus axis 3 organization.

---

## Axis 1 — boundary integrity

### 1. Name consistency
**Clean.** `GoogleIntegration` is used as folder, namespace, doc title; controller is `GoogleController`; URL prefix is `/Google`; ViewComponents prefix is `Google` / `MyGoogleResources`. No collisions.

### 2. Controller existence
**Clean.** `src/Humans.Web/Controllers/GoogleController.cs` exists with `[Route("Google")]`. No foreign controllers host Google routes.

### 3. URL surface
All ~30 routes under `/Google/*`. No `/Admin/Google/*` drift. No URL aliases.

**Discussion item (not drift):** Section doc lists routes flat under `/Google/*` rather than splitting Admin vs non-Admin under `/Google/Admin/*`. Most routes are `[Authorize(Policy = AdminOnly)]` but a few (`/Google/Sync`, `/Google/Human/{id}/SyncAudit`, `/Google/Sync/Resource/{id}/Audit`, `/Google/AuditLog/CheckDriveActivity`) admit `TeamsAdmin`, `Board`, or `HumanAdmin`. Heterogeneous role mix → keeping flat is defensible. **No action.**

### 4. Views folder
**Clean.** `src/Humans.Web/Views/Google/` exists with all section page views (`Accounts.cshtml`, `Sync.cshtml`, `SyncSettings.cshtml`, …). No page views in `Views/Shared/`.

### 5. ViewModel placement
**DRIFT.** Section ViewModels are scattered across **four** files:
- `Models/GoogleSyncViewModels.cs` (23 LOC) ✓ section-named
- `Models/SyncSettingsViewModels.cs` (17 LOC) ← section-affiliated but not Google-prefixed
- `Models/TeamSyncViewModels.cs` (16 LOC) ← ambiguous; sync ↔ Teams×Google
- `Models/AdminViewModels.cs` lines 230–253 hold `GoogleSyncAuditEntryViewModel` + `GoogleSyncAuditListViewModel` ← parked in the grab-bag file
- `Models/TeamResourceViewModels.cs` holds `GoogleResourceViewModel` ← legitimately co-owned (Teams owns `google_resources` table per §8)
- `Models/TeamViewModels.cs` line 317 has `ResourceAccessViewModel` ← used by `TeamAdminController` for resource mapping; Teams-owned, leave

**Phase 1 fix.** Consolidate Google-owned types into `Models/Google/GoogleSyncViewModels.cs`, `Models/Google/SyncSettingsViewModels.cs`, `Models/Google/GoogleSyncAuditViewModels.cs`. Extract `GoogleSyncAuditEntryViewModel` + `GoogleSyncAuditListViewModel` out of `AdminViewModels.cs`. `TeamSyncViewModels.cs` stays with Teams (named for the Team-side view of sync). `TeamResourceViewModels.cs` + `TeamViewModels.cs` stay where they are.

### 6. Controller-base leak
**DRIFT.** `HumansControllerBase.cs` lines 74–100 have section-specific helpers:
- `protected IActionResult GoogleSyncAuditView(...)` (line 74)
- `protected static GoogleSyncAuditListViewModel BuildGoogleSyncAuditViewModel(...)` (line 83)

These render Google-sync audit views. Per A1.6, section-named protected helpers in the base = drift. Move to `GoogleController` (or to a `GoogleControllerHelpers` static within the section). The base method is called from `GoogleController` and indirectly by audit-routing on other controllers — verify call sites before moving.

**Phase 1 fix.**

### 7. Extensions placement
**DRIFT — missing module.** All 25 other sections have `src/Humans.Web/Extensions/Sections/<Section>SectionExtensions.cs`. **GoogleIntegration does not.** Its DI registrations are scattered:
- `AdminSectionExtensions.AddAdminSection()` registers `ISyncSettingsRepository` + `ISyncSettingsService` (lines 18–20)
- `ProfileSectionExtensions.AddProfileSection()` registers `IEmailProvisioningService` (line 60)
- `Extensions/Infrastructure/GoogleWorkspaceInfrastructureExtensions.cs` registers the bridge clients + remaining services

**Phase 1 fix.** Create `Extensions/Sections/GoogleIntegrationSectionExtensions.cs`. Move SyncSettings + EmailProvisioning + remaining Google services out of `AdminSectionExtensions` / `ProfileSectionExtensions` / `GoogleWorkspaceInfrastructureExtensions`. Keep Infrastructure-only bits (credential loader, real/stub SDK clients toggle, configuration binding) in the Infrastructure extension; the Application-layer wiring (services + repository interfaces + bridge interfaces) moves to the section extension.

### 8. Role surface
**Clean.** Admin policy is `AdminOnly`; section-scoped roles (`TeamsAdmin`, `HumanAdmin`, `Board`) follow the `*Admin` suffix per `feedback_admin_superset`.

### 9. INBOUND cross-section DB access (reads + writes)
**Clean.** Grep against `_db.SyncServiceSettings`, `_db.GoogleSyncOutbox`, `_db.GoogleResources` outside `Infrastructure/Repositories/GoogleIntegration/` and `Infrastructure/Data/` returns **zero** matches. Section doc's "zero non-repository direct DbSet reads or writes" claim verified.

### 10. INBOUND EF navigations
**ONE violation found.** `src/Humans.Domain/Entities/AuditLogEntry.cs:75`:
```csharp
public GoogleResource? Resource { get; set; }
```
This is a navigation FROM the AuditLog section's entity INTO our `GoogleResource`. It is **actively read** via `.Include(e => e.Resource)` in `src/Humans.Infrastructure/Repositories/AuditLog/AuditLogRepository.cs:64` and `:80` (§6 cross-domain `.Include` violation).

**Boundary-fix protocol — we are the producer.** Our side: ensure the public API on a Google or TeamResource service satisfies the AuditLog viewer's need (resource display label by ID). `ITeamResourceService` already exposes resource lookups; verify if it has a `GetByIdsAsync(IEnumerable<Guid>) → IDictionary<Guid, ResourceDisplay>` or equivalent. If not, **add it in Phase 2**.

Flag **AuditLog** as a follow-up `/section-align` target: their job is to drop the nav from `AuditLogEntry`, remove the two `.Include` calls in `AuditLogRepository`, and switch to the service-side projection. We do NOT touch their code in this PR — only ensure our API exists.

### 11. OUTBOUND cross-section access (this section reaches OUT)
**Clean (raw DB).** Zero `_db.<OtherSectionTable>` reads in Google services or repositories. Zero `.Include(...)` across into other-section navs.

**Documented cross-section service calls** (all through public service interfaces, OK):
- `ITeamService`, `ITeamResourceService` (Teams)
- `IUserService`, `IUserEmailService`, `IProfileService` (Profiles/Users)
- `IEmailService` (Email)
- `IAuditLogRepository` (AuditLog — write path is OK; read path goes through `IAuditViewerService` per doc)

### 12. Controller → DbContext
**Clean.** `GoogleController` injects no `HumansDbContext`/`IDbContextFactory<HumansDbContext>`.

### 13. Migrations
**Out of scope for this run.** No migrations touch Google tables in the active branch. Section doc notes a `design-rules.md §8` stale name (`google_sync_outbox_events` vs actual `google_sync_outbox`) — fix is to update §8, no migration needed.

### 14. Section invariant doc shape
**Clean.** Follows `SECTION-TEMPLATE.md`. Concepts, Data Model, Routing, Actors & Roles, Invariants, Negative Access Rules, Triggers, Cross-Section Dependencies, Architecture all present and detailed. Two touch-and-clean items already named:
- `GoogleResource.Team` nav (cross-domain; owned by Teams to remove — flag Teams as follow-up)
- `design-rules.md §8` table-name correction

### 15. Prior review items
N/A — fresh branch, no PR yet.

---

## Axis 2 — internal cohesion

### 1. EF leakage from service layer
**Clean.** Grep of `Microsoft.EntityFrameworkCore`, `IQueryable`, `DbContext`, `DbSet`, `.Include(`, `.ToListAsync`, `.SaveChangesAsync` across `src/Humans.Application/Services/GoogleIntegration/` returns only doc-comment references (4 hits, all in `///` lines or string literals). No `using` statements, no ctor params, no method bodies use EF.

### 2. Caching placement
**DRIFT — but it's a Profile/Users-section invariant leak, not ours.**

`GoogleController.cs` injects `IMemoryCache _cache` only to call `_cache.InvalidateNobodiesTeamEmails()` (a `MemoryCacheExtensions` static method) at lines 483 and 725 — after admin actions that mutate the nobodies-team emails projection. The cache key is owned by `IUserEmailService` / `IUserService` (the projection of "current humans with @nobodies.team emails"). `ProfileController` does the same in 7 places.

§15 says caching belongs in the service layer; controllers should not directly invalidate. The right fix is to:
1. Expose `IUserEmailService.InvalidateNobodiesTeamEmailsCache()` (or fold the invalidation into the service methods that already mutate the underlying state).
2. Drop the `IMemoryCache` injection from `GoogleController`.

The owning service is in the **Users/Profiles** section, not Google. Flag **Users/Profiles** as a follow-up `/section-align` target.

**This PR's local action (Phase 2):** drop `_cache` from `GoogleController`, replace the two `Invalidate*` calls with `IUserEmailService.InvalidateNobodiesTeamEmailsCacheAsync()` (or equivalent) — *if* that method exists on `IUserEmailService` already, switch to it; *if not*, document the gap and leave the existing direct invalidation in place pending the Users/Profiles section pass.

### 3. DI lifetimes
**Cannot fully assess until Phase 1 consolidates wiring.** Spot check from current scatter:
- `ISyncSettingsRepository`: `Singleton` ✓ (via `AdminSectionExtensions`)
- `ISyncSettingsService`: `Scoped` ✓
- `IEmailProvisioningService`: `Scoped` ✓ (via `ProfileSectionExtensions`)

Other Google services will be re-verified during Phase 1 consolidation.

### 4. Repository pattern
**Clean.**
- `SyncSettingsRepository`, `GoogleResourceRepository`, `GoogleSyncOutboxRepository`, `DriveActivityMonitorRepository` all live under `Infrastructure/Repositories/GoogleIntegration/`
- All four interface contracts (`ISyncSettingsRepository`, `IGoogleResourceRepository`, `IGoogleSyncOutboxRepository`, `IDriveActivityMonitorRepository`) live in `Application/Interfaces/Repositories/`
- Verify `sealed` + `IDbContextFactory` constructor in each during Phase 1 audit pass (architecture tests pin `IsSealed` per-class already)

### 5. Shared visual components
**Single ViewComponent: `MyGoogleResourcesViewComponent`** — invoked from `Views/Home/Dashboard.cshtml` and `Views/WidgetGallery/Index.cshtml`. Good reuse pattern. No TagHelpers or cross-section partials in scope. No inverse drift (no inline Razor stitching that should be a VC). **Clean.**

### 6. Interface budget + segregation + consolidation
**DRIFT — missing budget entries.** None of the Google interfaces have entries in `tests/Humans.Application.Tests/Architecture/InterfaceMethodBudgetTests.cs`. Counts (approximate, from signature grep):

| Interface | Methods | Budget action |
|-----------|---------|---------------|
| `IGoogleSyncService` | ~20 | Add entry. Review for consolidation candidates (status-split methods, UI plumbing). |
| `IGoogleResourceRepository` | ~19 | Add entry. Review for predicate-push justification (`google_resources` is small — most reads could be `GetAll()` + caller-filter). |
| `IGoogleSyncOutboxRepository` | ~8 | Below ratchet threshold; section is append-only retention so predicate-push is justified per A2.6 exception. No action. |
| `IGoogleAdminService` | ~9 | Below threshold. No action. |
| `IGoogleWorkspaceUserService` | ~7 | Below threshold. |
| `IDriveActivityMonitorService` | ~1 | Below threshold. |
| `IEmailProvisioningService` | ~1 | Below threshold. |
| `ISyncSettingsService` | ~3 | Below threshold. |
| All `IGoogle*Client` bridges (~2–7) | — | Connector clients; intentionally narrow. |

**Phase 2 fix.** Add `Budgets` entries for `IGoogleSyncService` and `IGoogleResourceRepository`. **Phase 3 fix.** Audit `IGoogleResourceRepository` for status-split methods that could collapse to `GetAll()` + caller projection given the small dataset (Anti-pattern check per A2.6). Audit `IGoogleSyncService` for UI-plumbing leaks (methods called only by another in-section service).

### 7. Architecture test coverage
**Good but per-class — Phase 3 consolidation candidates exist.**

Current Google architecture-test files (all under `tests/Humans.Application.Tests/Architecture/`):
- `GoogleIntegrationArchitectureTests.cs` — pins `EmailProvisioningService` + `GoogleWorkspaceSyncService` (namespace, no-DbContext, no-Google.Apis, sealed)
- `GoogleAdminArchitectureTests.cs` — pins `GoogleAdminService`
- `GoogleWorkspaceUserArchitectureTests.cs` — pins `GoogleWorkspaceUserService` + `IWorkspaceUserDirectoryClient`
- `DriveActivityMonitorArchitectureTests.cs` — pins `DriveActivityMonitorService` + `IDriveActivityMonitorRepository`
- `GoogleWorkspaceSyncBridgeArchitectureTests.cs` — pins 4 bridge interfaces

**Generic-rule candidates** (per A2.7) — `Rules/` folder already exists with `NoServiceInjectsDbContextRule.cs`, `NoLinqAtDbLayerRule.cs`, etc. The repeated per-class tests `_HasNoDbContextConstructorParameter`, `_HasNoDbContextFactoryConstructorParameter`, `_IsSealed`, `_HasNoGoogleApisImports` look like duplicates of generic patterns. Phase 3 candidate: verify `Rules/NoServiceInjectsDbContextRule` covers `_HasNoDbContextConstructorParameter` and remove the per-class copies. Same for sealed-impl checks if a generic `IsSealed` rule exists.

**Missing tests:**
- `GoogleRemovalNotificationService` — no architecture test (production class added 2026-04+, not yet pinned). Phase 2 add (namespace + no-DbContext + sealed).
- `SyncSettingsService` — no architecture test. Phase 2 add (or rely on generic Rules + `Rules/NoServiceInjectsDbContextRule` if Phase 3 generic-test consolidation lands first).

---

## Axis 3 — test focus

### 1. Test folder placement
**DRIFT — scattered by-type instead of by-section.** Current layout:

| File | Folder | Section-aligned home |
|------|--------|----------------------|
| `Architecture/GoogleIntegrationArchitectureTests.cs` | `Architecture/` | **stay** (A3.1 carve-out) |
| `Architecture/GoogleAdminArchitectureTests.cs` | `Architecture/` | **stay** |
| `Architecture/GoogleWorkspaceUserArchitectureTests.cs` | `Architecture/` | **stay** |
| `Architecture/DriveActivityMonitorArchitectureTests.cs` | `Architecture/` | **stay** |
| `Architecture/GoogleWorkspaceSyncBridgeArchitectureTests.cs` | `Architecture/` | **stay** |
| `Services/GoogleAdminServiceTests.cs` | `Services/` | `GoogleIntegration/` |
| `Services/GoogleRemovalNotificationServiceTests.cs` | `Services/` | `GoogleIntegration/` |
| `Services/GoogleSyncRemovalNotificationIntegrationTests.cs` | `Services/` | `GoogleIntegration/` |
| `Services/GoogleWorkspaceUserServiceTests.cs` | `Services/` | `GoogleIntegration/` |
| `Services/DriveActivityMonitorServiceTests.cs` | `Services/` | `GoogleIntegration/` |
| `Services/EmailProvisioningServiceTests.cs` | `Services/` | `GoogleIntegration/` |
| `Services/SyncSettingsServiceTests.cs` | `Services/` | `GoogleIntegration/` |
| `Repositories/GoogleResourceRepositoryTests.cs` | `Repositories/` | `GoogleIntegration/` |
| `Repositories/GoogleSyncOutboxRepositoryTests.cs` | `Repositories/` | `GoogleIntegration/` |
| `Jobs/GoogleResourceReconciliationJobTests.cs` | `Jobs/` | `GoogleIntegration/` |
| `Jobs/ProcessGoogleSyncOutboxJobTests.cs` | `Jobs/` | `GoogleIntegration/` |
| `Infrastructure/GoogleWorkspace/GoogleDrivePermissionsClientClassifierTests.cs` | `Infrastructure/GoogleWorkspace/` | `GoogleIntegration/Infrastructure/` |
| `Infrastructure/GoogleWorkspace/GoogleWorkspaceSyncBridgeDependencyInjectionTests.cs` | `Infrastructure/GoogleWorkspace/` | `GoogleIntegration/Infrastructure/` |
| `Infrastructure/GoogleWorkspace/StubGoogleDirectoryClientTests.cs` | `Infrastructure/GoogleWorkspace/` | `GoogleIntegration/Infrastructure/` |
| `Infrastructure/GoogleWorkspace/StubGoogleDrivePermissionsClientTests.cs` | `Infrastructure/GoogleWorkspace/` | `GoogleIntegration/Infrastructure/` |
| `Infrastructure/GoogleWorkspace/StubGoogleGroupMembershipClientTests.cs` | `Infrastructure/GoogleWorkspace/` | `GoogleIntegration/Infrastructure/` |
| `Infrastructure/GoogleWorkspace/StubGoogleGroupProvisioningClientTests.cs` | `Infrastructure/GoogleWorkspace/` | `GoogleIntegration/Infrastructure/` |

**Phase 1 mechanical fix.** Create `tests/Humans.Application.Tests/GoogleIntegration/`. `git mv` all Service/Repository/Jobs/Infrastructure tests into it (preserve `Infrastructure/` as a subfolder under the section, mirroring `AuditLog/`). Architecture tests stay where they are. Update namespaces.

### 1a. One test file per production class (1-to-1 rule)
**Mostly clean with one gap.**

| Production class | Test file | Status |
|------------------|-----------|--------|
| `GoogleAdminService` | `GoogleAdminServiceTests.cs` | ✓ |
| `GoogleRemovalNotificationService` | `GoogleRemovalNotificationServiceTests.cs` | ✓ |
| `GoogleWorkspaceUserService` | `GoogleWorkspaceUserServiceTests.cs` | ✓ |
| `DriveActivityMonitorService` | `DriveActivityMonitorServiceTests.cs` | ✓ |
| `EmailProvisioningService` | `EmailProvisioningServiceTests.cs` | ✓ |
| `SyncSettingsService` | `SyncSettingsServiceTests.cs` | ✓ |
| `GoogleWorkspaceSyncService` | — **missing** (only integration tests via `GoogleSyncRemovalNotificationIntegrationTests.cs`) | **add** |
| `SyncSettingsRepository` | — **missing** | **add or document why** |
| `GoogleResourceRepository` | `GoogleResourceRepositoryTests.cs` | ✓ |
| `GoogleSyncOutboxRepository` | `GoogleSyncOutboxRepositoryTests.cs` | ✓ |
| `DriveActivityMonitorRepository` | — **missing** | **add or document why** |

**Phase 2 add:** `GoogleWorkspaceSyncServiceTests.cs` — the section's largest service (~900+ LOC) currently has zero direct unit tests. High-value add. Focus on invariants (gateway-operations sync-mode enforcement, permanent-vs-transient error classification, `GoogleEmailStatus` reset).

**Phase 2 add or document:** `SyncSettingsRepositoryTests.cs` and `DriveActivityMonitorRepositoryTests.cs` — assess if the repo is a pure pass-through covered by architecture tests + service tests, or if behavior needs pinning.

### 2. Coverage map against section doc

Invariants & where covered:

| Invariant | Covered? | Where |
|-----------|----------|-------|
| All Drive resources on Shared Drives | Partial | Implicit via `GoogleResource` shape + bridge interface; no explicit test asserting My-Drive paths are rejected. |
| Direct permissions only | Likely | `GoogleDrivePermissionsClientClassifierTests` — verify. |
| `RestrictInheritedAccess` + `inheritedPermissionsDisabled` enforcement | **No direct test** for the reconciliation drift-correction path. |
| Sync settings per-service; None disables sync | **Partial** | `SyncSettingsServiceTests` covers settings CRUD; need test that proves the four gateway operations actually check the mode before calling Google. |
| Email resolution rule (`@nobodies.team` else OAuth) | Partial | `EmailProvisioningServiceTests`. |
| `GoogleEmailStatus` reset on email change | **No direct test** found — verify in Phase 2. |
| Permanent (400/403/404) → `FailedPermanently`; transient retries | **No direct test** in the absent `GoogleWorkspaceSyncServiceTests`. |
| Service-account auth (no domain-wide delegation) | Configuration-time invariant; not unit-testable. |
| "Exactly four gateway operations" enforcing sync mode | **No structural test** — could be a tagged-method or roslyn arch test. |

**Phase 2 adds:** sync-mode gating, `GoogleEmailStatus` reset on email change, permanent-vs-transient classification, drift-correction for `RestrictInheritedAccess`. Each: one focused test. Skip if already implicit in integration tests (verify by re-reading).

Negative Access Rules:
- TeamsAdmin/Board cannot manage sync settings — should be a `GoogleAuthorizationHandlerTests` or controller test. Verify.
- Coordinators cannot manage sync settings / bulk sync — same.
- Regular humans no access — same.

**Phase 2 add:** any of those missing. Probably one parameterized auth-handler test.

Triggers:
- Member-change → outbox event queue — covered? Check `ProcessGoogleSyncOutboxJobTests` or a Teams-side test. Likely covered indirectly.
- Email change → status reset + re-enqueue — covered? Probably not. Add.
- Link resource → sync current members — covered? Probably integration-level. Verify.
- Unlink resource → managed permissions removed — covered? Verify.
- Hourly system-team sync, daily reconciliation — job-tagged; cron schedule verified by job tests.

### 3. Redundancy / over-testing flags
Defer to actual file reads in Phase 3. No high-confidence redundancy candidates from Phase 0 grep alone. Eyeball candidates:
- The four `Stub*ClientTests.cs` files: thin wrapper tests around hand-written stubs; assess if any are exercising-the-mock-not-the-behavior style. Phase 3 spot check.
- `GoogleSyncRemovalNotificationIntegrationTests.cs` (Services folder) overlaps in scope with `GoogleRemovalNotificationServiceTests.cs` (unit) — verify the integration test asserts something the unit test cannot.

### 4. Test-to-section ratio
**Production:** 7,620 LOC (Application/Services + Infrastructure/Repositories + Infrastructure/Services/GoogleWorkspace).
**Tests:** 3,986 LOC.
Ratio ~52% — reasonable, neither under nor over.

### 5. Brittleness signals
Defer to Phase 3 file-level read. No reflection/clock-coupling/order-dependency signals from Phase 0 grep.

### 6. Mutation signal (Stryker.NET)
**No recent report.** `local/stryker-runs/` has no `google` subdirectory.

**Phase 3 proposal — optional, ask before running.** Run Stryker against the section before Phase 3 prune pass:
```powershell
Push-Location tests/Humans.Application.Tests
dotnet tool run dotnet-stryker --config-file stryker-google-integration-config.json `
  --output ..\..\local\stryker-runs\google-integration
Pop-Location
```
Will need a `stryker-google-integration-config.json` mirroring `stryker-profiles-config.json` with `mutate` narrowed to `src/Humans.Application/Services/GoogleIntegration/**` + `src/Humans.Infrastructure/Repositories/GoogleIntegration/**`. Estimated runtime: probably 15–30 minutes given service LOC. Surface to user before Phase 3.

---

## Test-attribute gate (per `docs/testing/mutation-testing.md`)
- Baseline as of last gate update: tracked in doc; will check during Phase 2 before adding tests.
- This run's expected net delta: **+3 to +6** (sync-mode gating + GoogleEmailStatus reset + permanent/transient classification + `GoogleWorkspaceSyncServiceTests`) **− 0 to −3** (Phase 3 prune of generic-pattern duplicates, redundancies). Net likely small positive; justification: adding invariant tests for previously-uncovered Invariants/Triggers from `GoogleIntegration.md` per A3.2.

---

## Stop conditions tripped
**None.** All check items pass:
- No name collision.
- Cross-section DB fix (AuditLog `.Include` removal) is a consumer-side gap — flagged as follow-up, NOT pursued in this PR.
- Owning sections are unambiguous.
- Section doc is well-shaped.
- Branch is fresh; no uncommitted work.

One **deliberate hold** (not a stop condition): the cache invalidation cleanup in `GoogleController` depends on `IUserEmailService` exposing an invalidation method. If it doesn't, document the gap and leave it; flag Users/Profiles as follow-up.

---

## Follow-up `/section-align` targets

This run surfaces three other sections that need their own pass to close cross-section drift:

| Section | Reason |
|---------|--------|
| **AuditLog** | `AuditLogEntry.Resource` cross-domain nav + `AuditLogRepository.cs:64,80` `.Include(e => e.Resource)` reads INTO `GoogleResource`. Their job (after we ensure `ITeamResourceService` has the display lookup) is to strip the nav and switch to service-side projection. |
| **Teams** | `GoogleResource.Team` nav lives on our entity but is owned by Teams per §8. Documented as touch-and-clean in our section doc. Their job to remove the nav and convert `GoogleResourceConfiguration.cs` to typed-FK form. |
| **Users / Profiles** | `MemoryCacheExtensions.InvalidateNobodiesTeamEmails` is a static cache extension called from controllers (GoogleController × 2, ProfileController × 7). Caching belongs in the service layer per §15. The cached projection is owned by their section. Their job: expose `IUserEmailService.InvalidateNobodiesTeamEmailsAsync()` (or fold invalidation into the mutating service methods), have consumers switch. |

---

## Phase plan

### Phase 1 — Surface alignment (Sonnet subagents, mechanical)
1. **Create `Extensions/Sections/GoogleIntegrationSectionExtensions.cs`.** Move SyncSettings + EmailProvisioning + remaining Google service+repository registrations out of `AdminSectionExtensions`, `ProfileSectionExtensions`, and `GoogleWorkspaceInfrastructureExtensions`. Wire it into `Program.cs` next to the other section extension calls.
2. **Move controller-base Google helpers** out of `HumansControllerBase.cs:74–100` into `GoogleController` (or `GoogleControllerHelpers` static helper if the views are rendered elsewhere). Verify no other controller calls them; if any do, expose via a more targeted helper.
3. **Consolidate ViewModels**: extract `GoogleSyncAuditEntryViewModel` + `GoogleSyncAuditListViewModel` out of `AdminViewModels.cs:230–253`. Group all Google-owned ViewModels under `Models/Google/` (`GoogleSyncViewModels.cs`, `SyncSettingsViewModels.cs`, `GoogleSyncAuditViewModels.cs`). Update view bindings + controller using statements.
4. **Move tests into `tests/Humans.Application.Tests/GoogleIntegration/`** with `Infrastructure/` subfolder mirroring `AuditLog/`. `git mv` 13 test files. Update namespaces. Architecture tests stay in `Architecture/`.

Build green between steps. Push at end of phase.

### Phase 2 — Fix arch violations + missing coverage (Sonnet; Opus for nuanced)
1. **Cross-section read API for AuditLog.** Verify or add a `GetByIdsAsync(IEnumerable<Guid> ids, CancellationToken)` (or similar display-name projection) on `ITeamResourceService` so AuditLog can resolve resource labels without nav-loading. If already present, no change; just confirm and document. AuditLog flagged as follow-up regardless.
2. **`IUserEmailService` cache-invalidation API check.** If method exists, switch GoogleController to call it and drop `IMemoryCache` injection. If not, leave the direct cache call in place; document in plan; flag Users/Profiles.
3. **Add `InterfaceMethodBudgetTests.Budgets` entries** for `IGoogleSyncService` and `IGoogleResourceRepository` at current counts.
4. **Add missing tests** for invariants identified in Axis 3 §2:
   - `GoogleWorkspaceSyncServiceTests.cs` — gateway-operations sync-mode enforcement + permanent-vs-transient error classification + GoogleEmailStatus reset
   - One auth-handler test asserting TeamsAdmin / Board / Coordinator cannot manage sync settings
   - Trigger test: email change → status reset + re-enqueue (likely a `GoogleWorkspaceSyncService` or `UserEmailService` test)
5. **Add missing architecture tests** for `GoogleRemovalNotificationService` and `SyncSettingsService` (namespace + no-DbContext + sealed) — defer if Phase 3 generic consolidation will subsume them.
6. **Optionally add** `SyncSettingsRepositoryTests`, `DriveActivityMonitorRepositoryTests` — only if not pure pass-through.

Push at end of phase.

### Phase 3 — /simplify pass (Opus)
Scope target: 7,620 LOC ⇒ ~6–8 fixes per the simplify-scope rule.

1. Review `IGoogleSyncService` (~20 methods) for status-split anti-pattern and UI-plumbing leaks. Trim.
2. Review `IGoogleResourceRepository` (~19 methods) — the `google_resources` table is small; many predicate-pushed `GetByXAsync` methods are candidates for collapse to `GetAllAsync` + caller filter (per A2.6 at-scale guidance). Trim where justified.
3. Consolidate per-class arch tests into generic `Rules/` tests where a generic rule already exists. Specifically:
   - Per-class `_HasNoDbContextConstructorParameter` → covered by `Rules/NoServiceInjectsDbContextRule`. Remove duplicates.
   - Per-class `_HasNoGoogleApisImports` and `_DoesNotReferenceGoogleSdkTypes` → if not already generic, propose one assembly-level `Rules/NoGoogleApisInApplicationRule` test that subsumes per-class assertions.
   - `_IsSealed` per repo/service → if a generic `Rules/RepositoryImplementations_AreSealed` / `ApplicationServices_AreSealed` exists, remove per-class. Otherwise add the generic.
4. (Optional) Run Stryker against the section after asking user — use surviving mutants to drive 1–2 targeted behavior tests; use high-confidence test-debt scores to drive 1–2 redundant-test deletions.
5. Spot-check brittleness signals in the moved test files (`GoogleIntegration/` folder) — rewrite any mock-graph-assertion-only tests to assert observable behavior.

Push at end of phase.

### Phase 4 — Doc polish (Opus)
1. Update `docs/sections/GoogleIntegration.md`:
   - **Status** — reflect the section extension consolidation, controller-base cleanup, ViewModel regroup, test relocation
   - **Cross-Section Dependencies** — name AuditLog as the pending consumer-side gap (and Users/Profiles for the cache invalidation gap)
   - Verify `## Routing` table is still accurate after any route-attribute touches
2. Update `docs/architecture/dependency-graph.md` — Google Integration's inbound/outbound edges. Specifically:
   - Inbound from Teams (already via `IGoogleSyncService` — confirm captured)
   - Inbound from AuditLog flagged as pending strip
   - Outbound to TeamResource, UserEmail, Profile, Email, AuditLog — confirm captured
3. Update `data-model.md` — `google_sync_outbox` table-name correction noted in §8 (skill doc says §8 lists `google_sync_outbox_events`).
4. Update `design-rules.md §8` table-ownership map row for `google_sync_outbox` (rename from `google_sync_outbox_events`).
5. Update `todos.md` if needed; `maintenance-log.md` after merge.
6. **Closing /section-align run** in the same impl session — should return "clean except for `<AuditLog, Teams, Users/Profiles>` pending suppliers."

Push. Final bot-review loop.

---

## Notes for orchestrator (Phase 1 impl session)

- This plan file is the source of truth — read it once at the start of Phase 1, do not re-read the codebase wholesale.
- Each phase item above is one subagent dispatch. Hard parallel cap of 3.
- Target end of Phase 1 ≤ 100k tokens in orchestrator context.
- If approaching 200k by Phase 3, commit and `/cls` then resume from this plan.
