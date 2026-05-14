# Section Align — AuditLog
**Run started:** 2026-05-12 | **Mode:** existing-section | **Worktree:** `H:\source\Humans\.worktrees\section-align-AuditLog`
**Branch:** `align/auditlog` (off `origin/main` @ `760d98f2`)

Phase 0 inventory across three axes per `.claude/skills/section-align/SKILL.md`:
1. **Boundary integrity** — clean section name, addressable routes/views/models, no cross-section DB access (except User ↔ Profile).
2. **Internal cohesion** — no EF leak from service layer, caching only in service layer, proper interfaces, reusable ViewComponents, architecture-test coverage.
3. **Test focus** — section-aligned tests in one canonical folder, 1-to-1 production-class to test-file mapping, coverage maps to invariants/negatives/triggers, redundancy pruned.

Boundary-fix protocol: producers own the API surface; consumers own the call site. When we can't fix our consumer-side cross-section access because the supplier doesn't have the API yet, we flag the supplier as a follow-up /section-align target and the section becomes **provisionally aligned**.

---

## Axis 1 — Boundary integrity

### 1.1 Section name consistency — names ✓, surfaces ✗

Names align everywhere — folders, namespaces, interfaces, services, repo, tests, configurations, DbSet, ViewComponent partial folder. No variants.

**But the section has no front door:**
- No `AuditLogController.cs`
- No `Views/AuditLog/` directory
- No `/AuditLog/*` route prefix

### 1.2 URL surface — **CONVENTION VIOLATION**

| Current route | Host controller | Should be |
|---------------|-----------------|-----------|
| `GET /Board/AuditLog` | `BoardController.AuditLog` | `GET /AuditLog` on `AuditLogController.Index` |
| `POST /Google/AuditLog/CheckDriveActivity` | `GoogleController.CheckDriveActivity` | `POST /AuditLog/CheckDriveActivity` |
| `GET /Google/Sync/Resource/{id}/Audit` | `GoogleController.GoogleSyncResourceAudit` | `GET /AuditLog/Resource/{id}` |
| `GET /Google/Human/{id}/SyncAudit` | `GoogleController.HumanGoogleSyncAudit` | `GET /AuditLog/Human/{id}` |

`BoardController.Index` consuming `IAuditViewerService.GetRecentAsync(15)` for the dashboard activity widget is fine — that's widget consumption, not route ownership.

### 1.3 Views surface — **CONVENTION VIOLATION**

| Current view | Should be |
|--------------|-----------|
| `Views/Shared/AuditLog.cshtml` (list page) | `Views/AuditLog/Index.cshtml` |
| `Views/Shared/GoogleSyncAudit.cshtml` (per-resource / per-user) | `Views/AuditLog/GoogleSync.cshtml` |
| `Views/Shared/_AuditLogContent.cshtml`, `_AuditLogScripts.cshtml` | keep in Shared if Board dash reuses; else move |
| `Views/Shared/Components/AuditLog/{Default,_Entry,_EntryList}.cshtml` | ✓ correctly placed (ViewComponent partials are genuinely shared widgets) |

### 1.4 Controller-base leak — **CONVENTION VIOLATION**

`Controllers/HumansControllerBase.cs:74-109` defines section-specific render helpers (`GoogleSyncAuditView`, `BuildGoogleSyncAuditViewModel`) on a generic base. Move into `AuditLogController`.

### 1.5 ViewModel placement — **CONVENTION VIOLATION**

`Models/AdminViewModels.cs` houses `AuditLogListViewModel` (line 219), `GoogleSyncAuditEntryViewModel` (230), `GoogleSyncAuditListViewModel` (245). Move to `Models/AuditLogViewModels.cs`.

### 1.6 Extensions placement — minor violation

`Extensions/AuditLogUiExtensions.cs` (Razor filter-class helpers) — should live under `Extensions/Sections/` to match the DI-wiring sibling `Extensions/Sections/AuditLogSectionExtensions.cs`.

### 1.7 Inbound WRITES to `ctx.AuditLogEntries` — **CRITICAL VIOLATION (1)**

Exhaustive grep across `src/`:

| File:Line | Operation | Verdict |
|-----------|-----------|---------|
| `Infrastructure/Repositories/AuditLog/AuditLogRepository.cs` (17 sites) | reads + Add | ✓ legitimate |
| `Infrastructure/Repositories/GoogleIntegration/DriveActivityMonitorRepository.cs:81` | `ctx.AuditLogEntries.AddRange(anomalies)` | **✗ VIOLATION** |
| `Infrastructure/Data/HumansDbContext.cs:40` | DbSet declaration | ✓ |

**Boundary-fix protocol applied:** caller bulk-writes pre-shaped `AuditLogEntry` objects. Our public surface (`IAuditLogService`) offers only single-entry `LogAsync` and `LogGoogleSyncAsync`. There's no `LogManyAsync` or anomaly-specific surface that matches the caller's batch shape.

**Our job in THIS PR:** add the API the caller needs (one of):
- (a) `IAuditLogService.LogAnomaliesAsync(IReadOnlyList<AnomalyEvent>)` — domain-specific, hides AuditLogEntry construction.
- (b) Loop of per-entry `LogAsync` calls — no new API needed; document the call pattern explicitly.

Option (b) is preferred (audit is already best-effort per-entry; batching loses no useful semantic since the "atomic with LastRunAt" rationale isn't section-grade). No new method = no IAuditLogService budget bump.

**GoogleIntegration's job (next /section-align target):** switch `DriveActivityMonitorRepository.PersistAnomaliesAsync` to call through `IAuditLogService` and move the LastRunAt update to its own call (or accept that anomalies persist independently — they're best-effort).

### 1.8 Inbound READS / EF joins on AuditLog — none

Zero. No section reads `ctx.AuditLogEntries`, no `.Include(...Audit...)`, no LINQ traversal from outside. No entity declares an `AuditLogEntry?` nav inbound. **Boundary clean on the read side.**

### 1.9 Cross-section `IAuditLogService` consumers — sink pattern working

19 sections inject for writes (intentional sink). 5 inject `IAuditViewerService` for reads. All through public interfaces. This is exactly the design and not a violation.

### 1.10 Controller → DbContext — clean

No controller injects `HumansDbContext`.

---

## Axis 2 — Internal cohesion

### 2.1 EF leakage from service layer — ✓ CLEAN

`AuditLogService` and `AuditViewerService` both clean of `Microsoft.EntityFrameworkCore`, `IQueryable`, `DbContext`, `DbSet`, `.Where(...)`, `.Include(...)`, `.ToListAsync(...)`, `.SaveChangesAsync(...)`. Architecture test `AuditLogService_HasNoDbContextConstructorParameter` pins this.

### 2.2 Caching placement — ✓ CLEAN

No `IMemoryCache`/`MemoryCache`/`IDistributedCache` anywhere in service or repository. Section is small-volume and admin-only; doc justifies "no caching decorator" per §15 Option A. Architecture test `AuditLogService_HasNoIMemoryCacheConstructorParameter` pins this.

### 2.3 Repository pattern — ✓ CLEAN

`AuditLogRepository` is `sealed`, uses `IDbContextFactory<HumansDbContext>`, registered as Singleton. `AuditLogService` is Scoped, depends on `IAuditLogRepository`. Standard §15 shape.

### 2.4 DI lifetimes — ✓ CLEAN

`AuditLogSectionExtensions.AddAuditLogSection`:
- Repository: Singleton ✓
- Services: Scoped ✓
- `IUserDataContributor` re-exports the scoped service ✓

### 2.5 OUTBOUND cross-section access — **§6 VIOLATION** (1)

`AuditLogRepository.GetGoogleSyncByUserAsync` (line 64) and `GetGoogleSyncByUserIdsAsync` (line 80):

```csharp
.Include(e => e.Resource)   // AuditLog → GoogleResource cross-domain Include
```

Violates design-rules §6 (no cross-domain `.Include`). The `Resource` nav exists on `AuditLogEntry` and points at `GoogleResource` (owned by GoogleIntegration). The repo uses it to populate `AuditEvent.ResourceName` for the GoogleSync audit view.

**Boundary-fix protocol applied (we are the consumer):**
- Does GoogleIntegration's public surface offer a batch name lookup for resources?
- `IGoogleResourceRepository.GetByIdAsync(Guid)` exists — single, repository-layer (we shouldn't reach into another section's repo from our service).
- No `IGoogleResourceService` exists in `Interfaces/GoogleIntegration/` (only `IGoogleSyncService`, `IGoogleRemovalNotificationService`, plus consumer-side `ITeamResourceService`/`ITeamPageService`).
- **API gap on GoogleIntegration:** no public service-layer batch lookup `GetByIdsAsync(IReadOnlyCollection<Guid>) → Dictionary<Guid, string>` (or `(Name, …)` tuple), which is what AuditLog needs for in-memory display stitching (mirrors the pattern AuditLog already uses for User/Team names).

**Disposition:**
- THIS PR — leave the `.Include` in place; document the gap. AuditLog cannot fix this cleanly without an upstream API.
- **GoogleIntegration becomes a /section-align target.** Its work includes: introduce `IGoogleResourceService` (or extend an existing one) with `GetByIdsAsync` for batched name resolution, then AuditLog migrates off the Include in a follow-up.
- Flag for the AuditLog section doc: Cross-Section Dependencies should explicitly name "GoogleIntegration — currently joined via EF `Include(e => e.Resource)`; awaiting `IGoogleResourceService.GetByIdsAsync` to migrate to in-memory stitching."

### 2.6 OUTBOUND cross-section access — **§2c VIOLATIONS** (2) — fix now

Zero-tolerance rule (updated 2026-05-12): only User ↔ Profile cross-section access is allowed. The previously-"sanctioned" reads are now violations.

| File:Line | Read | Disposition |
|-----------|------|-------------|
| `AuditLogRepository.GetUserDisplayNamesAsync` (line 221) | `ctx.Users.Where(u => userIds.Contains(u.Id)).Select(...)` | **Phase 2 fix.** Move the resolution out of the Infrastructure repo into `AuditLogService` (Application layer); have the service call `IUserService.GetByIdsAsync(IReadOnlyList<Guid>)`. Verify the return shape includes `DisplayName`; if not, **Users section is a follow-up target** to expose the right shape. |
| `AuditLogRepository.GetTeamNamesAsync` (line 234) | `ctx.Teams.Where(t => teamIds.Contains(t.Id)).Select(...)` | **Phase 2 fix.** Same shape: lift to `AuditLogService` calling `ITeamService.GetTeamNamesByIdsAsync` (referenced in Camps section doc as the existing pattern). Verify shape `Dictionary<Guid, (Name, Slug)>`; if not, **Teams section is a follow-up target**. |

Side effect: `AuditLogRepository` drops two methods (15 → 13); both cross-table helpers move into `AuditLogService` as private compose-step + service-call. `IAuditLogService` surface unchanged (still exposes `GetUserDisplayNamesAsync`/`GetTeamNamesAsync` for `AuditViewerService` to consume — these now delegate to the cross-section service calls instead of the repo).

### 2.7 Cross-domain navs on AuditLog entity — fix now

| Nav | Status | Disposition |
|-----|--------|-------------|
| `AuditLogEntry.ActorUser` (→ User) | Declared, never read | **Phase 2 delete.** Drop the nav; keep `ActorUserId` scalar FK. EF config `OnDelete: SetNull` already present and stays. |
| `AuditLogEntry.Resource` (→ GoogleResource) | Read by `AuditLogRepository.GetGoogleSyncByUserAsync` (line 64) and `GetGoogleSyncByUserIdsAsync` (line 80) via `.Include` | **§6 violation, blocked.** Cannot delete until `.Include` is removed. `.Include` cannot be removed until GoogleIntegration exposes batched name resolution. **Follow-up: GoogleIntegration.** Section becomes provisionally aligned; we return here to close out once GoogleIntegration ships `IGoogleResourceService.GetByIdsAsync` (or equivalent). |

### 2.8 Interface segregation + consolidation evaluation

| Interface | Methods | Budgeted? | Notes |
|-----------|---------|-----------|-------|
| `IAuditLogService` | 14 | ❌ no | 3 writes + 8 reads + 2 cross-section display helpers + 2 specialized lookups |
| `IAuditViewerService` | 6 | n/a | wraps Service for resolved-event reads |
| `IAuditLogRepository` | 15 → 13 after §2.6 | ❌ no | 1 write + reads (cross-table helpers removed in Phase 2) |

**Issues:**
- **Consolidation check (axis 2.6 anti-pattern):** AuditLog has many `GetByUser` / `GetGoogleSyncByUser` / `GetFiltered` / `GetByResource` methods rather than `GetAll().Where(...)`. **This section is one of the legitimate exceptions** — `audit_log` is a large append-only table where predicate-push to DB is required (~96 writers across the app, retention is indefinite). Keep the predicate-pushed reads on the interface; the section doc must justify this under `## Architecture` (Phase 4 doc add).
- **UI plumbing on the main interface:** `IAuditLogService.GetUserDisplayNamesAsync` / `GetTeamNamesAsync` exist only so `AuditViewerService` can call them. After §2.6's move (resolution out of repo, into service), these become thin facade methods. Phase 3 candidate: move them private to AuditViewerService.
- **Read-shape duplication between Service and ViewerService** — Service returns raw `AuditLogEntry`, ViewerService returns resolved `AuditEvent`. Few callers outside ViewerService consume raw entries (jobs + GDPR contributor). Phase 3: trim Service reads down to what non-UI callers need.
- **Budget entries (Phase 2 add):** `InterfaceMethodBudgetTests.Budgets` entries for `IAuditLogService` (14) and `IAuditLogRepository` (post-§2.6 count, expected 13).

### 2.9 Shared visual components — inventory by type

| Type | Component | Location | Verdict |
|------|-----------|----------|---------|
| **ViewComponent** ✓ preferred | `AuditLogViewComponent` | `src/Humans.Web/ViewComponents/AuditLogViewComponent.cs` | Keep. Strong reuse (Calendar/Event, Profile/AdminDetail, TeamAdmin/Members, WidgetGallery demo). Partials at `Views/Shared/Components/AuditLog/{Default,_Entry,_EntryList}.cshtml`. Wired to `IAuditViewerService.GetFilteredAsync`. |
| **TagHelper** | none for AuditLog | — | n/a |
| **Partial view** | `Views/Shared/_AuditLogContent.cshtml`, `_AuditLogScripts.cshtml` | Shared root | **Evaluate.** Used by `Views/Shared/AuditLog.cshtml` (the list page). If only the list page uses these, they should move with the page into `Views/AuditLog/_Content.cshtml` and `_Scripts.cshtml` (intra-section partials, not cross-section shared). Phase 1 verification step. |

The ViewComponent dominates the cross-section render path. No conversion candidates (no TagHelpers wrapping audit data, no cross-section partials).

### 2.10 Architecture test coverage — partial, plus per-section duplication to generalize

Present:
- ✓ `AuditLogService_LivesInHumansApplicationServicesAuditLogNamespace` — **per-section duplicate** of a generalizable pattern
- ✓ `AuditLogService_HasNoDbContextConstructorParameter` — **per-section duplicate** (every section's service has the same constraint)
- ✓ `AuditLogService_HasNoIMemoryCacheConstructorParameter` — **per-section duplicate**
- ✓ `AuditLogService_TakesRepository` — **per-section duplicate**
- ✓ `AuditLogService_ConstructorTakesNoStoreType` — **per-section duplicate**
- ✓ `IAuditLogRepository_LivesInApplicationInterfacesRepositoriesNamespace` — **per-section duplicate**
- ✓ `AuditLogRepository_IsSealed` — **per-section duplicate** — Peter explicitly called out: should become a generic `IRepository_Implementations_AreAllSealed` reflecting over every `IRepository` impl
- ✓ `IAuditLogRepository_HasNoUpdateOrDeleteMethods` — **section-specific** (append-only invariant; keep)

Missing (Phase 2 additions, section-specific):
- ✗ `Only<Section>Repository_References_AuditLogEntries_DbSet` — Roslyn/reflection scan; catches DriveActivityMonitorRepository.cs:81 today and prevents regression. Append-only-DbSet sole-writer rule is section-specific.

Missing (Phase 2 additions, **as new generic tests** in `tests/Humans.Application.Tests/Architecture/Rules/`):
- ✗ `Application_Services_TakeNoDbContext` — reflect over all `IApplicationService` impls
- ✗ `Application_Services_TakeNoIMemoryCache_UnlessRegistered` — same
- ✗ `IRepository_Implementations_AreAllSealed` — reflect over all `IRepository` impls
- ✗ `Repository_Implementations_LiveInInfrastructureRepositories` — namespace check across all repos

Phase 3 prune: once the generic tests exist and pass, delete the per-section `AuditLogService_*` / `AuditLogRepository_IsSealed` duplicates. Section-specific arch tests retained: append-only repo shape, sole-writer DbSet rule.

### 2.11 Migrations — clean

`20260403192839_DropAuditLogActorName.cs` is EF-generated. `20260212152552_Initial.cs` contains intentional `migrationBuilder.Sql(...)` for `prevent_audit_log_update` / `prevent_audit_log_delete` triggers — sanctioned per section doc.

---

## Stop conditions tripped

**None blocking.** Two judgment calls for the user:

1. **`AuditLogController` introduction (Phase 1)** — moves 4 routes off BoardController/GoogleController. Inbound link updates needed in views, redirects, tests. Observable URL change. Medium-size Phase 1.
2. **GoogleResource Include retention (Phase 2)** — leave the §6 violation in place documented and wait for GoogleIntegration's section-align to provide a batch service API. Confirms the "consumer doesn't fix supplier API" protocol.

---

## Tooling & process for impl

- **Worktree:** `H:\source\Humans\.worktrees\section-align-AuditLog\`. All commands run from there. Branch `align/auditlog` already pushed.
- **/reforge** before any rename, move, or interface signature change. Use `reforge references`, `reforge callers`, `reforge call-chain` to get the exact impact set before editing. If the binary isn't installed, the new `.claude/skills/reforge/SKILL.md` will prompt for install permission.
- **Background processes**: at the start of each phase, kick off `dotnet watch test` in background; tail with `BashOutput` between commits. `KillShell` at phase end. Don't leak background watchers across phases.
- **Subagent orchestration**: Phase 1+ runs as an **Opus orchestrator over Sonnet subagents** in a fresh `/cls` session. Orchestrator stays under 100k tokens; each subagent gets one bounded job and returns a 200-word summary. See `.claude/skills/section-align/SKILL.md` § "Context discipline."

## Phase plan

### Phase 1 — surface alignment (axis 1 + axis 3 mechanical)

1. Create `AuditLogController.cs` with `[Route("AuditLog")]` and section-appropriate policies (BoardOrAdmin globally, HumanAdminBoardOrAdmin on the per-user route).
2. Move 4 route handlers (Board.AuditLog, Google.CheckDriveActivity, Google.GoogleSyncResourceAudit, Google.HumanGoogleSyncAudit) into the new controller; rename action methods to match the cleaner route shape.
3. Create `Views/AuditLog/` and move `Views/Shared/AuditLog.cshtml` → `Index.cshtml`, `Views/Shared/GoogleSyncAudit.cshtml` → `GoogleSync.cshtml`. Evaluate `_AuditLogContent`/`_AuditLogScripts` partials — keep in `Shared` if Board dashboard reuses them; otherwise move.
4. Move `GoogleSyncAuditView` + `BuildGoogleSyncAuditViewModel` off `HumansControllerBase` into `AuditLogController`.
5. Move `AuditLogListViewModel`, `GoogleSyncAuditEntryViewModel`, `GoogleSyncAuditListViewModel` out of `Models/AdminViewModels.cs` into `Models/AuditLogViewModels.cs`.
6. Move `Extensions/AuditLogUiExtensions.cs` → `Extensions/Sections/AuditLogUiExtensions.cs`.
7. Update inbound links: `RedirectToAction(nameof(BoardController.AuditLog), "Board", ...)` (e.g. `GoogleController.cs:413`) becomes the new `AuditLogController.Index` target. Same for tag-helper links in views and any tests/nav that reference the old paths.
8. Build + tests green after each commit. 4–6 commits expected.

### Phase 2 — fix arch violations + boundary protocol

Order matters — boundary fixes that depend on supplier APIs go last in case they get blocked.

1. **Axis 1 — Inbound write boundary (DriveActivityMonitorRepository.cs:81).** Our API: `IAuditLogService.LogAsync` already covers per-entry writes; no new method needed. AuditLog work in this PR: none beyond documenting. **Follow-up target: GoogleIntegration** (owns switching `PersistAnomaliesAsync` off direct `ctx.AuditLogEntries`).
2. **Axis 1 — Outbound cross-section reads (§2.6).** Verify `IUserService.GetByIdsAsync` returns display names and `ITeamService.GetTeamNamesByIdsAsync` returns `(Name, Slug)`. Use `/reforge members IUserService` and `/reforge members ITeamService` to confirm. If yes, lift `GetUserDisplayNamesAsync`/`GetTeamNamesAsync` out of `AuditLogRepository` into `AuditLogService` calling the cross-section services. If either shape is wrong, **flag that section as a follow-up** and pause this specific fix.
3. **Axis 1 — Cross-domain nav delete (§2.7).** Drop `AuditLogEntry.ActorUser` nav (unread). Keep `AuditLogEntry.Resource` nav — blocked on GoogleIntegration follow-up.
4. **Axis 2 — Section-specific arch tests.** Add `Only<Section>Repository_References_AuditLogEntries_DbSet` (Roslyn or reflection scan). Adding it green is the goal — it should be green after step 1 documents and follow-up routing puts the call-site fix on GoogleIntegration's plate. *(Note: the test will go red until GoogleIntegration migrates. Decide: gate this test on follow-up completion vs ship with a tracking comment.)*
5. **Axis 2 — Generic arch tests (new, lift from per-section duplicates).** In `tests/Humans.Application.Tests/Architecture/Rules/`: `IRepository_Implementations_AreAllSealed`, `Application_Services_TakeNoDbContext`, `Application_Services_TakeNoIMemoryCache_UnlessRegistered`, `Repository_Implementations_LiveInInfrastructureRepositories`. These benefit every section, not just AuditLog.
6. **Axis 2 — `AuditViewerService` arch test pair** (no DbContext, no IMemoryCache) — only needed if step 5's generic tests don't cover it via interface reflection. Likely covered; delete this step in that case.
7. **Axis 2 — `InterfaceMethodBudgetTests.Budgets` entries** for `IAuditLogService` (14) and `IAuditLogRepository` (final post-§2.6 count, expected 13).
8. **Axis 3 — Missing test coverage.** Per A3.2 coverage map: add tests for invariants/negatives/triggers that don't have one. Specifically:
   - Append-only invariant — direct DbSet write rejected (covered at DB by trigger; an arch test now covers it at compile time too via step 4).
   - Self-persisting semantics — `LogAsync` saves immediately without caller `SaveChanges`.
   - Best-effort — `LogAsync` swallows repo failures and logs at Error.
   - Cross-merge chain-follow — `GetByUserAsync` surfaces source-tombstone rows for the fold target.
9. **Axis 3 — Add missing 1-to-1 unit-test file.** `AuditLogRepositoryTests.cs` doesn't exist (only `AuditLogArchitectureTests`). Add a minimal happy-path test file or document why none is needed.
10. **Axis 3 — Split bundled-class test file.** `AuditEventRenderTests.cs` covers `AuditEvent` + `AuditEventTextualizer` — split into `AuditEventTests.cs` + `AuditEventTextualizerTests.cs`.

### Phase 3 — /simplify

- **Per-section arch test prune** — after step 5 above lands, delete per-section duplicates: `AuditLogService_HasNoDbContextConstructorParameter`, `AuditLogService_HasNoIMemoryCacheConstructorParameter`, `AuditLogService_TakesRepository`, `AuditLogService_ConstructorTakesNoStoreType`, `AuditLogService_LivesInHumansApplicationServicesAuditLogNamespace`, `AuditLogRepository_IsSealed`, `IAuditLogRepository_LivesInApplicationInterfacesRepositoriesNamespace`. Keep `IAuditLogRepository_HasNoUpdateOrDeleteMethods` (section-specific append-only invariant). Net test-attribute delta: substantial negative.
- Move display-name helpers private (`AuditViewerService` owns them after §2.6 has lifted resolution into `AuditLogService`).
- Trim `IAuditLogService` reads not consumed outside `AuditViewerService` (verify with `/reforge callers IAuditLogService.<method>` per candidate).
- **Stryker probe** — propose creating `tests/Humans.Application.Tests/stryker-auditlog-config.json` mirroring the Profile-section probe pattern. Run `analyze-test-utility.ps1` against the resulting report. Prune from the High-Confidence queue.
- Move list-page partials (`_AuditLogContent`, `_AuditLogScripts`) from `Views/Shared/` into `Views/AuditLog/` if they aren't reused by the Board dashboard (Phase 1 already split this open).

### Phase 4 — doc polish

- `docs/sections/AuditLog.md § Routing` — rewrite around `AuditLogController`.
- `docs/sections/AuditLog.md § Architecture` — restore "only `AuditLogRepository` touches `ctx.AuditLogEntries`" once it's truly true and test-pinned. Add the predicate-pushed-reads exception justification (axis 2.6 — large-dataset section).
- `docs/sections/AuditLog.md § Negative Access Rules` — same.
- `docs/sections/AuditLog.md § Cross-Section Dependencies` — explicit "GoogleIntegration: `AuditLogEntry.Resource` Include retained pending `IGoogleResourceService.GetByIdsAsync`" + "Users: display names via `IUserService.GetByIdsAsync`" + "Teams: names via `ITeamService.GetTeamNamesByIdsAsync`."
- `docs/sections/AuditLog.md § Data Model` — correct entity-nav claims: `ActorUser` deleted; `Resource` retained, awaiting GoogleIntegration.
- **`docs/architecture/dependency-graph.md`** — update AuditLog node's outbound edges to: IUserService (display names), ITeamService (team names), GoogleResource Include (pending — flag as provisional). Remove any references to direct `ctx.Users`/`ctx.Teams` reads.
- **Closing /section-align re-run** — same worktree, same impl session if budget allows. Should return: "clean except for [GoogleIntegration follow-up]."

---

## Follow-up /section-align targets surfaced by this run

- **GoogleIntegration** — (a) introduce `IGoogleResourceService` (or extend an existing service) with batched `GetByIdsAsync` returning name dictionary so AuditLog can drop the `Include(e => e.Resource)`. (b) Switch `DriveActivityMonitorRepository.PersistAnomaliesAsync` off direct `ctx.AuditLogEntries` writes onto `IAuditLogService.LogAsync` per-anomaly.
- **Users** — verify `IUserService.GetByIdsAsync` returns a display-name dictionary shape suitable for AuditLog's display stitching. If not, add or extend that method. (Likely already correct — Camps section align previously consumed it. Verify in Phase 2 step 2.)
- **Teams** — verify `ITeamService.GetTeamNamesByIdsAsync` exists and returns `Dictionary<Guid, (Name, Slug)>`. (Referenced in Camps section doc as the canonical pattern. Verify in Phase 2 step 2.)

Section status after this PR lands: **provisionally aligned** — fully aligned once GoogleIntegration ships its follow-up and we return to drop the Include + Resource nav.

---

## Status

Phase 0 complete. This plan is the impl-ready handoff. The skill's "Context discipline" rule applies — open a fresh `/cls` before Phase 1 and run the impl as an Opus orchestrator over Sonnet subagents in this same `align/auditlog` worktree.
