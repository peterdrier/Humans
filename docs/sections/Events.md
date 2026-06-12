<!-- freshness:triggers
  src/Humans.Application/Services/Events/**
  src/Humans.Application/Interfaces/Events/**
  src/Humans.Application/Interfaces/Repositories/IEventRepository.cs
  src/Humans.Domain/Entities/Event.cs
  src/Humans.Domain/Entities/EventCategory.cs
  src/Humans.Domain/Entities/EventFavourite.cs
  src/Humans.Domain/Entities/EventGuideSettings.cs
  src/Humans.Domain/Entities/EventModerationAction.cs
  src/Humans.Domain/Entities/EventPreference.cs
  src/Humans.Domain/Entities/EventVenue.cs
  src/Humans.Domain/Enums/EventStatus.cs
  src/Humans.Domain/Enums/EventModerationActionType.cs
  src/Humans.Infrastructure/Data/Configurations/EventConfiguration.cs
  src/Humans.Infrastructure/Data/Configurations/EventCategoryConfiguration.cs
  src/Humans.Infrastructure/Data/Configurations/EventFavouriteConfiguration.cs
  src/Humans.Infrastructure/Data/Configurations/EventGuideSettingsConfiguration.cs
  src/Humans.Infrastructure/Data/Configurations/EventModerationActionConfiguration.cs
  src/Humans.Infrastructure/Data/Configurations/EventPreferenceConfiguration.cs
  src/Humans.Infrastructure/Data/Configurations/EventVenueConfiguration.cs
  src/Humans.Infrastructure/Repositories/Events/EventRepository.cs
  src/Humans.Infrastructure/Services/Events/CachingEventService.cs
  src/Humans.Web/Controllers/EventsController.cs
  src/Humans.Web/Controllers/EventsModerationController.cs
  src/Humans.Web/Controllers/EventsDashboardController.cs
  src/Humans.Web/Controllers/EventsExportController.cs
  src/Humans.Web/Controllers/EventsAdminController.cs
  src/Humans.Web/Controllers/Api/EventsApiController.cs
  src/Humans.Web/ViewComponents/EventsCardViewComponent.cs
-->
<!-- freshness:flag-on-change
  Event submission/moderation lifecycle, guide settings window, public API surface, favourites/preferences, and the IEventServiceRead cross-section read surface — review when Events services/entities/controllers/view component change.
-->

# Events — Section Invariants

Event programming: submission, moderation, browsing, export, and preference management for festival events.

## Concepts

- An **Event** is a submitter-authored event entry (title, description, category, schedule, location) in a Pending → Approved/Rejected/ResubmitRequested lifecycle.
- An **EventGuideSettings** singleton configures the submission window, publish date, and print slot cap for the active event edition.
- An **EventCategory** is a moderator-managed taxonomy with display order, sensitivity flag, and active/inactive status.
- An **EventVenue** is a named on-site location available as a venue selection for events.
- An **EventModerationAction** is an append-only audit record of a single moderation decision on an Event.
- An **EventFavourite** records that a user has bookmarked an Event for their personal schedule.
- An **EventPreference** stores a user's excluded category slugs as a JSON list.
- **Recurring events** have `IsRecurring = true` and a comma-separated `RecurrenceDays` field encoding integer day offsets from gate-opening date.

## Data Model

### Event

**Table:** `events`

| Property | Type | Notes |
|----------|------|-------|
| Id | Guid | PK |
| Title | string | max 80 |
| Description | string | max 450 |
| CategoryId | Guid | FK → EventCategory |
| CampId | Guid? | FK only → Camp (Camps section); null for individual submissions |
| GuideSharedVenueId | Guid? | FK → EventVenue; nullable |
| SubmitterUserId | Guid | FK only → User (Users section) |
| StartAt | Instant | NodaTime; UTC |
| DurationMinutes | int | 15–1440 |
| IsRecurring | bool | |
| RecurrenceDays | string? | comma-separated day offsets, e.g. "0,1,2" |
| LocationNote | string? | max 120 |
| Host | string? | max 40; optional display name for the person running the event |
| PriorityRank | int | 0 = unprioritised; lower = higher priority in print guide |
| Status | EventStatus | enum (see below) |
| AdminNotes | string? | moderator-only notes |
| SubmittedAt | Instant | set on first Submit(); updated on resubmit |
| LastUpdatedAt | Instant | |

**Cross-section FKs:** `CampId` → Camp entity (Camps section) — FK only; `SubmitterUserId` → User (Users section) — FK only. No cross-section navigation properties exist on the entity; its navs (`EventVenue`, `Category`, `EventModerationActions`, `EventFavourites`) are all section-local.

### EventStatus

| Value | Description |
|-------|-------------|
| Draft | Saved but not yet submitted for review |
| Pending | Submitted, awaiting moderation |
| Approved | Published to the public guide |
| Rejected | Declined; submitter notified |
| ResubmitRequested | Returned for edits; submitter notified |
| Withdrawn | Pulled by submitter |

### EventModerationAction (append-only)

**Table:** `event_moderation_actions`

| Property | Type | Notes |
|----------|------|-------|
| Id | Guid | PK |
| GuideEventId | Guid | FK → Event (Restrict delete) |
| ActorUserId | Guid | FK only → User |
| Action | EventModerationActionType | string-stored enum |
| Reason | string? | max 500 |
| CreatedAt | Instant | |

Append-only audit log. DB-level: `OnDelete(DeleteBehavior.Restrict)` prevents cascade-deleting history when a GuideEvent is deleted. Action values: `Approved` / `Rejected` / `ResubmitRequested` (state-transition decisions, Pending-only) and `Edited` (an admin/moderator in-place field edit — **not** a state transition; appended directly without going through `Event.ApplyModerationAction`).

### EventGuideSettings (singleton)

**Table:** `event_guide_settings`

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

### EventVenue

**Table:** `event_venues`

| Property | Type | Notes |
|----------|------|-------|
| Id | Guid | PK |
| Name | string | max 80 |
| Description | string? | |
| LocationDescription | string? | max 120 |
| IsActive | bool | |
| DisplayOrder | int | |

### EventFavourite

**Table:** `event_favourites`

| Property | Type | Notes |
|----------|------|-------|
| Id | Guid | PK |
| UserId | Guid | FK only → User |
| GuideEventId | Guid | FK → Event |
| DayOffset | int? | Day offset (from gate opening) of the favourited occurrence of a recurring event. Null = whole event (every occurrence). Rows created before this column existed also mean "whole event". |
| CreatedAt | Instant | |

Unique constraint on (UserId, GuideEventId, DayOffset) with `NULLS NOT DISTINCT` (PG15+), so a user cannot hold two whole-event (null-day) favourites for the same event.

### EventPreference

**Table:** `event_preferences`

| Property | Type | Notes |
|----------|------|-------|
| Id | Guid | PK |
| UserId | Guid | FK only → User; unique |
| ExcludedCategorySlugs | string | JSON array of category slugs |
| UpdatedAt | Instant | |

## Routing

| Controller | Route prefix | Audience |
|-----------|--------------|----------|
| `EventsController` | `/Events/` | All active members |
| `EventsController` (barrio actions) | `/Events/Barrio/{slug}/` | Camp Lead or Workshop Lead (per camp); CampAdmin / Admin globally |
| `EventsController` (barrio bulk upload) | `/Events/Barrio/{slug}/BulkUpload` | Camp Lead or Workshop Lead (per camp); CampAdmin / Admin globally |
| `EventsController.ToggleCardFavourite` | POST `/Events/Card/Favourite/{eventId}` | All active members (favourite toggle for the events card; redirects to `returnUrl` if local, falls back to Browse) |
| `EventsModerationController` | `/Events/Moderate/` | GuideModerator, Admin |
| `EventsDashboardController` | `/Events/Dashboard/` | GuideModerator, Admin |
| `EventsExportController` | `/Events/Export/` | GuideModerator, Admin |
| `EventsAdminController` | `/Events/Admin/` | GuideModerator, Admin |
| `EventsApiController` | `/api/events/` | Public (CORS) + authenticated same-origin |

## Actors & Roles

| Actor | Capabilities |
|-------|--------------|
| Any active member | Browse approved events; submit individual events during open window; manage own favourites and category preferences; view own submissions |
| Camp Lead or Workshop Lead | Submit and manage barrio events via `EventsController` (`/Events/Barrio/{slug}/*`), shown in their **My Submissions** page alongside personal submissions; authority resolved via `ICampService.GetEventManagedCampsAsync` (unions `CampRoleAssignment` Lead/Workshop rows + legacy `CampLead` table). Workshop Leads do NOT gain general camp-management authority. Can bulk-upload events via CSV at `/Events/Barrio/{slug}/BulkUpload` (US-26.10). |
| GuideModerator, Admin | All active member capabilities. Additionally: view moderation queue, approve/reject/request-resubmit events, **edit any event's fields in place from the moderation queue (any status; status preserved, no re-queue)**, view dashboard, download CSV export, print guide; manage guide settings, event categories, shared venues |

## Invariants

- Submissions are only accepted when `now >= EventGuideSettings.SubmissionOpenAt && now <= EventGuideSettings.SubmissionCloseAt`; the controller enforces this with `IClock` before creating or resubmitting.
- A moderation action (Approve/Reject/RequestEdit) may only be applied to a `Pending` event; the controller validates status before calling `ApplyModerationAsync`.
- An admin/moderator in-place edit (`EventsModerationController.Edit`/`Update` → `IEventService.AdminUpdateAsync`) may edit **any** event in **any** status and **preserves `Status`** — an Approved event stays Approved/published and is never re-queued to Pending (contrast `UpdateAndResubmitAsync`, the submitter path, which does re-queue). It appends an `Edited` `ModerationAction` (actor + optional note) and never changes the event's camp association or submitter.
- `ModerationAction` records are never deleted or updated — `OnDelete(DeleteBehavior.Restrict)` prevents cascade; no Update paths exist in the repository.
- Category slugs are globally unique (unique constraint enforced at DB level; service validates before create/update).
- `EventsApiController` is gated behind `EventsFeatureFilter` at class level and `[EnableCors("EventsApi")]`; the public endpoints allow unauthenticated access while `[Authorize] + [DisableCors]` endpoints are same-origin only.
- Excluded category slugs stored in `EventPreference.ExcludedCategorySlugs` are validated against active categories before save.
- Bulk CSV upload is all-or-nothing: if any row fails validation, no events are created or updated. Rows with a non-empty `Id` update the matched camp event; rows with an empty `Id` create a new event. `Withdrawn` events cannot be modified via bulk upload.
- `StartAt` is always stored as UTC `Instant`; timezone conversion is done at presentation layer using `EventGuideSettingsView.TimeZoneId`.

## Negative Access Rules

- Non-moderators **cannot** approve, reject, or request edits on any event.
- Non-moderators **cannot** create, edit, or delete event categories or shared venues, or modify guide settings.
- A submitter **cannot** moderate their own event.
- The public API (`/api/events/events`, `/api/events/barrios`, `/api/events/categories`) **cannot** return unapproved events.
- Favourites and preferences endpoints **cannot** be accessed cross-origin (enforced by `[DisableCors]` on all `[Authorize]` API actions).
- Barrio events **cannot** be submitted by a user who is not a Camp Lead, Workshop Lead, CampAdmin, or Admin for that camp.

## Triggers

- When a moderation action is applied: an email notification is sent to the submitter (`IEmailService.SendAsync` with the `IEmailMessageFactory.EventLifecycle` message), coordinated by `EventsModerationController.ProcessActionAsync`.
- When a moderator approves an event: `Event.Status` transitions to `Approved` and an `EventModerationAction` record is appended.

## Cross-Section Dependencies

- **Users**: controllers call `IUserService.GetUserInfoAsync(userId)` for submitter display name and email (replaces the dropped `Event.SubmitterUser` navigation). `UserManager<User>` (Identity) still resolves the current user.
- **Camps**: controllers call `ICampService.GetCampsForYearAsync(year)` to resolve camp display data per event (replaces the dropped `Event.Camp` navigation). `Event.CampId` remains a bare FK column. Camp-event submission authority on `EventsController` barrio actions is sourced from `CampOperationRequirement.SubmitEvent` resource authorization (backed by `CampInfo.IsEventManager`) — the Lead OR Workshop OR-check that consumes `CampRoleAssignment` rows whose `CampRoleDefinition.SpecialRole` is `Lead` or `Workshop` (issue nobodies-collective/Humans#753). Moderation authority remains global (GuideModerator / Admin) — no camp-scoped moderation.
- **Camps (downstream consumer, PR peterdrier#915):** the Camp detail page (`/Camps/{slug}`) embeds the Events-owned `EventsCardViewComponent` (`src/Humans.Web/ViewComponents/EventsCardViewComponent.cs`), which reads `IEventServiceRead` to list the camp's approved events — title, category, description (PR peterdrier#919), schedule, venue, host — with per-row favourite toggles posting to `POST /Events/Card/Favourite/{eventId}` (redirects back via `returnUrl`). Web-layer view composition only — Camps services do not depend on Events, and no Event types cross into the Camps views (the component is invoked with the camp's id only). Auth-gated at the call site; auto-hides when the camp has no approved events or the Events feature is off. Profile pages also embed the card scoped to `userId` (PR peterdrier#925), showing only the user's personal (non-camp) submitted events — events they submitted under a camp appear on the camp's page, not the profile card.
- **Shifts (burn settings)** — `EventGuideSettings.EventSettings` navigation was dropped along with the cross-section FK. The Events section reads the linked burn (`event_settings` row owned by Shifts) via `IBurnSettingsService.GetByIdAsync(EventGuideSettings.EventSettingsId)`, which returns a `BurnSettingsInfo` DTO (identity, timezone, gate-opening date, build-calendar offsets, EE capacity) — the Shifts-internal entity never crosses the section boundary. Issue [#719](https://github.com/nobodies-collective/Humans/issues/719).
- **Email**: `IEmailService` for moderation outcome notifications.

## Architecture

**Owning services:** `EventService` (`Humans.Application.Services.Events`), `EventRepository` (`Humans.Infrastructure.Repositories.Events`)
**Owned tables:** `events`, `event_guide_settings`, `event_categories`, `event_venues`, `event_moderation_actions`, `event_favourites`, `event_preferences`
**Status:** (A) Migrated — PR peterdrier#374, 2026-04-30

### For (A) Migrated sections

- `EventService` lives in `Humans.Application/Services/Events/` and never imports `Microsoft.EntityFrameworkCore`.
- `IEventRepository` (impl `EventRepository` in `Humans.Infrastructure/Repositories/Events/`) is the only code path that touches this section's tables via `DbContext`.
- **Decorator decision** — **§15 caching decorator** (T-03, 2026-05-16). The earlier "no decorator" rationale (mutable, moderated, stale = rejected-shown-as-approved) was correct in shape but was answered by making **only approved events** cacheable and routing every write through the decorator so post-moderation invalidation is inline. The public CORS API (`/api/events/events`) is the read path the cache absorbs; the moderation dashboard (`GetAllEventsForDashboardAsync`) stays direct-DB because it needs a fresh pending count the approved-only cache cannot answer. Split projections: `ApprovedEventView` (per-event dict keyed by id, pre-stitched with `Category.Slug/Name/IsSensitive` and `Venue.Name`), flat `EventCategoryView[]`, flat `EventVenueView[]`, and the `EventGuideSettingsView` singleton (pre-stitched with foreign `EventSettings.TimeZoneId`). In-memory filtering in C# handles the `(campId, venueId, categoryId, q)` browse params against the cached snapshot. No `SaveChangesInterceptor` — every event_* write flows through `IEventService` by design (enforced by the universal `HUM0025` analyzer — only `EventRepository` references the Event DbSets), so the decorator handles invalidation inline after each delegated write. `IEventViewInvalidator` exposes the future cross-section hook for `EventSettings` edits (issue [#719](https://github.com/nobodies-collective/Humans/issues/719)). Warmed eagerly at startup (`CachingEventService` registers itself as the `IHostedService` and runs `WarmAllAsync`); warmup failures are logged and swallowed (lazy population on miss still works).
- **Cross-domain navs** — Stripped (PR #539, Stage 3). `Event.CampId`, `Event.SubmitterUserId`, `EventModerationAction.ActorUserId`, `EventFavourite.UserId`, `EventPreference.UserId`, and `EventGuideSettings.EventSettingsId` are bare FK columns — no navigation properties, no DB-level FK constraints, no cross-section `.Include()` chains. Camp / User / burn-settings data is fetched via supplier services (`ICampService`, `IUserService`, `IBurnSettingsService`). `TimeZoneId` is cached at warm time on `EventGuideSettingsView` and stays stale on direct burn-settings edits until the next event-section write or process restart — invalidation hook still pending under [#719](https://github.com/nobodies-collective/Humans/issues/719).
- **Cross-section calls** — `UserManager<User>` (Identity, in controllers only), `ICampService.GetCampsForYearAsync`, `IUserService.GetUserInfoAsync`, `IEmailService` (in `EventsModerationController`). `EventService` implements `ICalendarFeedContributor` to supply a user's favourited approved events to the ICalFeed section's personal feed fanout (PR peterdrier#931).
- **Architecture test/analyzer** — `tests/Humans.Application.Tests/Architecture/EventsArchitectureTests.cs` pins the service/repository split, the canonical `Events` / `Events/Dashboard` / `Events/Export` / `Events/Moderate` / `api/events` route names, and the T-03 caching invariants (decorator wraps `IEventService`, `IEventViewInvalidator` shares the decorator's Singleton, inner `EventService` injects no caching abstraction). The universal `HUM0025` (a DbSet table must be referenced by exactly one repository) enforces that only `EventRepository` touches the Event Guide DbSets — it subsumes the retired per-section `HUM0023` Event-write analyzer.
- **Read/write interface split (PR peterdrier#915).** `IEventServiceRead` (`Humans.Application.Interfaces.Events`) is the cross-section read surface — 3 methods: `GetApprovedEventsAsync`, `GetGuideSettingsAsync`, `GetFavouriteEventIdsAsync` — returning only the cached projections (`ApprovedEventView`, `EventGuideSettingsView`, favourite-id set); no EF entities. `IEventService : IEventServiceRead` adds submission/moderation/settings writes and Events-internal reads. `IEventServiceRead` is registered as a forward to the `CachingEventService` Singleton (`EventsSectionExtensions`), so cross-section reads are served from the T-03 approved-only cache — interface segregation only, no new cache layer. External consumers: the Events-owned `EventsCardViewComponent` composed onto the Camp detail page and Profile pages (Web-layer composition; see Cross-Section Dependencies). External sections inject `IEventServiceRead`. See `memory/architecture/section-read-write-split.md`.
