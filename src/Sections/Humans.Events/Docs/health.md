# Events — Health

Target shape, derived fresh each `/section-doctor` run before any scan. History at the bottom.

## 1. What the section does

Runs the festival programme. People propose things to do — on their own or on behalf of their
barrio — during a window the organisers open and close. Moderators decide what goes in, and can
correct a listing without knocking it off the programme. Everyone else browses what was accepted,
hearts the ones they want, and gets that back as a personal schedule, an iCal feed, and a printed
guide. Organisers configure the window, the taxonomy of activity types, and the named places
things happen.

## 2. The shapes

Six question-shapes. Every route, contract method, component and job answers exactly one.

| Shape | The question | Answered by |
|---|---|---|
| **Programme** | what is on? | `/Events/Browse`, `/api/events`, `/api/events/{id}`, `/api/barrios`, `/api/barrios/{id}`, `/api/categories`, `EventsCard` + `EventsSearchResult` view components, all of `IEventServiceRead` |
| **Mine** | what did I pick or propose? | `/Events/MySubmissions`, `/Events/Schedule`, `/api/events/favourites` (GET/POST/DELETE), `/api/events/preferences` (GET/PUT), `ICalendarFeedContributor`, `IUserDataContributor` |
| **Propose** | I want to run something | `/Events/Submit` (+`/{id}/Edit`, `/Withdraw`), `/Events/Barrio/{slug}/*` (submit, edit, withdraw, bulk upload + template) |
| **Decide** | should this be in the programme? | `/Events/Moderate` (queue, Approve, Reject, RequestEdit, Withdraw) and `/Events/Moderate/{id}/Edit` (in-place, status-preserving) |
| **Configure** | what are the rules of this edition? | `/Events/Admin/Settings`, `/Events/Admin/Categories`, `/Events/Admin/Venues` |
| **Publish** | get the programme out | `/Events/Dashboard`, `/Events/Export/Csv`, `/Events/Export/PrintGuide` |

Collapse pressure this grouping exposes: **Programme** is answered five different ways over one
cached snapshot, each re-deriving occurrence expansion, camp-name resolution and submitter-name
fallback from scratch; **Propose** has two near-identical form pipelines (individual, barrio) that
differ only in which two fields apply.

## 3. Structure

The layout the shapes imply:

- One repository over the seven owned tables; one service; one caching decorator over the
  approved-only projections. As built.
- **Programme** wants one shared occurrence-and-naming projection, used by Browse, the API, the
  export and the print guide alike. Today each caller re-implements it; `EventOccurrenceExpander`
  covers only Browse.
- **Propose** wants one form pipeline parameterised by `isCampEvent`, not two view models and two
  dropdown-population methods. `EventsModerationController` already runs the single-pipeline
  version (`AdminEventFormViewModel` + `ApplyFormToEvent` branch on `isCampEvent`) — that is the
  target form; the submitter side is the outlier.
- **Decide** and **Configure** are correct as built.
- Cross-section reads (camp names, submitter names) belong behind the two helpers in
  `EventsLookupHelpers`; every controller should go through them rather than hand-rolling the loop.

## 4. Invariants

Behavioural facts stated so a violation is recognisable. (`Docs/Events.md` holds the full list;
these are the ones this shape rests on.)

1. Only `Pending` events accept a moderation decision.
2. An admin in-place edit preserves `Status` — an approved listing is never silently re-queued.
3. Nothing unapproved leaves through `/api/events*`.
4. Favourites and preferences are same-origin and self-scoped; no cross-user read exists.
5. A submission is accepted only inside `[SubmissionOpenAt, SubmissionCloseAt]`.
6. Bulk CSV import is all-or-nothing, and its template round-trips: exporting a camp's events and
   re-uploading them unchanged is a no-op.
7. Moderation history is append-only.
8. `StartAt` is stored UTC; every local rendering goes through the burn timezone.

## 5. Seams

- **#719** — no invalidation hook when the Shifts-owned `event_settings` row changes, so a cached
  `TimeZoneId` stays stale until the next Events write. `IEventViewInvalidator` is the reserved
  seat.
- Individual events have a `Draft` status the domain honours (`IsEditableBySubmitter`) that no
  route ever produces. Either a seam for a save-without-submitting flow, or dead.

## 6. Deliberately not done

- **No per-event public page.** The calendar feed points at `/Events/Schedule` on purpose.
- **No camp-scoped moderation.** Moderation authority is global (EventsAdmin/Admin); barrio leads
  submit, they do not decide.
- **No cache for the moderation queue.** It needs a live pending count the approved-only cache
  cannot answer, so `GetAllEventsForDashboardAsync` stays direct-DB.
- **No CSV injection escaping on the bulk template.** It is a round-trip data file; escaping would
  come back as data.

## Load-bearing weirdness

- **`SearchAsync` throws on the inner service.** Deliberate: search is cache-only, so reaching the
  inner `EventService` proves a DI mistake. Mirrors Teams and Camps.
- **`CachingEventService` is its own `IHostedService`.** Warm-up must run on the same Singleton
  that serves reads.
- **`PriorityRank` is camp-events-only.** Camp events carry 1–100 (print-guide ordering) or
  null = unranked (sorted last); individual events are always null. The bulk validator's `1..100`
  applies only when a value is present; blank round-trips as blank.
- **Recurrence is stored as day offsets from gate opening, displayed as weekday names.** Bulk
  import compares by day-name set, not the raw string, so a lossless round-trip is not read as an
  edit.
- **`GuideEventId`, not `EventId`.** The column names predate the rename to Events; they are load-
  bearing in the migration baseline.

## History

| Run | Date | Reforge | Notes |
|---|---|---|---|
| 1 | 2026-08-24 | 258 (loc=6367, cogP95=9, cogMax=35) | first pass — peterdrier/Humans#1483 |
