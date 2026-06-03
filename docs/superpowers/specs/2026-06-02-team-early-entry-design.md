# Team Early Entry — Teams as an `IEarlyEntryProvider`

**Status:** Draft for review (no code changed yet)
**Date:** 2026-06-02
**Author:** Peter + Claude (design dialogue)

## As-built amendments (post-design)

The design below describes an earlier shape (and an interim art-specific/single-team console). The shipped feature is generic and per-team (original text preserved below as history):

- **Early Entry is a generic per-team capability.** The `Team.EarlyEntryEnabled` checkbox ("Enable Early Entry") on Edit Team can be set on **multiple teams at once** — it is not limited to one team and is not art-specific.
- **Management surface is a per-team page** at **`Teams/{slug}/EarlyEntry`** (`TeamAdminController.EarlyEntry` + `Views/TeamAdmin/EarlyEntry.cshtml`). There is **no** global admin-shell console — `EarlyEntryAdminController`, its view, and the "Art Early Entry" admin-nav group were deleted. The add form is **human + date + project**; the grants list has inline edit that **auto-saves on change** (date pick / project blur) with no Save button.
- **Authorization is resource-based** via `TeamOperationRequirement.ManageEarlyEntry` (handled by `TeamAuthorizationHandler`):
  - **Coordinators** of a team manage that team's early entry de facto (same authority as managing its members/roles) — no extra role needed.
  - A cross-team role **`EETeamAdmin`** can manage early entry on **any** EE-enabled team. It is registered in `RoleNames.All` and `RoleNames.BoardManageableRoles`. It is **not** in `AnyAdminRole` (its surface is the team's own Details page, not the admin shell).
  - `TeamsAdmin` / `Board` / `Admin` continue to manage any team for any operation.
  - The Team Details "Team Management" card surfaces an **Early Entry** link when EE is enabled and the viewer can manage it (so a cross-team `EETeamAdmin` who is not a coordinator still sees just that link).
- **Source label is team-derived:** `"{TeamName}: {ProjectName}"` (mirroring Shifts' `"Shift: {team}"`), not `"Art: …"`. The projection requires the grant's `Team` nav to be loaded (`GetEarlyEntryGrantsForEnabledTeamsAsync` `.Include`s it).
- **Read model:** the service returns a `TeamEarlyEntryGrantInfo` projection (not the EF entity), per the service-entity-boundary ratchet.
- **Admin flag binding:** the Edit Team `EarlyEntryEnabled` checkbox follows the page's own gate (TeamsAdmin/Board/Admin) — the prior admin-only suppression hack (controller passing `null` for non-Admins) was removed.
- **Service guard:** `AddEarlyEntryGrantAsync` rejects an empty `UserId` (`ArgumentException`).
- **Migration:** sentinel-safe bool (`IsRequired()`, not `HasDefaultValue(false)`).
- **GDPR export + right-to-erasure + user-merge** are covered as designed.
- **Known follow-up:** the same admin-only-flag-suppression footgun affects `IsSensitive` (pre-existing) — tracked as **nobodies-collective/Humans#824**. The admin dashboard "Recent activity" panel was gated to `AdminOnly` as part of the security review.

## Problem

The Creativity department needs to bring people in for **early entry** (EE) tied
to specific art projects — e.g. an artist arriving days before the gate to build
an installation. The system already aggregates EE across sections via
`IEarlyEntryProvider` (Camps and Shifts implement it today), but **Teams does
not contribute**, so there is nowhere to record "this human gets EE for art
project X on date Y."

The fix: let an **admin** switch EE on for a team, give **specific humans
individually granted the `EarlyEntryArtAdmin` role** (cantina-style) a place to
grant EE (human + date + project name), and make `TeamService` answer the
existing `IEarlyEntryProvider` fan-out so the rest of the EE machinery (roster,
ticket stubs, caching) works unchanged.

This is **load-bearing** for the data shape (a new PII-bearing table + GDPR
obligations) and **prototype-grade** for the UI polish.

## Existing machinery (audited, not assumed)

- **`IEarlyEntryProvider`** — `src/Humans.Application/Interfaces/EarlyEntry/IEarlyEntryProvider.cs`:
  ```csharp
  public interface IEarlyEntryProvider
  {
      Task<IReadOnlyList<EarlyEntryGrant>> GetEarlyEntriesAsync(CancellationToken ct);
  }
  public sealed record EarlyEntryGrant(Guid UserId, LocalDate EntryDate, string Source);
  ```
- **Implementations today:** `CampService` (`Source = "Camp: {season}"`, single
  global `EeStartDate`) and `VolunteerTrackingExportService` (Shifts,
  `Source = "Shift: {team}"`, date derived from build-period shifts). Each is
  registered as `IEarlyEntryProvider` against the **inner scoped service**, not
  the caching decorator.
- **Orchestrator:** `EarlyEntryService` (`EarlyEntryOrchestrator`) injects
  `IEnumerable<IEarlyEntryProvider>`, fans out sequentially, groups by `UserId`
  (earliest date wins, distinct sources, `HasMultiple` flag). Wrapped by the
  singleton `CachingEarlyEntryService` (per-user cache; roster always live).
- **Invalidation:** `IEarlyEntryInvalidator` with `InvalidateUser(Guid)` and
  `InvalidateAll()`. Camps and Shifts services already depend on it — a Teams
  dependency follows the same accepted cross-section-service pattern.
- **Teams section:** `Team` entity already carries several **admin-only boolean
  flags** (`IsSensitive`, `HasBudget`, `IsHidden`, …). `TeamRepository`
  (singleton over `IDbContextFactory`) **exclusively** owns the `team_*` tables.
  `TeamService` is wrapped by the `CachingTeamService` singleton decorator.
- **Edit Team page:** `TeamController.EditTeam` GET/POST, gated by
  `PolicyNames.TeamsAdminBoardOrAdmin` (coordinators cannot reach it).
- **Team Management card:** `Views/Team/Details.cshtml` (lines ~342–377),
  rendered when `CanCurrentUserManage`; individual links gate on
  `CanCurrentUserManage` (coordinator + admin) vs `CanCurrentUserEditTeam`
  (admin only).
- **Team management actions:** `TeamAdminController` (Members, Roles, EditPage)
  all call `ResolveTeamManagementAsync(slug)` → `TeamOperationRequirement.ManageCoordinators`
  (admin/board/teams-admin OR coordinator-of-this-team).
- **Human picker:** reusable `<vc:human-search>` ViewComponent
  (`HumanSearchViewComponent`), `scope="Name"|"All"`, `exclude-user-ids`,
  backed by `/api/profiles/search`.

**Negative result (verified):** there is **no** existing "art project" / "project"
/ installation entity, and no per-human project tracking anywhere in the
codebase. `CampaignGrant` is discount codes; Budget categories are unrelated.
Therefore **project name is free text per grant**, not a reference.

## Decisions (from design dialogue)

1. **Source label:** always `"Art: {ProjectName}"` — hardcoded `"Art:"` prefix +
   per-grant free-text project name, regardless of which team enabled EE.
   (Creativity is the realistic sole user; revisit only if another dept adopts
   this.)
2. **Search scope:** **any human**, not restricted to team membership —
   Creativity routinely brings in external artists who are not formal members.
3. **Disable behavior:** when an admin turns EE **off** for a team, the grant
   rows are **kept** but the provider stops contributing them (the repo query
   excludes non-EE-enabled teams). Re-enabling restores them. Rationale: a grant
   is self-contained free text with no durable project record to orphan, so
   non-destructive is the safe default.
4. **Editing:** add / remove / **edit in place** (change date and/or project
   name of an existing row without re-adding).
5. **Who manages grants (cantina-style individual enablement):** management is
   gated by a **dedicated, independent role `EarlyEntryArtAdmin`**, granted to
   specific humans through the existing role-assignment flow (Profile → Admin →
   Add Role), exactly like `CantinaAdmin`. The role is wired into **exactly one**
   policy, `EarlyEntryArtAdminOrAdmin` (`RequireRole(EarlyEntryArtAdmin, Admin)`),
   which gates only the EE management surface — it is never added to any other
   role group/policy, so it confers no permission beyond EE management.
   **Coordinator status no longer grants EE management** (replaces the earlier
   coordinator+admin model). Granting the role is delegated like cantina's: Admin
   always, plus Board/HumanAdmin via `BoardManageableRoles`.
   - Turning EE **on/off** for a team stays **admin-only** (the Edit Team flag,
     `TeamsAdminBoardOrAdmin`) — unchanged.
   - Because an `EarlyEntryArtAdmin` need not be a team coordinator, the EE
     management entry point must be reachable independent of the coordinator-only
     Team Management card (see Components §2).

## Data model

New Teams-owned table **`team_early_entry_grants`** (owned exclusively by
`TeamRepository` — Peter's hard rule: one table, one repository). One row = one
human's EE grant for one art project.

| Column | Type | Notes |
|---|---|---|
| `Id` | `Guid` | PK |
| `TeamId` | `Guid` | FK → `teams`, `OnDelete(Cascade)` (grants die with the team) |
| `UserId` | `Guid` | the human granted EE (no EF nav to `User` — cross-section, resolve via `IUserServiceRead`) |
| `EntryDate` | `LocalDate` | NodaTime; per-grant (each person may differ) |
| `ProjectName` | `string` (≤256) | free text; surfaces as `"Art: {ProjectName}"` |
| `CreatedAt` | `Instant` | audit |
| `CreatedByUserId` | `Guid` | audit |
| `UpdatedAt` | `Instant?` | audit; null until first edit-in-place, then set |

- **No** unique constraint on `(TeamId, UserId)`: the same human may legitimately
  hold EE for two different art projects on the same team (two rows → two
  sources, deduped to earliest date by the orchestrator).
- Index on `TeamId` (list a team's grants) and `UserId` (merge / GDPR lookups).
- Per `memory/architecture/no-concurrency-tokens.md`: **no** row-version /
  concurrency token.

**Team flag:** new `bool Team.EarlyEntryEnabled` (default `false`, store default
`false`).

**Migration:** one EF migration adds the `team_early_entry_grants` table **and**
the `teams.EarlyEntryEnabled` column. (Subject to the EF migration-review gate.)

## Components & flow

### 1. The flag (admin-only)
- `Team.EarlyEntryEnabled` added to the domain entity + `TeamConfiguration`.
- `EditTeamViewModel` gains `EarlyEntryEnabled`; an **"Enable Early Entry"**
  checkbox is rendered on `EditTeam.cshtml`. Wired through the existing
  `UpdateTeamAsync` path (which already invalidates the team cache).
- Toggling **off** invalidates the EE cache for every currently-granted human on
  that team (enumerate the team's grants → `InvalidateUser` each), so their EE is
  re-evaluated immediately. Toggling **on** likewise invalidates those users.

### 2. Management entry point
- `Views/Team/Details.cshtml`: new **"Early Entry"** list-group link gated by a new
  `TeamDetailViewModel.CanManageEarlyEntry` flag — true when
  `EarlyEntryEnabled && (User is Admin || User holds EarlyEntryArtAdmin)`. Because
  an `EarlyEntryArtAdmin` may **not** be a coordinator, the Team Management card
  (which renders only on `CanCurrentUserManage`) must also render when
  `CanManageEarlyEntry` so EE-admins can reach the link. The role/claims check is
  computed in the Web layer (controller, from `User`), not in the page service.
- `TeamDetailViewModel` gains `EarlyEntryEnabled` and `CanManageEarlyEntry`.

### 3. Management page (EarlyEntryArtAdmin or Admin)
New actions on `TeamAdminController`, gated by
`[Authorize(Policy = EarlyEntryArtAdminOrAdmin)]` (role `EarlyEntryArtAdmin` or
`Admin`) — **not** `ResolveTeamManagementAsync`. Each action resolves the team by
slug (read-only, e.g. `ITeamServiceRead.GetTeamBySlugAsync`) and the current user,
then enforces an `EarlyEntryEnabled` check (defense in depth — `NotFound` if off):
- `GET  /Teams/{slug}/EarlyEntry` — lists current grants (human name + photo via
  the existing profile lookup, date, project) + the add form.
- `POST /Teams/{slug}/EarlyEntry/Add` — `<vc:human-search field-name="UserId"
  scope="Name">` + date + project name.
- `POST /Teams/{slug}/EarlyEntry/Edit` — edit date and/or project name of one row.
- `POST /Teams/{slug}/EarlyEntry/Remove` — delete one row.

Controllers parse/format only; all logic in `TeamService`. Each write →
`ITeamRepository` mutation, then `IEarlyEntryInvalidator.InvalidateUser(userId)`,
then an audit-log entry (mirrors the Camps grant/revoke pattern via the existing
`IAuditLogService`).

### 3a. Role + policy (cantina-clone)
- `RoleNames.EarlyEntryArtAdmin` — new role constant; add to `BoardManageableRoles`
  so Admin/Board/HumanAdmin can grant it via the existing Add-Role flow (it then
  appears in `RoleChecks.GetAssignableRoles`). Claims are populated by the existing
  `RoleAssignmentClaimsTransformation` — no new infra.
- `PolicyNames.EarlyEntryArtAdminOrAdmin` + registration in
  `AuthorizationPolicyExtensions` as `RequireRole(EarlyEntryArtAdmin, Admin)`.
  **Used only** to gate the four EE management actions; not referenced elsewhere.

### 4. Provider wiring
- `TeamService : IEarlyEntryProvider`:
  ```csharp
  public async Task<IReadOnlyList<EarlyEntryGrant>> GetEarlyEntriesAsync(CancellationToken ct)
  {
      var grants = await _repo.GetEarlyEntryGrantsForEnabledTeamsAsync(ct);
      return TeamEarlyEntryProjection.Project(grants);
  }
  ```
- New repo method `GetEarlyEntryGrantsForEnabledTeamsAsync` returns grants **only
  for teams where `EarlyEntryEnabled`** (the disable gate lives here).
- `TeamEarlyEntryProjection.Project` — pure, testable: `grant => new EarlyEntryGrant(
  grant.UserId, grant.EntryDate, $"Art: {grant.ProjectName}")`. Symmetric with
  `CampEarlyEntryProjection` / `ShiftEarlyEntryProjection`.
- DI: register `IEarlyEntryProvider` against the **inner** scoped `TeamService`
  (mirror `CampsSectionExtensions` / `ShiftsSectionExtensions`), not the caching
  decorator.

### 5. Service & caching surface
- New **Teams-internal** write/read methods (`AddEarlyEntryGrantAsync`,
  `EditEarlyEntryGrantAsync`, `RemoveEarlyEntryGrantAsync`,
  `GetEarlyEntryGrantsForTeamAsync`) live on **`ITeamService`**, *not* on the
  cross-section `ITeamServiceRead` (budget 4) — only `TeamAdminController` inside
  the Teams section calls them.
- `CachingTeamService` gets **pass-through** implementations (the grants are not
  part of the `TeamInfo` read-model cache, so no new caching/index logic; the
  decorator must not touch the repo, per the hard rules — it delegates to inner).
- `GetEarlyEntriesAsync` is on the provider interface only; `CachingTeamService`
  does **not** implement `IEarlyEntryProvider` (the inner service is registered
  for it), so the decorator is unaffected.

## Cross-cutting

- **GDPR (non-negotiable).** EE grants are PII (human ↔ date ↔ project). They
  must be (a) included in the user-data export via Teams' `IUserDataContributor`
  participation, and (b) deleted on right-to-erasure. Implementation verifies how
  Teams currently participates in export/erasure and extends it to cover
  `team_early_entry_grants`.
- **User merge (`IUserMerge`).** On merge, reassign the source human's EE grants
  to the target `UserId` and `InvalidateUser` both — same pattern Camps/Shifts
  use. Folded into Teams' existing merge path.
- **Terminology.** User-facing strings say "humans," never "users/members"
  (collective framing). Code identifiers keep `UserId` (existing convention).

## Testing

- **Pure-logic (mocked repo):** `TeamEarlyEntryProjection` mapping (label format,
  per-grant date); `GetEarlyEntriesAsync` returns grants only for EE-enabled
  teams and empty when none enabled; service add/edit/remove call the repo +
  `InvalidateUser` + audit; merge reassigns grants; **right-to-erasure deletes
  the human's grants and GDPR export includes them** (Teams' `IUserDataContributor`
  path — required deliverable, not discovery work).
- **Architecture test:** `team_early_entry_grants` is referenced only by
  `TeamRepository` (table-ownership invariant).
- **Integration (real Postgres):** repo query for enabled-teams grants + cascade
  delete with team, if the change touches query translation. (Per the test-tier
  reshaping doctrine, prefer pure-logic; add a real-DB test only for the query.)
- Skip browser/UI tests for the management page (prototype-grade UI).

## Out of scope (YAGNI)

- Capacity limits / EE slot counting for teams (Shifts has its own; Teams grants
  are explicit and curated by EarlyEntryArtAdmins/admins).
- Date validation against event gate/build windows (free date entry; revisit if
  coordinators ask for guardrails).
- A durable art-project entity or per-human project roster (none exists; not
  needed for EE).
- Per-team configurable label prefix (hardcoded `"Art:"`).

## Change-enforcement notes

- **If you add the migration** → run the EF migration-review gate.
- **If you add `team_early_entry_grants`** → add the table-ownership architecture
  test and confirm only `TeamRepository` references it.
- **If you add a new user-facing management page** → run the nav-completeness /
  backlink check (the Team Management card link is the entry point).
- **If you add EE grant storage** → ensure it is covered by GDPR export +
  erasure (Teams `IUserDataContributor`) and by `IUserMerge`.
