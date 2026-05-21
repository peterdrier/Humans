# Event Guide Management

## Business Context

Elsewhere publishes a digital event guide (currently a standalone PWA) listing all scheduled events, theme camps, and communal locations during the event. The guide management system moves content submission, moderation, and publication into Humans so that camp organisers and individual humans can submit events through one platform, moderators have a structured review queue, and the PWA is served from Humans' API rather than a manually maintained static file.

Two types of events exist: **camp events** (submitted by a team Lead, anchored to a GuideCamp) and **individual events** (submitted by any registered human, anchored to a communal GuideSharedVenue such as "The Middle of Elsewhere").

Both kinds of submission are managed from a single page — **My Event Submissions** (`/Events/MySubmissions`). It shows the human's own individual events in one block, plus one block per barrio they lead for managing that barrio's events. There is no separate per-barrio events page.

## User Stories

### US-26.1: Moderator Configures the Guide
**As a** GuideModerator
**I want to** configure guide settings and manage shared venues and event categories
**So that** the guide is ready for the current event edition

**Acceptance Criteria:**
- Create/edit GuideSettings: submission open/close dates, guide publish date, max print guide slots
- Create, edit, and deactivate EventCategory records (name, slug, is_sensitive, display order)
- Create, edit, and deactivate GuideSharedVenue records (name, description, grid address)
- Only active categories and venues are available to submitters

### US-26.2: Camp Organiser Submits Events
**As a** team Lead
**I want to** submit events for my camp
**So that** they appear in the digital and print event guide

**Acceptance Criteria:**
- Submission form reached from the barrio's block on **My Event Submissions** (`/Events/MySubmissions`)
- Fields: title (≤ 80 chars), description (≤ 450 chars), category, date/time, duration, location note, host (optional), is_recurring, recurrence pattern, priority rank
- **Host** is an optional free-text field (≤ 40 chars) naming who runs the event. For camp events it is supplementary detail — when blank, the published guide shows no host line and the event remains attributed to the barrio.
- Event is anchored to the team's GuideCamp; GuideCamp is auto-created on first submission if not yet present
- Submission creates a GuideEvent in `Pending` status
- Submitter receives email confirmation on submission
- Submitter can edit a Pending or ResubmitRequested event
- Status of all the barrio's submissions visible in the barrio block on My Event Submissions

### US-26.3: Individual Human Submits an Event
**As any** registered human
**I want to** submit an event at a communal venue
**So that** individually organised activities appear in the guide

**Acceptance Criteria:**
- Submission form reached from the "My personal events" block on **My Event Submissions** (`/Events/MySubmissions`)
- Same fields as barrio events except location is chosen from the GuideSharedVenue list, not a barrio
- An optional location note can add specificity (e.g. "near the fire pit")
- **Host** (optional, ≤ 40 chars) names who runs the event. In the published guide the event is attributed to the host when provided; when blank it falls back to the submitter's name (or chosen display name).
- Same email notifications and status visibility as barrio organiser flow; individual events appear in the "My personal events" block on My Event Submissions

### US-26.4: Moderator Reviews Submissions
**As a** moderator
**I want to** review all pending event submissions in a queue
**So that** only appropriate content is published in the guide

**Acceptance Criteria:**

- Queue at `/Events/Moderate` lists all Pending submissions in order of receipt
- Duplicate flag shown when a submission shares a barrio and overlapping time slot with an existing Pending or Approved event (advisory only — moderator decides)
- Actions: Approve, Reject (requires reason), Request Edit (requires reason)
- On Reject or Request Edit: submitter receives email with the reason
- On Approve: submitter receives confirmation email
- All decisions logged as append-only ModerationAction records

### US-26.4b: Withdrawing an Approved Event

**As a** moderator or the original submitter
**I want to** withdraw an approved event
**So that** it is hidden from the published guide

**Acceptance Criteria:**

- Withdraw action available to GuideModerator/Admin on any Approved event (moderation queue)
- Withdraw action available to the original submitter on their own Approved event (the relevant block on My Event Submissions)
- Transitions status to `Withdrawn`; event no longer returned by the public API
- No email sent on withdrawal

### US-26.5: Submitter Responds to Rejection / Edit Request
**As a** submitter (barrio organiser or individual human)
**I want to** edit and resubmit a rejected or edit-requested event
**So that** I can address the moderator's feedback

**Acceptance Criteria:**
- Events in `Rejected` or `ResubmitRequested` status are editable by the original submitter
- On resubmit, status returns to `Pending` and re-enters the moderation queue
- Previous ModerationAction entries are preserved in the audit log

### US-26.6: Attendee Browses the Published Guide
**As an** attendee (via the PWA)
**I want to** browse all approved events filtered by day, time, and category
**So that** I can plan what to attend

**Acceptance Criteria:**
- Published API (`/api/events/events`, `/api/events/barrios`, `/api/events/categories`) returns only Approved events
- Filter by day, time of day, and category
- Keyword search across title and description
- Event detail includes barrio name or shared venue name, host (barrio events: when provided; individual events: host or submitter name), grid address, time, duration, full description
- Recurring events expanded into one entry per occurrence in API responses

### US-26.7: Attendee Opts Out of Sensitive Categories
**As an** attendee
**I want to** hide events in certain categories (e.g. Adult, Spiritual)
**So that** I only see content relevant to me

**Acceptance Criteria:**
- Sensitive categories (is_sensitive = true) visible by default
- Attendee can toggle off any category; preference persists across sessions
- If logged in to Humans: preference stored in UserGuidePreference (server-side)
- If not logged in: preference stored in localStorage on the PWA

### US-26.8: Attendee Saves Favourites and Builds a Personal Schedule
**As an** attendee
**I want to** favourite events and see them in a personal schedule view
**So that** I can follow my plan during the event

**Acceptance Criteria:**
- Favourite / unfavourite any approved event
- Personal schedule shows favourited events sorted chronologically by day and start time
- If logged in: favourites stored as UserEventFavourite records (survives device switch)
- If not logged in: favourites stored in localStorage

### US-26.10: Barrio Lead Bulk-Uploads Events via CSV

**As a** Camp Lead or Workshop Lead
**I want to** download my barrio's events as a CSV, add or edit rows, and upload the file back
**So that** I can manage a full programme without submitting events one by one

**Acceptance Criteria:**

- Each barrio block on `/Events/MySubmissions` has a download link for the current events CSV and a file input to upload an updated CSV — no separate page
- Downloading the template produces a CSV pre-filled with the barrio's non-Withdrawn events; comment lines at the top explain the format, valid categories, and the Id rule
- Upload is all-or-nothing: if any row fails validation, no events are saved and a per-row error table is shown inline in the barrio block
- New rows (empty `Id`) are submitted as new events in `Pending` status; no submission email is sent (bulk upload may create many events at once)
- Existing rows (non-empty `Id`) that are unchanged are skipped — status is preserved; rows with changed fields update the matched event and re-queue it for moderation
- Rows referencing a `Withdrawn` event are rejected (Withdrawn events cannot be reactivated via bulk upload)
- Rows absent from the CSV are left untouched — bulk upload does not delete or withdraw events
- Upload is rejected if the submission window is closed
- Only Camp Leads, Workshop Leads, CampAdmin, and Admin may access the bulk upload routes for a given barrio slug

#### Routes

```text
GET  /Events/Barrio/{slug}/BulkUpload/Template  → stream CSV of existing camp events
POST /Events/Barrio/{slug}/BulkUpload           → parse, validate, submit (all-or-nothing); on error redirects back to MySubmissions with errors in TempData
```

Both actions use the existing `ResolveCampEventManagementAsync(slug)` guard — same auth as `BarrioSubmit`/`BarrioCreate`.

#### CSV Format

```text
Id,Barrio,Status,Title,Description,Category,Date,StartTime,DurationMinutes,LocationNote,Host,IsRecurring,RecurrenceDays,PriorityRank
```

| Column | Rules |
| --- | --- |
| `Id` | Guid or empty. **Empty** → new event. **Non-empty** → update existing event. Do not change or delete an existing Id — the upload will fail. |
| `Barrio` | Read-only. Pre-filled with the camp name. Ignored on upload — for reference only. |
| `Status` | Read-only. Pre-filled with the current event status. Ignored on upload — for reference only. Helps leads know which events are live before editing. |
| `Title` | Required. Max 80 chars. |
| `Description` | Required. Max 450 chars. |
| `Category` | Required. Category name, case-insensitive (e.g. `Food and drink`). |
| `Date` | Required. `yyyy-MM-dd`. |
| `StartTime` | Required. `HH:mm`. |
| `DurationMinutes` | Required. Integer, 15–480, in 15-minute increments. |
| `LocationNote` | Optional. Max 120 chars. |
| `Host` | Optional. Max 40 chars. |
| `IsRecurring` | `true` or `false`. |
| `RecurrenceDays` | Only used when `IsRecurring` is true. Space-separated day names: `Mon Tue Wed Thu Fri Sat Sun`. Converted to day offsets from gate-opening date on import. |
| `PriorityRank` | Required. Integer, 1–100. |

**Encoding:** comma-separated, UTF-8, RFC 4180 quoting — fields containing commas are wrapped in `"double quotes"`. `RecurrenceDays` uses spaces as the day separator (`Mon Tue Fri`) so it never needs quoting.

#### Validation (all-or-nothing)

All rows are parsed and validated before any event is saved. If any row fails, the upload page is returned with a per-row error table and nothing is persisted.

Per-row checks:

- Required fields present.
- Field length and range constraints (see table above). `DurationMinutes` must be divisible by 15.
- `Date` parses to a valid date; `StartTime` parses to a valid time.
- `Category` matches an active category (case-insensitive by name).
- If `Id` is provided: event must exist for this camp and must not be `Withdrawn`.

#### Update Semantics (rows with Id)

If a row with an Id is identical to the stored event (all fields unchanged), it is skipped — the status is kept as-is and no service call is made.

If any field differs:

| Existing status | Included in template | Action on upload |
| --- | --- | --- |
| Draft | Yes | Update fields → `SubmitEventAsync` |
| Pending | Yes | Update fields → `UpdateAndResubmitAsync` (re-queues for moderation) |
| Approved | Yes | Update fields → `UpdateAndResubmitAsync` (moves back to Pending for re-moderation) |
| Rejected | Yes | Update fields → `UpdateAndResubmitAsync` (re-queues for moderation) |
| ResubmitRequested | Yes | Update fields → `UpdateAndResubmitAsync` (re-queues for moderation) |
| Withdrawn | No | **Validation error** — row rejected, upload fails |

#### Files to Change

Modified:

- `src/Humans.Web/Controllers/EventsController.cs` — add `BulkUploadTemplate` (GET), `BulkUploadImport` (POST); add `ParseCsvRows` and `ValidateBulkRows` private helpers; update `MySubmissions` to read bulk upload errors from TempData
- `src/Humans.Web/Models/Events/BarrioEventViewModels.cs` — add `BulkRowError` (row number, title, error list); add `BulkUploadErrors` to the barrio block view model
- `src/Humans.Web/Views/Events/MySubmissions.cshtml` — add download link, file upload form, and error table to each barrio block

No changes needed to domain, service interface, or repository — all existing methods are sufficient (`GetCampSubmissionsAsync`, `SubmitEventAsync`, `UpdateAndResubmitAsync`). No EF migration.

#### Key Reused Pieces

| What | Where |
| --- | --- |
| `ResolveCampEventManagementAsync(slug)` | `EventsController` base helpers — auth guard |
| `GetCampSubmissionsAsync(campId)` | `IEventService` — fetches existing events for template |
| `SubmitEventAsync(event)` | `IEventService` — submits new events |
| `UpdateAndResubmitAsync(event)` | `IEventService` — updates + resubmits existing events |
| `GetActiveCategoriesAsync()` | `IEventService` — category lookup for validation |
| `ToInstant(date + time, tz)` | `EventsController` private helper — date+time → UTC Instant |

#### Verification

1. `dotnet build Humans.slnx -v quiet` — 0 errors.
2. `dotnet test Humans.slnx -v quiet` — all pass.
3. Manual:
   - Camp lead opens `/Events/MySubmissions` → barrio block shows download link and file upload form.
   - Download template → CSV has correct columns, existing events populated, Withdrawn events absent, comment lines at top.
   - Upload CSV with one new row (empty Id) → event appears in MySubmissions as Pending.
   - Upload same CSV again (now has an Id, fields unchanged) → event not duplicated, status preserved.
   - Upload CSV with a changed field on an existing event → event updated and re-queued for moderation.
   - Upload CSV with an invalid row (bad category, missing title, etc.) → error table shown, nothing saved.
   - Non-lead user attempts upload → 403.

### US-26.9: Moderator Exports the Print Guide

**As a** GuideModerator
**I want to** generate a print-ready PDF of all approved events
**So that** the layout team has no manual data extraction step

**Acceptance Criteria:**
- Export triggered on demand from the guide export page
- PDF contains all Approved events sorted by day then start time
- Events selected for the print guide respect submitter priority rank when total approved exceeds max print slots
- CSV export also available as a backup

## Data Model

| Entity | Purpose |
|--------|---------|
| `GuideSettings` | Singleton per edition: submission dates, guide publish date, timezone, max print slots |
| `EventCategory` | Lookup: name, slug, is_sensitive, display order, is_active |
| `GuideCamp` | Links a `Team` to guide-specific fields: camp name, description, grid address, is_published |
| `GuideSharedVenue` | Moderator-curated communal spaces (e.g. "The Middle of Elsewhere"): name, description, grid address, is_active |
| `GuideEvent` | Single event submission: anchored to GuideCamp OR GuideSharedVenue (exactly one), plus SubmitterUserId. Optional `Host` (≤ 40 chars) names who runs the event |
| `ModerationAction` | Append-only log of every moderation decision per GuideEvent |
| `UserGuidePreference` | Per-user excluded category slugs (JSON array), upserted on change |
| `UserEventFavourite` | User-to-GuideEvent favourite link; deleted on unfavourite |

## State Machine (GuideEvent)

```
Draft --> Pending           (Submit)
Pending --> Approved        (Moderator: Approve)
Pending --> Rejected        (Moderator: Reject)
Pending --> ResubmitRequested (Moderator: Request Edit)
Pending --> Withdrawn       (Submitter: withdraw)
Rejected --> Pending        (Submitter: resubmit)
ResubmitRequested --> Pending (Submitter: resubmit)
Approved --> Withdrawn      (Submitter or Moderator: withdraw — hides from guide)
```

Hard deletion is not supported; `Withdrawn` is the terminal state for events removed from the guide.

## Authorization Model

| Role | Permissions |
|------|------------|
| Admin | Full access: GuideSettings, categories, venues, moderation, exports |
| GuideModerator | Full event guide management: GuideSettings, categories, venues, moderation, exports |
| Team Lead | Submit and edit camp events for own team; view submission status |
| Any registered human | Submit and edit individual events at shared venues |
| Attendee (anonymous) | Read-only access to published guide via PWA API |

## Email Triggers

| Event | Recipient |
|-------|-----------|
| Event submitted | Submitter — confirmation |
| Moderation: Approved | Submitter — confirmation |
| Moderation: Rejected | Submitter — rejection with reason |
| Moderation: ResubmitRequested | Submitter — edit request with reason |

All emails use the existing `EmailOutboxMessage` / `ProcessEmailOutboxJob` infrastructure.

## Route Summary

| Route | Purpose |
|-------|---------|
| `/Events/MySubmissions` | Any human: unified view — own individual events plus a block per led barrio |
| `/Events/Submit` | Any human: individual event submission form |
| `/Events/Moderate` | GuideModerator: pending submissions queue |
| `/Admin/Guide*` | GuideModerator/Admin: GuideSettings, categories, venues |
| `/Events/Export` | GuideModerator/Admin: CSV and print-guide exports |
| `/Events/Barrio/{slug}/Submit` | Lead: submit/edit a barrio event (barrio block on My Event Submissions) |
| `/Events/Barrio/{slug}/BulkUpload` | Lead: download CSV template of existing events; upload updated CSV |
| `/api/events/events` | Public API: approved events (PWA data source) |
| `/api/events/barrios` | Public API: published barrios with hosted events |
| `/api/events/categories` | Public API: event categories |

## Related Features

- **Teams** (06): GuideCamp is anchored to a Team; Lead role gates barrio event submission
- **Profiles** (02): SubmitterUserId links GuideEvent to a user; UserGuidePreference and UserEventFavourite extend the user record
- **Audit Log** (12): ModerationAction provides an append-only decision trail per event
- **Shift Management** (25): Shares EventSettings (dates, timezone) and the Team/Department hierarchy
- **Email Outbox** (21): All guide email notifications route through the existing outbox infrastructure
