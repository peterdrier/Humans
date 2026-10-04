<!-- freshness:triggers
  src/Sections/Humans.Calendar/**
  tests/Humans.Calendar.Tests/**
  docs/guide/Calendar.md
-->

# Calendar — Target Shape

Regenerated every section-doctor run, before any scan. Not a description of today's code:
the shape the section's behavior implies. History rows at the bottom.

## 1. What the section does

Separate things that share a roof and no data path.

**A community calendar.** Anyone signed in can see what is happening across the
organisation — this month, as a grid or a list, or as an agenda of what is coming up — in a
display zone they choose on the page. Most of it is entries people put there: an entry
belongs to a team, is either a one-off or a repeating series, and a repeating series can
have a single one of its occurrences moved, retitled or dropped without disturbing the
rest. An entry either occupies whole days or runs between two clock times, and the two are
stored differently. The rest of what shows up is not the calendar's at all: other parts of
the system hand it things already on their own schedules, and those appear beside the
calendar's own entries, labelled with where they came from and linking back there. Nothing
is gated by role: anyone signed in can add, change or remove anything the calendar owns, for
any team, and every change is written to the audit trail instead. Deleting hides an entry
from the calendar and keeps it in the record.

**A personal subscription feed.** Each person has a private, secret URL that any calendar
app can subscribe to, listing the dated commitments they have made elsewhere in the system —
shifts they signed up for, guide events they favourited, working groups they are in. It
carries no calendar entries at all. The URL is shown to its owner on the month page, minted
the first time they look, and they can replace it, which kills every copy handed out
before. Anyone knowing the URL sees the feed; anything wrong with it looks identical to a
URL that was never issued. An admin can see the same list on a person's record and whether a
URL was ever minted, never the URL itself. A person's data export says whether they have a
feed, never the secret; deleting the person deletes it; merging two accounts drops the
absorbed one's.

## 2. The shapes

Every externally reachable thing, grouped by the question it answers.

| Question-shape | Surfaces answering it | Notes |
|---|---|---|
| *What is scheduled between these two instants (optionally for one team)?* | The routes `/Calendar`, `/Calendar/List`, `/Calendar/Agenda`, `/Calendar/Team/{teamId}`, plus `ICalendarServiceRead.GetOccurrencesInWindowAsync` | They differ in how the window is derived and how it is rendered. Nothing else. |
| *What is this one entry?* | `/Calendar/Event/{id}` route; `GetEventByIdAsync` (UI projection) and `GetEventInfoAsync` / `GetAllEventInfosAsync` (cache projections) | One question, several projections of it — all but the UI one exist for the cache, not for a caller. |
| *Change a whole entry* | `Event/Create`, `Event/{id}/Edit`, `Event/{id}/Delete`; `CreateEventWithResultAsync` / `UpdateEventWithResultAsync` / `DeleteEventAsync` on `ICalendarService` | One silhouette each. Create and update share one input record and are published only in their result-returning form. |
| *Change one occurrence of a repeating entry* | `Event/{id}/Occurrence/{originalStartUtc}/Edit`, `.../Cancel`; `OverrideOccurrenceAsync`, `CancelOccurrenceAsync` | Both are one upsert against the occurrence's identity with a different field-setter. The identity is a date for an all-day series and an instant for a timed one. |
| *What has this person committed to?* | `GET /api/ical/{userId}/{token}.ics`; `IICalFeedService.GetFeedItemsAsync` (Scanner's ticket card, admin widget); `<vc:user-calendar>` | Reads no Calendar table except the token check. |
| *What is my feed URL, and replace it* | the card on `/Calendar`; `POST /Calendar/Ical/Regenerate`; `ICalendarFeedTokenService`; `IICalFeedService.HasFeedAsync` | Mint on first view, rotate on demand. Only the yes/no leaves the section. |
| *What does Calendar hold about this person, erase it, fold it into another account* | `CalendarFeedTokenService` as `IUserDataContributor` and `IUserMerge` | Inbound fan-outs owned by Gdpr and Users; Calendar answers for the token row only. |
| *Contribute items to a person's feed, or to the community window* | `ICalendarFeedContributor` + `CalendarFeedItem` | Inbound-only: implementers reference Calendar, never the reverse. Two independent calls on one interface — a section may answer either, both, or neither with an empty list. |

Everything else the section exposes — `Section`, `CalendarResource`, `SectionNav`,
`SectionUserParts` — is framework discovery, not a question.

## 3. Structure

The layout those shapes imply, written fresh.

- **One window read.** A single path answers the window shape: snapshot the cached
  projections, prefilter against the window, expand recurrence, merge per-occurrence
  exceptions, merge whatever the contributors hand in, order once. It exists once, and
  every route reaches it through one shared window-resolver that turns *(year, month)* or
  *(from, to)* plus the viewer's zone into a pair of instants. A route contributes a window
  and a view name; nothing else. The read is told the viewer's zone, so the order it returns
  is the order a caller can render — nothing downstream re-derives it.
- **One entry read**, projected for the caller that asks: the UI gets the detail shape,
  the cache gets the row shape.
- **One silhouette per mutation**: validate the recurrence pair → repository write
  → audit (best-effort, loud on failure) → refresh this event's cache entry. The
  per-occurrence pair is one upsert with a field-setter argument. A mutation reports
  failure one way, not two.
- **All-day and timed are one entry type with two storages**, and the boundary between
  them is one place: everything above the projection sees dates for an all-day entry and
  instants for a timed one, whatever the row underneath actually holds.
- **The repository is the only thing that names a `DbSet`**, and it takes and returns
  entities. `OwningTeamId` is a bare Guid; team names are stitched in memory from
  `ITeamServiceRead` at the layer that renders them — once, not once per implementation.
- **The feed is a separate object with no repository**: validate the token through the
  token service, fan out over contributors in registration order, sort, serialize.
- **The token is its own small service over its own table**, uncached, with the
  user-lifecycle fan-outs registered beside it; nothing outside the section writes it.
- **Every user-facing string is a resource key**; admin surfaces are exempt.

## 4. Invariants

Stated so a violation is recognisable.

1. Anonymous callers reach exactly one endpoint in this section: the `.ics` feed
   (`Controllers/ICalFeedApiController.cs:24`). Everything else is `[Authorize]`
   (`Controllers/CalendarController.cs:17`).
2. The feed's failure modes — unknown user, merged user, no token issued, wrong token — are
   indistinguishable from outside: one null return covers all four
   (`Services/ICalFeedService.cs:66`, `Services/ICalFeedService.cs:71`), and the controller
   maps it to a bare 404 (`Controllers/ICalFeedApiController.cs:31`).
3. The feed URL and the token never appear in any rendered admin view: the view model
   carries a bool, not the token (`Contracts/UserCalendarViewComponent.cs:30`), and the view
   renders only that (`Views/Shared/Components/UserCalendar/Default.cshtml:10`).
4. Every mutation writes an audit entry naming the actor. Entry-level mutations also name
   the owning team (`Services/CalendarService.cs:90`); occurrence-level ones do not
   (`Services/CalendarService.cs:489`).
5. A failed audit write never rolls back or hides a committed change, and never passes
   silently (`Services/CalendarService.cs:94`).
6. On a timed entry, `RecurrenceRule` and `RecurrenceTimezone` are both set or both null —
   never one (`Domain/CalendarEvent.cs:54`). An all-day series carries a rule and no zone:
   its dates do not convert.
7. A malformed RRULE (`Services/CalendarService.cs:145`) or an unknown IANA zone
   (`Services/CalendarService.cs:154`) is rejected at write time, so no read can fail
   expanding a stored row. An all-day rule that would introduce a time of day is rejected
   with them (`Services/CalendarService.cs:254`).
8. `RecurrenceUntilUtc` bounds a timed series and `RecurrenceUntilDate` an all-day one, each
   the last point the rule can produce or null for open-ended rules, and
   `CalendarOccurrenceExpander.FilterForWindow` — the only prefilter — reads them as that
   and keeps every row carrying an exception, which can sit outside either bound
   (`Services/CalendarOccurrenceExpander.cs:200`).
9. A timed entry has a start and an end instant and no dates; an all-day entry has a start
   date and an exclusive end date and no instants (`Domain/CalendarEvent.cs:36`). Forms show
   the inclusive last day; `CalendarService.AllDayWindow` and `AllDayInclusiveEndDate` are
   the only conversion (`Services/CalendarService.cs:29`).
10. A row written before the date columns existed is read as dates once, at the projection
    boundary, in the zone it was written with (`Services/CalendarOccurrenceExpander.cs:231`).
    Nothing below that boundary needs a backfill, and nothing above it sees the old columns.
11. Soft-delete hides an entry (`Data/Configurations/CalendarEventConfiguration.cs:30`) and
    its exception rows (`Data/Configurations/CalendarEventExceptionConfiguration.cs:30`) from
    every read. The one deliberate exception is the upsert's existence lookup, which must see
    a row orphaned by a concurrent soft-delete or it violates the unique index
    (`Data/CalendarRepository.cs:98`).
12. At most one exception row per occurrence identity: unique on
    `(EventId, OriginalOccurrenceStartUtc)` for a timed series
    (`Data/Configurations/CalendarEventExceptionConfiguration.cs:19`) and on
    `(EventId, OriginalOccurrenceDate)` for an all-day one (`:25`).
13. An exception row either cancels its occurrence or overrides at least one field
    (`Domain/CalendarEventException.cs:41`).
14. A series that already has exceptions cannot switch between all-day and timed — its saved
    occurrence identities would stop naming anything (`Services/CalendarService.cs:281`).
15. An occurrence's identity in a URL is an ISO date for an all-day series and an ISO instant
    for a timed one, and the controller parses only the one the series is
    (`Controllers/CalendarController.cs:369`).
16. Every mutation leaves the cache agreeing with the database for that entry; per-occurrence
    writes refresh the parent entry, because there is no exception cache row
    (`Services/CachingCalendarService.cs:162`).
17. Contributor items reach the community window only when no team filter is applied — they
    belong to no team (`Services/CachingCalendarService.cs:41`). A contributor that throws
    costs the community calendar its items and nothing else
    (`Services/CachingCalendarService.cs:74`); one that throws on the personal feed fails the
    whole feed rather than serve a short one (`Services/ICalFeedService.cs:47`).
18. Any authenticated person may change any entry on any team. This is the policy, not an
    oversight — the audit trail is what replaces the gate
    (`Controllers/CalendarController.cs:184`).
19. A member has at most one token (`Data/Configurations/CalendarFeedTokenConfiguration.cs:17`);
    two first views racing to mint both end holding the winner's token rather than one of them
    failing (`Data/CalendarRepository.cs:160`); replacing it revokes every earlier URL
    (`Services/CalendarFeedTokenService.cs:33`).
20. The data export reports whether a feed exists, never the token
    (`Services/CalendarFeedTokenService.cs:46`); merging two accounts deletes the absorbed
    account's token rather than moving it (`Services/CalendarFeedTokenService.cs:76`).

## 5. Seams — specified, not built

Reserved, not ranked, not built this run.

- **A tier or ownership check on edit/delete.** The section doc names it ("no additional
  calendar-specific privileges *in v1*") and the code reserves its single switch point
  (`CanEdit`, hard-coded true in one place). Anything touching edit-button rendering is
  shaped by this seam.
- **The viewer's zone inside the window read.** The routes already resolve the viewer's
  chosen zone and turn it into instants; the read itself still derives all-day window dates
  and display order in the organisation's zone. Every view clips to its own days, so the
  result is a superset, never a loss.
- **Calendar entries in the personal feed.** The halves of the section share no data path
  today; the cache projection was designed to absorb that traffic if they ever do.

## 6. Deliberately not done

- **Resource-based authorization on calendar entries.** Open by design; the audit log is the
  control. Not an unfinished gate.
- **An FK from `calendar_events` to the Teams tables**, or an `OwningTeam` nav. Cross-section
  FK constraints were removed org-wide; the in-memory stitch is the replacement.
- **A `Humans.Calendar.Contracts` leaf project.** A leaf exists only where a cycle forces one.
  The contributor fan-out inverts the arrow, so a folder is enough.
- **A cache row per exception.** Exceptions live inside their parent's projection; the parent
  is the eviction unit.
- **A cache, or any persistence, for contributed items.** They belong to the sections that
  hand them over; the window read asks for them every time.
- **A cache for feed tokens.** One lookup per feed fetch or month-page view; nothing to save.
- **An index on the feed token.** Validation is a lookup by member plus a compare, never a
  lookup by token.
- **A parallel contributor fan-out.** Independent factory-created contexts would be safe;
  sequential is kept for consistency with the other fan-outs, not for safety.
- **An index on the all-day date columns.** The window prefilter is an in-memory scan of the
  cache; the only SQL read is load-everything.
- **Pagination, or SQL-side occurrence expansion.** The whole event set fits in memory at this
  org's size.
- **Deleting calendar entries with their creator.** An entry is not personal data; after the
  creator is erased it renders with a deleted creator (Peter's ruling on
  nobodies-collective/Humans#1199, which keeps the export half open).

## Load-bearing weirdness

Settled decisions. Later runs should stop re-litigating these.

- **`AuditEntityTypes` are string literals, never `nameof`.** They are persisted values matched
  by equality against rows already in the database; regenerating them from CLR names would
  silently change what is written and queried.
- **`UpsertExceptionAsync` calls `IgnoreQueryFilters()` on its existence lookup.** Deliberate:
  a parent soft-deleted between the caller's check and the upsert would otherwise cause a
  duplicate insert against the unique index.
- **All-day ends are stored exclusive and displayed inclusive.** The one-day subtraction in the
  edit form is the conversion back, not an off-by-one.
- **The old instant columns stay on all-day rows.** New all-day writes use the date columns
  only; the projection reads either. No backfill was run and none is owed — the read is the
  migration.
- **The two contributor fan-outs fail in opposite directions on purpose.** A short personal
  feed would silently lose someone's commitments, so it throws; a broken contributor must not
  take down the whole community calendar, so the community window logs and skips it.
- **The caching decorator answers the window and detail reads from its snapshot, and it is the
  only implementation of them.** `ICalendarServiceRead` is deliberately not part of
  `ICalendarService`: the keyed inner would only be able to implement it with code nothing can
  reach. `GetAllAsync` is the only bulk read, and `CalendarOccurrenceExpander.FilterForWindow`
  the only window prefilter.
- **The token mint catches the primary-key collision instead of locking.** Two first views of
  `/Calendar` can race; the loser reloads and returns the winner's token. No concurrency token
  is wanted.
- **`CalendarResource` is public** only so the boot localization diagnostic can find it via
  `GetExportedTypes()`.

## History

| Run | Date | Headline | PR |
|---|---|---|---|
| 1 | 2026-09-01 | List view rendered all-day and multi-day events wrong; documented-but-unpinned invariants given tests; false crefs and a phantom `OwningTeam` nav cut | peterdrier/Humans#1578 |
| 2 | 2026-09-19 | Workgroups' contributor named everywhere the set was enumerated as Shifts + Events only; comments describing code that is not there corrected; resolved debt rows and dead prose cut | peterdrier/Humans#1744 |
| 3 | 2026-10-04 | Guide said editing a series changes only future occurrences and never mentioned the personal feed; target gained the feed-token half; the audit-failure LogCritical pinned | peterdrier/Humans#1898 |
