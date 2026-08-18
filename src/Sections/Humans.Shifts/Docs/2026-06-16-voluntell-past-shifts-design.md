# Voluntell for past shifts — design

**Date:** 2026-06-16
**Branch:** `feat/voluntell-past-shifts`
**Section:** Shifts
**Status:** design approved, pre-implementation
**Load-bearing:** yes — coordinator-facing roster correctness during the live event.

## Problem

During the event, the rota changes on the ground. A coordinator needs to record who
actually covered a shift after the fact — e.g. someone swapped in, or a build-week shift
was reassigned. Today the coordinator cannot do this through the UI: in the department
shift admin page, the **Voluntell** and **Manage** (unassign) controls are hidden for any
shift whose end time has already passed.

The service layer already permits it — `ShiftSignupService.VoluntellAsync` /
`VoluntellRangeAsync` have no past-shift guard. The block is purely in the view
(`ShiftAdmin/Index.cshtml`), and the **Voluntell Range** UI already exposes past days. So
this is a targeted "surface the existing capability in the per-shift UI" change, plus one
notification-correctness fix, not new domain logic.

## Goal

A coordinator (or dept coordinator) with `CanApproveSignups` can assign a human to, and
manage confirmed signups on, a shift that has already ended — to keep the historical rota
accurate — without spamming the human with a now-meaningless "you've been assigned"
notification.

## Non-goals

- No change to volunteer **self-signup**: humans still cannot self-sign-up for past shifts.
  This is a coordinator backfill capability only. Note: self-signup-for-past is prevented by
  the public browse view + `IsShiftBrowsingOpen` being closed, **not** by a hard `now > end`
  guard in `SignUpAsync` (which has none). This change touches only the ShiftAdmin view, which
  never offers self-signup, so the property is preserved — but it is a browsing-window
  property, not a service invariant. (`MarkNoShow` *is* hard-guarded post-shift at ~line 455,
  consistent with treating past shifts as a blessed coordinator-mutation surface.)
- No ability to **create or edit** shifts on past days — only assign/manage humans on
  existing shifts.
- No change to event/strike vs build classification, capacity model, or schema. No migration.
- Out of scope: the set-up-signup-freeze feature (`EarlyEntryClose`) — tracked separately,
  intentionally not bundled here.

## Design

### 1. UI — `src/Humans.Web/Views/ShiftAdmin/Index.cshtml`

The view computes `isPast = now > shift.GetAbsoluteEnd(EventSettings)` (line ~382). The
**only** capability missing for past shifts is **Voluntell** — past shifts *already* have a
full management surface: the `@if (isPast && shiftSignups.Count > 0 && Model.CanApproveSignups)`
panel at lines ~601–661 renders a "Signups (N)" collapse with **Remove**, **Mark No-Show**,
and **Bail Range** for each confirmed signup. So this design **only adds Voluntell to past
shifts**; it deliberately does **not** touch the future-only "Manage" controls, which would
otherwise duplicate the Remove buttons that the 601 panel already provides.

Three `!isPast` sites exist; we change the Voluntell ones and leave the Manage ones alone:

- **line ~469** — `@if (Model.CanApproveSignups && !isPast)` wraps *both* the "Manage (N)"
  button *and* the **Voluntell** button. **Split it:** keep the **Manage (N)** button gated
  `!isPast` (past shifts use the 601 panel instead), and render the **Voluntell** button under
  `@if (Model.CanApproveSignups)` with no `isPast` term.
- **line ~567** — `@if (!isPast && Model.CanApproveSignups)`, the Manage collapse body.
  **Leave unchanged** (future-only; its past equivalent is the 601 panel).
- **line ~662** — `@if (Model.CanApproveSignups && !isPast)`, the Voluntell collapse body
  (volunteer search + assign form). **Relax** to `@if (Model.CanApproveSignups)`.

Leave untouched: the `Model.CanManageShifts` edit/delete controls, and the 601–661 past
signups panel (it already covers past management).

Add a small muted **"past"** badge on past-shift rows so a coordinator sees at a glance that
they are correcting history rather than staffing an upcoming shift. It must sit alongside,
not overlap, the existing `phaseBadge` (line ~416). This is additive affordance, not
load-bearing. Per **i18n exemption (§3)** the badge is a plain hardcoded English string.

### 2. Service — `src/Humans.Application/Services/Shifts/ShiftSignupService.cs`

`VoluntellAsync` (and the per-shift dispatch inside `VoluntellRangeAsync`) currently always
fires a volunteer-facing notification:

> `ShiftAssigned` — "You were assigned to {rota} on day {offset}" → action "View shifts"

For a shift that has already ended this is confusing. Change: **suppress the
volunteer-facing `ShiftAssigned` notification when the shift end is in the past**
(`shift.GetAbsoluteEnd(es) <= now`).

Kept unchanged in all cases (past or future):

- The audit entry `AuditAction.ShiftSignupVoluntold` — always written.
- The coordinator-facing `ShiftSignupChange` notification (to dept coordinators) — always sent.
- The **capacity** ceiling (`confirmedCount >= shift.MaxVolunteers`) and **overlap** check —
  these are correctness invariants, not date gates; a past backfill must not record a
  historically impossible roster (over capacity / double-booked).
- Early-entry view invalidation (`earlyEntryInvalidator.InvalidateUser`) — harmless for past
  build shifts; left as-is.

`VoluntellRangeAsync` (Build/Strike all-day rotas) does **not** dispatch per-shift — it sends
a **single aggregate** `ShiftAssigned` ("You were assigned to {rota} ({N} shifts)", ~line 429).
So "suppress per past shift" does not apply literally. Rule instead: **send the aggregate
notification only if at least one assigned shift ends in the future** (`GetAbsoluteEnd(es) > now`);
if every assigned shift is already past, suppress it. Audit + coordinator change-ping +
capacity/overlap skips stay as-is.

### 3. Localization — exempt (no resx changes)

The ShiftAdmin Index page is a coordinator/admin view (`[Route("Teams/{slug}/Shifts")]`,
gated by department approval/management). Per [`memory/code/localization-admin-exempt.md`],
admin/coordinator-facing views are **exempt** from i18n: do **not** add `@Localizer` keys or
locale `.resx` entries. The page's strings are hardcoded English ("Voluntell", "Manage",
"Remove", …); the new "past" badge follows suit as a plain hardcoded string. **No locale
files change.**

## Affected files

| File | Change |
|------|--------|
| `src/Humans.Web/Views/ShiftAdmin/Index.cshtml` | Split line ~469 (Voluntell button ungated from `isPast`, Manage button stays `!isPast`); relax line ~662 Voluntell body; add hardcoded "past" badge. Lines ~567 and ~601 untouched. |
| `src/Humans.Application/Services/Shifts/ShiftSignupService.cs` | `VoluntellAsync`: suppress `ShiftAssigned` when shift end ≤ now. `VoluntellRangeAsync`: suppress aggregate `ShiftAssigned` when all assigned shifts are past. |
| `Humans.Application.Tests` (Shifts) | Cover past-voluntell notification suppression + invariants still enforced |
| `docs/sections/Shifts.md` | Update invariant (~line 269): voluntell permitted on past shifts; volunteer notification suppressed |

No locale `.resx` changes (admin view i18n exemption, §3).

## Testing

Service-level (xUnit, where the regression risk lives):

1. Voluntell a human onto a **past** shift → signup created `Confirmed`; audit written;
   **no** `ShiftAssigned` notification dispatched; coordinator `ShiftSignupChange` still sent.
2. Voluntell onto a **future** shift → `ShiftAssigned` notification still dispatched
   (no regression).
3. Past shift at capacity → voluntell still fails "at capacity".
4. Past shift overlapping an existing confirmed signup → voluntell still fails overlap.
5. `VoluntellRangeAsync` over an **all-past** range → no `ShiftAssigned` notification; signups
   still created; audit written.
6. `VoluntellRangeAsync` over a range **spanning past and future** days → aggregate
   `ShiftAssigned` notification still dispatched (≥1 future shift present).

UI gating is verified manually (open the shift admin page on a past shift as a coordinator,
confirm Voluntell + Manage render) — view-template gating is throwaway-grade and not worth a
brittle render test.

## Open questions

None blocking. The "past" badge copy is the implementer's call (reuse vs. new key).
