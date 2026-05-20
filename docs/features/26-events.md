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

## Routes

| Route | Purpose |
|-------|---------|
| `/Events/MySubmissions` | Any human: unified view — own individual events plus a block per led barrio |
| `/Events/Submit` | Any human: individual event submission form |
| `/Events/Moderate` | GuideModerator: pending submissions queue |
| `/Admin/Guide*` | GuideModerator/Admin: GuideSettings, categories, venues |
| `/Events/Export` | GuideModerator/Admin: CSV and print-guide exports |
| `/Events/Barrio/{slug}/Submit` | Lead: submit/edit a barrio event (barrio block on My Event Submissions) |
| `/api/events/events` | Public API: approved events (PWA data source) |
| `/api/events/barrios` | Public API: published barrios with hosted events |
| `/api/events/categories` | Public API: event categories |

## Related Features

- **Teams** (06): GuideCamp is anchored to a Team; Lead role gates barrio event submission
- **Profiles** (02): SubmitterUserId links GuideEvent to a user; UserGuidePreference and UserEventFavourite extend the user record
- **Audit Log** (12): ModerationAction provides an append-only decision trail per event
- **Shift Management** (25): Shares EventSettings (dates, timezone) and the Team/Department hierarchy
- **Email Outbox** (21): All guide email notifications route through the existing outbox infrastructure
