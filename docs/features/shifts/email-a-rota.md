<!-- freshness:triggers
  src/Humans.Application/Interfaces/Shifts/IRotaCoordinatorMessageService.cs
  src/Humans.Application/Services/Shifts/RotaCoordinatorMessageService.cs
  src/Humans.Application/Interfaces/Email/IEmailService.cs
  src/Humans.Application/Services/Email/OutboxEmailService.cs
  src/Humans.Infrastructure/Services/EmailRenderer.cs
  src/Humans.Web/Controllers/ShiftAdminController.cs
  src/Humans.Web/Models/Shifts/EmailRotaViewModel.cs
  src/Humans.Web/Views/ShiftAdmin/EmailRota.cshtml
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

- Email lists only the recipient's signups on the target rota where `SignupStatus is Pending or Confirmed`.
- Shifts are sorted chronologically by absolute start (event timezone).
- Email rendering uses the recipient's `PreferredLanguage` culture.

## Recipient Selection

The recipient set is computed once per dispatch:

1. Load the rota with its shifts and `EventSettings` (`IShiftSignupRepository.GetRotaWithShiftsAsync`).
2. Load all active signups for the rota (`IShiftSignupRepository.GetActiveByRotaAsync`) — currently `Pending` or `Confirmed`.
3. Group signups by `UserId` (one email per distinct user, even if they have multiple shifts on the rota).
4. Skip users with no user record or no email address; record skipped count for the audit row.

## Email Template (per recipient)

Shape (template in `IEmailRenderer`):

```
Dear {BurnerName},

A message from the coordinator for your shift:

{coordinator's free-text message}

—

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
- `Culture` — recipient's preferred language for template rendering.

## Authorization

The compose + submit endpoints (`GET/POST /Teams/{slug}/Shifts/Rotas/{rotaId}/Email`) use the standard shift-admin gate:

- Resolves the team and confirms the current user can **manage** that department (`ResolveDepartmentManagementAsync`).
- That gate excludes `NoInfoAdmin` and non-managers; it admits Admin, VolunteerCoordinator, and department coordinators — matching the existing rota CRUD authorization scope.
- `404` is returned when the rota does not belong to the resolved team (prevents cross-team probing).

## Data Model

No new entities or migrations. The feature is pure orchestration over existing types:

- `IShiftSignupRepository` — new (or reused) read methods:
  - `GetRotaWithShiftsAsync(rotaId, ct)` — rota + shifts + `EventSettings`.
  - `GetActiveByRotaAsync(rotaId, ct)` — active signups for grouping.
- `IUserService.GetUserInfosAsync(userIds, ct)` — recipient name/email/culture lookup.
- `IEmailService.SendCoordinatorRotaMessageAsync(request, ct)` — outbox enqueue.
- `IEmailRenderer` — rendering for the new template.
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
    → IRotaCoordinatorMessageService.SendRotaMessageAsync(rotaId, senderUserId, message)
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
  → Sender not found            → "Sender not found."
  → Recipient skipped (no user / no email) → logged, counted, does not abort dispatch
```

## Architecture Status

- **Section:** Shifts.
- **Layering:** new orchestrator service `RotaCoordinatorMessageService` lives in Application; no EF types leak across the boundary; recipient set computed via existing repository abstractions; rendering + delivery delegated to the Email service surface; controller is thin and authorization-gated.
- **Cross-section dependencies:** Users (`IUserService`), Email (`IEmailService`, `IEmailRenderer`), AuditLog (`IAuditLogService`). All consumed through Application interfaces — no direct DbContext access.
- **Caching:** none (one-shot dispatch path; per-recipient lookups bounded by signup count).

## Related Features

- [Shift Management](shift-management.md) — broader shift/rota admin context.
- [Coordinator Roles](coordinator-roles.md) — Volunteer Coordinator role that gates many shift-admin actions.
- [Shift Signup Visibility](shift-signup-visibility.md) — signup statuses used to scope the recipient set.
- [`docs/sections/shifts.md`](../../sections/shifts.md) — section invariants for Shifts.
