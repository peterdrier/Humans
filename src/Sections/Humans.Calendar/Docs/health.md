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

**A community calendar.** Anyone signed in can see what every team has scheduled — this
month, as a grid or a list, or as an agenda of what is coming up — and can add, change or
remove anything on it, for any team. An entry is either a one-off or a repeating series;
a repeating series can have a single one of its occurrences moved, retitled or dropped
without disturbing the rest. Nothing is gated by role: every change is written to the
audit trail instead, and that record is the safeguard. Deleting hides an entry from the
calendar and keeps it in the record.

**A personal subscription feed.** Each person has a private, secret URL that any calendar
app can subscribe to, listing the dated commitments they have made elsewhere in the system
— shifts they signed up for, guide events they favourited. It carries no calendar entries
at all. Anyone knowing the URL sees the feed; anything wrong with it looks identical to a
URL that was never issued. An admin can see the same list on a person's record, never the
URL itself.

## 2. The shapes

Every externally reachable thing, grouped by the question it answers.

| Question-shape | Surfaces answering it | Notes |
|---|---|---|
| *What is scheduled between these two instants (optionally for one team)?* | The routes `/Calendar`, `/Calendar/List`, `/Calendar/Agenda`, `/Calendar/Team/{id}`, plus `ICalendarServiceRead.GetOccurrencesInWindowAsync` | They differ in how the window is derived and how it is rendered. Nothing else. |
| *What is this one entry?* | `/Calendar/Event/{id}` route; `GetEventByIdAsync` (UI projection) and `GetEventInfoAsync` / `GetAllEventInfosAsync` (cache projections) | One question, several projections of it — all but the UI one exist for the cache, not for a caller. |
| *Change a whole entry* | `Event/Create`, `Event/{id}/Edit`, `Event/{id}/Delete`; `Create`/`Update`/`Delete` on `ICalendarService` | Create and update each ship twice: a throwing form and a result form. |
| *Change one occurrence of a repeating entry* | `Event/{id}/Occurrence/{start}/Edit`, `.../Cancel`; `OverrideOccurrenceAsync`, `CancelOccurrenceAsync` | Both are one upsert against `(event, original start)` with a different field-setter. |
| *What has this person committed to?* | `GET /api/ical/{userId}/{token}.ics`; `IICalFeedService.GetFeedItemsAsync` (Scanner's ticket card, admin widget); `<vc:user-calendar>` | The only public cross-section surface. Table-free. |
| *Contribute items to a person's feed* | `ICalendarFeedContributor` + `CalendarFeedItem` | Inbound-only: implementers reference Calendar, never the reverse. |

Everything else the section exposes — `Section`, `CalendarResource`, `SectionNav` — is
framework discovery, not a question.

## 3. Structure

The layout those shapes imply, written fresh.

- **One window read.** A single path answers the window shape: snapshot the cached
  projections, prefilter by `(StartUtc, RecurrenceUntilUtc)` against the window, expand
  recurrence, merge per-occurrence exceptions, sort. It exists once, and every route
  reaches it through one shared window-resolver that turns *(year, month)* or *(from, to)*
  plus the viewer's zone into a pair of instants. A route contributes a window and a view
  name; nothing else.
- **One entry read**, projected for the caller that asks: the UI gets the detail shape,
  the cache gets the row shape.
- **One silhouette per mutation**: validate the recurrence pair → repository write
  → audit (best-effort, loud on failure) → refresh this event's cache entry. The
  per-occurrence pair is one upsert with a field-setter argument. A mutation reports
  failure one way, not two.
- **The repository is the only thing that names a `DbSet`**, and it takes and returns
  entities. `OwningTeamId` is a bare Guid; team names are stitched in memory from
  `ITeamServiceRead` at the layer that renders them — once, not once per implementation.
- **The feed is a separate object with no repository**: validate the token through
  `IUserServiceRead`, fan out over contributors in registration order, sort, serialize.
  A contributor failing is loud, never a silently short feed.
- **Every user-facing string is a resource key**; admin surfaces are exempt.

## 4. Invariants

Stated so a violation is recognisable.

1. Anonymous callers reach exactly one endpoint in this section: the `.ics` feed. Everything
   else is `[Authorize]`.
2. The feed's failure modes — unknown user, merged user, no token issued, wrong token — are
   indistinguishable from outside: all 404, no body, no timing tell.
3. The feed URL and the token never appear in any rendered admin view.
4. Every mutation writes an audit entry naming the actor. Entry-level
   mutations also name the owning team; occurrence-level ones do not.
5. A failed audit write never rolls back or hides a committed change, and never passes silently.
6. `RecurrenceRule` and `RecurrenceTimezone` are both set or both null — never one.
7. A malformed RRULE or an unknown IANA zone is rejected at write time, so no read can
   fail expanding a stored row.
8. `RecurrenceUntilUtc` is the last instant the rule can produce, or null for open-ended
   rules; the in-memory prefilter and the SQL prefilter agree on it exactly.
9. A timed entry has an end; an all-day entry stores its end as exclusive midnight after
   the last covered day, and the display layer converts back to inclusive. Legacy all-day
   rows with no end are single-day.
10. Soft-delete hides an entry and its exception rows from every read. The one deliberate
    exception is the upsert's existence lookup, which must see a row orphaned by a
    concurrent soft-delete or it violates the unique index.
11. At most one exception row per `(event, original occurrence start)`.
12. An exception row either cancels its occurrence or overrides at least one field.
13. Every mutation leaves the cache agreeing with the database for that entry; per-occurrence
    writes refresh the parent entry, because there is no exception cache row.
14. Any authenticated person may change any entry on any team. This is the policy, not an
    oversight — the audit trail is what replaces the gate.

## 5. Seams — specified, not built

Reserved, not ranked, not built this run.

- **A tier or ownership check on edit/delete.** The section doc names it ("no additional
  calendar-specific privileges *in v1*") and the code reserves its single switch point
  (`CanEdit`, hard-coded true in one place). Anything touching edit-button rendering is
  shaped by this seam.
- **A viewer timezone.** Every view resolves `Europe/Madrid` from one private helper, marked
  as awaiting a browser/profile source. Every window route is a future caller.
- **The feed's deep-link base**, hardcoded in `CalendarFeedItem` and marked for configuration.
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
- **A parallel contributor fan-out.** Independent factory-created contexts would be safe;
  sequential is kept for consistency with the other fan-outs, not for safety.
- **Pagination, or SQL-side occurrence expansion.** The whole event set fits in memory at this
  org's size.

## Load-bearing weirdness

Settled decisions. Later runs should stop re-litigating these.

- **`AuditEntityTypes` are string literals, never `nameof`.** They are persisted values matched
  by equality against rows already in the database; regenerating them from CLR names would
  silently change what is written and queried.
- **`UpsertExceptionAsync` calls `IgnoreQueryFilters()` on its existence lookup.** Deliberate:
  a parent soft-deleted between the caller's check and the upsert would otherwise cause a
  duplicate insert against the unique index.
- **All-day ends are stored exclusive and displayed inclusive.** The one-tick subtraction in the
  edit form is the conversion back, not an off-by-one.
- **The caching decorator answers reads from its snapshot without consulting the inner service.**
  The inner's window and detail reads therefore do not execute in production; they exist because
  the interface is shared. That is a consequence of the §15 decorator shape, not dead code
  someone forgot.
- **`CalendarResource` is public** only so the boot localization diagnostic can find it via
  `GetExportedTypes()`.

## History

| Run | Date | Headline | PR |
|---|---|---|---|
| 1 | 2026-09-01 | List view rendered all-day and multi-day events wrong; documented-but-unpinned invariants given tests; false crefs and a phantom `OwningTeam` nav cut | peterdrier/Humans#1578 |
