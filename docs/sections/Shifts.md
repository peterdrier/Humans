# Shifts — Section Invariants

## Concepts

- A **Rota** is a named container for shifts, belonging to a department and an event. Each rota has a period (Build, Event, or Strike) that determines whether its shifts are all-day or time-slotted.
- A **Shift** is a single work slot with a day offset, optional start time, duration, and maximum volunteer count.
- A **Shift Signup** links a human to a shift. Signups progress through states: Pending, Confirmed, Refused, Bailed, Cancelled, or NoShow.
- **Range Signups** link multiple shifts via a block ID. Operations on a range (bail, approve, refuse) apply to the entire block atomically.
- **Event Settings** is a singleton per event controlling dates, timezone, early-entry capacity, global volunteer cap, and whether shift browsing is open to regular volunteers.
- **General Availability** tracks per-human per-event day availability.
- **Volunteer Event Profile** stores per-event volunteer data including skills, dietary preferences, and medical information.
- **Rota Tags** are labels on rotas used for filtering and volunteer preference matching.
- **Voluntelling** is when an admin or coordinator signs up a human for a shift on their behalf.

## Actors & Roles

| Actor | Capabilities |
|-------|-------------|
| Any active human | Browse available shifts (when browsing is open or they have existing signups). Sign up for shifts. View own signups and schedule. Bail from own signups. Set general availability. Fill out volunteer event profile |
| Department coordinator | Manage rotas and shifts for their department. Approve, refuse, and bail signups. Voluntell humans. Manage rota tags. View volunteer event profiles (except medical data) |
| VolunteerCoordinator | All coordinator capabilities across all departments. Move rotas between departments. Access the cross-department shift dashboard |
| NoInfoAdmin, Admin | Approve, refuse, and bail signups across all departments. View volunteer medical data. Access the cross-department shift dashboard |
| Admin | Manage event settings (dates, timezone, early-entry capacity, global volunteer cap, shift browsing toggle) |

## Invariants

- Shift signup status follows: Pending then Confirmed, Refused, Bailed, Cancelled, or NoShow. Only valid forward transitions are allowed.
- Rota visibility is controlled by an "is visible to volunteers" toggle (default: visible). Hidden rotas are only shown to coordinators and privileged roles.
- Voluntelling (admin-initiated signup) records who enrolled the human.
- Range signups create or cancel all shifts in the date range atomically.
- Event settings is a singleton per event.
- Rota period (Build, Event, Strike) determines the shift creation UX (all-day vs. time-slotted) and signup UX (date-range vs. individual).
- Medical data in volunteer event profiles is restricted to Admin and NoInfoAdmin.
- When shift browsing is closed, regular volunteers can only see shifts if they already have signups. Coordinators and privileged roles can always browse.

## Negative Access Rules

- Regular humans **cannot** manage rotas or shifts. They can only browse and sign up.
- Regular humans **cannot** approve, refuse, or bail other humans' signups.
- Regular humans **cannot** voluntell other humans.
- Department coordinators **cannot** manage rotas or approve signups outside their own department.
- Department coordinators **cannot** view volunteer medical data.
- NoInfoAdmin **cannot** create or edit rotas or shifts. They can only manage signups (approve, refuse, bail) and view medical data.
- VolunteerCoordinator **cannot** view volunteer medical data.

## Triggers

- When a signup is approved or refused, an email notification is queued to the volunteer.
- When a human is voluntelled, an email notification is queued to them.
- Range signup or bail operations create or cancel all shifts in the block atomically.

## Cross-Section Dependencies

- **Teams**: Rotas belong to a department. Coordinator status on a department determines shift management access.
- **Profiles**: Volunteer event profile stores per-event volunteer data (skills, dietary, medical). NoShow history is shown on a human's profile to coordinators and privileged roles.
- **Admin**: Event settings management is Admin-only.
- **Email**: Signup status change notifications are queued through the email outbox.
