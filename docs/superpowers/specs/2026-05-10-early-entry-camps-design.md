# Early Entry — camps consumer (v1)

**Issue:** [nobodies-collective/Humans#490](https://github.com/nobodies-collective/Humans/issues/490)
**Branch:** `issue-490-early-entry`
**Date:** 2026-05-10
**Status:** design

## Context

Camps need to bring people on-site before gate opens — build leads, infra crew, etc. The org allocates a capped number of Early Entry (EE) slots to each camp. Camp leads then decide which of their members fill those slots. Today: spreadsheets.

The original issue framed EE as a standalone source-agnostic service (pools / allocations / grants) so a future Build/Shifts consumer plugs in for free. **This v1 deliberately collapses to a camps-only shape** — the org-side cap is informational, not enforced, and the camp side is just "N slots per season, per-member bool". When Build/Shifts EE arrives, it gets its own surface; cross-source aggregation is a future shared query layer, not a redesign of v1.

## What v1 ships

EE state lives entirely inside the existing Camps section.

### Entity changes (no new tables)

| Field | Type | Default | Where |
|-------|------|---------|-------|
| `CampSeason.EeSlotCount` | `int` | `0` | `camp_seasons` |
| `CampMember.HasEarlyEntry` | `bool` | `false` | `camp_members` |
| `CampSettings.EeStartDate` | `LocalDate?` | `null` | `camp_settings` (singleton) |

No new tables, no new entities. EE state attaches to rows the Camps section already owns.

### Service surface (ICampService + 3, 51→54)

```csharp
Task SetCampSeasonEeSlotCountAsync(Guid campSeasonId, int slotCount, Guid actorUserId, CancellationToken ct);
Task SetEarlyEntryAsync(Guid campMemberId, bool granted, Guid actorUserId, CancellationToken ct);
Task SetEeStartDateAsync(LocalDate? eeStartDate, CancellationToken ct);
```

Budget exception authorized by Peter on 2026-05-10. EE lives on `CampSeason` / `CampMember` / `CampSettings` — tables `ICampService` already owns — so the methods belong here per design-rules §6 (no service split). Add a corresponding entry to `memory/architecture/interface-method-budget-ratchet.md` and bump the test ceiling from 51 to 54.

### Service rules

- **Grant overflow:** `SetEarlyEntryAsync(granted=true)` is rejected if it would push the count of non-removed `CampMember`s with `HasEarlyEntry=true` for that season above `CampSeason.EeSlotCount`. Returns a structured failure outcome (matches `AssignCampRoleOutcome` shape).
- **Active member precondition:** Grant requires `CampMember.Status == Active`. Removed/Pending members cannot hold EE.
- **Slot-count reduction:** CampAdmin lowering `EeSlotCount` below current grant count is **allowed**. No auto-revoke. UI flags overflow (`granted N / capped M` where N > M) until a lead manually revokes.
- **Member-removal cascade:** When `CampService` transitions a `CampMember` to `Removed` (Leave / Withdraw / Remove paths), set `HasEarlyEntry = false` in the same `SaveChangesAsync`. Same pattern as the existing `ICampRoleService.RemoveAllForMemberAsync` cascade.
- **Re-add:** A new `CampMember` row created after a Removed tombstone starts with `HasEarlyEntry = false`.
- **Idempotent set:** Calling `SetEarlyEntryAsync` with the value already set is a no-op (no audit entry, no error).

### Authorization

New `CampOperation` value: `SetEarlyEntry`. `CampAuthorizationHandler` grants it to **Primary, CoLead, CampAdmin, Admin**.

`SetCampSeasonEeSlotCountAsync` and `SetEeStartDateAsync` reuse the existing CampAdmin/Admin gate (same as `SetPublicYearAsync`, `SetNameLockDateAsync`).

| Action | Who |
|--------|-----|
| Edit `EeSlotCount` per camp | CampAdmin, Admin |
| Edit `EeStartDate` (global) | CampAdmin, Admin |
| Grant / revoke EE on a member | Primary, CoLead, CampAdmin, Admin |
| View EE state on a camp roster | Primary, CoLead, CampAdmin, Admin |
| View own EE state | **Not in v1** — humans learn out-of-band |

### UI

**`/Camps/Admin`** — add an **EE Slots** column. Inline editor per camp. Sticky header shows total slots configured and total granted across all camps (informational; never blocks). EE date editor sits in the existing settings strip on the same page.

**`/Camps/{slug}/Edit/Members`** (existing leads roster page) — add an **Early Entry** toggle column on each Active member row. Toggle disabled when slot count is `0`, or when all slots are used and that row isn't already granted. Pending / Removed rows do not show the toggle.

**`/Camps/{slug}`** (public detail) — **never** renders EE state. Camp members aren't even rendered publicly, so this falls out automatically; add an assertion in tests to keep it that way.

**No self-view in v1.** No profile row, no dashboard tile. Per Peter (2026-05-10): humans learn about their EE grant the same way they do today (lead tells them on WhatsApp). Self-view is cheap to add later when there's a real consumer like a gate scanner.

### Audit log

New `AuditAction` values:

- `CampEarlyEntryGranted` — context: `CampMemberId`, `CampSeasonId`, `UserId` (the grantee)
- `CampEarlyEntryRevoked` — same shape
- `CampSeasonEeSlotCountChanged` — context: `CampSeasonId`, old value, new value
- `CampSettingsEeStartDateChanged` — context: old value, new value

All written through the existing `IAuditLogService` via the established `repo.Save → auditLog.LogAsync` ordering used in `CampRoleService`.

### Migration

Single migration `AddEarlyEntryFields`:

- `camp_seasons` += `ee_slot_count int not null default 0`
- `camp_members` += `has_early_entry bool not null default false`
- `camp_settings` += `ee_start_date date null`

No data backfill needed — defaults are correct.

## Out of scope (deferred)

- **Build / Shifts EE source.** Future issue. v1 deliberately doesn't model "source" because there's only one.
- **Gate endpoint.** Future issue. When it ships it can query `camp_members.has_early_entry` directly for v1; later it queries a cross-source aggregator.
- **Cross-source aggregation.** Future issue, only meaningful once a second source exists.
- **Self-view (profile / dashboard).** Future issue. Trivially added on top of `CampMember.HasEarlyEntry`.
- **Org-wide hard cap.** Pool table dropped. Total budget is informational on `/Camps/Admin` only.
- **Notifications on grant.** Mirror `CampRoleAssigned` if/when needed.
- **Standalone `docs/sections/EarlyEntry.md`.** No new section in v1 — EE state is part of the Camps section.

## Tests

- Grant rejected when it would exceed `EeSlotCount`.
- Grant rejected when `CampMember.Status != Active`.
- Slot-count reduction below current grants is allowed and surfaces an overflow indicator (asserted via service-layer data, not UI).
- Member-removal cascade: `Leave`, `Withdraw`, `Remove` all clear `HasEarlyEntry` in the same transaction.
- Re-request after removal starts `HasEarlyEntry = false`.
- Idempotent `SetEarlyEntryAsync` writes no audit row.
- Authz matrix:
  - Lead grants OK.
  - Non-lead 403.
  - CampAdmin / Admin grants OK on any camp.
  - Only CampAdmin / Admin can edit `EeSlotCount` and `EeStartDate`.
- `/Camps/{slug}` public detail asserts no EE rendering.
- Audit entries written for grant, revoke, slot count change, EE date change.

## Files

**Modified:**

- `src/Humans.Domain/Entities/CampSeason.cs` — `EeSlotCount`
- `src/Humans.Domain/Entities/CampMember.cs` — `HasEarlyEntry`
- `src/Humans.Domain/Entities/CampSettings.cs` — `EeStartDate`
- `src/Humans.Infrastructure/Data/Configurations/Camps/CampSeasonConfiguration.cs`
- `src/Humans.Infrastructure/Data/Configurations/Camps/CampMemberConfiguration.cs`
- `src/Humans.Infrastructure/Data/Configurations/Camps/CampSettingsConfiguration.cs`
- `src/Humans.Application/Interfaces/Camps/ICampService.cs` — +3 methods
- `src/Humans.Application/Services/Camps/CampService.cs` — implementations + member-removal cascade hook
- `src/Humans.Application/Interfaces/Repositories/ICampRepository.cs` — count-granted query if needed (likely already covered by existing member-fetch)
- `src/Humans.Infrastructure/Repositories/CampRepository.cs`
- `src/Humans.Web/Authorization/Requirements/CampOperationRequirement.cs` — `SetEarlyEntry` value
- `src/Humans.Web/Authorization/Requirements/CampAuthorizationHandler.cs` — map `SetEarlyEntry` to the lead/admin set
- `src/Humans.Web/Controllers/CampController.cs` — POST endpoints for grant / revoke on `/Camps/{slug}/Members/{memberId}/EarlyEntry`
- `src/Humans.Web/Controllers/CampAdminController.cs` — POST for slot-count edit + EE start date
- `src/Humans.Web/Views/Camp/Admin.cshtml` — EE slots column + start-date editor + totals strip
- `src/Humans.Web/Views/Camp/Members.cshtml` — EE toggle column (or wherever the leads-only members table lives)
- `src/Humans.Domain/Enums/AuditAction.cs` — 4 new values
- `tests/Humans.Application.Tests/Architecture/InterfaceMethodBudgetTests.cs` — 51 → 54 with comment
- `memory/architecture/interface-method-budget-ratchet.md` — authorized-exception log entry
- `docs/sections/Camps.md` — invariants, data model, triggers, authz updates

**New:**

- `src/Humans.Infrastructure/Migrations/<timestamp>_AddEarlyEntryFields.cs` (EF-generated)
- `tests/Humans.Application.Tests/Services/Camps/CampServiceEarlyEntryTests.cs`
- `tests/Humans.Web.Tests/Controllers/CampControllerEarlyEntryTests.cs` (if web-layer authz tests are the existing pattern)

## Open questions

None. All design choices locked with Peter on 2026-05-10.
