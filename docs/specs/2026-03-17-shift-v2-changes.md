# Shift Management v2 — Change List

**March 17, 2026 | Based on volunteer coordinator feedback**

Source: Conversation with Frank (volunteer coordinator) reviewing VIM capabilities vs current Humans implementation.

Designer reference: `https://finder-vector-52685742.figma.site/`

---

## Context

Slices 1–3 of the shift management system shipped March 17, 2026. The volunteer coordinator reviewed the system and identified gaps — primarily around build/strike period handling, which works fundamentally differently from event-time shifts. The VIM system had a "Projects" concept for this that was explicitly deferred during the initial design. This change list addresses that gap and several other refinements.

---

## Schema Changes

| # | Change | Approach |
|---|--------|----------|
| 1 | **All-day shifts for build/strike** | Add `IsAllDay` bool to Shift entity. All-day shifts have no meaningful StartTime/Duration — store conventional values (00:00, 24h) but UI ignores them. |
| 2 | **Remove shift title** | Remove `Title` from Shift. Shifts are identified by rota name + time (or date for all-day). Migration drops the column. Existing code referencing `shift.Title` switches to `shift.Rota.Name`. |
| 3 | **Add practical info to Rota** | Add `PracticalInfo` (string?, max 2000) to Rota entity. Meeting point, instructions, what to bring. |
| 4 | **Add period to Rota** | Add `Period` (RotaPeriod enum: Build/Event/Strike) to Rota entity. Stored as string. Drives creation UX (all-day vs time-slotted) and signup UX (date-range vs individual). |
| 5 | **Role period tag** | Add `Period` (RolePeriod enum: YearRound/Build/Event/Strike) to `TeamRoleDefinition`. Default YearRound. |
| 6 | **General volunteer pool** | Add `GeneralAvailability` entity — UserId, EventSettingsId, available day offsets (jsonb list of ints). Volunteer registers availability without committing to a team. |
| 7 | **Remove deactivation** | Remove `IsActive` from Rota and Shift. Delete replaces deactivate (still blocked if confirmed signups exist). Remove the GC job that cancels signups on deactivated shifts. |

## UX Changes

| # | Change | Approach |
|---|--------|----------|
| 8 | **Build/strike rota creation** | When creating a rota with Period=Build or Strike, show a per-day staffing grid: each day in the period range with min/max inputs. Auto-generates one all-day shift per day. |
| 9 | **Event rota bulk shift generation** | "Generate Shifts" action on event-time rotas: specify start day, end day, and time slot templates (start time + duration). Creates the Cartesian product. |
| 10 | **Build/strike volunteer signup** | Volunteer picks a team's build/strike rota and selects a date range (start day to end day). System creates one DutySignup per day-shift in that range. |
| 11 | **Dates for volunteers, offsets for coordinators** | Volunteer-facing views show resolved dates only. Coordinator views show both (e.g., "Day -5 — Wed Jul 2"). |
| 12 | **Rota deactivation → delete only** | Replace Deactivate buttons with Delete. Blocked if confirmed signups. Pending signups cancelled on delete. |
| 13 | **General pool signup page** | New page/section where volunteers mark their available days. Coordinators see the pool when voluntelling. |

## Authorization Changes

| # | Change | Approach |
|---|--------|----------|
| 14 | **NoInfoAdmin stays as-is** | No change. They can view, voluntell, approve/refuse. Cannot create/edit rotas or shifts (except for their own team). Current spec is correct. |

## Documentation Updates

| # | Change |
|---|--------|
| 15 | Update `docs/features/25-shift-management.md` |
| 16 | Update `docs/specs/2026-03-16-shift-management-design.md` |
| 17 | Update `.claude/DATA_MODEL.md` |
| 18 | Update `.claude/CODING_RULES.md` if needed |

## Test Coverage

| # | Change |
|---|--------|
| 19 | New tests for all-day shift creation/resolution |
| 20 | New tests for bulk shift generation (build/strike + event macro) |
| 21 | New tests for date-range signup flow |
| 22 | Update existing tests that reference `Shift.Title` |
| 23 | New tests for general availability pool |
| 24 | New tests for delete-replaces-deactivate behavior |
