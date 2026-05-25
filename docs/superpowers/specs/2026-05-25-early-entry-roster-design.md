# Early Entry — cross-source roster + ticket-stub self-view

**Date:** 2026-05-25
**Branch:** `early-entry-roster`
**Status:** design
**Builds on:** [`2026-05-10-early-entry-camps-design.md`](2026-05-10-early-entry-camps-design.md) (the deferred "cross-source aggregation" + "self-view" items it lists as out of scope are exactly what this ships)

## Context

Early Entry (EE) — permission to be on-site before gate opens — can now be earned from **two independent sources**:

1. **Camps** — an explicit per-member grant: `CampMember.HasEarlyEntry`. The entry date is the global `CampSettings.EeStartDate`. Camp leads toggle it. (Shipped in the camps EE spec above.)
2. **Shifts** — **derived, nothing stored**: a confirmed signup on a build/setup shift (`Shift.IsEarlyEntry`, i.e. `DayOffset < 0`) earns entry on `firstBuildShiftDay − 1`. If your first build shift is Wednesday, you get EE for Tuesday. Event/strike shifts never count.

Two gaps remain:

- **Self-view** — a human cannot see that they hold EE. Today they learn out-of-band (a lead tells them on WhatsApp).
- **Cross-source roster** — no site-wide view of who holds EE. The org has **per-date on-site limits**, so a person holding EE from *both* sources wastes a scarce slot (they were already getting in via the earlier source). These need to be **flagged for manual reallocation** — usually the camp slot is freed and given to someone else. No auto-resolution.

## The two sources are asymmetric — the design absorbs that

|  | **Camps EE** | **Shifts EE** |
|---|---|---|
| Storage | Stored bool `CampMember.HasEarlyEntry` | Derived; nothing stored |
| Date | Global `CampSettings.EeStartDate` (same for everyone) | `firstBuildShiftDay − 1`, per user |
| Multiplicity | One date per user even across multiple camp memberships (the date is global) | Always collapses to one (earliest build shift) |

A person therefore holds **at most two distinct EE dates** — one from camps, one from shifts. The ticket stub shows the **earliest**; the roster shows the **per-source breakdown** and flags anyone with more than one source.

## Pattern — mirror the GDPR contributor fan-out

This is **not** an `I<Section>ServiceRead` cross-section read (that pattern is for one section reading another's data). It is a **contributor fan-out**, structurally identical to GDPR export (`IUserDataContributor` + `GdprExportService`): each section that owns EE data implements one read-only interface; an orchestrator injects `IEnumerable<>` of them and assembles the result. No section reaches into another's tables.

### New contract (`Humans.Application.Interfaces.EarlyEntry`)

```csharp
public interface IEarlyEntryProvider
{
    Task<IReadOnlyList<EarlyEntryGrant>> GetEarlyEntriesAsync(CancellationToken ct);
}

public sealed record EarlyEntryGrant(Guid UserId, LocalDate EntryDate, string Source);
```

`Source` is a display label: `"Camp: Flaming Lotus"` or `"Shift: Flags"`. Read-only, returns the whole active event's grants (matches the GDPR contributor shape — small data at ~500 users; orchestrator filters per-user when needed).

### Implementers — existing services, no new section services

Exactly like GDPR, where `AuditLogService` / `BudgetService` / `ShiftSignupService` each implement `IUserDataContributor` and DI registers the concrete under the interface.

**Camps — `CampService`** implements `IEarlyEntryProvider`. It already owns `CampMember` / `CampSeason` / `CampSettings`. Returns one grant per non-removed `CampMember` with `HasEarlyEntry == true`, `EntryDate = CampSettings.EeStartDate`, `Source = "Camp: {campName}"`. If `EeStartDate` is null (unset), Camps contributes nothing. No new repository surface beyond the member + camp-name read it already performs for the leads roster.

**Shifts — `VolunteerTrackingExportService`** implements `IEarlyEntryProvider`. **No new type, no new query.** This service already:
- fetches confirmed shifts via `IVolunteerTrackingRepository.GetConfirmedShiftsInRangeAsync(eventSettingsId, start, end, departmentId, ct)`,
- computes per-user earliest shift day (`ComputeFirstShiftDay`),
- resolves team names.

The only refactor: extract its existing "userId → first-shift-day" step (currently inline in `BuildAsync` + the `ComputeFirstShiftDay` static) into a shared private method so both `BuildAsync` and `GetEarlyEntriesAsync` call it. `GetEarlyEntriesAsync`:
- resolves the active `EventSettings`,
- calls the **same** repo method with range `[GateOpeningDate.PlusDays(BuildStartOffset) .. GateOpeningDate.PlusDays(-1)]` and `departmentId: null` — so only build/setup shifts are ever fetched; event/strike are never touched,
- emits `EntryDate = firstBuildShiftDay.PlusDays(-1)`, `Source = "Shift: {team of that earliest build shift}"`.

Because the fetch window ends at gate − 1, every derived date is inherently before gate — no extra "is it really early" filter needed.

### Orchestrator (`Humans.Application.Services.EarlyEntry`)

`EarlyEntryService` mirrors `GdprExportService`: injects `IEnumerable<IEarlyEntryProvider>`, fans out **sequentially** (the providers share the scoped `HumansDbContext`, which is not thread-safe — same reason GDPR is sequential, not `Task.WhenAll`). No repository — it is an orchestrator per the hard rules. No tables; a cross-cutting read orchestrator like GDPR, **not** a new vertical section.

```csharp
public interface IEarlyEntryService : IApplicationService
{
    Task<IReadOnlyList<EarlyEntryRosterRow>> GetRosterAsync(CancellationToken ct);
    Task<UserEarlyEntry?> GetForUserAsync(Guid userId, CancellationToken ct);
}

// one row per user, grouped from all providers
public sealed record EarlyEntryRosterRow(
    Guid UserId,
    LocalDate EarliestEntryDate,
    IReadOnlyList<string> Sources,   // ["Camp: Flaming Lotus", "Shift: Flags"]
    bool HasMultiple);               // Sources.Count > 1 → wasted-slot flag

public sealed record UserEarlyEntry(LocalDate EarliestEntryDate, IReadOnlyList<string> Sources);
```

- `GetRosterAsync` — concat all provider results, group by `UserId`, `EarliestEntryDate = min(EntryDate)`, `Sources` = distinct labels, `HasMultiple = Sources.Count > 1`. **Pass-through (uncached)** — admin page, infrequent, and reallocation decisions must see live data.
- `GetForUserAsync` — same fan-out, filtered to one user (fine at ~500 users per the scale guidance); returns null when the user holds no EE. **Cached** (see below) — hit on every `/Profile/Me` render.

### Caching decorator (`Humans.Infrastructure.Services.EarlyEntry`)

`CachingEarlyEntryService` is a Singleton decorator over `IEarlyEntryService`, following §15d: inherits `TrackedCache<Guid, UserEarlyEntry>("EarlyEntry.UserEarlyEntry", warmOnStartup: false, logger)`. Per-user `GetForUserAsync` results are cached by `UserId`; **no startup warmup** (per Peter — the data is cold-loaded on first stub render). `GetRosterAsync` delegates straight to the inner service. The inner `EarlyEntryService` is registered keyed under `CachingEarlyEntryService.InnerServiceKey` (§15c).

`UserEarlyEntry?` is nullable — a user with no EE must cache the *negative* (a sentinel or `TrackedCache` null-marker) so repeat renders for the ~majority with no EE don't re-fan-out every time.

### Invalidation — §15e one-way signal (the derived-data wrinkle)

EE is derived from camp grants and shift signups, so a `TrackedCache` entry would otherwise stay stale until app restart — meaning a human who *just* earned EE would never see it. The decorator exposes a one-method invalidator per §15e:

```csharp
public interface IEarlyEntryInvalidator   // Humans.Application.Interfaces.EarlyEntry
{
    void InvalidateUser(Guid userId);
}
```

Implemented by `CachingEarlyEntryService` (same Singleton instance as the read interface — §15e CRITICAL). Injected and called by the two write paths that change a user's EE state:

- **Camps** — `CampService.SetEarlyEntryAsync` (grant/revoke) and the member-removal cascade that clears `HasEarlyEntry`.
- **Shifts** — `ShiftSignupService` confirm / bail (a build-shift signup changing alters the derived date). Evicting one dict entry is cheap even on this hotter path.

This is the sanctioned cross-section signal (external section injects the invalidator when its writes make the cached view stale), not a cross-section read — no call-graph loop.

### DI

In each section's `*SectionExtensions`, register the concrete service under the new interface — copy the GDPR registration shape:

```csharp
services.AddScoped<IEarlyEntryProvider>(sp => sp.GetRequiredService<CampCampService>());
services.AddScoped<IEarlyEntryProvider>(sp => sp.GetRequiredService<VolunteerTrackingExportService>());
```

Orchestrator registered in its own extension (mirror `GdprSectionExtensions`).

## Consumers

### Ticket stub self-view (requirement 1)

`TicketStubInfo` gains an optional `LocalDate? EarlyEntryDate`. It is populated **only when the stub is the viewer's own ticket** — the stub also renders in the transfer wizard (showing a ticket being handed to someone else), where EE must not leak. The builder of the viewer's own stub (the `/Profile/Me` ticket card path) calls `IEarlyEntryService.GetForUserAsync(viewerUserId, ct)` and threads the earliest date in. When set, the stub renders an "Early entry — {date}" line; when null, nothing changes.

### Roster (requirement 2)

A new admin page at **`/Shifts/Admin/EarlyEntry`** (a temporary home — a better one will be found later). Reuses the existing volunteer-tracking authorization gate (same coordinator/admin audience as the export, which lives in the same area). Renders the `GetRosterAsync` rows: human name (playa name, via `IUserServiceRead` like the export does), earliest entry date, source list. **Rows with `HasMultiple == true` are visually flagged** so a coordinator can free the redundant (usually camp) slot. No write actions on this page in v1.

## Out of scope (deferred)

- **Auto-reallocation of wasted slots.** v1 only flags; humans resolve.
- **Per-date capacity display / enforcement on the roster.** The per-date site limit is the *reason* for the flag, not something this page renders or enforces.
- **A permanent roster home.** `/Shifts/Admin/EarlyEntry` is explicitly interim.
- **A standalone `docs/sections/EarlyEntry.md`.** Like GDPR, EE is a cross-cutting orchestrator, not a vertical section.
- **Notifications on grant.** Unchanged from the camps spec.

## Tests

- **Orchestrator aggregation:** two providers returning grants for the same user → one roster row, `EarliestEntryDate == min`, both sources listed, `HasMultiple == true`.
- **Single source:** one provider → `HasMultiple == false`.
- **Earliest wins:** camp date later than shift date → `EarliestEntryDate` is the shift date.
- **`GetForUserAsync`:** returns null for a user with no EE; returns earliest + sources otherwise.
- **Camps provider:** non-removed `HasEarlyEntry` members emitted with global `EeStartDate`; removed/non-granted excluded; null `EeStartDate` → empty.
- **Shifts provider:** earliest build shift − 1 emitted; user with only event/strike shifts → no grant (build window excludes them); source carries the earliest build shift's team.
- **Shared first-shift helper:** `BuildAsync` output unchanged after the extraction (regression guard on the export).
- **Ticket stub:** EE line renders on the viewer's own stub when set; absent on a transfer-wizard stub for another person; absent when the viewer holds no EE.
- **Roster authz:** coordinator/admin OK; non-privileged 403.
- **Caching decorator:** second `GetForUserAsync` is a cache hit (no second fan-out); negative result (no EE) is cached too; `GetRosterAsync` always delegates to inner.
- **Invalidation:** `InvalidateUser` evicts the entry; a grant via `SetEarlyEntryAsync` (and a build-shift confirm/bail) makes the next `GetForUserAsync` reflect the change; the invalidator and read interface resolve to the same Singleton.

## Files

**New:**

- `src/Humans.Application/Interfaces/EarlyEntry/IEarlyEntryProvider.cs` — interface + `EarlyEntryGrant`
- `src/Humans.Application/Interfaces/EarlyEntry/IEarlyEntryService.cs` — orchestrator interface + `EarlyEntryRosterRow` / `UserEarlyEntry`
- `src/Humans.Application/Interfaces/EarlyEntry/IEarlyEntryInvalidator.cs` — §15e one-way invalidator
- `src/Humans.Application/Services/EarlyEntry/EarlyEntryService.cs` — orchestrator (fan-out)
- `src/Humans.Infrastructure/Services/EarlyEntry/CachingEarlyEntryService.cs` — Singleton decorator (`TrackedCache`, no warmup) + invalidator
- `src/Humans.Web/Extensions/Sections/EarlyEntrySectionExtensions.cs` — orchestrator + decorator DI (mirror `GdprSectionExtensions` + a caching section's keyed-inner registration)
- `src/Humans.Web/Controllers/` — roster action (in the existing Shifts admin controller that owns volunteer tracking, if present; else a thin new one)
- `src/Humans.Web/Views/.../EarlyEntry.cshtml` — roster page
- `tests/Humans.Application.Tests/Services/EarlyEntry/EarlyEntryServiceTests.cs`
- provider tests under the Camps + Shifts test folders

**Modified:**

- `src/Humans.Application/Services/Camps/CampService.cs` — implement `IEarlyEntryProvider`; inject + call `IEarlyEntryInvalidator` from `SetEarlyEntryAsync` + member-removal cascade
- `src/Humans.Application/Services/Shifts/VolunteerTrackingExportService.cs` — implement `IEarlyEntryProvider`; extract shared first-shift-day method
- `src/Humans.Application/Services/Shifts/ShiftSignupService.cs` — inject + call `IEarlyEntryInvalidator` on confirm / bail of build-shift signups
- `src/Humans.Web/Extensions/Sections/CampsSectionExtensions.cs` — register `IEarlyEntryProvider`
- `src/Humans.Web/Extensions/Sections/ShiftsSectionExtensions.cs` (or wherever `VolunteerTrackingExportService` is registered) — register `IEarlyEntryProvider`
- `src/Humans.Application/DTOs/...TicketStubInfo` — add optional `EarlyEntryDate`
- the `/Profile/Me` own-ticket stub builder — populate `EarlyEntryDate` via `IEarlyEntryService.GetForUserAsync`
- `src/Humans.Web/Views/Shared/Components/TicketStub/Default.cshtml` — render the EE line when set
- caching: if `CampService` is fronted by `CachingCampService`, confirm the `IEarlyEntryProvider` registration resolves the concrete (caching of this read is not required in v1)
- `docs/sections/Shifts.md` / `docs/sections/Camps.md` — note the EE contributor implementation

## Open questions

None. Pattern (GDPR-style contributor fan-out), Shifts reuse (`VolunteerTrackingExportService`, no new type/query), source-label shape, double-holder flag, and interim roster location all locked with Peter on 2026-05-25.
