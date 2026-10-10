# Events — Health

Target shape, derived fresh each `/section-doctor` run before any scan. History at the bottom.

## 1. What the section does

Runs the festival programme. People propose things to do — on their own or on behalf of their
barrio — during a window the organisers open and close. Moderators decide what goes in, and can
correct a listing without knocking it off the programme. Everyone else browses what was accepted,
hearts the ones they want, and gets that back as a personal schedule, an iCal feed, and a printed
guide. Organisers configure the window, the taxonomy of activity types, and the named places
things happen. When a person leaves, their hearts and preferences go with them and their name
comes off their listings; the programme itself stays whole.

## 2. The shapes

Every route, contract method, component and job answers exactly one of these.

| Shape | The question | Answered by |
|---|---|---|
| **Programme** | what is on? | `/Events/Browse`, `/api/events/events`, `/api/events/events/{id}`, `/api/events/barrios`, `/api/events/barrios/{id}`, `/api/events/categories`, `EventsCard` + `EventsSearchResult` view components, all of `IEventServiceRead` |
| **Mine** | what did I pick or propose? | `/Events/MySubmissions`, `/Events/Schedule`, `/api/events/favourites` (GET/POST/DELETE), `/api/events/preferences` (GET/PUT), `ICalendarFeedContributor`, `IUserDataContributor` |
| **Propose** | I want to run something | `/Events/Submit` (+`/{id}/Edit`, `/Withdraw`), `/Events/Barrio/{slug}/*` (submit, edit, withdraw, bulk upload + template) |
| **Decide** | should this be in the programme? | `/Events/Moderate` (queue, Approve, Reject, RequestEdit, Withdraw) and `/Events/Moderate/{id}/Edit` (in-place, status-preserving) |
| **Configure** | what are the rules of this edition? | the `/Settings#event-guide` tab (`EventGuideSettingsTabViewComponent`, saved by `POST /Events/Admin/Settings`), `/Events/Admin/Categories`, `/Events/Admin/Venues` |
| **Publish** | get the programme out | `/Events/Dashboard`, `/Events/Export` (+`/Csv`, `/PrintGuide`) |

Collapse pressure this grouping exposes: **Programme** is answered by every route in its row over
one cached snapshot. Occurrence expansion itself is now one helper
(`EventRecurrenceDays.GetOccurrenceInstants`), but each caller still writes out the
"gate date and zone known, else the single start" guard and its own camp-name and submitter-name
fallback; **Propose** has near-identical form pipelines (individual, barrio) that differ only in
which fields apply.

## 3. Structure

The layout the shapes imply:

- One repository over the seven owned tables; one service; one caching decorator over the
  approved-only projections. As built.
- **Programme** wants one occurrence-and-naming projection, used by Browse, Schedule, the API, the
  export and the print guide alike. The expansion is shared; the guard and the naming are not
  (`EVENTS-1`).
- **Propose** wants one form pipeline parameterised by `isCampEvent`, not two view models and two
  dropdown-population methods. `EventsModerationController` already runs the single-pipeline
  version (`AdminEventFormViewModel` + `ApplyFormToEvent` branch on `isCampEvent`) — that is the
  target form; the submitter side is the outlier.
- **Decide** and **Configure** are correct as built.
- Cross-section reads (camp names, submitter names, the edition's dates and zone) go through the
  helpers in `EventsLookupHelpers`; the edition row is Settings' and is read only through
  `ISettingsService`.
- The GDPR contributor sits on the caching decorator, because erasure edits rows the cache serves
  and the inner service cannot invalidate what it does not own.

## 4. Invariants

Behavioural facts stated so a violation is recognisable, each with the line that enforces it.
(`Docs/Events.md` holds the full list; these are the ones this shape rests on.)

1. Only `Pending` events accept a moderation decision — `Domain/Event.cs:188`.
2. An admin in-place edit preserves `Status` — an approved listing is never silently re-queued —
   `Services/Service.cs:283`.
3. Nothing unapproved leaves through `/api/events*`: every read there is served from the
   approved-only projection — `Data/Repository.cs:264`, `Data/Repository.cs:291`,
   `Data/Repository.cs:389`.
4. Favourites and preferences are same-origin and self-scoped: CORS is off on them and the user id
   comes only from the signed-in principal — `Controllers/EventsApiController.cs:161`,
   `Controllers/EventsApiController.cs:166`; favourite writes also require the antiforgery token —
   `Controllers/EventsApiController.cs:242`.
5. A new submission — individual, barrio, or bulk upload — is accepted only inside
   `[SubmissionOpenAt, SubmissionCloseAt]` — `Controllers/EventsController.cs:145`,
   `Controllers/EventsController.cs:546`, `Controllers/EventsController.cs:720`. Editing or
   withdrawing an existing submission is not window-gated, by design: late corrections still reach a
   moderator.
6. Bulk CSV import is all-or-nothing, and its template round-trips: exporting a camp's events and
   re-uploading them unchanged is a no-op — `Services/Service.cs:307`, `Services/Service.cs:354`.
7. Moderation history is append-only: the repository only ever adds to it —
   `Data/Repository.cs:365`.
8. `StartAt` is stored as an `Instant`; every local rendering goes through the edition's zone —
   `Helpers/EventsTimeHelpers.cs:16`.
9. Erasure deletes a person's favourites and preference and blanks `Host` on their submissions;
   the submissions themselves survive, and the approved-events cache reflects the blanked host
   immediately — `Data/Repository.cs:485`, `Data/Repository.cs:501`,
   `Services/CachingEventService.cs:482`.

## 5. Seams

- Individual events have a `Draft` status the domain honours (`IsEditableBySubmitter`) that no
  route ever produces. Either a seam for a save-without-submitting flow, or dead (Peter deferred
  the call).
- `IEventViewInvalidator` has no caller: Settings-side saves reach the decorator through
  `IEventSettingsChangeListener`. It is kept as a reserved seat at Peter's direction.

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
- **`CachingEventService` is the `IUserDataContributor`, not `EventService`.** Erasure blanks
  `Host` on rows the approved-events cache serves; binding the inner service would leave the
  erased name visible until the next unrelated write.
- **`PriorityRank` is camp-events-only.** Camp events carry 1–100 (print-guide ordering) or
  null = unranked (sorted last); individual events are always null. The bulk validator's `1..100`
  applies only when a value is present; blank round-trips as blank.
- **Recurrence is stored as day offsets from gate opening, displayed as weekday names.** Bulk
  import compares by day-name set, not the raw string, so a lossless round-trip is not read as an
  edit.
- **`GuideEventId`, not `EventId`.** The column names predate the rename to Events; they are load-
  bearing in the migration baseline.
- **`EventGuideSettings.EventSettingsId` points at Settings' `settings_event`, not Shifts'
  `event_settings`.** Both tables exist; Shifts' keeps its name and is no input to this section.

## History

| Run | Date | Reforge | Notes |
|---|---|---|---|
| 1 | 2026-08-24 | 258 (loc=6367, cogP95=9, cogMax=35) | first pass — peterdrier/Humans#1483 |
| 2 | 2026-09-09 | — | docs and comments caught up to the two August fixes; one settings lookup for every controller; the moderation queue names its moderators — peterdrier/Humans#1621 |
| 3 | 2026-10-07 | — | docs, comments and the user guide name Settings as the edition's owner; the Guide Settings link reaches its tab — peterdrier/Humans#1931 |
