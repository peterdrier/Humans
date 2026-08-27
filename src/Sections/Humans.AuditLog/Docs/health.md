# AuditLog — Target Shape

Derived shape for the section-doctor cycle. What AuditLog should be, written to make a
violation recognisable. Not a changelog — the run files under `docs/health/runs/` carry history.

## History

| Run | Date | Headline | PR |
|---|---|---|---|
| 1 | 2026-08-27 | Delete dead `GetRecentAsync` read chain; true up section doc | [#1545](https://github.com/peterdrier/Humans/pull/1545) |

## 1. What the section does

A crosscut that records what the system and its admins did — who did what, when, to which
entity — as an append-only trail, and renders that trail back on demand. Every other section
*writes* into it after a privileged or irreversible action; almost nothing writes outward from
it. Two faces:

- **Write** — any service or job appends an entry, immediately and best-effort: a failed append
  is logged loudly and swallowed so it never breaks the business operation, and audit is always
  written *after* the business save so a rollback leaves no ghost row.
- **Read + render** — a Board/Admin browser at `/AuditLog`, a per-entity/per-user history
  rendered on any host page through a shared component, a recent-activity card on the admin
  dashboard, and a plain-text feed for the agent's personal-history tool. Every rendered view
  resolves actor/subject/team ids to display names inside the section, so no raw id ever reaches
  a page and the section names none of the sections whose ids it shows.

It also contributes each user's slice to the GDPR export, and retains everything — there is no
prune or delete path, by design (Art. 30 / Art. 17(3)(b)).

## 2. The shapes

| Shape | Surface | Callers |
|---|---|---|
| Append an action | `IAuditLogService.LogAsync` (human + job overloads) | most sections, after a privileged action |
| Read rows as data | `IAuditLogService.GetFilteredEntriesAsync` | Issues (interleave with comments) |
| Distinct entity ids for (type, actions) | `IAuditLogService.GetEntityIdsForEntityTypeActionsAsync` | Shifts orphan-signup reconcile |
| Render a page/history | `IAuditViewerService` (`GetPageAsync`, `GetFilteredAsync`, `GetForUserAsync`) | `/AuditLog`, admin tile, `<vc:audit-log>`, Agent tool |
| Render on a host page | `<vc:audit-log>` component, layouts `line` / `table` / `activity` | Web, Users, Teams, Store, Tickets, admin dashboard |
| Legacy Google-column read | `ILegacyGoogleSyncAuditReader` | GoogleIntegration migration screen — scaffolding, deleted with those columns |
| GDPR export slice | `IUserDataContributor` | Gdpr orchestrator |

## 3. Structure

- **Contracts, two homes on purpose.** Leaf project `Humans.AuditLog.Contracts` = the write path
  (`IAuditLogService`, `AuditLogEntrySnapshot`, `AuditAction`, `ILegacyGoogleSyncAuditReader`) —
  a standalone leaf so the many sections that only *write* can depend on the contract alone,
  without a `ProjectReference` to the whole AuditLog section (its EF, its views). The section
  project's `Contracts/` folder = the read+render types (`IAuditViewerService`, `AuditEvent`,
  `AuditEventPage`), whose consumers already `ProjectReference` the section to render its component.
- **Write:** `AuditLogService` (internal, implements `IAuditLogService` + `IAuditLogReader` +
  `IUserDataContributor` + `ILegacyGoogleSyncAuditReader`) → `IAuditLogRepository` (the only file
  that touches `DbContext.AuditLogEntries`) → `AuditLogDbContext` via `IDbContextFactory`.
- **Read+render:** `AuditViewerService` (internal) wraps the section-internal `IAuditLogReader`
  raw reads with `IEntityNameContributor` name resolution, producing `AuditEvent`. Verb tables
  live once in `AuditEventTextualizer`, shared by `RenderPlainText` (agent) and `RenderStructured`
  (HTML).
- **UI:** one controller (`AuditLogController.Index`), one `<vc:audit-log>` component with a
  layout view per render shape (`line` / `table` / `activity`), one internal `AdminActivityCard`
  chrome component.
- **No caching decorator, no resource set** (admin-only English), on purpose.

## 4. Invariants

- Append-only: repository exposes `AddAsync` and reads only — no `Update`/`Delete`/`Remove`;
  Postgres triggers reject UPDATE/DELETE at the row regardless.
- Self-persisting: each `LogAsync` opens its own context and saves; callers never flush audit,
  and audit never rolls back with an outer transaction.
- Best-effort: append failures are logged at Error and swallowed inside `PersistAsync`.
- Audit after the business save, always.
- `ActorUserId` nullable = system/job action; job overload prepends the job name.
- Per-user reads chain-follow merge tombstones via `IUserServiceRead.GetMergedSourceIdsAsync`,
  and merge never rewrites the id columns.
- Rendered output carries no raw Guid — ids resolve to names, and the viewer's own id renders as
  "You" in agent output.

## 5. Seams (specified-but-unbuilt)

- **Drop the Google-sync columns** (`ResourceId`, `Success`, `ErrorMessage`, `Role`,
  `SyncSource`, `UserEmail`) and `ILegacyGoogleSyncAuditReader` with them, once the
  GoogleIntegration history-migration screen has run in prod (`no-drops-until-prod-verified`).
  A schema change — Peter's, not a doctor strike.

## 6. Deliberately not done

- **No caching decorator** — writes scatter across most sections, reads are admin-only and
  index-filtered; a section cache buys nothing (§15 Option A).
- **No resource set** — the two pages are admin-only English; `SectionTypesTakeNoStringLocalizer`
  pins it so adding copy forces carving a resource set first.
- **Predicate-pushed reads, not load-into-RAM** — `audit_log` is the one unbounded, ever-growing
  table, written from most sections; the section keeps `Where`-at-the-DB query methods as a sanctioned
  exception to `no-linq-at-db-layer`.
- **No FK/nav on the id columns** — `ActorUserId`/`EntityId`/`RelatedEntityId` are bare
  cross-section Guids; names come from the contributor fan-out, not a join.

## Load-bearing weirdness

- **`AuditLogEntry` still carries the nullable Google-sync columns** (seam 5) with no writer. They exist
  only so historical rows stay readable until the column-drop PR; the entity keeps them, the
  snapshot/event shapes do not. Do not "clean them up" — the drop is sequenced behind a prod
  verification (seam 5).
- **Two `Contracts` homes sharing one namespace** (`Humans.AuditLog.Contracts`) is intended, not
  an accident — a leaf project for Base-reachable write consumers, a folder for section-reachable
  read consumers.
- **`AuditLogRepository` is Singleton** while its context is Scoped — it owns context lifetime via
  `IDbContextFactory`, which is why it can be a singleton at all.
- **The `AnomalousPermissionDetected` anomaly count and the Drive-activity trigger button** live
  on the `/AuditLog` page though the Drive scan itself is Monitor's — the page is where an admin
  looks for anomalies, so it surfaces the count and a link to Monitor's action.
