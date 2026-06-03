# Barrio Shift Obligations — track & prompt per-barrio shifts owed to function teams

**Status:** Draft for review (no code changed yet)
**Date:** 2026-06-04
**Author:** Frank + Claude (design dialogue)
**Branch / worktree:** `barrio-shift-obligations` (`.worktrees/barrio-shift-obligations`)

## Problem

Each barrio (camp) is **obligated to contribute a number of shifts to certain
function teams** in exchange for a service — the canonical case: do *N* Power
shifts or you don't get electricity. The same pattern applies to other function
teams (Water-truck, Shit-ninja, Auger). Today there is **no way to track,
check, or prompt** this: a function-team coordinator (e.g. Cupcake on Power)
cannot see which barrios have met their obligation, and has no lever to nudge
the ones that haven't.

The fix: a **per-barrio × per-function compliance view** (done / required), with
a one-click **reminder email** to the barrio's leads + that barrio's function
lead. This is **load-bearing** for the data shape (two new tables + one new
cross-section read contract) and **prototype-grade** for UI polish.

**Out of scope (Frank's call — separate PR):** the dedicated power-rota page
with high-visibility unfilled days. This spec only *links out* to each
function team's existing rota page.

## Existing machinery (audited, not assumed)

- **Barrios = Camps.** `Camp` → `CampSeason` (per-year) → `CampMember`
  (`UserId` scalar, per season; `Status` Pending/Active/Removed). Membership is
  **sparse** — only humans who actively joined have rows. `CampSeasonInfo`
  exposes `LeadUserIds`; leads are `CampMember`s with a `CampRoleAssignment` to
  the `SpecialRole = Lead` role definition.
  (`src/Humans.Domain/Entities/Camp*.cs`, `CampService.CreateCampSeasonInfo`.)
- **Function teams already exist as `Team`s.** "Power" is a `Team` and owns all
  its shifts. Identify a team via `ITeamServiceRead.GetTeamAsync(id)` /
  `GetTeamBySlugAsync(slug)` (budget 5 read interface).
- **Some function camp-roles already exist; others don't.**
  `DevelopmentCampRoleSeeder` defines `power` and `shit-ninja` (alongside
  `consent-lead`, `lnt`, `build-lead`) — but **no `water` or `auger` camp
  roles**, and the corresponding function *Teams* may also not exist yet. A
  barrio's "power lead" = the `CampMember` holding that camp's `power` role for
  the season; for a function whose camp-role has no holder (or doesn't exist),
  reminder recipients fall back to **leads only** (Decision 7). So configuring a
  new function is purely admin data entry — see the Functions sub-page.
- **Shifts data:** `ShiftSignup (UserId → Shift)`, `Shift → Rota`,
  `Rota.TeamId`. "Confirmed Power shifts for human X" = signups with
  `Status = Confirmed` on shifts whose rota's `TeamId` is Power. `EventSettings`
  (the active event, `IsActive`) is **Shifts-owned**; only Shifts knows it.
- **Cross-section read surfaces today:** Shifts has **no `IShiftServiceRead`**;
  cross-section callers use the read+write `IShiftManagementService` or narrower
  reads (`IShiftView`, `IVolunteerTrackingServiceRead`). None expose
  "confirmed-signup counts grouped by user for a team." (Verified.)
- **Dependency direction (verified):** `Shifts` has **zero** references to
  `Camps` (no `ICampService`/`CampInfo`/`CampSeason` in `Services/Shifts`).
  Therefore a new **`Camps → Shifts`** read edge introduces **no call-graph
  loop**. Camps already depends outward on EarlyEntry today.
- **Email:** `IEmailService.SendAsync(EmailMessage)`; typed bodies are built by
  `IEmailMessageFactory` (one method per message type) + a renderer template.
  Recipient resolution pattern (resolve `UserId`s → `IUserService.GetByIdsAsync`
  → send per human) is established in `RotaCoordinatorMessageService` and
  `CampaignService`.
- **Audit:** `IAuditLogService` + `AuditAction` enum
  (`src/Humans.Domain/Enums/AuditAction.cs`); Camps already writes
  `CampEarlyEntryGranted/Revoked`.
- **Camps admin surface:** `CampAdminController` already hosts
  `/Camps/Admin`, `/Camps/Admin/Roles/{slug}`, `/Camps/Admin/Compliance`
  (camp **role** compliance) under the CampAdmin policy — the natural
  neighbourhood for a **shift** obligation page.

**Negative result (verified):** there is no existing "obligation" / "quota" /
"shifts owed" concept anywhere — this is net-new data.

## Decisions (from design dialogue)

1. **Ownership — Camps owns the feature** (config + override tables +
   orchestration + UI), reading Shifts/Teams via their read interfaces.
   Rejected: Shifts-owned (forces a wrong-direction `Shifts → Camps` edge) and a
   new dedicated section (over-engineered for ~few barrios × 4 functions at this
   scale).
2. **General from the start** — obligations are configurable per function team,
   not Power-hardcoded. Power is the only fully-wired function today (team +
   `power` camp-role exist); Water/Shit-ninja/Auger are added as admin config
   when their teams/camp-roles exist (columns resolve leads-only/empty until
   then).
3. **Obligation = X shifts total per barrio**, with a **global default per
   function and a sparse per-barrio override**.
4. **Cross-section read shape — team-scoped count map (#1).** Shifts owns the
   `signup → shift → rota → team` join and returns `{ userId : count }`; Camps
   intersects with barrio membership and sums. Resolves the **active event
   internally** so Camps passes only a `teamId`.
5. **Interface placement (b)** — introduce a **minimal `IShiftServiceRead`** in
   the Shifts section carrying *only* this method (seed of the read/write split;
   migrating existing cross-section callers off `IShiftManagementService` is
   **out of scope** and remains the `section-read-split` skill's job).
6. **Access — CampAdmin / Admin only.** Cupcake uses it via a CampAdmin grant.
   (Considered scoped per-function-coordinator access; deferred to keep auth
   simple — noted as follow-up.)
7. **Function lead mapping** — each obligation function carries a
   `CampRoleSlug`. Reminder recipients = barrio **leads + the holder of that
   camp role**; if no role-holder, leads only.
8. **Email granularity** — per-barrio "Remind" **and** per-function "Remind all
   non-compliant".
9. **Membership-gap handling** — show computed counts as-is, but **flag** any
   cell where a barrio's active member count `< required` (can't be met as
   joined). Surfaces the sparse-membership limitation rather than hiding it.
10. **Function applicability — a barrio only owes a function it actually
    consumes.** Power: only barrios **on the event grid** owe power shifts; a
    self-powered barrio doesn't. Modelled generally (not Power-hardcoded) via an
    `Applicability` flag on the function config. The grid signal already exists:
    `CampSeason.ElectricalGrid` (enum `Yellow / Red / Norg / OwnSupply /
    Unknown`, nullable). For the `ElectricalGridConnected` applicability,
    **obligated = `{Yellow, Red, Norg}`**; **`OwnSupply`** = self-powered and
    **`Unknown`/null** = unclassified are excluded from the obligation and
    listed **below the matrix** (so they're visible, not silently dropped, and
    the admin can spot barrios that still need a grid classification). Camps owns
    `ElectricalGrid`, so this filter stays inside the Camps section.

## Data model

Two new **Camps-owned** tables. Per Peter's hard rule (one table → one
repository), both are owned by a new **`ShiftObligationRepository`** in the
Camps section (sibling to `CampRepository`; final fold-vs-separate decision in
the plan). No concurrency tokens (`memory/architecture/no-concurrency-tokens.md`).

### `shift_obligations` — global function config (one row per function team)

| Column | Type | Notes |
|---|---|---|
| `Id` | `Guid` | PK |
| `TeamId` | `Guid` | the function team (Power, …). No EF nav — resolve name via `ITeamServiceRead`. **Unique.** |
| `CampRoleSlug` | `string` (≤64) | camp role used to find the barrio's function lead (`power`, …) |
| `Applicability` | enum `ObligationApplicability` | `AllBarrios` (default) or `ElectricalGridConnected` (only barrios with `CampSeason.ElectricalGrid` in `{Yellow,Red,Norg}`). Power = `ElectricalGridConnected`. New Domain enum. |
| `DefaultRequiredShiftCount` | `int` | default obligation per barrio (≥0) |
| `IsActive` | `bool` | inactive functions drop out of the matrix. **Migration store default `true`** (sentinel-safe: `IsRequired()`, not `HasDefaultValue`) |
| `SortOrder` | `int` | column order in the matrix |
| `CreatedAt` / `UpdatedAt` | `Instant` / `Instant?` | NodaTime; audit |

### `camp_season_shift_obligations` — sparse per-barrio override

| Column | Type | Notes |
|---|---|---|
| `Id` | `Guid` | PK |
| `CampSeasonId` | `Guid` | FK → `camp_seasons`, `OnDelete(Cascade)` |
| `ShiftObligationId` | `Guid` | FK → `shift_obligations`, `OnDelete(Cascade)` |
| `RequiredShiftCount` | `int` | override (≥0). **A row exists only when overridden.** |

- Unique `(CampSeasonId, ShiftObligationId)`. Indexes on both FKs.
- Effective requirement = override `RequiredShiftCount` ?? function
  `DefaultRequiredShiftCount`.

**Migration:** one EF migration adds both tables (subject to the EF
migration-review gate). Creating function rows (Power first; others as their
teams/camp-roles come online) is a **data/ops step**, not schema — done via the
admin-managed Functions config page (below), not the migration.

## Components & flow

### 1. Cross-section read contract ⚠️ (new public surface — Peter approval)

New **`IShiftServiceRead`** in `src/Humans.Application/Interfaces/Shifts/`,
implemented by the existing Shifts service (and pass-through on its caching
decorator, which must delegate to inner — hard rule):

```csharp
public interface IShiftServiceRead : IApplicationService
{
    /// Confirmed signup counts grouped by user, for shifts under the given
    /// team's rotas, in the currently-active event. Resolves the active event
    /// internally. Users with zero confirmed signups are absent from the map.
    Task<IReadOnlyDictionary<Guid, int>> GetConfirmedSignupCountsByUserForTeamAsync(
        Guid teamId, CancellationToken ct = default);
}
```

- **Reuse audit (why net-new):** no existing read returns team-scoped per-user
  counts; `GetByUserAsync` is per-user and team-blind, and the
  `read-model-enrichment` rule doesn't apply (no canonical shift read DTO
  carries this). One call per active function (≤4) — fine at this scale.
- The join + `Status = Confirmed` filter live **inside** Shifts; no shift/rota
  entity crosses the boundary (only `Guid → int`).
- **Active-event resolution:** route through the existing Shifts active-event
  path (`ShiftRepository.GetActiveEventSettingsAsync`), **not** a fresh
  `EventSettings` read — `EventSettings` carries a `Grandfathered("HUM0025")`
  marker (read by both `ShiftRepository` and `VolunteerTrackingRepository`);
  don't widen it.

### 2. Orchestration — `ShiftObligationService : IApplicationService` (Camps)

- **Config CRUD:** create/update/deactivate `shift_obligations` rows; set/clear
  a barrio's `camp_season_shift_obligations` override. Reads/writes its own repo
  only.
- **Compliance computation** `GetComplianceMatrixAsync(year)`:
  1. Active functions ← `shift_obligations` (`IsActive`, ordered).
  2. Barrios ← active `CampSeason`s for `year` (reuse `ICampServiceRead`
     projections for members/leads + `ElectricalGrid`; **enrich `CampSeasonInfo`**
     with role-holder `UserId`s by slug if not already exposed, per
     read-model-enrichment — preferred over reading the Camps repo twice).
  2a. **Applicability partition per function:** for an `ElectricalGridConnected`
     function, split barrios into **obligated** (`ElectricalGrid ∈
     {Yellow,Red,Norg}`) and **excluded** (`OwnSupply`, `Unknown`, or null).
     Excluded barrios carry no cell value and surface in a separate group.
     `AllBarrios` functions skip the partition.
  3. Per function: `counts = IShiftServiceRead.GetConfirmedSignupCountsByUserForTeamAsync(fn.TeamId)`.
  4. Per (barrio, function): `done = Σ counts[memberUserId]`,
     `required = override ?? default`, `activeMembers = |Active members|`,
     `underMembered = activeMembers < required`.
  Returns a `BarrioObligationMatrix` projection (rows, columns, cells) — no EF
  entities cross into Web.
- **Reminder dispatch** `SendReminderAsync(campSeasonId, shiftObligationId)` and
  `RemindAllNonCompliantAsync(shiftObligationId)`:
  - recipients = season `LeadUserIds` ∪ holder(s) of `fn.CampRoleSlug`
    (fallback: leads only);
  - resolve emails via `IUserService.GetByIdsAsync`; build via new
    `IEmailMessageFactory.BarrioShiftObligationReminder(...)`; send via
    `IEmailService`; write an `AuditAction.BarrioShiftReminderSent` entry per
    send. Bulk = the same per-barrio path over every non-compliant barrio for
    the function.

Compliant with the hard rules: a Camps section service calling **its own repo**
+ other sections' **read interfaces** (`IShiftServiceRead`, `ITeamServiceRead`,
`IUserService`, `IEmailService`). It is **not** a pure orchestrator (it owns
tables), so calling its own repo is allowed.

### 3. Email (new typed message)

- `IEmailMessageFactory.BarrioShiftObligationReminder(recipientEmail,
  recipientName, barrioName, functionName, doneCount, requiredCount,
  rotaUrl, culture?)` + a renderer template. **Category = `VolunteerUpdates`**
  (shift/schedule coordination) — it is default-on but **opt-out-able**, so the
  reminder honours a lead who opted out of volunteer updates. (Rejected
  locked-on `System`/`CampaignCodes`: this is coordination mail, not an
  account/system notice, so suppressing the opt-out isn't justified.)

### 4. UI — `/Camps/Admin/ShiftObligations` (CampAdmin / Admin)

New actions on `CampAdminController` (parse/format only; all logic in the
service), gated by the existing CampAdmin policy:

- `GET /Camps/Admin/ShiftObligations` — **matrix**: rows = barrios (current
  year), columns = active functions. Cell shows `done / required`, colored
  met/unmet; **under-membered cells flagged** (icon + tooltip); the barrio's
  active member count shown on the row. A cell for a function the barrio doesn't
  consume (excluded by applicability) renders as **`—` / N/A**. Each column
  header links to that team's rota page. Below the matrix, a **"Not on the grid"**
  group lists barrios excluded from a grid-connected function (split
  `Own supply` vs `Unclassified — needs grid`), so they're visible and the admin
  can spot ones still needing classification. No reminder action on excluded rows.
- `POST /Camps/Admin/ShiftObligations/Remind` — `{campSeasonId, shiftObligationId}`.
- `POST /Camps/Admin/ShiftObligations/RemindAllNonCompliant` — `{shiftObligationId}`.
- `POST /Camps/Admin/ShiftObligations/SetOverride` — `{campSeasonId,
  shiftObligationId, requiredShiftCount?}` (null clears the override; edited
  inline in the cell).
- **Function config** sub-page `/Camps/Admin/ShiftObligations/Functions`
  (CRUD: team, camp-role slug, default count, active, sort) — how the four
  functions get seeded/maintained without a migration.

### 5. Caching

No new caching layer. The matrix is computed on demand from live reads; at ~few
barrios × ≤4 functions the cost is trivial (scale doctrine: don't optimize).
If the Shifts read needs memoizing later, it follows the section caching-decorator
pattern (§15) — explicitly deferred.

## Cross-cutting

- **GDPR.** No new PII table: `shift_obligations` is config; `camp_season_shift_obligations`
  is a count keyed to a season. The reminder email sends to humans but stores no
  new personal data beyond the existing audit entry (which already records actor
  `UserId`s). No new `IUserDataContributor` / erasure surface required — confirm
  during implementation.
- **User merge.** No per-`UserId` rows in the new tables → **no merge handling
  needed** (overrides are per season, not per human).
- **Terminology.** User-facing strings say "barrios" and "humans" — never
  "camps as users"/"members"/"volunteers". Code keeps `Camp*`/`UserId`
  identifiers (existing convention).
- **Authorization follow-up (noted, not built):** scoped access so a function
  team's coordinator sees/empties only their column. Deferred.

## Testing

- **Pure-logic (mocked repos/reads):** compliance computation — `done` sums only
  a barrio's active members' counts; `required` uses override else default;
  `underMembered` true iff `activeMembers < required`; inactive functions
  excluded; barrios with zero members yield `done = 0` + flag.
- **Reminder recipients:** leads ∪ role-holder; role-holder absent → leads only;
  emails resolved + one audit entry per send; bulk hits exactly the
  non-compliant barrios.
- **Shifts read:** `GetConfirmedSignupCountsByUserForTeamAsync` counts only
  `Confirmed` signups on the team's rotas in the active event; users with none
  are absent. (Real-Postgres integration test for the query translation per the
  test-tier doctrine; pure-logic elsewhere.)
- **Architecture tests:** the two new tables are referenced only by
  `ShiftObligationRepository` (table-ownership invariant); `Camps → Shifts`
  read edge is via `IShiftServiceRead` only.
- Skip browser/UI tests for the admin matrix (prototype-grade UI).

## Out of scope (YAGNI)

- Dedicated power-rota page / unfilled-day visibility (separate PR; we only link
  out).
- Per-function-coordinator scoped access (CampAdmin/Admin only for v1).
- Counting anything other than `Confirmed` signups (pending/bailed excluded).
- Auto-prompting / scheduled reminders (manual trigger only).
- Migrating existing Shifts cross-section callers onto `IShiftServiceRead`
  (seed the interface with one method; full split is the `section-read-split`
  skill's job).

## Change-enforcement notes

- **If you add the migration** → run the EF migration-review gate.
- **If you add `shift_obligations` / `camp_season_shift_obligations`** → add the
  table-ownership architecture test (only `ShiftObligationRepository` references
  them).
- **If you add `IShiftServiceRead`** → it is read-only and implemented by the
  Shifts service; the caching decorator delegates to inner (no repo access).
- **If you add a new user-facing admin page** → run the nav-completeness /
  backlink check (`/Camps/Admin` is the entry point; add the link there).
- **If you add `AuditAction.BarrioShiftReminderSent`** → it is append-only at the
  end of the enum.
- **If a function team or its camp-role slug changes** → it is config in
  `shift_obligations`, edited via the Functions sub-page (no code change).
