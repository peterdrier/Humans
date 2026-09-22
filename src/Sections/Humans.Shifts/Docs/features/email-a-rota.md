<!-- freshness:triggers
  src/Sections/Humans.Shifts/Services/IRotaCoordinatorMessageService.cs
  src/Sections/Humans.Shifts/Services/RotaCoordinatorMessageService.cs
  src/Sections/Humans.Email/**
  src/Sections/Humans.Email.Contracts/**
  src/Sections/Humans.Shifts/Controllers/ShiftAdminController.cs
  src/Sections/Humans.Shifts/Models/EmailRotaViewModel.cs
  src/Sections/Humans.Shifts/Models/EmailTeamRotasViewModel.cs
  src/Sections/Humans.Shifts/Services/TeamRotasAudienceFilter.cs
  src/Sections/Humans.Shifts/Views/ShiftAdmin/EmailRota.cshtml
  src/Sections/Humans.Shifts/Views/ShiftAdmin/EmailTeamRotas.cshtml
-->
<!-- freshness:flag-on-change
  Email template shape, recipient selection rules, and authorization scope — review when ShiftAdminController authorization, signup status filtering, or the coordinator-rota email body changes.
-->

# Email a Rota

## Business Context

Coordinators routinely need to communicate with everyone working a given rota — a last-minute schedule clarification, a venue change, a thank-you. Before this feature, coordinators built recipient lists by hand from the rota page or fell back on the per-person "Contact a person" button one signup at a time. Coordinators improvised via spreadsheets + Gmail, losing the personalization that makes operational messages actionable — specifically the "your shifts on this rota are…" tail that lets each recipient know exactly which shifts the message applies to.

The "Email a rota" action gives coordinators a single bulk-to-rota messaging path that **preserves per-recipient personalization** (one email per recipient, each carrying that recipient's own shift list on this rota), while reusing the existing outbox/audit/opt-out infrastructure so logging and consent routing stay consistent with every other transactional send.

Source: [nobodies-collective/Humans#732](https://github.com/nobodies-collective/Humans/issues/732).

## User Stories

### US-732.1: Coordinator emails everyone on a rota

**As a** rota coordinator (Admin, VolunteerCoordinator, or department coordinator)
**I want to** send one personalised email to every active signup on a rota
**So that** I can broadcast schedule clarifications, venue changes, or thanks without losing the per-recipient shift context

**Acceptance Criteria:**

- An "Email a rota" entry point is visible on the rota admin view for users who can manage the department's shifts.
- Compose form accepts a free-text message body (1–4000 characters, required).
- Compose form shows the recipient count and the list of recipient names (`BurnerName`, alphabetical) so the coordinator can verify scope before sending.
- Compose form carries an **include-shifts** checkbox, ticked by default; clearing it drops the shift section (lead-in and list) from every email.
- On submit, each distinct active signup user receives a **separate, personalised email** — not a single CC/BCC blast.
- Each email body contains the coordinator's free-text message plus that recipient's own chronologically ordered shifts on this rota.
- Shift list uses the event's timezone (matches the rota detail page convention): `"ddd MMMM d"` for all-day shifts, `"ddd MMMM d @ HH:mm"` for time-slotted shifts.
- Delivery flows through `IEmailService` → outbox so audit, opt-out routing, and category suppression stay consistent with every other transactional send.
- A single audit row (`AuditAction.CoordinatorRotaMessageSent`) records the dispatch, including queued count, skipped count, and a truncated copy of the message text.
- Non-coordinators (and `NoInfoAdmin`) do not see the entry point and are blocked at the controller by `ResolveDepartmentManagementAsync`.

### US-732.2: Recipients see exactly their own shifts

**As a** rota recipient
**I want** the email I receive to list only my shifts on this rota
**So that** the message is unambiguous about which commitments it applies to

**Acceptance Criteria:**

- Email lists only the recipient's signups on the target rota where `SignupStatus is Pending or Confirmed` — and lists nothing at all when the coordinator cleared include-shifts.
- Shifts are sorted chronologically by absolute start (event timezone).
- Email rendering uses the recipient's `PreferredLanguage` culture.

### US-732.3: Coordinator thanks everyone who worked the department

**As a** department coordinator
**I want to** message everyone who held a shift with my department this event, not just those with one still ahead
**So that** I can thank the whole crew once the event is over

**Acceptance Criteria:**

- The team-wide compose form (`/Teams/{slug}/Shifts/Email`) offers **Upcoming rotas only** (default) and **All rotas in this event**.
- Choosing "all" reveals **Build / Event / Strike** checkboxes, all ticked by default, filtering on `Rota.Period`. A `RotaPeriod.All` rota is admitted by any ticked period.
- The period checkboxes apply only under "all". Hidden under "upcoming", their values never narrow the audience — `TeamRotasAudienceFilter.Includes` short-circuits on `UpcomingOnly`.
- Changing any audience control re-posts the form with `intent=refresh`, which re-previews recipients against the new selection and leaves the half-written message and its validation alone. The refresh button is the no-JS fallback.
- A send only dispatches to an audience the coordinator has been shown. The form carries the `TeamRotasAudienceFilter.Key` its recipient list was built from; if the posted selection differs — no script, or the script failed — the send is turned back, the form re-renders against the new audience with a notice, and sending again dispatches it.
- The recipient count on the Send button always reflects the previewed selection.

## Recipient Selection

The recipient set is computed once per dispatch:

1. Load the rota with its shifts, `EventSettings`, and signups in one read (`IShiftManagementRepository.GetRotaAsync(rotaId, RotaReadShape.View, ct)`).
2. Filter the loaded signups to the active set (`Pending` or `Confirmed`) in memory (`RotaCoordinatorMessageService.GetActiveSignups(rota)`).
3. Group signups by `UserId` (one email per distinct user, even if they have multiple shifts on the rota).
4. Skip users with no user record or no email address; record skipped count for the audit row.

## Email Template (per recipient)

Shape (template in `ShiftsEmails`):

```
Dear {BurnerName},

A message from the coordinator for your shift:

{coordinator's free-text message}

—

(shift section — omitted entirely when include-shifts is cleared)
FYI, your shifts on this rota are:
- Mon July 6 @ 19:30
- Tue July 7 @ 12:30
- …

Thank you,
— Humans & {sender BurnerName}
```

`CoordinatorRotaMessageRequest` carries the per-recipient inputs:

- `RecipientEmail`, `RecipientName` — addressing + greeting.
- `SenderName`, `SenderEmail` — signature + Reply-To attribution.
- `RotaName` — subject + body context.
- `MessageText` — coordinator's free-text body.
- `ShiftLines` — pre-formatted, chronologically sorted, recipient-scoped shift labels.
- `IncludeShifts` — false drops the whole shift section. Distinct from an empty `ShiftLines`, which still prints the "no shifts yet" note.
- `Culture` — recipient's preferred language for template rendering.

## Authorization

The compose + submit endpoints (`GET/POST /Teams/{slug}/Shifts/Rotas/{rotaId}/Email`) use the standard shift-admin gate:

- Resolves the team and confirms the current user can **manage** that department (`ResolveDepartmentManagementAsync`).
- That gate excludes `NoInfoAdmin` and non-managers; it admits Admin, VolunteerCoordinator, and department coordinators — matching the existing rota CRUD authorization scope.
- `404` is returned when the rota does not belong to the resolved team (prevents cross-team probing).

## Data Model

No new entities or migrations. The feature is pure orchestration over existing types:

- `IShiftManagementRepository.GetRotaAsync(rotaId, RotaReadShape.View, ct)` — loads the rota with shifts, `EventSettings`, and signups in one read; active signups are filtered in memory for grouping.
- `IUserService.GetUserInfosAsync(userIds, ct)` — recipient name/email/culture lookup.
- `IEmailService.SendAsync(ShiftsEmails.CoordinatorRotaMessage(request), ct)` — outbox enqueue.
- `ShiftsEmails` — the section's own builder for the template (copy in `ShiftsResource*.resx`).
- `AuditAction.CoordinatorRotaMessageSent` — new audit action value.

## Workflow

```
Coordinator (GET)
  → ShiftAdminController.EmailRota (GET)
    → Resolve team + management permission
    → Load rota + recipient names → render compose view

Coordinator submits (POST)
  → ShiftAdminController.EmailRota (POST)
    → Re-resolve team + management permission
    → Repopulate display fields (recipient list)
    → Validate ModelState (Message required, ≤4000 chars)
    → IRotaCoordinatorMessageService.SendRotaMessageAsync(rotaId, senderUserId, message, includeShifts)
        → Load rota + EventSettings
        → Load active signups → group by user
        → Load sender + recipient infos
        → For each recipient: build chronological shift lines → enqueue email
        → Single audit row: CoordinatorRotaMessageSent (queued/skipped counts)
        → RotaMessageDispatchResult.Success(count, rotaName)
    → SetSuccess("Queued N email(s) to recipients on rota '…'")
    → Redirect to rota index anchor (#rota-{id})

Failure paths
  → Rota missing                → "Rota not found." (validation summary)
  → Empty/whitespace message    → ModelState error
  → No active signups           → "This rota has no active signups to email."
  → Team-wide: nothing selected → "No rota in this team matches that selection and has active signups to email."
  → Sender not found            → "Sender not found."
  → Recipient skipped (no user / no email) → logged, counted, does not abort dispatch
```

## Architecture Status

- **Section:** Shifts.
- **Layering:** new orchestrator service `RotaCoordinatorMessageService` lives in Application; no EF types leak across the boundary; recipient set computed via existing repository abstractions; rendering + delivery delegated to the Email service surface; controller is thin and authorization-gated.
- **Cross-section dependencies:** Users (`IUserService`), Email (`IEmailService`), AuditLog (`IAuditLogService`). All consumed through Application interfaces — no direct DbContext access.
- **Caching:** none (one-shot dispatch path; per-recipient lookups bounded by signup count).

## Related Features

- [Shift Management](shift-management.md) — broader shift/rota admin context.
- [Coordinator Roles](coordinator-roles.md) — Volunteer Coordinator role that gates many shift-admin actions.
- [Shift Signup Visibility](shift-signup-visibility.md) — the signup statuses that scope the recipient set.
- [`docs/sections/shifts.md`](../Shifts.md) — section invariants for Shifts.
