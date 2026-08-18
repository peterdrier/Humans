# Manage past shifts through the same "Manage" affordance — design

**Date:** 2026-06-24
**Branch:** `feat/manage-past-shifts`
**Section:** Shifts
**Status:** implemented
**Load-bearing:** yes — coordinator-facing roster correctness during/after the live event.

## Problem

A coordinator needs to manage past shifts the same way they manage current/future
shifts — to correct the rota when plans change, people arrive early or late, or someone
covered a slot they were not assigned to. Today the department shift admin page
(`ShiftAdmin/Index.cshtml`) presents past and future shifts through **two different,
divergent affordances**:

- **Future shift:** a **"Manage (N)"** button → collapse listing confirmed signups, each
  with a **Remove** button. Plus a **Voluntell** button.
- **Past shift:** the "Manage" button is hidden (`!isPast`); instead a separately-styled
  **"Signups (N)"** button → collapse listing confirmed/no-show/bailed signups, with
  **Remove**, **Mark No-Show**, and **Bail Range** on confirmed rows. Plus a **Voluntell**
  button.

Because past shifts use a differently-labelled, differently-laid-out panel, coordinators do
not recognise it as the same management surface — it reads as "you can't manage past
rotas". Reassigning a human after the fact (Remove from shift A + Voluntell onto shift B)
is already physically possible on past shifts, but the inconsistent UI hides that.

### Relationship to the prior design

The [voluntell-past-shifts design (2026-06-16)](2026-06-16-voluntell-past-shifts-design.md)
deliberately **kept the panels separate**: it added Voluntell to past shifts but chose not
to touch the future-only "Manage" control because doing so "would duplicate the Remove
buttons that the 601 panel already provides". That reasoning held *while the past panel was
being kept alongside the future one*. This design takes the next step the prior one stopped
short of: **collapse both panels into one**, so there is exactly one Remove path and no
duplication. This is the "fix it right" version (per Peter's hard rules), not a relabel.

## Goal

A coordinator (or dept coordinator) with `CanApproveSignups` manages a past shift through
the **same "Manage (N)" button and panel** as a current/future shift. The action set inside
the panel adapts to timing: **Remove** is always available; **Mark No-Show** and **Bail
Range** appear only when the shift is past (these are post-shift corrections — `MarkNoShow`
is hard-guarded post-shift in the service at ~line 455).

## Non-goals

- **No new backend functionality.** No new "Move/Reassign" command. Reassigning stays the
  existing two-step Remove + Voluntell, both already past-safe at the service layer. This is
  a single-view change.
- No change to **self-signup** (humans still cannot self-sign-up for past shifts — a
  browsing-window property, unchanged here).
- No change to the **Voluntell** button/panel (already shown for all shifts since the prior
  design), to **Edit/Delete** (gated by `CanManageShifts`), to capacity/overlap invariants,
  or to schema. No migration.

## Design

### UI — `src/Humans.Web/Views/ShiftAdmin/Index.cshtml`

The view computes `isPast = now > shift.GetAbsoluteEnd(EventSettings)` (~line 382). Three
sites change; everything else is untouched.

1. **Manage button (~line 469).** Currently `@if (Model.CanApproveSignups && !isPast)`.
   **Drop the `!isPast` term** so the **Manage (N)** button renders for past shifts too.
   Gating: a future shift shows the button when there is ≥1 **confirmed** signup; a past
   shift shows it when there is ≥1 signup of **any** status (confirmed, no-show, or bailed),
   so a past shift whose entire roster no-showed/bailed still exposes its history — nothing
   becomes unreachable. The Voluntell button alongside it is already ungated and stays as-is.

2. **Manage panel body (~line 570).** Currently the `!isPast` panel renders confirmed
   signups with **Remove** only. Generalise it to render for **both past and future**
   (drop `!isPast`), and inside each confirmed row:
   - **Remove** — always (existing `RemoveSignup` form).
   - **Mark No-Show** + **Bail Range** — wrapped in `@if (isPast)` (existing `MarkNoShow` /
     `BailRange` forms moved over from the old Signups panel).
   - When `isPast`, also show read-only **No-Show / Bailed** history rows (as the old
     Signups panel did) so no information is lost; future shifts simply have none.

3. **Old past-only "Signups" panel (~lines 604–660).** **Removed** — its button row and
   collapse are folded into the unified Manage panel above. This deletes the divergent code
   path rather than leaving two.

The **count** on the Manage button stays the confirmed-signup count (actionable rows);
No-Show/Bailed history is shown inside the panel but not counted, matching the future
panel's existing semantics. When the confirmed count is **zero** — only possible on a past
shift surfaced solely for its no-show/bailed history — the numeral is omitted entirely and
the button reads plain **"Manage"** rather than a misleading **"Manage (0)"** that opens to
a non-empty panel.

### Localization — exempt (no resx changes)

The ShiftAdmin Index page is a coordinator/admin view, exempt from i18n per
[`memory/code/localization-admin-exempt.md`]. Strings stay hardcoded English ("Manage",
"Remove", "Mark No-Show", "Bail Range"). No locale files change.

### Invariant doc — `docs/sections/Shifts.md`

Update the invariant at ~line 232. It currently reads that past shifts expose the Voluntell
control and "the future-only **Manage** control is replaced by the existing past-signups
panel". Reword to: past shifts expose the **same Manage control** as future shifts;
**Mark No-Show** and **Bail Range** appear in that panel only when the shift is past.

## Affected files

| File | Change |
|------|--------|
| `src/Humans.Web/Views/ShiftAdmin/Index.cshtml` | Ungate Manage button (~469) and panel body (~570) from `!isPast`; add `isPast`-only No-Show/Bail-Range actions + history rows into the panel; delete the old `isPast` "Signups" panel (~604–660). |
| `docs/sections/Shifts.md` | Reword invariant (~232): past shifts use the unified Manage control. |
| `docs/superpowers/specs/2026-06-16-voluntell-past-shifts-design.md` | (Optional) add a one-line "superseded-in-part by 2026-06-24" pointer. |

No service, controller, repository, DTO, or locale changes. No new public surface (reuse-first).

## Testing

View-template gating is throwaway-grade (no brittle render test, consistent with the prior
design's stance). Verification is manual:

1. As a coordinator, open `/Teams/{slug}/Shifts` with both a past and a future shift that
   have confirmed signups.
2. Both show a **Manage (N)** button opening a panel with the confirmed humans + **Remove**.
3. The **past** shift's panel additionally shows **Mark No-Show** and **Bail Range** on
   confirmed rows, and lists any No-Show/Bailed humans as history.
4. The **future** shift's panel shows neither No-Show/Bail nor history.
5. The separate "Signups (N)" button no longer appears on past shifts.
6. Reassign flow: Remove a human from a past shift, then Voluntell them onto another past
   shift — both actions reachable from the unified affordance.

Backend behaviour (capacity ceiling, overlap, post-shift No-Show guard, notification
suppression on past voluntell) is unchanged and already covered by the prior design's tests.

## Open questions

None blocking.
