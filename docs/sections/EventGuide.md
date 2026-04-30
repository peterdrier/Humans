# Event Guide — Section Invariants

Event programming guide: submission, moderation, browse, export, and preference management for festival events.

## Concepts

- A **GuideEvent** is a submitter-authored event entry (title, description, category, schedule, location) in a Pending → Approved/Rejected/ResubmitRequested lifecycle.
- A **GuideSettings** singleton configures the submission window, publish date, and print slot cap for the active event edition.
- An **EventCategory** is a moderator-managed taxonomy with display order, sensitivity flag, and active/inactive status.
- A **GuideSharedVenue** is a named on-site location available as a venue selection for events.
- A **ModerationAction** is an append-only audit record of a single moderation decision on a GuideEvent.
- A **UserEventFavourite** records that a user has bookmarked a GuideEvent for their personal schedule.
- A **UserGuidePreference** stores a user's excluded category slugs as a JSON list.
- **Recurring events** have `IsRecurring = true` and a comma-separated `RecurrenceDays` field encoding integer day offsets from gate-opening date.

## Data Model

### GuideEvent

**Table:** `guide_events`

| Property | Type | Notes |
|----------|------|-------|
| Id | Guid | PK |
| Title | string | max 80 |
| Description | string | max 450 |
| CategoryId | Guid | FK → EventCategory |
| CampId | Guid? | FK only → Camp (Camps section); null for individual submissions |
| GuideSharedVenueId | Guid? | FK → GuideSharedVenue; nullable |
| SubmitterUserId | string | FK only → User (Users section) |
| StartAt | Instant | NodaTime; UTC |
| DurationMinutes | int | 15–1440 |
| IsAllDay | bool | when true DurationMinutes=1440 |
| IsRecurring | bool | |
| RecurrenceDays | string? | comma-separated day offsets, e.g. "0,1,2" |
| LocationNote | string? | max 120 |
| PriorityRank | int | 0 = unprioritised; lower = higher priority in print guide |
| Status | GuideEventStatus | enum (see below) |
| SubmittedAt | Instant | set on first Submit(); updated on resubmit |
| CreatedAt | Instant | |
| UpdatedAt | Instant | |

**Cross-section FKs:** `CampId` → Camp entity (Camps section) — FK only; `SubmitterUserId` → User (Users section) — FK only. Navigation properties for these exist on the entity for legacy queries but are only included within owning-section includes.

### GuideEventStatus

| Value | Description |
|-------|-------------|
| Pending | Submitted, awaiting moderation |
| Approved | Published to the public guide |
| Rejected | Declined; submitter notified |
| ResubmitRequested | Returned for edits; submitter notified |
| Withdrawn | Pulled by submitter |

### ModerationAction (append-only)

**Table:** `moderation_actions`

| Property | Type | Notes |
|----------|------|-------|
| Id | Guid | PK |
| GuideEventId | Guid | FK → GuideEvent (Restrict delete) |
| ActorUserId | string | FK only → User |
| Action | ModerationActionType | string-stored enum |
| Reason | string? | max 500 |
| CreatedAt | Instant | |

Append-only audit log. DB-level: `OnDelete(DeleteBehavior.Restrict)` prevents cascade-deleting history when a GuideEvent is deleted.

### GuideSettings (singleton)

**Table:** `guide_settings`

| Property | Type | Notes |
|----------|------|-------|
| Id | Guid | PK |
| EventSettingsId | Guid | FK → EventSettings (Camps/EventSettings section) |
| SubmissionOpenAt | Instant | |
| SubmissionCloseAt | Instant | |
| GuidePublishAt | Instant | |
| MaxPrintSlots | int | 0 = unlimited |
| CreatedAt | Instant | |
| UpdatedAt | Instant | |

### EventCategory

**Table:** `event_categories`

| Property | Type | Notes |
|----------|------|-------|
| Id | Guid | PK |
| Name | string | max 60 |
| Slug | string | max 60; unique |
| IsSensitive | bool | |
| IsActive | bool | |
| DisplayOrder | int | |

### GuideSharedVenue

**Table:** `guide_shared_venues`

| Property | Type | Notes |
|----------|------|-------|
| Id | Guid | PK |
| Name | string | max 80 |
| Description | string? | |
| LocationDescription | string? | max 120 |
| IsActive | bool | |
| DisplayOrder | int | |

### UserEventFavourite

**Table:** `user_event_favourites`

| Property | Type | Notes |
|----------|------|-------|
| Id | Guid | PK |
| UserId | string | FK only → User |
| GuideEventId | Guid | FK → GuideEvent |
| CreatedAt | Instant | |

Unique constraint on (UserId, GuideEventId).

### UserGuidePreference

**Table:** `user_guide_preferences`

| Property | Type | Notes |
|----------|------|-------|
| Id | Guid | PK |
| UserId | string | FK only → User; unique |
| ExcludedCategorySlugs | string | JSON array of category slugs |
| UpdatedAt | Instant | |

## Routing

| Controller | Route prefix | Audience |
|-----------|--------------|----------|
| `EventGuideController` | `/EventGuide/` | All active members |
| `CampEventsController` | `/Camps/{slug}/Events/` | Camp coordinators |
| `ModerationController` | `/EventGuide/Moderate/` | GuideModerator, Admin |
| `EventGuideDashboardController` | `/EventGuide/Dashboard/` | GuideModerator, Admin |
| `EventGuideExportController` | `/EventGuide/Export/` | GuideModerator, Admin |
| `GuideAdminController` | `/Admin/Guide*/` | Admin |
| `GuideApiController` | `/api/guide/` | Public (CORS) + authenticated same-origin |

## Actors & Roles

| Actor | Capabilities |
|-------|--------------|
| Any active member | Browse approved events; submit individual events during open window; manage own favourites and category preferences; view own submissions |
| Camp coordinator (CampAdmin role) | Submit events on behalf of their camp |
| GuideModerator, Admin | All active member capabilities. Additionally: view moderation queue, approve/reject/request-resubmit events, view dashboard, download CSV export, print guide |
| Admin | All moderator capabilities. Additionally: manage guide settings, event categories, shared venues |

## Invariants

- Submissions are only accepted when `now >= GuideSettings.SubmissionOpenAt && now <= GuideSettings.SubmissionCloseAt`; the controller enforces this with `IClock` before creating or resubmitting.
- A moderation action (Approve/Reject/RequestEdit) may only be applied to a `Pending` event; the controller validates status before calling `ApplyModerationAsync`.
- `ModerationAction` records are never deleted or updated — `OnDelete(DeleteBehavior.Restrict)` prevents cascade; no Update paths exist in the repository.
- Category slugs are globally unique (unique constraint enforced at DB level; service validates before create/update).
- `GuideApiController` is gated behind `EventGuideFeatureFilter` at class level and `[EnableCors("GuideApi")]`; the public endpoints allow unauthenticated access while `[Authorize] + [DisableCors]` endpoints are same-origin only.
- Excluded category slugs stored in `UserGuidePreference.ExcludedCategorySlugs` are validated against active categories before save.
- `StartAt` is always stored as UTC `Instant`; timezone conversion is done at presentation layer using `GuideSettings.EventSettings.TimeZoneId`.

## Negative Access Rules

- Non-moderators **cannot** approve, reject, or request edits on any event.
- Non-admins **cannot** create, edit, or delete event categories or shared venues, or modify guide settings.
- A submitter **cannot** moderate their own event.
- The public API (`/api/guide/events`, `/api/guide/camps`, `/api/guide/categories`) **cannot** return unapproved events.
- Favourites and preferences endpoints **cannot** be accessed cross-origin (enforced by `[DisableCors]` on all `[Authorize]` API actions).
- Camp events **cannot** be submitted or withdrawn via the individual `EventGuideController`; those use `CampEventsController`.

## Triggers

- When a moderation action is applied: an email notification is sent to the submitter (`IEmailService.SendEventApprovedAsync`, `SendEventRejectedAsync`, or `SendEventResubmitRequestedAsync`), coordinated by `ModerationController.ProcessActionAsync`.
- When a moderator approves an event: `GuideEvent.Status` transitions to `Approved` and a `ModerationAction` record is appended.

## Cross-Section Dependencies

- **Users**: `UserManager<User>` (Identity) to resolve current user in controllers; `User.GetEffectiveEmail()` for email dispatch; `User.Profile?.BurnerName` for display names.
- **Camps**: `GuideEvent.CampId` FK; edit URLs for camp events routed through `CampEventsController`.
- **Calendar/EventSettings**: `IEventGuideService` reads `EventSettings` (gate opening date, timezone, event name) via the `GuideSettings.EventSettings` navigation for day-offset computation and timezone conversion.
- **Email**: `IEmailService` for moderation outcome notifications.

## Architecture

**Owning services:** `EventGuideService` (`Humans.Application.Services.EventGuide`), `EventGuideRepository` (`Humans.Infrastructure.Repositories.EventGuide`)
**Owned tables:** `guide_events`, `guide_settings`, `event_categories`, `guide_shared_venues`, `moderation_actions`, `user_event_favourites`, `user_guide_preferences`
**Status:** (A) Migrated — PR peterdrier#374, 2026-04-30

### For (A) Migrated sections

- `EventGuideService` lives in `Humans.Application.Services.EventGuide/` and never imports `Microsoft.EntityFrameworkCore`.
- `IEventGuideRepository` (impl `EventGuideRepository` in `Humans.Infrastructure/Repositories/EventGuide/`) is the only code path that touches this section's tables via `DbContext`.
- **Decorator decision** — No caching decorator. Rationale: guide data is mutable and moderated; stale cache would show rejected events as approved. Reads are lightweight at ~500-user scale.
- **Cross-domain navs** — `GuideEvent.Camp` (navigation) and `GuideEvent.SubmitterUser` (navigation) are included only within this section's repository queries. They are legacy navigation properties retained for query convenience; no cross-section `.Include()` calls are made from outside this section.
- **Cross-section calls** — `UserManager<User>` (Identity, in controllers only), `IEmailService` (in `ModerationController`).
- **Architecture test** — Not yet added; track in `todos.md`.
