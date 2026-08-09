# Admin in-place event edit — design

**Date:** 2026-06-09
**Section:** Events
**Status:** Approved (design), pending implementation

## Problem

A full site admin (or Events moderator) cannot edit an event/workshop record manually.
Tracing every edit path:

| Path | Who | Which events | Which states | On save |
|------|-----|--------------|--------------|---------|
| `/Events/Submit/{id}/Edit` (`EventsController.Edit`) | Submitter **or** Events admin | individual only (`CampId != null` → `NotFound`) | Draft / Pending / Rejected / ResubmitRequested | `UpdateAndResubmitAsync` |
| `/Events/Barrio/{slug}/{id}/Edit` (`EventsController.BarrioEdit`) | Camp event manager **or** Admin | camp/barrio only | Pending / Rejected / ResubmitRequested | `UpdateAndResubmitAsync` |
| `/Events/Moderate` (`EventsModerationController`) | Events admin / Admin | any | only Pending | approve / reject / request-edit / withdraw — **no field editing** |
| `/Events/Admin/*` (`EventsAdminController`) | Events admin / Admin | — | — | Settings / Categories / Venues — **no event records** |

Three walls block an "in a pinch" admin fix:

1. **No admin field-edit surface at all.** Moderation only does decisions; the admin controller only manages settings/categories/venues.
2. **Approved is locked to everyone.** Both edit flows hard-gate on state; an Approved (published) event yields *"This event cannot be edited in its current state"* — for nobody, including full admin. The only options on a live workshop are Withdraw (→ Withdrawn, also un-editable) or Request-Edit (bounces to the submitter + re-queues).
3. **Every edit re-queues to Pending.** `UpdateAndResubmitAsync` flips Approved → Pending on save (`EventService.cs`), so editing a typo would un-publish the event until a moderator re-approves it.

## Goal

A full admin / Events moderator can edit **any** event in **any** state, in place, **without changing its status** — an Approved event stays Approved and published. Admin edit is itself the authoritative review, so it never re-queues. The edit is recorded in the event's moderation history.

## Design

### 1. Entry point & authorization

No new controller or policy. The moderation queue (`/Events/Moderate`) already lists every event across status tabs and is gated `[Authorize(Policy = EventsAdminOrAdmin)]` (Admin role + Events moderators). Add an **Edit** link to each row in `Index.cshtml` → new admin edit form.

### 2. New controller actions (`EventsModerationController`)

- `GET Events/Moderate/{eventId}/Edit` — load via existing `GetEventForModerationAsync` (returns the entity in any state); build the form. **No state gate.**
- `POST Events/Moderate/{eventId}/Edit` — validate, mutate fields, call `AdminUpdateAsync`, redirect to the queue tab matching the event's (unchanged) status.

### 3. New service method (status-preserving save)

`IEventService.AdminUpdateAsync(Event guideEvent, Guid actorUserId, string? note, CancellationToken ct)`:

- sets `LastUpdatedAt = now`; **leaves `Status` untouched**;
- builds `EventModerationAction { Action = Edited, ActorUserId, Reason = note, CreatedAt = now }`;
- persists both in one transaction via the **existing** `repo.SaveEventAndModerationActionAsync` (same path `ApplyModerationAsync` uses — no new repo surface).

Caching decorator (`CachingEventService.AdminUpdateAsync`): delegate to inner, then `RefreshEventEntryAsync(eventId)` — re-projects the approved view if still approved, no-op otherwise. Mirrors the `ApplyModerationAsync` invalidation pattern exactly.

**This one new `IEventService` method is the only new durable interface surface.** Reusing `UpdateAndResubmitAsync` was rejected: its purpose is to re-queue Approved → Pending (the opposite of the requirement); a `bool preserveStatus` flag would be a worse smell than a clearly-named method.

### 4. Audit — `ModerationAction` history

Record the edit as an `Edited` entry in the event's `ModerationAction` history (new `EventModerationActionType.Edited = 3`). This is the section's existing audit home — it renders inline in the queue's per-event History beside Approve/Reject, where moderators look. The optional **note** field on the form becomes the history `Reason`.

- Append-only is preserved.
- The documented invariant "a moderation action may only be applied to a Pending event" stays intact: an `Edited` marker is explicitly **not** a state-transition decision and does not go through `Event.ApplyModerationAction` (which gates on Pending). It is appended directly alongside the status-preserving save.
- The global `IAuditLogService` was rejected: the Events section records nothing there today (no event `AuditAction` values exist), so using it would break section consistency and the edit would not appear in the queue history.

### 5. Editable fields — content only

Same content fields the submitter forms expose:
Title, Description, Category, Venue (individual) / PriorityRank (camp), Start date/time, All-day, Duration, IsRecurring + RecurrenceDays, LocationNote, Host — plus the optional edit **note**. Status shown read-only with "saving keeps this status."

**Not editable:** camp association (`CampId`) and submitter (`SubmitterUserId`) — structural reassignment is a different, rarer operation, out of scope. The unused `AdminNotes` entity field stays out of scope.

New `AdminEventFormViewModel` + `AdminEventForm.cshtml` (one form handling both individual and camp events); reuses existing dropdown/time/recurrence helpers (`EventsTimeHelpers`, `EventsLookupHelpers`, category/venue/day population).

### 6. Out of scope

Changing an event's **status** from this form (e.g. re-publishing a Withdrawn event). Editing a Withdrawn event keeps it Withdrawn. Status changes remain the approve/reject/withdraw actions.

## Files

**New**
- `src/Humans.Web/Views/EventsModeration/AdminEventForm.cshtml`
- `AdminEventFormViewModel` (in `src/Humans.Web/Models/Events/EventsModerationViewModels.cs`)

**Modified**
- `src/Humans.Domain/Enums/EventModerationActionType.cs` — add `Edited = 3`
- `src/Humans.Application/Interfaces/Events/IEventService.cs` — add `AdminUpdateAsync`
- `src/Humans.Application/Services/Events/EventService.cs` — implement `AdminUpdateAsync`
- `src/Humans.Infrastructure/Services/Events/CachingEventService.cs` — decorator override
- `src/Humans.Web/Controllers/EventsModerationController.cs` — `Edit` (GET) + `Update` (POST)
- `src/Humans.Web/Views/EventsModeration/Index.cshtml` — per-row Edit link; render the new `Edited` history action label
- `docs/sections/Events.md` — new capability + `Edited`-action note + invariant clarification

## Tests

- Editing an Approved event preserves Approved status (no re-queue).
- An `Edited` `ModerationAction` is appended with the actor and note.
- The cached approved-event view is refreshed for an edited Approved event.
- Individual-vs-camp field round-trip (venue vs priority).
- Existing `EventsArchitectureTests` continue to pass (route names, decorator-wraps-IEventService, single-repository write).
