# Section Align — AuditLog (Two-Axis Re-Evaluation)
**Run started:** 2026-05-12 | **Mode:** existing-section | **Worktree:** `H:\source\Humans\.worktrees\section-align-AuditLog`
**Branch:** `align/auditlog` (off `origin/main` @ `760d98f2`)

> **Re-evaluation note (2026-05-12):** the first pass treated convention exceptions in the section doc as closed questions and missed the second axis entirely. Peter clarified the framing:
>
> 1. **Boundary integrity (axis 1)** — the lines around the section. Other sections never reach across, our publics are clean, our routes/views/models are addressable under our name.
> 2. **Internal cohesion (axis 2)** — the contents of the slice match current app standards. No EF leak from service, no caching outside service layer, proper interfaces, reusable view components, architecture-test coverage.
>
> **Boundary-fix protocol:** when an external section reaches across our boundary, our job is to *first* ensure the right public API exists on our service to satisfy that consumer. *Only then* do we flag the external section as needing its own /section-align. We own the API surface; they own the migration.
>
> Same flipped: when we reach across someone else's boundary, we flag the missing API on THEIR section. We don't fix it here — they become the next /section-align target. We document the gap and either live with the current code or fall back to a less-bad path.

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

### 2.6 OUTBOUND cross-section access — sanctioned (1)

`AuditLogRepository.GetUserDisplayNamesAsync` reads `ctx.Users`; `GetTeamNamesAsync` reads `ctx.Teams`. Both batch-load name dictionaries. The section doc owns this as a sanctioned exception ("narrow cross-table lookups behind the repository"). Could be migrated to `IUserService.GetByIdsAsync` / `ITeamService.GetTeamNamesByIdsAsync` (both exist per the doc — verify) — same pattern as the Camps section already follows for stripped navs. Pre-existing pattern; not blocking, but candidate for Phase 3 (`/simplify`) once verified those service methods deliver the right shape.

### 2.7 Cross-domain navs on AuditLog entity

`AuditLogEntry.ActorUser` (→ User) and `AuditLogEntry.Resource` (→ GoogleResource). Section doc claims "declared but never read" — **partly false**: `AuditLogRepository` actively uses `Resource` via Include (§2.5 above). `ActorUser` does appear unread. Phase 4 doc correction: revise the claim to "ActorUser declared but never read; Resource read by AuditLogRepository.GetGoogleSync* via Include — pending IGoogleResourceService batch API."

### 2.8 Interface segregation — moderate issue

| Interface | Methods | Budgeted? | Notes |
|-----------|---------|-----------|-------|
| `IAuditLogService` | 14 | ❌ no | 3 writes + 8 reads + 2 UI-batch helpers (`GetUserDisplayNamesAsync`, `GetTeamNamesAsync`) + 2 specialized lookups |
| `IAuditViewerService` | 6 | n/a | wraps Service for resolved-event reads |
| `IAuditLogRepository` | 15 | ❌ no | 1 write + 13 reads + 2 cross-table |

**Issues:**
- `IAuditLogService.GetUserDisplayNamesAsync` and `GetTeamNamesAsync` exist purely so `AuditViewerService` can call them. UI-rendering plumbing on the section's main write/read interface. Phase 3 candidate: move these private to AuditViewerService (it can inject the repo directly for those two methods, or AuditLogService keeps them but moves them to an internal helper).
- IAuditLogService and IAuditViewerService both expose `GetRecentAsync`/`GetByResource`/`GetGoogleSyncByUser`/`GetFilteredEntries` shapes — Service returns raw `AuditLogEntry`, ViewerService returns resolved `AuditEvent`. Few callers outside AuditViewerService consume the raw-entry shape (jobs + GDPR contributor). Phase 3: trim IAuditLogService reads down to what non-UI callers actually need; route all UI reads through IAuditViewerService.
- Both over-threshold interfaces need `InterfaceMethodBudgetTests.Budgets` entries even if Phase 3 isn't run — pin current size to start the ratchet.

### 2.9 ViewComponent reuse — ✓ EXCELLENT

`AuditLogViewComponent` (`src/Humans.Web/ViewComponents/AuditLogViewComponent.cs`) takes optional `entityType`/`entityId`/`userId`/`actions`/`limit`/`title`/`showCard` parameters and calls `IAuditViewerService.GetFilteredAsync`. Active usage:
- `Views/Calendar/Event.cshtml:130`
- `Views/Profile/AdminDetail.cshtml:218`
- `Views/TeamAdmin/Members.cshtml:292`
- `Views/WidgetGallery/Index.cshtml:794` (demo)

Strong reuse pattern. Partials at `Views/Shared/Components/AuditLog/` properly placed.

### 2.10 Architecture test coverage — partial

Present:
- ✓ `AuditLogService_LivesInHumansApplicationServicesAuditLogNamespace`
- ✓ `AuditLogService_HasNoDbContextConstructorParameter`
- ✓ `AuditLogService_HasNoIMemoryCacheConstructorParameter`
- ✓ `AuditLogService_TakesRepository`
- ✓ `AuditLogService_ConstructorTakesNoStoreType`
- ✓ `IAuditLogRepository_LivesInApplicationInterfacesRepositoriesNamespace`
- ✓ `AuditLogRepository_IsSealed`
- ✓ `IAuditLogRepository_HasNoUpdateOrDeleteMethods`

Missing (Phase 2 additions):
- ✗ `AuditViewerService_HasNoDbContextConstructorParameter` (mirror for the second service)
- ✗ `AuditViewerService_HasNoIMemoryCacheConstructorParameter`
- ✗ `OnlyAuditLogRepository_TouchesAuditLogEntriesDbSet` — Roslyn/reflection scan over `Infrastructure/**` for any file outside `Repositories/AuditLog/` referencing `AuditLogEntries`. Would catch DriveActivityMonitorRepository.cs:81 today and prevent regressions tomorrow.
- ✗ Interface budget entries for both over-threshold interfaces.

### 2.11 Migrations — clean

`20260403192839_DropAuditLogActorName.cs` is EF-generated. `20260212152552_Initial.cs` contains intentional `migrationBuilder.Sql(...)` for `prevent_audit_log_update` / `prevent_audit_log_delete` triggers — sanctioned per section doc.

---

## Stop conditions tripped

**None blocking.** Two judgment calls for the user:

1. **`AuditLogController` introduction (Phase 1)** — moves 4 routes off BoardController/GoogleController. Inbound link updates needed in views, redirects, tests. Observable URL change. Medium-size Phase 1.
2. **GoogleResource Include retention (Phase 2)** — leave the §6 violation in place documented and wait for GoogleIntegration's section-align to provide a batch service API. Confirms the "consumer doesn't fix supplier API" protocol.

---

## Phase plan

### Phase 1 — surface alignment (axis 1)

1. Create `AuditLogController.cs` with `[Route("AuditLog")]` and section-appropriate policies (BoardOrAdmin globally, HumanAdminBoardOrAdmin on the per-user route).
2. Move 4 route handlers (Board.AuditLog, Google.CheckDriveActivity, Google.GoogleSyncResourceAudit, Google.HumanGoogleSyncAudit) into the new controller; rename action methods to match the cleaner route shape.
3. Create `Views/AuditLog/` and move `Views/Shared/AuditLog.cshtml` → `Index.cshtml`, `Views/Shared/GoogleSyncAudit.cshtml` → `GoogleSync.cshtml`. Evaluate `_AuditLogContent`/`_AuditLogScripts` partials — keep in `Shared` if Board dashboard reuses them; otherwise move.
4. Move `GoogleSyncAuditView` + `BuildGoogleSyncAuditViewModel` off `HumansControllerBase` into `AuditLogController`.
5. Move `AuditLogListViewModel`, `GoogleSyncAuditEntryViewModel`, `GoogleSyncAuditListViewModel` out of `Models/AdminViewModels.cs` into `Models/AuditLogViewModels.cs`.
6. Move `Extensions/AuditLogUiExtensions.cs` → `Extensions/Sections/AuditLogUiExtensions.cs`.
7. Update inbound links: `RedirectToAction(nameof(BoardController.AuditLog), "Board", ...)` (e.g. `GoogleController.cs:413`) becomes the new `AuditLogController.Index` target. Same for tag-helper links in views and any tests/nav that reference the old paths.
8. Build + tests green after each commit. 4–6 commits expected.

### Phase 2 — fix arch violations (axis 1 + axis 2)

1. **Axis 2 — Add architecture test** `OnlyAuditLogRepository_TouchesAuditLogEntriesDbSet` (this PR's enforcement). Mark it `[Skip]` initially and document expected failures, OR add it red and fix in step 2 same PR. Adding it green is the goal.
2. **Axis 1 — Inbound write boundary fix.** Decision: API surface for `DriveActivityMonitorRepository.PersistAnomaliesAsync`:
   - Recommended path: no new API. The caller switches to per-anomaly `IAuditLogService.LogAsync` after writing LastRunAt. AuditLog gains no new method, GoogleIntegration owns the call-site change. Document in this section's plan: "GoogleIntegration is the next /section-align target — needs to switch off direct ctx.AuditLogEntries write."
   - Alternative: add `LogAnomaliesAsync` if benchmarking shows per-call overhead matters. Pushes IAuditLogService 14 → 15; needs budget entry.
3. **Axis 2 — Add architecture tests** for `AuditViewerService`: no DbContext, no IMemoryCache (mirror the AuditLogService tests).
4. **Axis 2 — Add `InterfaceMethodBudgetTests.Budgets` entries** for `IAuditLogService` (14) and `IAuditLogRepository` (15), with current counts.

### Phase 3 — /simplify (axis 2)

- Move UI display-name helpers (`GetUserDisplayNamesAsync`, `GetTeamNamesAsync`) off `IAuditLogService`. They exist only for `AuditViewerService`; can become internal helpers or move to ViewerService (which then takes `IUserService.GetByIdsAsync` / `ITeamService.GetTeamNamesByIdsAsync` directly).
- Trim `IAuditLogService` read methods that aren't consumed outside the section. Candidates: `GetByResourceAsync`, `GetGoogleSyncByUserAsync`, `GetFilteredAsync`, `GetByUserAsync`, `GetFilteredEntriesAsync` — verify each has at least one non-AuditViewerService caller before removing.
- Migrate `AuditLogRepository.GetUserDisplayNamesAsync` / `GetTeamNamesAsync` off direct `ctx.Users`/`ctx.Teams` reads, onto `IUserService.GetByIdsAsync` / `ITeamService.GetTeamNamesByIdsAsync` if those service methods deliver the right shape. Removes two cross-table §6-adjacent reads (sanctioned but cleanable).
- Lower volume target (section is small).

### Phase 4 — doc polish

- AuditLog.md § Routing — rewrite around `AuditLogController` after Phase 1.
- AuditLog.md § Architecture — once Phase 2 fixes land in this PR, restore "only AuditLogRepository touches ctx.AuditLogEntries" (now actually true and test-pinned).
- AuditLog.md § Negative Access Rules — same.
- AuditLog.md § Cross-Section Dependencies — note GoogleResource Include as a known §6 violation pending `IGoogleResourceService.GetByIdsAsync`.
- AuditLog.md § Data Model — correct the "ActorUser and Resource navs declared but never read" claim — `Resource` is read by AuditLogRepository.GetGoogleSync*.

---

## Follow-up /section-align targets surfaced by this run

- **GoogleIntegration** — needs to (a) introduce `IGoogleResourceService` (or extend an existing service) with a batched `GetByIdsAsync` returning name dictionary, and (b) switch `DriveActivityMonitorRepository.PersistAnomaliesAsync` off direct `ctx.AuditLogEntries` writes.

---

## Skill gaps surfaced by this re-evaluation

Items the current skill misses that the two-axis framing demands:

### Axis 1 (boundary) gaps
1. **Controller existence check** — does `<Section>Controller` exist? Routes hosted elsewhere = drift.
2. **`Views/<Section>/` folder check** — section-owned page views in `Views/Shared/` = drift unless they are true cross-section partials.
3. **ViewModel placement check** — section ViewModels in `Models/<Section>ViewModels.cs` or `Models/<Section>/`; types in grab-bag files (`AdminViewModels.cs`) = drift.
4. **Controller-base leak check** — section-specific helpers on `HumansControllerBase` (or equivalent) = drift.
5. **`Extensions/Sections/` placement** — section helpers should live under `Extensions/Sections/`, not the Extensions root.
6. **Write-side cross-section DB access** — current regex hunts only reads (`Where|FirstOrDefault|FindAsync|ToListAsync`). Add writes (`Add|AddRange|Update|Remove|Attach`) and pure `DbSet` references.
7. **EF nav inbound** — search other entities for navs pointing AT this section's entities.
8. **Don't trust the section doc's exceptions** — when the doc declares "served from two controllers — required because…", treat as drift to evaluate, not as a closed question. The doc records what exists; it doesn't sanctify convention violations.

### Axis 2 (internal cohesion) gaps — entirely absent today
9. **EF leakage from service layer** — grep the section's `Application/Services/<Section>/**` for `Microsoft.EntityFrameworkCore`, `IQueryable`, `DbContext`, `DbSet`, EF query operators. Architecture test should pin if missing.
10. **Caching placement** — grep service layer + repo + controllers for `IMemoryCache`/`MemoryCache`/`IDistributedCache`. Caching belongs in the service layer per §15 (decorator or inline `IMemoryCache`); never in repos or controllers.
11. **DI lifetimes** — verify `<Section>SectionExtensions` registers repo as Singleton (factory-based), services as Scoped, decorator as Singleton if applicable.
12. **Architecture test coverage** — for each service in the section, ensure tests pin: lives in correct namespace, has no DbContext ctor param, has no IMemoryCache ctor param, takes the repository. For each over-threshold interface, ensure InterfaceMethodBudgetTests has an entry.
13. **ViewComponent reuse** — for sections whose data is rendered on other sections' pages (audit history, profile cards, notification meters), verify a `<Section>ViewComponent` exists and is invoked from the host views. Inverse check: section pages that bake in inline rendering of "should-be-a-VC" content.
14. **Interface segregation** — flag plumbing methods that exist solely for one consumer ("`GetUserDisplayNamesAsync` is called only by `AuditViewerService`, move it private").

### Boundary-fix protocol gap
15. The skill currently treats cross-section DB access as "fix the offending file." The correct protocol is:
    - **We are the producer (someone reads/writes our tables):** our job is to ensure the public API on our service satisfies the caller's need. Add it if missing (in THIS PR). Then flag the calling section as the next /section-align target — they own the migration.
    - **We are the consumer (we cross into someone else's tables/nav):** flag the API gap on THEIR section. Don't fix it here — they're the next /section-align target. Document the gap and either tolerate the current code or fall back to a less-bad path.
    - Never "we'll just fix the caller's file" — that violates the section ownership model and lets one section silently mutate another.
