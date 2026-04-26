<!-- freshness:triggers
  src/Humans.Web/Views/Shifts/**
  src/Humans.Web/Views/ShiftAdmin/**
  src/Humans.Web/Views/ShiftDashboard/**
  src/Humans.Web/Views/Vol/**
  src/Humans.Web/Views/Profile/Me/ShiftInfo.cshtml
  src/Humans.Web/Controllers/ShiftsController.cs
  src/Humans.Web/Controllers/ShiftAdminController.cs
  src/Humans.Web/Controllers/ShiftDashboardController.cs
  src/Humans.Web/Controllers/VolController.cs
  src/Humans.Web/ViewComponents/ShiftSignupsViewComponent.cs
  src/Humans.Web/Authorization/ShiftRoleChecks.cs
  src/Humans.Application/Services/Shifts/**
  src/Humans.Domain/Entities/Rota.cs
  src/Humans.Domain/Entities/Shift.cs
  src/Humans.Domain/Entities/ShiftSignup.cs
  src/Humans.Domain/Entities/ShiftTag.cs
  src/Humans.Domain/Entities/EventSettings.cs
  src/Humans.Domain/Entities/EventParticipation.cs
  src/Humans.Domain/Entities/VolunteerEventProfile.cs
  src/Humans.Domain/Entities/VolunteerTagPreference.cs
  src/Humans.Domain/Entities/GeneralAvailability.cs
  src/Humans.Infrastructure/Data/Configurations/Shifts/**
-->
<!-- freshness:flag-on-change
  Shift browsing/signup, rota management, signup approvals, event settings (early-entry capacity, browsing toggle), shift preferences wizard, and NoInfoAdmin/VolunteerCoordinator scoping. Review when shift views, services, or entities change.
-->

# Shifts

## What this section is for

Shifts is where the org schedules work slots and where humans sign up for them. The system covers the full arc of an event: Set-up (build), Event, and Strike. Set-up and Strike use all-day shifts you can book as a date range; Event shifts are time-slotted.

Shifts are team-owned. Every shift belongs to a **rota** (a named container), and every rota belongs to a department or sub-team. That ownership drives visibility and management — department coordinators run shifts for their department, and a rota can be hidden from humans until a coordinator is ready to open it.

The section also captures the data that makes scheduling work: your shift preferences (skills, work style, languages) and per-event profile info.

![TODO: screenshot — `/Shifts` browse page showing filters and a mix of Event and Build rotas]

## Key pages at a glance

| Page | Path | What it's for |
|---|---|---|
| Browse shifts | `/Shifts` | Find and sign up for shifts across all departments |
| My shifts | `/Shifts/Mine` | See your upcoming, pending, and past signups; bail if needed |
| Shift preferences wizard | `/Profile/Me/ShiftInfo` | Tell coordinators about your skills, work style, and languages |
| Team shift admin | `/Teams/{slug}/Shifts` | Coordinators: manage rotas and shifts for a department |
| Event settings | `/Shifts/Settings` | Admin: configure event dates, timezone, early-entry capacity, and the global browsing toggle |

Your dashboard also surfaces shift info — upcoming signups, or a guided discovery card with urgent understaffed shifts if you have none.

## As a Volunteer

**Set your preferences first.** At `/Profile/Me/ShiftInfo`, walk through the three-step wizard: skills (Bartending, Cooking, Sound, First Aid, Driving, etc.), work style (Early Bird / Night Owl / All Day / No Preference, plus toggles like Sober Shift, Work In Shade, No Heights), and languages. Change any of it later. Coordinators use this to match you with shifts that fit.

**Browse shifts at `/Shifts`.** Filter by department, date range, and period (Set-up / Event / Strike). Tag filters live above the list — click a tag like "Heavy lifting" or "Meeting new people" to narrow the view. Open the preferences panel to save tag preferences so matching shifts are highlighted with a star. Consecutive all-day Set-up and Strike shifts in the same rota are compressed into date ranges; click to expand individual days.

**Sign up.** For Event shifts, pick time slots — the system flags overlaps with your existing confirmed signups. For Set-up and Strike, pick a date range and the system creates signups for every day in that block under one signup-block ID; full or already-booked shifts in the range are skipped with a warning. If the rota's policy is **Public**, you're auto-confirmed. If **RequireApproval**, your signup goes in as **Pending** and a coordinator approves or refuses it. Approval/refusal updates show up under My Shifts; the coordinators of the rota's department are notified in-app on every signup state change.

**See your shifts at `/Shifts/Mine`.** Signups are grouped into upcoming, pending, and past. To cancel, use **Bail** — on a single shift, or a whole range you booked together (Mine groups range signups by `SignupBlockId` so the bail covers every day in the block). Once early-entry close passes, non-privileged humans can't sign up for, range-sign-up to, or bail from Set-up (build-period) shifts without a coordinator's help. The page also lets you set general availability (which build/event/strike day offsets you're around for) so coordinators can find you in volunteer search.

**Shift block patterns (2026).** Set-up and Strike use half-day or full-day blocks; sign up for the weeks you're on site within your department. Event-week 24-hour services use two blocks: Monday–Thursday and Friday–Sunday — you're on one block for the full run. Other Event-week roles vary by department; your coordinator confirms.

> **Don't sign up across multiple departments on the same day** — it creates double-booking and someone gets shorted. **Commit to your block.** Cherry-picking single shifts and disappearing makes coordination significantly harder.

![TODO: screenshot — `/Shifts/Mine` showing upcoming, pending, and past sections]

## As a Coordinator

If you're a department coordinator or sub-team manager, `/Teams/{slug}/Shifts` is your home. Sub-team managers only manage their own sub-team's rotas; department coordinators see everything in the department.

**Create a rota.** Give it a name, period (Build, Event, Strike, or All for rotas that span the whole event), priority (Normal / Important / Essential — feeds urgency scoring with weights 1×/3×/6×), signup policy (Public for auto-confirm, RequireApproval for review), and any practical info humans should read first. Rotas start visible by default; toggle **IsVisibleToVolunteers** off to stage them. Tag rotas with shared labels (Heavy lifting, Working in the shade, etc.) — reuse existing tags where you can.

> **Don't open a rota until the shift details are confirmed.** Half-filled or incorrectly described rotas create more work, not less. Stage them with visibility off, then flip the toggle when you're ready to take signups.

**Create shifts.** Build and Strike rotas get all-day shifts — use the bulk creator to generate one per day offset. Event rotas get time-slotted shifts — the generator produces the Cartesian product of day offsets and time slots. Add individual shifts by hand with day offset, start time, duration, and min/max volunteers. Mark a shift **AdminOnly** to hide it from regular humans.

**Manage signups.** The admin page shows a "Signed Up" column with names (Event rotas) or avatars (Build/Strike rotas). Pending signups have their own table — approve, refuse (with an optional reason), or handle a date-range block at once. **Voluntell** to enroll a specific human directly (auto-confirmed; the assigned volunteer gets an in-app "you were assigned" notification, and the department's coordinators get a signup-change notification). **Voluntell range** does the same across a date range for build/strike rotas. **Remove** unassigns a confirmed signup. After a shift ends you can **mark no-show**; the count surfaces in volunteer profiles for coordinators.

**Move a rota** if it lands under the wrong department — shifts and signups come along, and the move is audit-logged. Deleting a rota or shift is blocked if there are confirmed signups; bail or remove them first.

### Headcount and the permit model (2026)

There's a hard cap on how many people are on site during set-up and pre-event. Every open shift slot is a person arriving who needs feeding and coordination — confirm the headcount model with Volunteer Coordination before opening new shifts.

| Period | On-site cap |
|---|---|
| Set-up week 1 (15–21 Jun) | Max 80 people |
| Set-up week 2 (22–28 Jun) | Max 80 people |
| Pre-event week (29 Jun–5 Jul) | Rising toward full cap as barrios arrive |
| Event (7–12 Jul) | Full event capacity |
| Strike (13–22 Jul) | Max 80 people |

Reach Volunteer Coordination at [volunteers@nobodies.team](mailto:volunteers@nobodies.team) (Frank, Nurse, Hardcastle).

## As a Board member / Admin (NoInfo Admin)

Admins get the site-wide shift view and can approve, refuse, bail, or voluntell across any department. **NoInfoAdmin** has the same signup powers but can't create or edit rotas and shifts. **VolunteerCoordinator** has full coordinator capabilities across every department.

Admin-only controls:

- **Event settings** at `/Shifts/Settings` — gate opening date, build/event/strike offsets, timezone, early-entry capacity, barrios allocation, early-entry close instant, and the **shift browsing toggle** (when closed, regular humans only see shifts they already have signups for; privileged roles can always browse).
- **Medical data** on volunteer event profiles is visible only to Admin and NoInfoAdmin. Coordinators and VolunteerCoordinator see skills and dietary info but not medical.
- **Early-entry freeze** — after early-entry close, non-privileged humans can't sign up for or bail from Set-up shifts; admins can still adjust.

Only one active Event Settings exists at a time. Changes ripple through every shift date on the site.

![TODO: screenshot — `/Shifts/Settings` showing gate opening date, build/event/strike offsets, early-entry capacity, and the shift browsing toggle]

## Related sections

- [Teams](Teams.md) — rotas belong to departments and sub-teams; coordinator status on a team unlocks shift management.
- [Profiles](Profiles.md) — your volunteer event profile (skills, dietary, languages) feeds shift matching; no-show history shows on your profile to coordinators.
- [Camps](Camps.md) — camp timing and early-entry windows share the event settings configured here.
