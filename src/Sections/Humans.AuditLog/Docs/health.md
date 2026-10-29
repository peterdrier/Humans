# AuditLog — Target Shape

Derived shape for the section-doctor cycle. What AuditLog should be, written to make a
violation recognisable. Not a changelog — the run files under `docs/health/runs/` carry history.

## History

| Run | Date | Headline | PR |
|---|---|---|---|
| 1 | 2026-08-27 | Delete dead `GetRecentAsync` read chain; true up section doc | [#1545](https://github.com/peterdrier/Humans/pull/1545) |
| 2 | 2026-10-29 | Label every audit action in all six languages; one read per per-person question; true up the docs | pending |

## 1. What the section does

A crosscut that records what members, admins and jobs did to which record, as an append-only
trail, and shows that trail back. Every other section writes into it after a privileged or
irreversible action; it writes into no other section. Two faces:

- **Write** — any service or job appends an entry. The append saves on its own and is
  best-effort: a failure is logged loudly and swallowed, so it never breaks the business step that
  called it. Callers append after their own save, so a rolled-back business change leaves no row.
- **Read and show** — a Board/Admin browser at `/AuditLog`, a history panel any host page drops in
  through one shared component, a recent-activity card on the admin dashboard, and a plain-text
  feed for the agent's personal-history tool. Every shown entry names people and teams, never
  raw ids, and the section names none of the sections whose ids it shows.

It also contributes each person's slice to the GDPR export, and keeps everything: there is no
prune or delete path, by design (Art. 30 / Art. 17(3)(b)).

## 2. The shapes

| Shape | Surface | Callers |
|---|---|---|
| Append an action | `IAuditLogService.LogAsync` (human and job overloads) | most sections, after a privileged action |
| Read rows as data | `IAuditLogService.GetFilteredEntriesAsync` | Issues (interleaves audit with comments), Expenses (report history), Users (duplicate-account scan) |
| Which entities ever had these actions | `IAuditLogService.GetEntityIdsForEntityTypeActionsAsync` | Shifts orphan-signup screen |
| Named events for a page, a filter or a person | `IAuditViewerService` (`GetPageAsync`, `GetFilteredAsync`, `GetForUserAsync`) | `/AuditLog`, admin tile, `<vc:audit-log>`, Agent tool |
| Show history on a host page | `<vc:audit-log>`, layouts `line` / `table` / `activity` | Calendar, Debug, Expenses, Store, Teams, Tickets, Users, the admin dashboard card |
| Legacy Google-column read | `ILegacyGoogleSyncAuditReader` | GoogleIntegration's history-migration screen; leaves with those columns |
| GDPR export slice | `IUserDataContributor` | Gdpr orchestrator |

## 3. Structure

- **Contracts, one folder.** `Contracts/` (namespace `Humans.AuditLog.Contracts`) holds the
  append path (`IAuditLogService`, `AuditLogEntrySnapshot`, `AuditAction`,
  `ILegacyGoogleSyncAuditReader`) and the read surface (`IAuditViewerService`, `AuditEvent`,
  `AuditEventPage`).
- **Write and raw reads:** `AuditLogService` (internal; implements `IAuditLogService`,
  `IAuditLogReader`, `IUserDataContributor`, `ILegacyGoogleSyncAuditReader`) over
  `IAuditLogRepository`, the only type touching `AuditLogDbContext.AuditLogEntries`, through
  `IDbContextFactory`. One repository read per question: a per-person read takes the
  merge-resolved id list whatever its length.
- **Read and name:** `AuditViewerService` (internal) wraps the internal `IAuditLogReader` reads
  with the `IEntityNameContributor` fan-out and returns `AuditEvent`. Verb tables live once, in
  `AuditEventTextualizer`, shared by the agent's plain text and the HTML entry partial.
- **UI:** one controller action (`AuditLogController.Index`), one public `<vc:audit-log>`
  component with a view per layout, one internal `AdminActivityCard` chrome component.
- **Resources:** `AuditLogResource` in all six cultures for the member-facing entry views
  (`_Entry`, `Table`); the `/AuditLog` page and the activity feed are admin-only English.

## 4. Invariants

- Append-only: the repository's one write is `AddAsync` (`Data/IAuditLogRepository.cs:10`), and
  Postgres triggers reject UPDATE and DELETE on `audit_log` whatever the caller
  (`Data/Migrations/20260810193154_BaselineAuditLog.cs:93`, `:99`).
- Self-persisting: each append opens its own context and saves
  (`Data/AuditLogRepository.cs:11`), so audit never rolls back with a caller's transaction.
- Best-effort: an append failure is logged at Error and swallowed
  (`Services/AuditLogService.cs:78`).
- A job append records no actor and prefixes the job name to the description
  (`Services/AuditLogService.cs:35`).
- Per-person reads follow merges: they key on the resolved record's `AllUserIds`, never the id
  asked with (`Services/AuditLogService.cs:119`, `:136`, `:145`), and no path rewrites the stored
  id columns.
- Plain-text output names people, never ids, and says "You" for the viewer
  (`Contracts/AuditEvent.cs:41`).
- An empty `entity-ids` list on the component means no matches, never "drop the id filter"
  (`ViewComponents/AuditLogViewComponent.cs:87`).
- Paging past the end clamps instead of overflowing (`Data/AuditLogRepository.cs:34`).

## 5. Seams (specified-but-unbuilt)

- **Drop the Google-sync columns** (`ResourceId`, `Success`, `ErrorMessage`, `Role`,
  `SyncSource`, `UserEmail`) and `ILegacyGoogleSyncAuditReader` with them, once the
  GoogleIntegration history-migration screen has run in prod (`no-drops-until-prod-verified`).
  A schema change: Peter's, not a doctor strike.

## 6. Deliberately not done

- **No caching decorator**: writes scatter across most sections, reads are admin-only and
  index-filtered; a cache buys nothing (§15 Option A).
- **Predicate-pushed reads, not load-into-RAM**: `audit_log` is the one unbounded, ever-growing
  table; the repository keeps `Where`-at-the-database reads as a sanctioned exception to
  `no-linq-at-db-layer`.
- **No FK or nav on the id columns**: `ActorUserId`, `EntityId`, `RelatedEntityId` are bare
  cross-section Guids; names come from the contributor fan-out, not a join.
- **Admin pages stay English**: the `/AuditLog` page and the activity feed add no resource keys.

## Load-bearing weirdness

- **`AuditLogEntry` still carries the nullable Google-sync columns** (seam 5) with no writer, so
  historical rows stay readable until the drop. The snapshot and event shapes do not carry them.
  Do not "clean them up": the drop waits on a prod verification.
- **`AuditLogRepository` is a singleton** over a scoped context: it owns context lifetime through
  `IDbContextFactory`, which is why it can be one.
- **The anomaly count and the Drive-activity check button** sit on `/AuditLog` though the Drive
  scan is Monitor's: the page is where an admin looks for anomalies.
- **`IAuditLogReader.GetFilteredEntriesAsync` repeats the contract method** so the viewer takes one
  injection; both resolve to the same `AuditLogService` instance.
