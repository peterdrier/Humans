# Event Guide Management

## Business Context

Elsewhere publishes a digital event guide (currently a standalone PWA) listing all scheduled events, theme camps, and communal locations during the event. The guide management system moves content submission, moderation, and publication into Humans so that camp organisers and individual humans can submit events through one platform, moderators have a structured review queue, and the PWA is served from Humans' API rather than a manually maintained static file.

Two types of events exist: **camp events** (submitted by a team Lead, anchored to a GuideCamp) and **individual events** (submitted by any registered human, anchored to a communal GuideSharedVenue such as "The Middle of Elsewhere").

See `docs/specs/event-guide-proposal-v1.md` for the full design specification.

## User Stories

### US-26.1: Admin Configures the Guide
**As an** Admin
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
- Submission form accessible from the team page
- Fields: title (≤ 80 chars), description (≤ 300 chars), category, date/time, duration, location note, is_recurring, recurrence pattern, priority rank
- Event is anchored to the team's GuideCamp; GuideCamp is auto-created on first submission if not yet present
- Submission creates a GuideEvent in `Pending` status
- Submitter receives email confirmation on submission
- Submitter can edit a Pending or ResubmitRequested event
- Status of all team submissions visible on the team page

### US-26.3: Individual Human Submits an Event
**As any** registered human
**I want to** submit an event at a communal venue
**So that** individually organised activities appear in the guide

**Acceptance Criteria:**
- Submission form accessible from profile page or a dedicated link
- Same fields as camp events except location is chosen from the GuideSharedVenue list, not a team camp
- An optional location note can add specificity (e.g. "near the fire pit")
- Event is attributed to the submitter's name (or chosen display name) in the published guide
- Same email notifications and status visibility as camp organiser flow

### US-26.4: Moderator Reviews Submissions
**As a** moderator
**I want to** review all pending event submissions in a queue
**So that** only appropriate content is published in the guide

**Acceptance Criteria:**

- Queue at `/Event/Moderate` lists all Pending submissions in order of receipt
- Duplicate flag shown when a submission shares a camp and overlapping time slot with an existing Pending or Approved event (advisory only — moderator decides)
- Actions: Approve, Reject (requires reason), Request Edit (requires reason)
- On Reject or Request Edit: submitter receives email with the reason
- On Approve: submitter receives confirmation email
- All decisions logged as append-only ModerationAction records

### US-26.5: Submitter Responds to Rejection / Edit Request
**As a** submitter (camp organiser or individual human)
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
- Published API (`/api/guide/events`, `/api/guide/camps`, `/api/guide/venues`) returns only Approved events
- Filter by day, time of day, and category
- Keyword search across title and description
- Event detail includes camp name or shared venue name, grid address, time, duration, full description
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

### US-26.9: Admin Exports the Print Guide
**As an** Admin
**I want to** generate a print-ready PDF of all approved events
**So that** the layout team has no manual data extraction step

**Acceptance Criteria:**
- Export triggered on demand from the admin panel
- PDF contains all Approved events sorted by day then start time
- Events selected for the print guide respect submitter priority rank when total approved exceeds max print slots
- CSV export also available as a backup

## Data Model

| Entity | Purpose |
|--------|---------|
| `GuideSettings` | Singleton per edition: submission dates, guide publish date, timezone, max print slots |
| `EventCategory` | Lookup: name, slug, is_sensitive, display order, is_active |
| `GuideCamp` | Links a `Team` to guide-specific fields: camp name, description, grid address, is_published |
| `GuideSharedVenue` | Admin-curated communal spaces (e.g. "The Middle of Elsewhere"): name, description, grid address, is_active |
| `GuideEvent` | Single event submission: anchored to GuideCamp OR GuideSharedVenue (exactly one), plus SubmitterUserId |
| `ModerationAction` | Append-only log of every moderation decision per GuideEvent |
| `UserGuidePreference` | Per-user excluded category slugs (JSON array), upserted on change |
| `UserEventFavourite` | User-to-GuideEvent favourite link; deleted on unfavourite |

## State Machine (GuideEvent)

```
Draft --> Pending           (Submit)
Pending --> Approved        (Moderator: Approve)
Pending --> Rejected        (Moderator: Reject)
Pending --> ResubmitRequested (Moderator: Request Edit)
Rejected --> Pending        (Submitter: resubmit)
ResubmitRequested --> Pending (Submitter: resubmit)
Approved --> Deactivated    (Admin: soft delete — hides from guide, preserves audit trail)
```

Hard deletion is blocked if any ModerationAction exists for the event.

## Authorization Model

| Role | Permissions |
|------|------------|
| Admin | Full access: GuideSettings, categories, venues, moderation, exports |
| Moderator | Access moderation queue: approve, reject, request edit |
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

## Routes

| Route | Purpose |
|-------|---------|
| `/Event/Submit` | Any human: individual event submission form |
| `/Event/Moderate` | Moderator: pending submissions queue |
| `/Event/Admin` | Admin: GuideSettings, categories, venues, exports |
| `/Teams/{slug}/Events` | Lead: submit and manage camp events for a team |
| `/api/guide/events` | Public API: approved events (PWA data source) |
| `/api/guide/camps` | Public API: published camps with hosted events |
| `/api/guide/venues` | Public API: active shared venues |

## Related Features

- **Teams** (06): GuideCamp is anchored to a Team; Lead role gates camp event submission
- **Profiles** (02): SubmitterUserId links GuideEvent to a user; UserGuidePreference and UserEventFavourite extend the user record
- **Audit Log** (12): ModerationAction provides an append-only decision trail per event
- **Shift Management** (25): Shares EventSettings (dates, timezone) and the Team/Department hierarchy
- **Email Outbox** (21): All guide email notifications route through the existing outbox infrastructure
