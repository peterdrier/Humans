# Shift Summary by Camp — design / handoff

**Date:** 2026-06-06
**Status:** Ready for implementation — Peter-approved shape (one open auth question, below)
**Supersedes:** the obligation-engine approach in **PR #872** (Barrio Shift Obligations).
**Section:** Shifts (read-only view; one new in-section read). **No EF schema change.**

---

## 1. Why this exists (and what it replaces)

PR #872 built a configurable **obligation engine** to answer "are barrios pulling their
weight on shifts?" — two Camps-owned tables, a repository, an orchestration service, a new
cross-section `IShiftServiceRead`, reminder emails, a config UI, and a barrio-lead view —
reaching Camps → {Shifts, Teams, Users, Email, Audit}. Rule-compliant but over-built and
over-coupled (see `2026-06-05-barrio-shift-obligations-rescope-memo.md`).

This replaces all of that with **pure-visibility reporting**: a flat table and a
pivot-by-camp table of shift participation, computed live from data that already exists.
No tables, no required/owed compliance, no reminders, no config page.

**#872 disposition:** close as superseded once this lands. Keep the spec + rescope memo as
the decision record. The one reusable kernel from #872 is its in-section team-set signup
aggregation in `ShiftRepository.Management` (see §6); everything Camps-side is dropped.

---

## 2. What the user sees

Two routes, each rendering **two tables**:

### `GET /Shifts/Summary` — global (all teams, active event)

```
TABLE 1 — flat (one row per human with ≥1 confirmed signup this event)
┌──────────────┬──────────────┬───────┬───────┐
│ Human        │ Camp         │ Hours │ Count │
├──────────────┼──────────────┼───────┼───────┤
│ Ana Ruiz     │ Barrio Fuego │  12.0 │   3   │
│ Beto Sol     │ Barrio Fuego │   6.0 │   2   │
│ Cara Lin     │ (no camp)    │   8.0 │   2   │   ← signed up, no active camp membership
└──────────────┴──────────────┴───────┴───────┘

TABLE 2 — pivot by camp (LEFT JOIN the full active-camp roster → 0-rows for the absent)
┌──────────────┬────────┬───────┬───────┐
│ Camp         │ People │ Hours │ Count │
├──────────────┼────────┼───────┼───────┤
│ Barrio Fuego │   2    │  18.0 │   5   │
│ Camp Mwah    │   0    │   0.0 │   0   │   ← absent from all rotas, surfaced via roster seed
│ Tiny Camp    │   0    │   0.0 │   0   │
│ (no camp)    │   1    │   8.0 │   2   │   ← campless signups bucket
└──────────────┴────────┴───────┴───────┘
```

### `GET /Shifts/Summary/{teamSlug}` — team-scoped (e.g. `/Shifts/Summary/power`)

Same two tables, but every number is scoped to **that team's team-set** (the team plus its
non-promoted sub-teams — the same set the sign-up link shows). Table 1 lists only humans with
a confirmed signup in that team-set; Table 2 still left-joins the **full** active-camp roster,
so a camp with nobody on that team appears as a `0` row.

---

## 3. Locked decisions

| Decision | Value |
|---|---|
| Signup statuses counted | **Confirmed only** (Pending / Refused / Bailed / Cancelled / NoShow excluded) |
| Metrics | **Hours** (Σ `Shift.Duration`) **and Count** (number of confirmed signups), both tables |
| Campless humans | Their own row in **both** tables (Table 1 line + Table 2 `(no camp)` pivot row) |
| Grid / applicability column | **None.** Which camps owe Power is the coordinator's judgment (~5 exempt, held in head) |
| Required / threshold / pass-fail colouring | **None** — pure visibility |
| Global page scope | **All teams** in the active event |
| Team page scope | **Team-set** = team + non-promoted sub-teams (matches the sign-up link) |
| Roster (Table 2 rows) | **All active camp seasons** for the current public year — absent camps = `0` rows |

---

## 4. Data sources — all existing except ONE in-section read

| Value | Source | Status |
|---|---|---|
| Hours + Count per (human, team) | confirmed `ShiftSignup` → `Shift.Duration`; `Shift.RotaId → Rota.TeamId` | **NEW in-section read** (Shifts-owned tables only) |
| Human display names | `IUserServiceRead` | exists |
| Each human's camp (active season) | `ICampServiceRead.GetCampUserInfoAsync(userId)` — cached, no DB hit | exists |
| Active-camp roster (for 0-rows) | `ICampServiceRead.GetCampsForYearAsync(year)` | exists |

The only data crossing a section boundary is from **Camps** (camp label + roster), both via
the existing `ICampServiceRead`. The **shift** data never leaves Shifts — it's read
in-section. So this introduces **zero new cross-section interface** and inverts the #872
coupling to the preferred direction (Shifts → Camps).

---

## 5. Architecture (Shifts-owned, layered)

```
Controller (ShiftsController)              parse route, authorize, sort/format, render
   │  2 actions: Summary(), Summary(teamSlug)
   ▼
Service  (IShiftManagementService or a     resolve active event; get aggregated hours/count
   │      small ShiftSummaryService)        from own repo; resolve names (IUserServiceRead);
   │                                         resolve each human's camp + roster
   │                                         (ICampServiceRead); build flat + pivot rows
   ▼
Repository (ShiftRepository)               NEW read: confirmed-signup (UserId, TeamId, Duration)
   │                                         for the active event, optional team-set filter
   ▼
DbContext                                  reads shift_signups ⋈ shifts ⋈ rotas (all Shifts-owned)
```

**Hard-rules check:**
- Service calls its **own** repo + other sections' **read** interfaces — the normal
  section-service pattern, not a pure orchestrator (so owning a repo call is fine).
- Controller is logic-free beyond parse/authorize/sort/format.
- No EF entity crosses a boundary — only projection records (`SummaryRow`, `CampPivotRow`).
- `ShiftSignups` table stays single-owner (the Shifts repo already owns it).
- **Zero EF schema change, zero new tables, zero new cross-section interface.**

---

## 6. Reuse-first notes

- **Kernel from #872:** `ShiftRepository.Management` (on the `barrio-shift-obligations`
  branch) already resolves a team's **team-set** and aggregates confirmed signups by user.
  That logic is the reusable kernel — **port/adapt it to return hours+count** (it returns
  counts today) and add an all-teams (global) variant. Don't rebuild team-set resolution from
  scratch; reuse whatever the shift browse/links already use.
- **Drop** `IShiftServiceRead` (the cross-section interface #872 added) — unnecessary here,
  because the Summary reads Shifts in-section.
- **Display names / camp lookup / roster** — reuse existing `IUserServiceRead` and
  `ICampServiceRead` methods verbatim; add nothing to either interface.

---

## 7. Authorization

| Route | Policy |
|---|---|
| `GET /Shifts/Summary/{teamSlug}` | that team's coordinator (`IsDeptCoordinatorAsync(user.Id, teamId)`) **OR** `PolicyNames.VolunteerManager` / Admin |
| `GET /Shifts/Summary` (global) | **OPEN — see §10** |

---

## 8. Scale / caching

~500 users, single active event: one aggregation query + N cached camp lookups
(`GetCampUserInfoAsync` is cache-served). **No caching layer and no pagination needed
initially**; add a simple cached decorator later only if the page proves hot. Do not
over-engineer (CLAUDE.md scale guidance).

---

## 9. Out of scope (explicit)

- **Reminder emails** — the one explicitly-requested capability #872 carried. Can return
  later as a standalone "resolve leads → send", independent of any tables.
- **Per-function consolidated matrix** — the global page's total-hours-per-camp already
  serves the oversight need; no column-per-function grid.
- Required/owed compliance, per-barrio overrides, grid-exemption config, barrio-lead
  self-service view — all dropped.

---

## 10. Build sequence

1. **Repo** — `ShiftRepository`: new read returning confirmed-signup `(UserId, TeamId,
   Duration)` (or pre-aggregated `(UserId, TeamId, Hours, Count)`) for the active event, with
   an optional team-set filter. Tests: confirmed-only filter, team-set scoping, empty event.
2. **Service** — `BuildSummaryAsync(string? teamSlug)` → flat rows + pivot rows
   (roster left-join, campless bucket). Unit tests: campless human appears in both tables;
   absent camp → `0` pivot row; team-set scoping; hours = Σ duration; count = # signups.
3. **Controller** — `ShiftsController`: `Summary()` + `Summary(teamSlug)`; authorize per §7;
   sorting/formatting (default Table 2 by Hours desc, `(no camp)` last).
4. **Views** — two tables per page; from the global page, link each team to its
   `/Shifts/Summary/{slug}` page.
5. **Architecture test** — Summary stays Shifts-owned: no new cross-section interface;
   Camps reached only via `ICampServiceRead`; no Camps/Teams/Users repository injected.

---

## 11. Open question for Peter (1)

**Global `/Shifts/Summary` audience:** `PolicyNames.VolunteerManager` + Admin only (treat the
all-camps engagement picture as oversight scope)? Or also visible to **any** department
coordinator (so a Power coordinator can see the whole-event camp breakdown, not just Power)?
The team page (§7) is unambiguous — that team's coordinator + managers. Only the global page's
breadth is undecided.
