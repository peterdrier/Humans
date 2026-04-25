# Issue #489 re-implementation — per-camp role assignments

**Status:** Design — pending Peter's approval before plan + implementation.
**Issue:** [nobodies-collective#489](https://github.com/nobodies-collective/Humans/issues/489) — *Add configurable camp roles with per-role slots and public/private flag*
**Source PR (blueprint, not integration target):** [peterdrier#335](https://github.com/peterdrier/Humans/pull/335) by @FrankFanteev. Stale-base + ~954 files of upstream drift; rebase is more expensive than re-implementation. Frank's branch is the design reference for entity shape, EF config, view-model layout, and view markup. Code is ported, not merged.
**Prior review:** [/pr-review on #335](https://github.com/peterdrier/Humans/pull/335#issuecomment-4320697717) — full spec-vs-impl gap analysis; the C#/I# fix references in this spec point to that comment.

---

## Why re-implement instead of rebase

1. PR #335 was branched off a stale snapshot of merged-and-reworked PR #228 (`17b8ddfc`); main has since absorbed the §15 architectural refactor. Drift: ~954 files.
2. `CampMember.User`, `CampMember.ConfirmedByUser`, `CampMember.RemovedByUser` navigation properties — used at `CampService.cs:1873` in #335 — are removed in main. Won't compile after rebase.
3. `ICampService` and `CampService` moved Application-layer + section folder. Frank's file paths don't exist in main.
4. `CampService` is now 1,725 lines. Adding ~313 more (Frank's role methods) on top conflicts with the §15 doctrine of section-scoped service decomposition. Re-implementation lets us extract `CampRoleService` cleanly.

---

## Brainstorm decisions

| # | Decision | Outcome |
|---|---|---|
| **D1** | Restore the spec's `IsPublic` flag (issue AC7: anonymous users see public roles on Camp Details). | **Drop entirely.** All role visibility is auth-gated. Issue #489 will be updated with a ratification comment as part of this PR. |
| **D2** | Frank's auto-promote-on-assignment feature (creates CampMember(Active) in the same transaction). | **Drop auto-promote.** Replace with a separate, explicit "lead adds active member to camp" action that uses the existing `_HumanSearchInput` partial and produces an audit entry ("Bob added Phil to camp Foo"). After membership exists, role assignment proceeds by the spec's normal path. **Camp Lead → role retirement is split to a follow-up issue** — Camp Lead is NOT seeded as a role definition in this PR; existing `CampLead` entity and authz are unchanged here. |
| **D3** | Frank's 6th seed row "Wellbeing Lead" (beyond spec). | **Drop.** Seed exactly the 5 spec roles; CampAdmin can add via the GUI post-deploy. |
| **D4** | Frank's `MinimumRequired` field (separates compliance threshold from slot cap). | **Keep.** Compliance = `IsRequired && filledCount >= MinimumRequired`. Add cross-field validation at the viewmodel layer (`0 ≤ MinimumRequired ≤ SlotCount`). |
| **D5** | Frank's per-slot index storage (`SlotIndex` column + DB unique on `(season, role, slotIndex)`). | **Drop SlotIndex.** Storage is `(season, role, campMember)` unique. Service layer enforces `count < SlotCount` before insert. Slots are a display concern: the view orders existing assignments and pads with empty rows up to `SlotCount`. |

---

## Out of scope (split to follow-up)

- **Camp Lead → unified role assignment.** Retiring the `CampLead` entity, `camp_leads` table, `CampLeadRole` enum, `CampAuthorizationHandler.IsUserCampLeadAsync` lookup, and migrating existing lead rows to `CampRoleAssignment` is a separate authz refactor. A new follow-up issue will be opened (linked to #489) — drafted in this PR's body for Peter to publish.
- **`IsPublic` plumbing.** Per D1, dropped entirely. If a future need surfaces, a new issue revisits.
- **Auto-promote shortcut.** Per D2, dropped entirely. Lead-adds-member then role-assigns is the only path.

---

## Data model

### New entities

```csharp
// Humans.Domain.Entities

public class CampRoleDefinition
{
    public Guid Id { get; init; }
    public string Name { get; set; } = string.Empty;
    [MarkdownContent] public string? Description { get; set; }

    /// <summary>How many slot rows the view renders per camp-season. Soft cap; service enforces.</summary>
    public int SlotCount { get; set; } = 1;

    /// <summary>How many slots must be filled for compliance. 0 ≤ MinimumRequired ≤ SlotCount.</summary>
    public int MinimumRequired { get; set; } = 1;

    public int SortOrder { get; set; }

    /// <summary>True if the compliance report should track this role.</summary>
    public bool IsRequired { get; set; }

    public Instant CreatedAt { get; init; }
    public Instant UpdatedAt { get; set; }

    /// <summary>Soft-delete: deactivated definitions preserve historical assignments but are hidden from new-assignment UI.</summary>
    public Instant? DeactivatedAt { get; set; }

    public ICollection<CampRoleAssignment> Assignments { get; } = new List<CampRoleAssignment>();
}

public class CampRoleAssignment
{
    public Guid Id { get; init; }

    public Guid CampSeasonId { get; init; }
    public CampSeason CampSeason { get; set; } = null!;

    public Guid CampRoleDefinitionId { get; init; }
    public CampRoleDefinition Definition { get; set; } = null!;

    public Guid CampMemberId { get; init; }
    public CampMember CampMember { get; set; } = null!;

    public Instant AssignedAt { get; init; }
    public Guid AssignedByUserId { get; init; }
}
```

### EF configuration (new files under `src/Humans.Infrastructure/Data/Configurations/Camps/`)

- `CampRoleDefinitionConfiguration` — table `camp_role_definitions`; **unique index** on `Name` (case-insensitive collation if portable; else service-layer pre-check is sufficient — DB enforcement is not load-bearing per project doctrine).
- `CampRoleAssignmentConfiguration` — table `camp_role_assignments`;
  - **Unique index** on `(CampSeasonId, CampRoleDefinitionId, CampMemberId)` — same person can't hold the same role twice in one season.
  - FK to `CampSeason` — `OnDelete(Cascade)` (season deletion clears its assignments).
  - FK to `CampRoleDefinition` — `OnDelete(Restrict)` (deactivate, don't delete; preserves history).
  - FK to `CampMember` — `OnDelete(Cascade)` (member row deletion via hard delete clears assignments; soft deletes are handled in service layer — see C1 fix below).

### Migration

Single new migration `AddCampRoles` immediately after main's `20260424160739_AddCampMembers`. Generated fresh; no port from Frank. **Mandatory pre-commit step:** the EF migration reviewer agent (`.claude/agents/ef-migration-reviewer.md`) must pass with no CRITICAL issues.

#### Seed (`HasData`)

5 rows, deterministic GUIDs, `Instant.FromUnixTimeTicks(...)` for stable timestamps:

| Name | IsRequired | MinimumRequired | SlotCount | SortOrder |
|---|---|---|---|---|
| Consent Lead | true | 1 | 2 | 10 |
| LNT | true | 1 | 1 | 20 |
| Shit Ninja | true | 1 | 1 | 30 |
| Power | false | 0 | 1 | 40 |
| Build Lead | true | 1 | 2 | 50 |

`Description` left null on seed. CampAdmin can edit names, descriptions, slot counts, sort order, required flag, and minimum-required after deploy via the new GUI.

---

## Service architecture

### New service: `ICampRoleService` / `CampRoleService`

Following §15 (services own their tables, cross-section calls via interfaces, Application layer):

- Interface: `src/Humans.Application/Interfaces/Camps/ICampRoleService.cs`
- Implementation: `src/Humans.Application/Services/Camps/CampRoleService.cs`

**Owns tables:** `camp_role_definitions`, `camp_role_assignments`.
**Calls into:** `ICampService` (camp/season lookup, active-membership verification), `IUserService` (display name lookup — `CampMember.User` nav is gone in main), `IAuditLogService`, `INotificationService`.
**Caching:** Plain pass-through; no `Cached*` types. If list-of-definitions reads dominate, add `IMemoryCache` inside the service later — for now, simple queries.

### Public surface (proposed)

Decision-relevant slice; full method list elaborated in the implementation plan.

```csharp
public interface ICampRoleService
{
    // Definitions (CampAdmin)
    Task<IReadOnlyList<CampRoleDefinition>> ListDefinitionsAsync(bool includeDeactivated, CancellationToken ct);
    Task<CampRoleDefinition> CreateDefinitionAsync(CreateCampRoleDefinitionInput input, Guid actorUserId, CancellationToken ct);
    Task UpdateDefinitionAsync(Guid id, UpdateCampRoleDefinitionInput input, Guid actorUserId, CancellationToken ct);
    Task DeactivateDefinitionAsync(Guid id, Guid actorUserId, CancellationToken ct);
    Task ReactivateDefinitionAsync(Guid id, Guid actorUserId, CancellationToken ct);

    // Per-camp assignments (Lead / CampAdmin / Admin)
    Task<CampRolesPanelData> BuildPanelAsync(Guid seasonId, CancellationToken ct);
    Task<AssignCampRoleOutcome> AssignAsync(Guid seasonId, Guid roleDefinitionId, Guid campMemberId, Guid actorUserId, CancellationToken ct);
    Task UnassignAsync(Guid seasonId, Guid assignmentId, Guid actorUserId, CancellationToken ct);

    // Cross-service hooks (called by CampService cascades — see C1)
    Task RemoveAllForMemberAsync(Guid campMemberId, Guid actorUserId, CancellationToken ct);

    // Reporting
    Task<CampRoleComplianceReport> GetComplianceReportAsync(int year, CancellationToken ct);
}
```

`AssignCampRoleOutcome` is an enum / discriminated result: `Assigned`, `MemberNotActive`, `RoleDeactivated`, `SlotCapReached`, `AlreadyHoldsRole`. Controller maps each to a TempData message.

### `CampService` additions

- `AddCampMemberAsLeadAsync(seasonId, userId, addedByUserId, ct)` — creates `CampMember(Status=Active)` directly (bypasses the request/approve flow). Audit action: `CampMemberAddedByLead` (new enum value).
- Cascade hook in `LeaveCampAsync` and `WithdrawCampMembershipRequestAsync` — both soft-delete (set `RemovedAt`); call `ICampRoleService.RemoveAllForMemberAsync(campMemberId, actor, ct)` before soft-delete to clear role assignments. **C1 fix from prior review.** `RemoveCampMemberAsync` already does this correctly today — match its pattern.

### Decommissions / no changes

- `CampLead` entity, `CampLeadRole` enum, `CampAuthorizationHandler.IsUserCampLeadAsync`, `AddLeadAsync`/`RemoveLeadAsync`: **unchanged in this PR.** All authz continues to flow through `CampLead`. Camp Lead is **not** seeded as a `CampRoleDefinition`. Follow-up issue handles the unification.

---

## Controller surface and routing

**No new controllers, no new aliases beyond the existing Barrios↔Camps pair.**

### `CampAdminController` (existing, `[Route("Barrios/Admin")] [Route("Camps/Admin")]`) — global admin

| Verb | Path | Action | Auth |
|---|---|---|---|
| GET | `/Camps/Admin/Roles` | `Roles()` — list definitions | `CampAdmin` policy |
| POST | `/Camps/Admin/Roles/Create` | `CreateRole(...)` | `CampAdmin` |
| POST | `/Camps/Admin/Roles/{id:guid}/Edit` | `EditRole(id, ...)` | `CampAdmin` |
| POST | `/Camps/Admin/Roles/{id:guid}/Deactivate` | `DeactivateRole(id)` — wrapped in try/catch + logging (I2 fix) | `CampAdmin` |
| POST | `/Camps/Admin/Roles/{id:guid}/Reactivate` | `ReactivateRole(id)` — wrapped in try/catch + logging (I2 fix) | `CampAdmin` |
| GET | `/Camps/Admin/Compliance` | `Compliance(year?)` — required-role report | `CampAdmin` |

### `CampController` (existing, `[Route("Barrios")] [Route("Camps")]`) — per-camp pages

| Verb | Path | Action | Auth |
|---|---|---|---|
| POST | `/Camps/{slug}/Members/Add` | `AddMember(slug, userId)` — lead-adds-active-member (D2.1) | `CampOperationRequirement.Manage` on resolved Camp |
| POST | `/Camps/{slug}/Roles/Assign` | `AssignRole(slug, roleDefinitionId, campMemberId)` | `CampOperationRequirement.Manage` |
| POST | `/Camps/{slug}/Roles/{assignmentId:guid}/Unassign` | `UnassignRole(slug, assignmentId)` — **C2 fix:** resolve camp by slug, load assignment, verify `assignment.CampSeason.CampId == camp.Id` before delegating to service. | `CampOperationRequirement.Manage` |

### Critical-fix application points

- **C1** (`LeaveCampAsync` / `WithdrawCampMembershipRequestAsync` cascade) — implemented in `CampService` calling into `ICampRoleService.RemoveAllForMemberAsync`. Tested.
- **C2** (cross-camp `UnassignRole`) — implemented in `CampController.UnassignRole` before service delegation. Tested with a "lead of camp A cannot unassign in camp B" test.
- **I1** (audit-log-before-save in `CreateDefinitionAsync` / `UpdateDefinitionAsync`) — order in service is `_repo.Add(...)` → `SaveChangesAsync` → `_auditLog.LogAsync`.
- **I2** (Deactivate/Reactivate try/catch + logging) — controller actions follow the existing CampAdminController pattern of wrapping service calls.
- **I5** (`AssignAsync` `DbUpdateException` handling) — service catches `DbUpdateException` from the unique index and returns `AlreadyHoldsRole` outcome.
- **I6** (compliance report orphan inflation) — moot; no slot index, no orphans. Compliance is `assignments.Count(a => !a.Definition.DeactivatedAt.HasValue) >= def.MinimumRequired` for definitions where `IsRequired = true`.

---

## View structure

Match Frank's PR layout almost verbatim, with one addition.

### `Views/CampAdmin/Roles.cshtml` (new)

Ported from Frank's `Roles.cshtml` minus the IsPublic column. Lists active and deactivated definitions in two sections; CRUD via inline forms or modal.

### `Views/CampAdmin/RoleForm.cshtml` (new)

Ported from Frank's. **No IsPublic toggle.** Fields: `Name`, `Description` (markdown), `SlotCount`, `MinimumRequired`, `SortOrder`, `IsRequired`. Cross-field validation at the viewmodel layer enforces `0 ≤ MinimumRequired ≤ SlotCount`.

### `Views/CampAdmin/Compliance.cshtml` (new)

Ported from Frank's. Lists camps with any required role unfilled for the selected year (defaults to current public year). Linked from `Views/CampAdmin/Index.cshtml` nav.

### `Views/Camp/Edit.cshtml` (existing, modified)

- **New "Add active member" row** above the existing per-season member list, lead-only, gated by `Model.CanManage`. Uses `<partial name="_HumanSearchInput" view-data='@(new ViewDataDictionary(ViewData) { { "FieldName", "userId" } })' />`. POSTs to `/Camps/{slug}/Members/Add`. This is the only added screen element relative to Frank's design.
- **Roles panel** (ported from Frank): one card per active role definition, ordered by `SortOrder`. Each card shows N slot rows where N = `SlotCount`:
  - Filled slots: human link + unassign button.
  - Empty slots: `_HumanSearchInput` member picker scoped to active members of the season (server-side filtered) + assign button.
- **"Over capacity" indicator** on a card if `assignments.Count > def.SlotCount` (CampAdmin reduced the cap below current usage). Lead must unassign one to drop back into capacity. No "phased-out slot" rendering — soft cap means no slot index, no orphans.

### `Views/Camp/Details.cshtml` (existing, modified)

Per D1, all role assignments are auth-gated. Frank's authenticated-only roles section stays. **No public anonymous-visible role section.**

### Localization

All user-facing strings via existing `IStringLocalizer` patterns, English only in this PR (translators handle es/de/fr/it after merge per the existing flow). "Humans" not "members"/"volunteers" in UI copy.

---

## Audit log + notifications

### Audit actions (new enum values)

- `CampMemberAddedByLead` — emitted by `CampService.AddCampMemberAsLeadAsync`. Subject: `CampMember`. Description: "{Actor} added {target.DisplayName} to camp {camp.Name}."
- `CampRoleDefinitionCreated` / `CampRoleDefinitionUpdated` / `CampRoleDefinitionDeactivated` / `CampRoleDefinitionReactivated` — emitted by `CampRoleService` definition CRUD. Subject: `CampRoleDefinition`.
- `CampRoleAssigned` / `CampRoleUnassigned` — emitted by `CampRoleService.AssignAsync` / `UnassignAsync`. Subject: `CampRoleAssignment`. Description includes role name + assignee name + camp name for greppability.

### Notifications

- `NotificationSource.CampRoleAssigned` (new value) → mapped to user preference category `TeamUpdates`. Notification sent to assignee on assign; not sent on unassign.
- Best-effort try/catch wrapping in the controller, matching the PR #228 pattern.

---

## Authorization

- **Definition CRUD + compliance report:** `CampAdmin` role (or `Admin`). Standard `[Authorize(Policy = ...)]` on `CampAdminController` actions.
- **Per-camp role assign/unassign + lead-adds-member:** `CampOperationRequirement.Manage` resource-based authz on the resolved `Camp`. Existing `CampAuthorizationHandler` handles Admin/CampAdmin/CampLead lookup; nothing changes there.
- **Cross-camp safety (C2):** Controller verifies `assignmentId → CampSeason.CampId == camp.Id` before unassign delegation.
- **Authorization-free service:** `CampRoleService` does not consult roles or claims. Controllers handle authz; service handles data.

---

## Testing

**Project-banned `[Fact]`/`[Theory]` are replaced by `HumansFact`/`HumansTheory`.**

### `tests/Humans.Application.Tests/Services/CampRoleServiceTests.cs` (new)

Port Frank's 21 tests, adapt to the new service shape, drop tests that no longer make sense (slot index occupancy, public/private rendering), and add the following new tests required by this design:

- **C1** — `LeaveCampAsync` cascades to role-assignment cleanup (member with assignments → leave → assignments gone).
- **C1** — `WithdrawCampMembershipRequestAsync` cascades to role-assignment cleanup (rare path: member who already has assignments withdraws — possible if assignments were created before withdrawal).
- **C2** — Cross-camp unassign rejected: lead of camp A passing camp B's `assignmentId` returns auth or not-found error from the controller layer (controller-level test or integration test).
- **D2.1** — `AddCampMemberAsLeadAsync` creates `CampMember(Active)` directly, audits `CampMemberAddedByLead`, and rejects when caller is not a lead/CampAdmin/Admin.
- **D5** — Soft-cap enforcement: `AssignAsync` returns `SlotCapReached` when assignments would exceed `SlotCount`; over-capacity scenario (cap reduced after assignment) renders correctly in `BuildPanelAsync`.
- **I5** — `AssignAsync` returns `AlreadyHoldsRole` outcome when the unique-index race fires (mock or DB integration).
- **Compliance** — `GetComplianceReportAsync` correctly counts only required roles, only assignments tied to non-deactivated definitions.

### Integration tests

If the existing test surface includes integration tests for `CampService` cascades, add `LeaveCamp`/`Withdraw` + role-cascade scenarios there. Otherwise, application-layer tests with an in-memory or test SQLite DB are sufficient.

---

## Documentation updates

Required in this PR (per CLAUDE.md post-fix doc check):

- **`docs/features/20-camps.md`** — port Frank's role data-model section, MINUS the `IsPublic` description, MINUS the auto-promote section. Add the lead-adds-member flow. Authz table reflects "all role visibility auth-gated."
- **`docs/sections/Camps.md`** — invariants:
  - `CampRoleAssignment` requires `CampMember.Status == Active` for the same `CampSeasonId`.
  - `CampRoleAssignment.CampMemberId` cleared (cascade) when a `CampMember` is removed (any path: hard delete, soft delete, leave, withdraw).
  - All role-assignment data is private (no anonymous render).
  - Camp Lead authz remains on `CampLead` entity until follow-up issue retires it.
  - `CampService` owns `camp_members`, `camps`, `camp_seasons`, `camp_leads`, `camp_images`. `CampRoleService` owns `camp_role_definitions`, `camp_role_assignments`.
- **`docs/architecture/data-model.md`** — index entry for the two new entities, pointing at `Camps.md` for field-level detail.

Doc updates land in the same PR as the code, not a follow-up.

---

## Source material reuse map

Files in `.worktrees/pr-review-335/` that we port (read-only blueprint):

| Frank's file | Action in this PR |
|---|---|
| `src/Humans.Domain/Entities/CampRoleDefinition.cs` | Port; add `IsPublic`-removal note. Verbatim except no `IsPublic`. |
| `src/Humans.Domain/Entities/CampRoleAssignment.cs` | Port shape; replace any `SlotIndex` field; FK is `CampMemberId`. |
| EF configurations | Re-author at new path `src/Humans.Infrastructure/Data/Configurations/Camps/`. Indexes per data-model section above. |
| `Migrations/20260425132022_AddCampRoles.cs` | **Discard.** Generate fresh from current main. |
| Frank's seed `HasData` | Port 5 rows (drop Wellbeing Lead). No `IsPublic` column. |
| `CampRoleSlotViewModel`, `CampRoleRowViewModel`, `CampRolesPanelViewModel` | Port; drop `IsPublic` field on row VM. |
| `Views/CampAdmin/Roles.cshtml`, `RoleForm.cshtml`, `Compliance.cshtml` | Port; drop IsPublic toggle/column. |
| Camp Edit per-role panel rows | Port verbatim modulo slot-index → soft-cap rendering (order existing assignments + pad to SlotCount). |
| `Views/Camp/Details.cshtml` role section | Port the auth-gated render. No public section. |
| `tests/Humans.Application.Tests/Services/CampRoleTests.cs` (834 lines, 21 tests) | Port substantively; rewrite assertions for soft cap; add C1/C2/D2.1 tests. |
| AuditAction / NotificationSource enum additions | Port the new enum values; rename per the action list above. |
| `_AutoPromoteMemberModal.cshtml` | **Discard.** Auto-promote dropped per D2. |
| Frank's doc updates | Discard; rewrite to match this design's actual behavior. |

---

## Workflow

- Worktree: `.worktrees/issue-489-fresh` off `origin/main` (already created).
- Branch: `sprint/2026-04-26/issue-489-fresh`.
- Build/test: `dotnet build Humans.slnx -v quiet`, `dotnet test Humans.slnx -v quiet`.
- Issue/PR refs: always qualified — `peterdrier#NNN` or `nobodies-collective#NNN`.
- Push every 3–5 commits during long runs.
- EF migration reviewer agent runs before the migration commit. No commit if it returns CRITICAL findings.
- PR target: `peterdrier/Humans` `main`. PR body credits Frank's #335 as the design blueprint and enumerates the deviations resolved (D1–D5).

---

## Risks and open questions

1. **`_HumanSearchInput` partial behavior at scale.** The partial is reused for adding leads today — should be fine for adding members and assigning roles. Verify it's queryable for "active members of this camp-season only" filtering for the role-assign picker (server-side filter in the controller, not the partial itself).
2. **Notification fan-out on bulk assignment.** Five definition seed rows × N camps × 1–2 leads each could push notification volume on initial deploy. Best-effort try/catch is already designed in. Worth a check on QA before production rollout.
3. **Compliance report performance.** At ~500-user scale this is a non-issue; report iterates camps × required-role-definitions per year. If perception lag on the page surfaces post-deploy, add `IMemoryCache` on the report inside `CampRoleService`.
4. **Camp Lead retirement timing.** This PR ships role infrastructure WITHOUT folding Camp Lead into it. Until the follow-up issue ships, "Camp Lead" appears nowhere in the role admin UI. UX-wise, leads see role assignment for the 5 spec roles but their lead status is still managed via the existing `Add Lead` form. Acceptable for the interim.
5. **Issue #489 ratification comment.** D1 is a deliberate spec deviation. Before this PR merges, post a comment on `nobodies-collective#489` ratifying "no public/private flag — all role visibility auth-gated" so the issue's acceptance criteria match what was built.

---

## Definition of done

- All 12 issue ACs satisfied or explicitly ratified-as-deviated:
  - AC1, AC2, AC3, AC4 — implemented.
  - AC5 — enforced (active-member-only assignment; no auto-promote).
  - AC6 — soft cap enforced in service; over-capacity rendered.
  - AC7 — **deviated by D1, ratified.** Issue body updated.
  - AC8 — implemented + extended (C1: covers Leave/Withdraw soft-delete paths, not just hard `Remove`).
  - AC9 — compliance report implemented + I6 moot.
  - AC10, AC11 — docs updated in this PR.
  - AC12 — test coverage extended for C1/C2/D2.1/D5.
- Build green, tests green, EF migration reviewer green.
- Doc files in `docs/features/`, `docs/sections/`, `docs/architecture/data-model.md` updated.
- PR open against `peterdrier/main` with body crediting #335 + enumerating D1–D5 deviations.
- `nobodies-collective#489` updated with the D1 ratification comment.
- Follow-up issue drafted (in PR body or separate file) for Camp Lead → role assignment retirement, ready for Peter to publish.
