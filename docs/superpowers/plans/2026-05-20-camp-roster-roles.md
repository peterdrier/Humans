# Camp detail Roles + Roster cards & legacy CampLead read-decouple — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Source the `/Barrios/{slug}` detail page's leads from the role system, add read-only Roles + Roster cards, and decouple every live code path from the legacy `camp_leads` table (physical drop deferred to `nobodies-collective/Humans#774`).

**Architecture:** The new source of truth is `CampRoleAssignment` (special role `CampSpecialRole.Lead`), already used by `/Edit/Members` and by authorization. This plan repoints the detail page, camp creation, and the remaining readers/writers/fallbacks onto it. The legacy `CampLead` entity/table stay mapped but orphaned after this PR.

**Tech Stack:** ASP.NET Core MVC (Razor), EF Core (Npgsql), NodaTime, xUnit + NSubstitute + AwesomeAssertions, `ServiceTestHarness` (in-memory `HumansDbContext`).

**Spec:** `docs/superpowers/specs/2026-05-20-camp-detail-roles-roster-and-lead-decouple-design.md`

**Branch / worktree:** `feat/camp-roster-roles` at `H:\source\Humans\.worktrees\camp-roster-roles` (already created off `origin/main`).

**Build/test commands (run from worktree root):**
- `dotnet build Humans.slnx -v quiet`
- `dotnet test Humans.slnx -v quiet`
- Single test class: `dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~CampServiceTests"`

**Conventions that bind this work:**
- No EF migration in this PR (no schema change). Do **not** delete `CampLead`/`CampLeadRole`/`CampLeadConfiguration`/`Camp.Leads` nav/`CampLeads` DbSet — those generate a drop and belong to #774.
- Never block sign-in or camp registration; degrade gracefully (`no-startup-guards`).
- Resource-based auth only; services stay auth-free.

---

## File Structure

**Phase 1 — detail page cards**
- Modify: `src/Humans.Web/Models/Camp/CampRoleRowViewModel.cs` — add `IsLeadRole`.
- Modify: `src/Humans.Web/Controllers/CampController.cs` — `BuildRolesPanelAsync` maps `IsLeadRole`; `Details`/`SeasonDetails` build the read-only panel + roster; `MapCampDetailViewModel` drops `Leads`, gains panel/roster/`CanSeeFullCamp`.
- Modify: `src/Humans.Web/Models/CampViewModels.cs` — `CampDetailViewModel`: remove `Leads`, add `RolesPanel`, `Roster`, `CanSeeFullCamp`.
- Create: `src/Humans.Web/Views/Camp/_CampRolesCard.cshtml` — read-only roles card.
- Create: `src/Humans.Web/Views/Camp/_CampRosterCard.cshtml` — member grid.
- Modify: `src/Humans.Web/Views/Camp/Details.cshtml` — replace Leads card with `_CampRolesCard`; add `_CampRosterCard` above About.

**Phase 2 — fix the source (camp creation)**
- Modify: `src/Humans.Application/Interfaces/Repositories/ICampRepository.cs` — `CreateCampAsync` signature.
- Modify: `src/Humans.Infrastructure/Repositories/Camps/CampRepository.cs` — `CreateCampAsync` impl.
- Modify: `src/Humans.Infrastructure/Services/Camps/CachingCampService.cs` — decorator passthrough if present.
- Modify: `src/Humans.Application/Services/Camps/CampService.cs` — `CreateCampAsync` builds member + Camp Lead assignment.
- Modify: `tests/Humans.Application.Tests/Services/CampServiceTests.cs` — update creation test.

**Phase 3 — repoint remaining legacy readers**
- `CampController.Contact`; `CampService.GetCampDirectoryAsync`; `CampService.GetCampLeadSeasonIdForYearAsync`; `CampAdminPageBuilder` + `CampAdmin/Index.cshtml`; `CampCsvExportBuilder`; `CityPlanningController` + `CityPlanningApiController`; `CampService.ContributeForUserAsync` (GDPR); `CampService.GetCampEditDataAsync` / `Edit.cshtml`. Repo: add bulk lead-userids-by-season-for-year query + repoint `GetCampsByLeadUserIdAsync`/`GetCampLeadSeasonIdForYearAsync`.

**Phase 4 — strip fallbacks + remove dead writes/methods**
- `CampService.IsUserCampLeadAsync` / `IsUserCampEventManagerAsync` / `GetPendingMembershipCountForLeadAsync` (fallbacks); `CampService.ReassignAsync` (drop legacy call); `DevPersonaSeeder`; delete dead `AddLeadAsync`/`RemoveLeadAsync` + repo lead methods + `CampLeadInfo`/`CampLeadSummary`/`CampLeadViewModel`.

---

## Phase 1 — Detail page Roles + Roster cards

### Task 1: Add `IsLeadRole` to the role row view model

**Files:**
- Modify: `src/Humans.Web/Models/Camp/CampRoleRowViewModel.cs`

- [ ] **Step 1: Add the property**

```csharp
namespace Humans.Web.Models.Camp;

public sealed class CampRoleRowViewModel
{
    public required Guid DefinitionId { get; init; }
    public required string Name { get; init; }
    public required string? Description { get; init; }
    public required int SlotCount { get; init; }
    public required int MinimumRequired { get; init; }
    public required IReadOnlyList<CampRoleSlotViewModel> FilledSlots { get; init; }
    public required int EmptySlotCount { get; init; }
    public required bool OverCapacity { get; init; }
    public required int CurrentCount { get; init; }
    /// <summary>True when the backing definition's SpecialRole is Lead — the only row shown to non-member viewers on the public detail page.</summary>
    public required bool IsLeadRole { get; init; }
}
```

- [ ] **Step 2: Populate it in the controller mapper**

In `src/Humans.Web/Controllers/CampController.cs`, `BuildRolesPanelAsync`, the `panelData.Rows.Select(r => new CampRoleRowViewModel { ... })` block — add:

```csharp
            IsLeadRole = r.Definition.SpecialRole == CampSpecialRole.Lead,
```

(Add `using Humans.Domain.Enums;` if not already imported — it is.)

- [ ] **Step 3: Build to verify it compiles**

Run: `dotnet build Humans.slnx -v quiet`
Expected: success (the only consumer so far is `BuildRolesPanelAsync`; `Members.cshtml` ignores the new field).

- [ ] **Step 4: Commit**

```bash
git add src/Humans.Web/Models/Camp/CampRoleRowViewModel.cs src/Humans.Web/Controllers/CampController.cs
git commit -m "feat(camps): flag Camp Lead row in roles panel view model"
```

---

### Task 2: Extend `CampDetailViewModel` for the two cards

**Files:**
- Modify: `src/Humans.Web/Models/CampViewModels.cs`

- [ ] **Step 1: Replace the `Leads` property with panel/roster fields**

In `CampDetailViewModel`, delete `public List<CampLeadViewModel> Leads { get; set; } = [];` and add:

```csharp
    /// <summary>Read-only roles panel for the displayed season (null when anonymous or no season). Sourced from CampRoleAssignment.</summary>
    public Camp.CampRolesPanelViewModel? RolesPanel { get; set; }
    /// <summary>Active members of the displayed season. Populated only when CanSeeFullCamp is true.</summary>
    public List<CampMemberRowViewModel> Roster { get; set; } = [];
    /// <summary>True when the viewer may see all roles + the roster: CampAdmin (any camp) or an Active member of this camp.</summary>
    public bool CanSeeFullCamp { get; set; }
```

(`CampLeadViewModel` stays defined — still used by `CampEditViewModel`/`CampSummaryRowViewModel` until Phase 3/4.)

- [ ] **Step 2: Build — expect failures in `CampController` and `Details.cshtml`**

Run: `dotnet build Humans.slnx -v quiet`
Expected: FAIL — `CampController.MapCampDetailViewModel` still sets `Leads`; `Details.cshtml` still reads `Model.Leads`. These are fixed in Tasks 3–6. (Compile errors are the failing "test" for this structural task.)

- [ ] **Step 3: Commit after Task 6 (this VM change ships with its consumers).**

---

### Task 3: Build the read-only panel + roster in `Details`/`SeasonDetails`

**Files:**
- Modify: `src/Humans.Web/Controllers/CampController.cs`

- [ ] **Step 1: Update `MapCampDetailViewModel` to stop setting `Leads`**

Remove the `Leads = campDetail.Leads.Select(...)` initializer block from `MapCampDetailViewModel`. Leave the rest. The method now produces a VM with `RolesPanel`/`Roster`/`CanSeeFullCamp` left at defaults; the action fills them.

- [ ] **Step 2: Add a helper to assemble cards data, call it from both actions**

Add a private helper to `CampController`:

```csharp
    private async Task PopulateDetailCardsAsync(
        CampDetailViewModel vm, CampDetailData campDetail, bool isCampAdmin,
        CancellationToken ct)
    {
        if (User.Identity?.IsAuthenticated != true || campDetail.CurrentSeason is null)
        {
            return; // anonymous / no season → no cards
        }

        vm.CanSeeFullCamp = isCampAdmin || vm.Membership.Status == CampMemberStatusSummaryView.Active;

        // Read-only roles panel (same source as /Edit/Members).
        vm.RolesPanel = await BuildRolesPanelAsync(
            campDetail.Slug, campDetail.CurrentSeason.Id, canManage: false, ct);

        if (vm.CanSeeFullCamp)
        {
            var members = await _campService.GetCampMembersAsync(campDetail.CurrentSeason.Id);
            vm.Roster = members.Active
                .Select(m => new CampMemberRowViewModel
                {
                    CampMemberId = m.CampMemberId,
                    UserId = m.UserId,
                    RequestedAt = m.RequestedAt,
                    ConfirmedAt = m.ConfirmedAt,
                    HasEarlyEntry = m.HasEarlyEntry,
                    Status = m.Status,
                })
                .ToList();
        }
    }
```

Note: `GetCampMembersAsync` returns `CampMemberListData` whose `.Active` items are `CampMemberRow(CampMemberId, UserId, DisplayName, RequestedAt, ConfirmedAt, HasEarlyEntry, Status)`. Confirm property names against `CampMemberRow` and adjust the mapping if they differ.

- [ ] **Step 3: Call the helper in `Details`**

In `Details`, after `var membership = ...` and before `return View(...)`:

```csharp
        var vm = MapCampDetailViewModel(campDetail, isLead, isCampAdmin, membership);
        await PopulateDetailCardsAsync(vm, campDetail, isCampAdmin, ct);
        return View(vm);
```

Apply the identical change in `SeasonDetails` (it shares the `nameof(Details)` view).

- [ ] **Step 4: Build**

Run: `dotnet build Humans.slnx -v quiet`
Expected: now only `Details.cshtml` fails (still references `Model.Leads`). Fixed in Task 6.

---

### Task 4: Create the read-only Roles card partial

**Files:**
- Create: `src/Humans.Web/Views/Camp/_CampRolesCard.cshtml`

- [ ] **Step 1: Write the partial**

```cshtml
@model Humans.Web.Models.CampDetailViewModel
@{
    if (Model.RolesPanel is null) { return; }
    // Non-member viewers see only the Camp Lead row, and only its filled assignees
    // (matches the prior "Leads" card). Members/CampAdmin see every active role + open slots.
    var rows = Model.CanSeeFullCamp
        ? Model.RolesPanel.Rows
        : Model.RolesPanel.Rows.Where(r => r.IsLeadRole && r.FilledSlots.Count > 0).ToList();
    if (rows.Count == 0) { return; }
}
<div class="card mb-4">
    <div class="card-header">
        <i class="fa-solid fa-user-shield me-1"></i> @(Model.CanSeeFullCamp ? "Roles" : "Leads")
    </div>
    <ul class="list-group list-group-flush">
        @foreach (var role in rows)
        {
            <li class="list-group-item">
                <div class="d-flex justify-content-between align-items-center">
                    <strong>@role.Name</strong>
                    @if (Model.CanSeeFullCamp)
                    {
                        <small class="text-muted">@role.CurrentCount / @role.SlotCount</small>
                    }
                </div>
                @foreach (var slot in role.FilledSlots)
                {
                    <div class="mt-1"><vc:human user-id="@slot.UserId" /></div>
                }
                @if (Model.CanSeeFullCamp && role.EmptySlotCount > 0)
                {
                    <div class="mt-1 text-muted fst-italic small">@role.EmptySlotCount open</div>
                }
            </li>
        }
    </ul>
</div>
```

- [ ] **Step 2: No build yet** — wired in Task 6.

---

### Task 5: Create the Roster card partial

**Files:**
- Create: `src/Humans.Web/Views/Camp/_CampRosterCard.cshtml`

- [ ] **Step 1: Write the partial**

```cshtml
@model Humans.Web.Models.CampDetailViewModel
@{
    if (!Model.CanSeeFullCamp || Model.CurrentSeason is null) { return; }
}
<div class="card mb-4">
    <div class="card-header">
        <i class="fa-solid fa-users me-1"></i> Roster @Model.CurrentSeason.Year
        <span class="text-muted">(@Model.Roster.Count)</span>
    </div>
    <div class="card-body">
        @if (Model.Roster.Count == 0)
        {
            <p class="text-center text-muted mb-0">No humans recorded for this season yet.</p>
        }
        else
        {
            <div class="row g-3">
                @foreach (var member in Model.Roster)
                {
                    <div class="col-6 col-sm-4 col-md-3">
                        <vc:human user-id="@member.UserId" layout="Card" size="102" />
                    </div>
                }
            </div>
        }
    </div>
</div>
```

- [ ] **Step 2: No build yet** — wired in Task 6.

---

### Task 6: Wire both cards into `Details.cshtml`, remove the Leads card

**Files:**
- Modify: `src/Humans.Web/Views/Camp/Details.cshtml`

- [ ] **Step 1: Add the Roster card above About (left column, `col-md-8`)**

Immediately before the `@* Blurb *@` About card (`<div class="card mb-4">` containing the `About` header inside `if (Model.CurrentSeason != null)`), insert:

```cshtml
            <partial name="_CampRosterCard" model="Model" />
```

- [ ] **Step 2: Replace the Leads card with the Roles card (right column, `col-md-4`)**

Delete the entire `@* Leads (only if authenticated) *@` block (the `@if (User.Identity?.IsAuthenticated == true && Model.Leads.Count > 0) { ... }` card) and replace with:

```cshtml
        @* Roles (replaces the legacy Leads card; sourced from CampRoleAssignment) *@
        <partial name="_CampRolesCard" model="Model" />
```

- [ ] **Step 3: Build**

Run: `dotnet build Humans.slnx -v quiet`
Expected: success — no remaining `Model.Leads` references on the detail path.

- [ ] **Step 4: Commit Phase 1 view-side**

```bash
git add src/Humans.Web/Models/CampViewModels.cs src/Humans.Web/Controllers/CampController.cs src/Humans.Web/Views/Camp/_CampRolesCard.cshtml src/Humans.Web/Views/Camp/_CampRosterCard.cshtml src/Humans.Web/Views/Camp/Details.cshtml
git commit -m "feat(camps): detail page Roles + Roster cards sourced from role system

Replaces the legacy camp_leads-backed Leads card with a read-only Roles card
(Camp Lead row for everyone logged-in; all roles + roster for members/CampAdmin)
and a Roster card above About. Fixes detail/Members lead mismatch."
```

---

### Task 7: Manual verification on preview

- [ ] **Step 1:** After the PR opens and the preview env (`https://{pr_id}.n.burn.camp`) is up, sign in (dev login) and confirm on a camp where you are a lead:
  - Right column shows **Roles** with you **and** any role-panel-added lead (e.g. Hannah).
  - Left column shows **Roster** above About with the season's Active members.
  - As a logged-in non-member: only **Leads** (Camp Lead row) shows, no Roster.
  - As CampAdmin on another camp: both cards show.
  - Logged out: neither card shows.

---

## Phase 2 — Fix the source: camp creation writes a Camp Lead role assignment

### Task 8: Change `ICampRepository.CreateCampAsync` to persist member + lead assignment atomically

**Files:**
- Modify: `src/Humans.Application/Interfaces/Repositories/ICampRepository.cs`
- Modify: `src/Humans.Infrastructure/Repositories/Camps/CampRepository.cs`

- [ ] **Step 1: Change the interface signature**

Replace the existing `CreateCampAsync(Camp, CampSeason, CampLead, ...)` with:

```csharp
    /// <summary>
    /// Persist a new camp with its initial season, the creator's Active CampMember,
    /// an optional Camp Lead role assignment (null when the Lead role definition is
    /// not yet seeded), and optional historical names in a single transaction.
    /// </summary>
    Task CreateCampAsync(
        Camp camp,
        CampSeason initialSeason,
        CampMember creatorMember,
        CampRoleAssignment? creatorLeadAssignment,
        IReadOnlyList<CampHistoricalName>? historicalNames,
        CancellationToken ct = default);
```

- [ ] **Step 2: Update the repo implementation**

In `CampRepository.CreateCampAsync`, add the camp, season, `creatorMember`, then `creatorLeadAssignment` when non-null, then names, and `SaveChangesAsync` once. Concretely:

```csharp
    public async Task CreateCampAsync(
        Camp camp,
        CampSeason initialSeason,
        CampMember creatorMember,
        CampRoleAssignment? creatorLeadAssignment,
        IReadOnlyList<CampHistoricalName>? historicalNames,
        CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        ctx.Camps.Add(camp);
        ctx.CampSeasons.Add(initialSeason);
        ctx.CampMembers.Add(creatorMember);
        if (creatorLeadAssignment is not null)
        {
            ctx.CampRoleAssignments.Add(creatorLeadAssignment);
        }
        if (historicalNames is { Count: > 0 })
        {
            ctx.CampHistoricalNames.AddRange(historicalNames);
        }
        await ctx.SaveChangesAsync(ct);
    }
```

Match the existing field name for the context factory (`_factory`) and the DbSet names (`ctx.CampMembers`, `ctx.CampRoleAssignments`) by checking the current file; adjust if they differ.

- [ ] **Step 3: Build — expect failure at the `CampService` call site (fixed in Task 9) and the caching decorator (Task 10).**

Run: `dotnet build Humans.slnx -v quiet`
Expected: FAIL — `CampService.CreateCampAsync` and `CachingCampService` still pass a `CampLead`.

---

### Task 9: `CampService.CreateCampAsync` builds member + Camp Lead assignment

**Files:**
- Modify: `src/Humans.Application/Services/Camps/CampService.cs`

- [ ] **Step 1: Replace the legacy `CampLead` construction**

In `CreateCampAsync`, delete the `var lead = new CampLead { ... }` block (lines ~107–114) and replace the `_repo.CreateCampAsync(camp, season, lead, historicalNameEntities, ...)` call with member + optional assignment construction:

```csharp
        var member = new CampMember
        {
            Id = Guid.NewGuid(),
            CampSeasonId = season.Id,
            UserId = createdByUserId,
            Status = CampMemberStatus.Active,
            RequestedAt = now,
            ConfirmedAt = now,
            ConfirmedByUserId = createdByUserId,
        };

        var leadDef = await _roleRepo.GetSpecialDefinitionAsync(CampSpecialRole.Lead, cancellationToken);
        CampRoleAssignment? leadAssignment = null;
        if (leadDef is not null)
        {
            leadAssignment = new CampRoleAssignment
            {
                Id = Guid.NewGuid(),
                CampSeasonId = season.Id,
                CampRoleDefinitionId = leadDef.Id,
                CampMemberId = member.Id,
                AssignedAt = now,
                AssignedByUserId = createdByUserId,
            };
        }
        else
        {
            logger.LogWarning(
                "Camp Lead role definition missing while creating camp {CampId}; creator added as Active member without a lead assignment. Run 'Seed system roles'.",
                camp.Id);
        }

        await _repo.CreateCampAsync(camp, season, member, leadAssignment, historicalNameEntities, cancellationToken);
```

`_roleRepo` is the existing `ICampRoleRepository` field (confirm the field name in the ctor — used by `IsUserCampLeadAsync`). `logger` is the existing `ILogger<CampService>` field. Keep the existing `AuditAction.CampCreated` log and the `SyncMembershipForUserAsync(createdByUserId, SystemTeamType.BarrioLeads, ...)` call that follow — the BarrioLeads sync now reads role assignments, so the creator is correctly synced.

- [ ] **Step 2: Build**

Run: `dotnet build Humans.slnx -v quiet`
Expected: FAIL only at `CachingCampService` (Task 10).

---

### Task 10: Fix the `CachingCampService` decorator for the new signature

**Files:**
- Modify: `src/Humans.Infrastructure/Services/Camps/CachingCampService.cs`

- [ ] **Step 1: Update the `CreateCampAsync` override**

Find the `CreateCampAsync` override (decorator passthrough). Change its signature to match the new `ICampService.CreateCampAsync` (its public signature is unchanged — it still takes the registration params, not the repo entities). **Only** the `ICampRepository.CreateCampAsync` changed, so `CachingCampService` likely needs **no signature change** unless it forwards repo entities. Verify: if `CachingCampService` only decorates `ICampService` (params-based), no change is needed here — remove this task. If a decorator references `CampLead`, update it.

- [ ] **Step 2: Build**

Run: `dotnet build Humans.slnx -v quiet`
Expected: success.

---

### Task 11: Update the camp-creation test to assert the role assignment

**Files:**
- Modify: `tests/Humans.Application.Tests/Services/CampServiceTests.cs`

- [ ] **Step 1: Replace the legacy-lead assertion in `CreateCampAsync_NewCamp_CreatesCampWithPendingSeason`**

The test currently constructs `CampService` with a substitute `ICampRoleService` and asserts a `Db.CampLeads` row. The creation path now uses `ICampRoleRepository` directly (real `CampRoleRepository` is already injected as `roleRepo`). Seed a Camp Lead definition first, then assert a role assignment + Active member:

```csharp
        await SeedSettingsAsync();
        var leadDef = new Humans.Domain.Entities.CampRoleDefinition
        {
            Id = Guid.NewGuid(),
            Name = "Camp Lead",
            Slug = "camp-lead",
            SlotCount = 2,
            MinimumRequired = 1,
            SpecialRole = CampSpecialRole.Lead,
            CreatedAt = Clock.GetCurrentInstant(),
            UpdatedAt = Clock.GetCurrentInstant(),
        };
        Db.CampRoleDefinitions.Add(leadDef);
        await Db.SaveChangesAsync();

        var userId = Guid.NewGuid();
        var camp = await _service.CreateCampAsync(
            userId, "Camp Funhouse", "camp@fun.com", "+34612345678",
            "https://instagram.com/funhouse", null,
            isSwissCamp: false, timesAtNowhere: 0,
            MakeSeasonData(), historicalNames: null, year: 2026);

        var season = await Db.CampSeasons.AsNoTracking().FirstAsync(s => s.CampId == camp.Id);
        season.Status.Should().Be(CampSeasonStatus.Pending);

        var member = await Db.CampMembers.AsNoTracking()
            .FirstOrDefaultAsync(m => m.CampSeasonId == season.Id && m.UserId == userId);
        member.Should().NotBeNull();
        member!.Status.Should().Be(CampMemberStatus.Active);

        var assignment = await Db.CampRoleAssignments.AsNoTracking()
            .FirstOrDefaultAsync(a => a.CampMemberId == member.Id);
        assignment.Should().NotBeNull();
        assignment!.CampRoleDefinitionId.Should().Be(leadDef.Id);
```

- [ ] **Step 2: Add a no-definition test (graceful degrade)**

```csharp
    [HumansFact]
    public async Task CreateCampAsync_NoLeadDefinition_CreatesCampAndActiveMemberWithoutAssignment()
    {
        await SeedSettingsAsync();
        var userId = Guid.NewGuid();

        var camp = await _service.CreateCampAsync(
            userId, "Camp Seedless", "c@s.com", "+34600000001", null, null,
            false, 0, MakeSeasonData(), null, 2026);

        var season = await Db.CampSeasons.AsNoTracking().FirstAsync(s => s.CampId == camp.Id);
        (await Db.CampMembers.AsNoTracking().AnyAsync(m => m.CampSeasonId == season.Id && m.UserId == userId))
            .Should().BeTrue();
        (await Db.CampRoleAssignments.AsNoTracking().AnyAsync(a => a.CampSeasonId == season.Id))
            .Should().BeFalse();
    }
```

- [ ] **Step 3: Run the camp service tests**

Run: `dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~CampServiceTests"`
Expected: PASS.

- [ ] **Step 4: Commit Phase 2**

```bash
git add src/Humans.Application/Interfaces/Repositories/ICampRepository.cs src/Humans.Infrastructure/Repositories/Camps/CampRepository.cs src/Humans.Infrastructure/Services/Camps/CachingCampService.cs src/Humans.Application/Services/Camps/CampService.cs tests/Humans.Application.Tests/Services/CampServiceTests.cs
git commit -m "feat(camps): camp creation writes Camp Lead role assignment, not legacy CampLead

Creator becomes an Active CampMember + Camp Lead CampRoleAssignment atomically.
Degrades to member-only (logged) when the Lead definition is unseeded."
```

---

## Phase 3 — Repoint remaining legacy readers

> Each task swaps a `camp_leads` read for the role system. The `ICampRoleRepository` already exposes the primitives: `GetCampSpecialRoleSeasonIdForYearAsync`, `GetCampIdsBySpecialRolesForUserAsync`, `GetSpecialRoleHolderUserIdsAsync`, `IsUserSpecialRoleHolderForCampAsync`. Where a per-camp/season list of lead user-ids is needed, add the repo method in Task 12.

### Task 12: Add a "lead user-ids by season" repo query

**Files:**
- Modify: `src/Humans.Application/Interfaces/Repositories/ICampRoleRepository.cs`
- Modify: `src/Humans.Infrastructure/Repositories/Camps/CampRoleRepository.cs`
- Modify: `tests/Humans.Application.Tests/Services/CampRoleServiceTests.cs` (or a repo test if one exists)

- [ ] **Step 1: Add the interface method**

```csharp
    /// <summary>
    /// Returns the distinct user ids holding the given special role on the
    /// specified season (non-deactivated definition). Used to source the camp
    /// detail "Contact the leads" recipient list and admin/CSV lead columns.
    /// </summary>
    Task<IReadOnlyList<Guid>> GetSpecialRoleHolderUserIdsForSeasonAsync(
        Guid campSeasonId, CampSpecialRole specialRole, CancellationToken ct = default);
```

- [ ] **Step 2: Implement (mirror `GetSpecialRoleHolderUserIdsAsync`, scoped to season)**

```csharp
    public async Task<IReadOnlyList<Guid>> GetSpecialRoleHolderUserIdsForSeasonAsync(
        Guid campSeasonId, CampSpecialRole specialRole, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.CampRoleAssignments
            .AsNoTracking()
            .Where(a => a.CampSeasonId == campSeasonId
                && a.Definition.SpecialRole == specialRole
                && a.Definition.DeactivatedAt == null)
            .Select(a => a.CampMember.UserId)
            .Distinct()
            .ToListAsync(ct);
    }
```

- [ ] **Step 3: Test** (add to the role-service/repo test file using `ServiceTestHarness` — seed a camp/season/definition/member/assignment, assert the returned user-id set). Run: `dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~CampRole"` → PASS.

- [ ] **Step 4: Commit**

```bash
git add src/Humans.Application/Interfaces/Repositories/ICampRoleRepository.cs src/Humans.Infrastructure/Repositories/Camps/CampRoleRepository.cs tests/Humans.Application.Tests/Services/CampRoleServiceTests.cs
git commit -m "feat(camps): repo query for special-role holders by season"
```

---

### Task 13: Repoint `CampController.Contact` recipients

**Files:**
- Modify: `src/Humans.Web/Controllers/CampController.cs`

- [ ] **Step 1:** In the `Contact` POST action, replace the recipient argument `camp.Leads.Select(l => l.UserId).Distinct().ToList()` with role-sourced lead user-ids for the camp's current season. Resolve the season (`camp.Seasons.OrderByDescending(s => s.Year).First()`), then call a `campRoleService`/`campService` method that wraps `GetSpecialRoleHolderUserIdsForSeasonAsync(seasonId, CampSpecialRole.Lead)`. Add a thin `ICampRoleService.GetSeasonLeadUserIdsAsync(Guid campSeasonId, CancellationToken)` that delegates to the repo method from Task 12, and call it here.

- [ ] **Step 2:** Build + run camp web/controller tests if any. Run: `dotnet build Humans.slnx -v quiet` → success.

- [ ] **Step 3: Commit**

```bash
git add -A && git commit -m "fix(camps): Contact-the-leads notifies role-based leads, not legacy table"
```

---

### Task 14: Repoint directory pinning (`GetCampsByLeadUserIdAsync`)

**Files:**
- Modify: `src/Humans.Application/Services/Camps/CampService.cs` (`GetCampDirectoryAsync`, line ~257)

- [ ] **Step 1:** Replace `leadCamps = await _repo.GetCampsByLeadUserIdAsync(userId.Value, ct)` with a role-based camp-id lookup: `var leadCampIdList = await _roleRepo.GetCampIdsBySpecialRolesForUserAsync(userId.Value, [CampSpecialRole.Lead], ct);` then build `leadCampIds = leadCampIdList.ToHashSet();`. The downstream code only uses `leadCampIds` (a `HashSet<Guid>`) for ordering and `MyCamps`; re-derive `leadCamps` from the already-loaded `camps` collection: `leadCamps = camps.Where(c => leadCampIds.Contains(c.Id)).ToList();` (avoids a second camp load). Verify the `MyCamps` block below still compiles with this.

- [ ] **Step 2:** Build → success. Add/extend a `CampServiceTests` case asserting a role-only lead's camp is pinned/listed in `MyCamps`.

- [ ] **Step 3: Commit**

```bash
git add -A && git commit -m "fix(camps): directory 'my camps' pin uses role-based leads"
```

---

### Task 15: Repoint `GetCampLeadSeasonIdForYearAsync`

**Files:**
- Modify: `src/Humans.Infrastructure/Repositories/Camps/CampRepository.cs` (line ~416) **or** `src/Humans.Application/Services/Camps/CampService.cs` (line ~1153)

- [ ] **Step 1:** The service method `CampService.GetCampLeadSeasonIdForYearAsync` currently delegates to the legacy repo join. Repoint it to `_roleRepo.GetCampSpecialRoleSeasonIdForYearAsync(userId, year, CampSpecialRole.Lead, ct)` and stop calling the legacy `_repo.GetCampLeadSeasonIdForYearAsync`. (Callers: `StoreService`, `CityPlanningController` — behavior preserved.)

- [ ] **Step 2:** Build → success. Run store/cityplanning tests if present. Add a `CampServiceTests` case: role-lead of a camp participating in year Y → returns that season id; non-lead → null.

- [ ] **Step 3: Commit**

```bash
git add -A && git commit -m "fix(camps): lead-season-for-year resolves via role system"
```

---

### Task 16: Repoint CampAdmin index leads (`CampAdminPageBuilder` + view)

**Files:**
- Modify: `src/Humans.Web/Models/CampAdmin/CampAdminPageBuilder.cs` (line ~90)
- Modify: `src/Humans.Web/Views/CampAdmin/Index.cshtml` (line ~418)

- [ ] **Step 1:** `CampAdminPageBuilder` builds `CampSummaryRowViewModel.Leads` from `c.Leads`. Replace with role-sourced lead user-ids per season. The builder iterates camps with a season; for each season call the bulk year query (Task 12 method) — or, to avoid N+1 at admin scale (small), call `GetSpecialRoleHolderUserIdsForSeasonAsync` per season. Map results to `CampLeadViewModel { UserId = id }` (LeadId unused on the admin view; set `Guid.Empty`). Confirm `Index.cshtml` only renders `lead.UserId` via `<vc:human>` (line ~418 loop) — it does; the `LeadId` is not rendered.

- [ ] **Step 2:** Build → success.

- [ ] **Step 3: Commit**

```bash
git add -A && git commit -m "fix(camps): CampAdmin index leads sourced from role system"
```

---

### Task 17: Repoint CSV export leads (`CampCsvExportBuilder`)

**Files:**
- Modify: `src/Humans.Web/Models/CampAdmin/CampCsvExportBuilder.cs` (lines ~19, ~37)

- [ ] **Step 1:** This builder reads `c.Leads`. Inject the lead user-ids per season (from Task 12's method) into the builder's inputs rather than reading `camp.Leads`. If the builder is a pure mapper over loaded `Camp` entities, change its caller (the CampAdmin controller/export action) to pass a `IReadOnlyDictionary<Guid seasonId, IReadOnlyList<Guid> leadUserIds>` and have the builder format names from that. Resolve names via `IUserService` as the builder already does for leads.

- [ ] **Step 2:** Build → success. Add/adjust a `CampCsvExportBuilder` unit test if one exists.

- [ ] **Step 3: Commit**

```bash
git add -A && git commit -m "fix(camps): CSV export leads column sourced from role system"
```

---

### Task 18: Repoint CityPlanning lead checks

**Files:**
- Modify: `src/Humans.Web/Controllers/CityPlanningController.cs` (line ~296)
- Modify: `src/Humans.Web/Controllers/CityPlanningApiController.cs` (line ~46)

- [ ] **Step 1:** Both use `c.Leads.Any(l => l.UserId == userId)` to decide "is this user a lead of this camp." Replace with `await campService.IsUserCampLeadAsync(userId, c.Id, ct)` (already role-first). If the surrounding code iterates a list of camps to find the user's camp, prefer `_roleRepo.GetCampIdsBySpecialRolesForUserAsync` once and intersect, to avoid per-camp calls. (Lines ~48/~68 already use `GetCampLeadSeasonIdForYearAsync`, repointed in Task 15 — no further change there.)

- [ ] **Step 2:** Build → success.

- [ ] **Step 3: Commit**

```bash
git add -A && git commit -m "fix(cityplanning): camp-lead check uses role system"
```

---

### Task 19: Drop the legacy GDPR slice + Edit-page leads source

**Files:**
- Modify: `src/Humans.Application/Services/Camps/CampService.cs` (`ContributeForUserAsync` ~1858; `GetCampEditDataAsync` ~239)

- [ ] **Step 1: GDPR** — in `ContributeForUserAsync`, remove the legacy `leadAssignments`/`shapedLeads` block and the `new UserDataSlice(GdprExportSections.CampLeadAssignments, shapedLeads)` entry. Keep the `CampRoleAssignments` slice (already emitted). Remove the now-unused `_repo.GetAllLeadAssignmentsForUserAsync` call.

- [ ] **Step 2: Edit page** — `GetCampEditDataAsync` populates `editData.Leads` from `camp.Leads`, surfaced read-only on `Edit.cshtml`. Since lead management now lives entirely on `/Edit/Members` (roles panel), source `editData.Leads` from role assignments for the season **or** drop the leads display from `Edit.cshtml` and stop populating it. Check `Edit.cshtml` for `Model.Leads` usage and pick the lighter option; if dropped, also remove the `Leads` mapping in `PopulateEditReadOnlyFieldsAsync` / `MapToEditViewModel`.

- [ ] **Step 3:** Build → success. Run GDPR contributor tests if present (`dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~Gdpr"` or `~CampService`).

- [ ] **Step 4: Commit**

```bash
git add -A && git commit -m "refactor(camps): drop legacy GDPR lead slice + Edit-page legacy leads"
```

---

## Phase 4 — Strip fallbacks + remove dead writes/methods

### Task 20: Strip the legacy fallbacks in `CampService`

**Files:**
- Modify: `src/Humans.Application/Services/Camps/CampService.cs`

- [ ] **Step 1:** In `IsUserCampLeadAsync` (~1173), remove the trailing `return await _repo.IsUserActiveLeadAsync(...)` fallback so the method returns the role-side result only:

```csharp
    public Task<bool> IsUserCampLeadAsync(
        Guid userId, Guid campId, CancellationToken cancellationToken = default) =>
        _roleRepo.IsUserSpecialRoleHolderForCampAsync(userId, campId, LeadOnly, cancellationToken);
```

- [ ] **Step 2:** In `IsUserCampEventManagerAsync` (~1189), remove the `_repo.IsUserActiveLeadAsync` fallback similarly:

```csharp
    public Task<bool> IsUserCampEventManagerAsync(
        Guid userId, Guid campId, CancellationToken cancellationToken = default) =>
        _roleRepo.IsUserSpecialRoleHolderForCampAsync(userId, campId, LeadOrWorkshop, cancellationToken);
```

- [ ] **Step 3:** In `GetPendingMembershipCountForLeadAsync` (~1806), remove the `if (roleSide > 0) return roleSide; return await _repo.CountPendingMembershipsForLeadAsync(...)` fallback — return the role-side count directly.

- [ ] **Step 4:** Build → expect `LeadOnly` constant still referenced (keep it). Run the camp auth/service tests. Run: `dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~CampService"` → PASS (existing role-side tests should already cover these; add a test that a user with **no** role assignment is **not** a lead even if a legacy `CampLead` row exists, to lock the fallback removal).

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "refactor(camps): role assignment is sole source of lead truth (drop legacy fallbacks)"
```

---

### Task 21: Drop the legacy account-merge reassignment

**Files:**
- Modify: `src/Humans.Application/Services/Camps/CampService.cs` (`ReassignAsync` ~1840)

- [ ] **Step 1:** Remove the `await _repo.ReassignLeadsToUserAsync(...)` call and its two `_systemTeamSync.SyncMembershipForUserAsync(..., BarrioLeads, ...)` only if they were solely for the legacy reassignment — keep the BarrioLeads sync calls (still valid; role-based) and the `_leadBadgeInvalidator.Invalidate(...)` calls. Role-side reassignment is handled by `ICampRoleRepository.ReassignAssignmentsToUserAsync` — confirm `AccountMergeService` already fans out to a role-side `IUserMerge`/reassign; if `CampService.ReassignAsync` is the camps merge entry, call `_roleRepo.ReassignAssignmentsToUserAsync(sourceUserId, targetUserId, updatedAt, ct)` here instead of the legacy method.

- [ ] **Step 2:** Build → success. Run account-merge tests: `dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~Merge"` → PASS.

- [ ] **Step 3: Commit**

```bash
git add -A && git commit -m "refactor(camps): account-merge reassigns role assignments, not legacy leads"
```

---

### Task 22: Repoint `DevPersonaSeeder` to role assignments

**Files:**
- Modify: `src/Humans.Web/Infrastructure/DevPersonaSeeder.cs` (lines ~378–388)

- [ ] **Step 1:** Replace the `await campService.AddLeadAsync(camp.Id, leadUserId)` call (and its `camp.Leads.Any(...)` pre-check) with: ensure a Camp Lead definition exists (the dev role seeder `DevelopmentCampRoleSeeder` seeds these — ensure it runs first), then add an Active member + Camp Lead assignment via `campService.AddMemberAndAssignRoleInActiveSeasonAsync(camp.Id, leadDefId, leadUserId, actorUserId, ct)` (resolve `leadDefId` via `campRoleService.GetDefinitionBySlugAsync("camp-lead")`). Keep idempotency (skip if already assigned).

- [ ] **Step 2:** Build → success. Manually verify dev login still yields a working camp-lead persona (preview env or `dotnet run`).

- [ ] **Step 3: Commit**

```bash
git add -A && git commit -m "chore(dev): persona seeder creates camp-lead via role assignment"
```

---

### Task 23: Delete now-dead legacy C# (no schema change)

**Files:**
- Modify: `src/Humans.Application/Interfaces/Camps/ICampService.cs`, `src/Humans.Application/Services/Camps/CampService.cs`, `src/Humans.Infrastructure/Services/Camps/CachingCampService.cs`
- Modify: `src/Humans.Application/Interfaces/Repositories/ICampRepository.cs`, `src/Humans.Infrastructure/Repositories/Camps/CampRepository.cs`
- Modify: `src/Humans.Application/Interfaces/Camps/ICampService.cs` (DTOs) and any unused VM file

- [ ] **Step 1: Verify zero callers** before each deletion:

```bash
git grep -n "AddLeadAsync\|RemoveLeadAsync\|IsUserActiveLeadAsync\|CountActiveLeadsAsync\|GetLeadForMutationAsync\|UpdateLeadAsync\|GetAllLeadAssignmentsForUserAsync\|ReassignLeadsToUserAsync\|GetCampsByLeadUserIdAsync\|CountPendingMembershipsForLeadAsync\|GetCampLeadSeasonIdForYearAsync" -- 'src/**'
```

Each remaining hit must be only the declaration/implementation (no live caller) — Phases 2–4 removed the callers.

- [ ] **Step 2: Delete the dead methods** from `ICampService`/`CampService`/`CachingCampService` (`AddLeadAsync`, `RemoveLeadAsync`) and from `ICampRepository`/`CampRepository` (`IsUserActiveLeadAsync`, `CountActiveLeadsAsync`, `AddLeadAsync`, `GetLeadForMutationAsync`, `UpdateLeadAsync`, `GetAllLeadAssignmentsForUserAsync`, `ReassignLeadsToUserAsync`, `GetCampsByLeadUserIdAsync`, legacy `GetCampLeadSeasonIdForYearAsync`, `CountPendingMembershipsForLeadAsync`). **Keep** `EnsureActiveMemberForMigrationAsync` and the seed plumbing (used by the still-present admin button).

- [ ] **Step 3: Delete now-unused DTOs/VMs** — `CampLeadInfo`, `CampLeadSummary` (in `ICampService.cs`) and `CampLeadViewModel` (in `CampViewModels.cs`) **only if** `git grep` shows zero remaining references. If `CampInfo.Leads`/`CampLookup.Leads`/`CampEditData.Leads` still expose them, remove those properties too (verify no view/consumer references remain). Leave any that still have live consumers and note them.

- [ ] **Step 4: Do NOT touch** `CampLead.cs`, `CampLeadRole.cs`, `CampLeadConfiguration.cs`, `Camp.Leads` nav, `CampLeads` DbSet (schema-bound → #774).

- [ ] **Step 5: Full build + snapshot check** — removing only C# methods/DTOs must not change the EF model snapshot:

```bash
dotnet build Humans.slnx -v quiet
git diff --exit-code src/Humans.Infrastructure/Migrations/HumansDbContextModelSnapshot.cs
```

Expected: build success; snapshot diff empty. If the snapshot changed, you removed a schema-bound member — revert that deletion (it belongs to #774).

- [ ] **Step 6: Commit**

```bash
git add -A && git commit -m "refactor(camps): delete dead legacy CampLead C# (entity/table retained for #774)"
```

---

### Task 24: Full suite + architecture tests + grep gate

- [ ] **Step 1: Run everything**

Run: `dotnet test Humans.slnx -v quiet`
Expected: all green.

- [ ] **Step 2: Confirm the table is orphaned in live code** (entity/config/migrations may still mention it):

```bash
git grep -nE "camp\.Leads|ctx\.CampLeads|\.Leads\b" -- 'src/Humans.Web/**' 'src/Humans.Application/**' 'src/Humans.Infrastructure/Services/**' 'src/Humans.Infrastructure/Repositories/**'
```

Expected: no live read/write of camp leads remains (matches only unrelated `.Leads` like `SubteamLeads`, if any — verify each hit).

- [ ] **Step 3: Snapshot gate** (belt-and-suspenders, no migration expected):

```bash
git diff --exit-code src/Humans.Infrastructure/Migrations/HumansDbContextModelSnapshot.cs
```

Expected: empty.

- [ ] **Step 4: Open the PR** (per workflow — squash on merge to peter `main`):

```bash
gh pr create --repo peterdrier/Humans --base main --head feat/camp-roster-roles \
  --title "Camp detail Roles/Roster cards + decouple from legacy camp_leads" \
  --body "Adds read-only Roles + Roster cards to /Barrios/{slug} sourced from CampRoleAssignment, fixes camp creation to write role assignments, and repoints every live reader/writer/fallback off camp_leads. Physical table drop deferred to nobodies-collective/Humans#774. Spec: docs/superpowers/specs/2026-05-20-camp-detail-roles-roster-and-lead-decouple-design.md

🤖 Generated with [Claude Code](https://claude.com/claude-code)"
```

- [ ] **Step 5: Run the EF migration reviewer** — N/A (no migration in this PR). Note in the PR that the migration lives in #774.

---

## Self-Review notes (author)

- **Spec coverage:** Part A → Tasks 1–7; Part B → Tasks 8–11; Part C → Tasks 12–19; Part D → Tasks 20–23; testing/rollout → Tasks 7, 11, 24.
- **Type consistency:** `IsLeadRole` (Task 1) is consumed in `_CampRolesCard.cshtml` (Task 4). `RolesPanel`/`Roster`/`CanSeeFullCamp` (Task 2) are set in Task 3 and read in Tasks 4–6. New repo method `GetSpecialRoleHolderUserIdsForSeasonAsync` (Task 12) is used in Tasks 13/16/17.
- **Open verifications flagged inline** (confirm against current code during execution): exact `CampMemberRow` property names; whether `CachingCampService` needs a `CreateCampAsync` change (Task 10); whether `Edit.cshtml` renders `Model.Leads` (Task 19); exact `_roleRepo`/`_factory` field names; DbSet names (`CampMembers`, `CampRoleAssignments`, `CampRoleDefinitions`).
