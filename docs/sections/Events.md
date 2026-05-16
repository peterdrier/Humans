# Events — Section Invariants

Event programming: submission, moderation, browsing, export, and preference management for festival events.

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
| `EventGuideController` | `/Events/` | All active members |
| `CampEventsController` | `/Barrios/{slug}/Events/` | Barrio coordinators |
| `ModerationController` | `/Events/Moderate/` | GuideModerator, Admin |
| `EventGuideDashboardController` | `/Events/Dashboard/` | GuideModerator, Admin |
| `EventGuideExportController` | `/Events/Export/` | GuideModerator, Admin |
| `GuideAdminController` | `/Admin/Guide*/` | GuideModerator, Admin |
| `GuideApiController` | `/api/events/` | Public (CORS) + authenticated same-origin |

## Actors & Roles

| Actor | Capabilities |
|-------|--------------|
| Any active member | Browse approved events; submit individual events during open window; manage own favourites and category preferences; view own submissions |
| Barrio coordinator (CampAdmin role) | Submit events on behalf of their barrio |
| GuideModerator, Admin | All active member capabilities. Additionally: view moderation queue, approve/reject/request-resubmit events, view dashboard, download CSV export, print guide; manage guide settings, event categories, shared venues |

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
- Non-moderators **cannot** create, edit, or delete event categories or shared venues, or modify guide settings.
- A submitter **cannot** moderate their own event.
- The public API (`/api/events/events`, `/api/events/barrios`, `/api/events/categories`) **cannot** return unapproved events.
- Favourites and preferences endpoints **cannot** be accessed cross-origin (enforced by `[DisableCors]` on all `[Authorize]` API actions).
- Barrio events **cannot** be submitted or withdrawn via the individual `EventGuideController`; those use `CampEventsController`.

## Triggers

- When a moderation action is applied: an email notification is sent to the submitter (`IEmailService.SendEventApprovedAsync`, `SendEventRejectedAsync`, or `SendEventResubmitRequestedAsync`), coordinated by `ModerationController.ProcessActionAsync`.
- When a moderator approves an event: `GuideEvent.Status` transitions to `Approved` and a `ModerationAction` record is appended.

## Cross-Section Dependencies

- **Users**: controllers call `IUserService.GetUserInfoAsync(userId)` for submitter display name and email (replaces the dropped `Event.SubmitterUser` navigation). `UserManager<User>` (Identity) still resolves the current user.
- **Camps**: controllers call `ICampService.GetCampsForYearAsync(year)` to resolve camp display data per event (replaces the dropped `Event.Camp` navigation). `Event.CampId` remains a bare FK column.
- **Calendar/EventSettings** — **TEMP, see [#719](https://github.com/nobodies-collective/Humans/issues/719)**: `EventGuideSettings.EventSettings` navigation was dropped along with the cross-section FK. There is no `IEventSettingsService` yet, so `EventRepository.GetEventSettingsByIdAsync` reads `_db.EventSettings` directly as a documented stop-gap (marked with a `TODO: switch to IEventSettingsService once #719 ships` comment in both the repo and the interface). Callers fetch the settings row in two steps: load `EventGuideSettings` (bare `EventSettingsId`), then load `EventSettings` separately. Migrate the call sites once #719 ships.
- **Email**: `IEmailService` for moderation outcome notifications.

## Architecture

**Owning services:** `EventGuideService` (`Humans.Application.Services.EventGuide`), `EventGuideRepository` (`Humans.Infrastructure.Repositories.EventGuide`)
**Owned tables:** `guide_events`, `guide_settings`, `event_categories`, `guide_shared_venues`, `moderation_actions`, `user_event_favourites`, `user_guide_preferences`
**Status:** (A) Migrated — PR peterdrier#374, 2026-04-30

### For (A) Migrated sections

- `EventGuideService` lives in `Humans.Application.Services.EventGuide/` and never imports `Microsoft.EntityFrameworkCore`.
- `IEventGuideRepository` (impl `EventGuideRepository` in `Humans.Infrastructure/Repositories/EventGuide/`) is the only code path that touches this section's tables via `DbContext`.
- **Decorator decision** — **§15 caching decorator** (T-03, 2026-05-16). The earlier "no decorator" rationale (mutable, moderated, stale = rejected-shown-as-approved) was correct in shape but was answered by making **only approved events** cacheable and routing every write through the decorator so post-moderation invalidation is inline. The public CORS API (`/api/events/events`) is the read path the cache absorbs; the moderation dashboard (`GetAllEventsForDashboardAsync`) stays direct-DB because it needs a fresh pending count the approved-only cache cannot answer. Split projections: `ApprovedEventView` (per-event dict keyed by id, pre-stitched with `Category.Slug/Name/IsSensitive` and `Venue.Name`), flat `EventCategoryView[]`, flat `EventVenueView[]`, and the `EventGuideSettingsView` singleton (pre-stitched with foreign `EventSettings.TimeZoneId`). In-memory filtering in C# handles the `(campId, venueId, categoryId, q)` browse params against the cached snapshot. No `SaveChangesInterceptor` — every event_* write flows through `IEventService` by design (enforced by the `Only_EventRepository_Writes_Event_DbSets` arch test), so the decorator handles invalidation inline after each delegated write. `IEventViewInvalidator` exposes the future cross-section hook for `EventSettings` edits (issue [#719](https://github.com/nobodies-collective/Humans/issues/719)). Warmed eagerly at startup via `EventCacheWarmupHostedService`; warmup failures are logged and swallowed (lazy population on miss still works).
- **Cross-domain navs** — Stripped (PR #539, Stage 3). `Event.CampId`, `Event.SubmitterUserId`, `EventModerationAction.ActorUserId`, `EventFavourite.UserId`, `EventPreference.UserId`, and `EventGuideSettings.EventSettingsId` are bare FK columns — no navigation properties, no DB-level FK constraints, no cross-section `.Include()` chains. Camp / User data is fetched via supplier services (`ICampService`, `IUserService`). EventSettings remains a documented stop-gap until [#719](https://github.com/nobodies-collective/Humans/issues/719) ships an `IEventSettingsService`; in the meantime `TimeZoneId` is cached at warm time on `EventGuideSettingsView` and stays stale on direct `EventSettings` edits until the next event-section write or process restart.
- **Cross-section calls** — `UserManager<User>` (Identity, in controllers only), `ICampService.GetCampsForYearAsync`, `IUserService.GetUserInfoAsync`, `IEmailService` (in `EventsModerationController`).
- **Architecture test** — `tests/Humans.Application.Tests/Architecture/EventsArchitectureTests.cs` pins the service/repository split, the canonical `Events` / `Barrios` / `api/events` route names, and the T-03 caching invariants (decorator wraps `IEventService`, `IEventViewInvalidator` shares the decorator's Singleton, inner `EventService` injects no caching abstraction).
