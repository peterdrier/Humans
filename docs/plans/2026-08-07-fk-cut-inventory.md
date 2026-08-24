# Cross-section FK cut — index and delete-behavior inventory

**Issue:** nobodies-collective/Humans#992, tasks 1 and 3.
**Anchor:** `origin/main` `37eb40d99`, analysed 2026-08-07.
**Scope:** read-only inventory. No migration is written here. Conditions 2 and 4 of the
FK-cut carve-out (now `docs/architecture/conventions.md` §Cross-Section FK Columns) gates the migration
and are not this document's subject.

> **ACTED ON 2026-08-08.** Re-derived at `origin/main` `04dd8bc8c`:
> still exactly 54 relationships, matching this document row for row, and all 54 in
> `HumansDbContext` (no cross-section FK hides in a peeled context). What the migration did
> with each recommendation:
>
> - **Condition 1.** `20260808191329_DropCrossSectionForeignKeys` drops **23** convention
>   indexes — this document's 25 exposed, minus `campaign_grants.UserId` and
>   `budget_audit_logs.ActorUserId`, which now carry an explicit `HasIndex` and therefore
>   produce no schema operation at all. The generated migration is the empirical confirmation
>   of the survives/drops column below: none of the four columns the issue named appears in
>   the drop list. The `EXPLAIN` check this document asks for is still worth running against
>   prod, but nothing in the cut depends on it.
> - **Condition 3.** Implemented: the `CampSeason → camp_polygons` / `camp_polygon_histories`
>   `Restrict` pair, the only ordinary-production guard the cut removed —
>   `CampService.DeleteCampAsync` now clears them through `ICityPlanningService` before
>   deleting the camp. Accepted as recorded orphans: `email_outbox_messages.ShiftSignupId`
>   and `.CampaignGrantId`, `audit_log.ActorUserId` (the cut *fixes* the trigger/`SET NULL`
>   contradiction described below) and `audit_log.ResourceId` (no delete path).
>   **No replacement, by decision (Peter, 2026-08-09):** the eight `Team` rows and the 42
>   `User`-targeting actions. Permanently deleting a team does not happen —
>   `PermanentlyDeleteTeamAsync` is dev-only surface — and neither does hard-deleting a user,
>   so none of those orphans is reachable. When a real delete path is wanted, the shape is an
>   **`IDeleteUser` fanout**: sections with rows to remove implement it, mirroring the existing
>   `IUserDataContributor` / `IUserMerge` pair on `IFanout`. What each would have to clean up
>   is recorded in nobodies-collective/Humans#1009 so it does not have to be re-derived. This
>   supersedes the *Follow-up worth filing* section below, which proposed an analyzer over both
>   hard-delete doors: the fanout is the mechanism, not a guard against adding one.
>
> **The migration is one-way in practice, and that follows from the `audit_log.ActorUserId`
> decision.** `Down()` re-adds all 54 constraints, and Postgres validates existing rows when it
> does. Once the OAuth `CrossUserBlocked` path has deleted an actor whose `audit_log` row
> survives — exactly the orphan accepted above — `AddForeignKey` on
> `FK_audit_log_users_ActorUserId` fails and `Down()` cannot complete. Nulling the orphans first
> is barred twice: it is a data migration ([[no-data-backfills]]), and `prevent_audit_log_update`
> rejects the `UPDATE` it would need. This costs nothing operationally, because prod rollback is
> the pre-deploy `pg_dump` snapshot, not `Down()` —
> [`database-restore-runbook.md`](../database-restore-runbook.md) §5, and its line *"schema
> changes do not roll back with the image."* `Down()` stays correct for a clean database, which
> is what CI and local use it for. Recorded rather than fixed.

---

## How the list was derived

The plan's "55 relationships across 39 tables in 17 sections" was **not** taken on trust. The
list was re-derived from the code:

1. `dotnet build Humans.slnx -v quiet` at `37eb40d99` emits **exactly 55 HUM0024 warnings**,
   one per cross-section `HasOne`/`HasMany` invocation in
   `src/Humans.Infrastructure/Data/Configurations/**`.
2. Each warning site was read to extract the FK column and the `OnDelete(...)` behavior.
3. Table names, the full index set, and the referential actions were cross-checked against
   `Migrations/HumansDbContextModelSnapshot.cs` — the model the database actually matches.

**All 55 warnings map to 55 distinct `(table, column)` pairs** — no relationship is
double-reported from both ends. They cover **39 tables** in **17 sections**. The plan's
figures reconcile exactly.

Two completeness checks:

- The seven split contexts (`Agent`, `Containers`, `EventGuide`, `Expenses`, `Finance`,
  `Surveys`, `SystemSettings`) hold 12 FKs between them; all 12 are intra-section, so
  nothing hides outside `HumansDbContext`. Verified against their model snapshots.
- No `DbContext` configures a relationship inline — every relationship goes through an
  `IEntityTypeConfiguration<T>`, which is what HUM0024 inspects. There is no blind spot.

### Reconciliation against 55

| | Count |
|---|---|
| HUM0024 warnings at `37eb40d99` (gross) | **55** |
| less `camp_leads.UserId` — table dropped whole by nobodies-collective/Humans#774 via peterdrier/Humans#1199 | −1 |
| **Net, in scope for the bulk FK-cut migration** | **54** across **38 tables** in **17 sections** |

`camp_leads` is the only G2 table drop that removes an inventoried relationship. Camps keeps
`camps.CreatedByUserId` and `camp_seasons.ReviewedByUserId`, so the section count stays at 17.
The scope rule is the plan's: *a relationship is out of scope if its table is dropped by that
table's own G2 demolition item*.

> **Since the anchor:** peterdrier/Humans#1199 has now merged — migration
> `20260807155413_DropCampLeadsAndSpecialRoleDefault` drops the `camp_leads` table, and
> `CampLeadConfiguration` is gone from `origin/main` (`da45a29a3`). So **54 is no longer a
> projection; it is the live figure**, and a build at current main emits 54 HUM0024 warnings
> rather than 55. Everything below is still derived at `37eb40d99`, where the row existed — the
> `camp_leads` row in the full inventory is retained as history and marked accordingly. No other
> figure in this document changes.

---

## Condition 1 — index preservation

### The rule that decides each row

EF's `ForeignKeyIndexConvention` creates `IX_<table>_<column>` for a relationship **unless the
model already declares an index whose leading columns are that FK's columns**. Where a
configuration declares its own `HasIndex(...)` on the FK column, that explicit index *is* the
model's index; the convention adds nothing. So:

- **explicit `HasIndex` on the column** → the index survives the cut untouched, because the
  declaration lives outside the `HasOne(...)` chain being deleted.
- **explicit composite `HasIndex` with the FK column leading** → survives, and still serves
  equality lookups on that column.
- **index present in the snapshot but not declared in the configuration** → it is
  convention-generated, and EF removes it along with the relationship. **This is the failure
  mode.**

### Result

| | Count |
|---|---|
| Index survives (explicit solo `HasIndex`) | 23 |
| Index survives (explicit composite, FK column leading) | 6 |
| **Index drops with the relationship** | **26** (25 net of `camp_leads`) |

### The four columns the issue named are all safe

The issue and the plan both name `shift_signups.UserId`, `team_members.UserId`,
`consent_records.UserId` and `ticket_orders.MatchedUserId` as the hot columns at risk. **All
four already carry an explicit `HasIndex` and are unaffected by the cut** —
`ShiftSignupConfiguration.cs:30`, `TeamMemberConfiguration.cs:37`,
`ConsentRecordConfiguration.cs:57`, `TicketOrderConfiguration.cs:102`. The named list was a
reasonable guess, but it is not where the risk is.

### Where the risk actually is

Of the 25 in-scope dropping indexes, **18 are on columns no repository, job or store ever
filters, sorts, joins or groups by** — they are audit stamps (`CreatedByUserId`,
`ReviewedByUserId`, `ChangedByUserId`, `ModifiedByUserId`, `AssignedByUserId`,
`ResolvedByUserId`, `EnrolledByUserId`, `UpdatedByUserId`) written on insert and read only
through the row that carries them. Those need no `HasIndex(...)`; adding one would be
speculative. Five columns do carry query traffic — but carrying traffic is not the same as being
exposed by the cut, and only two of the five survive that second test:

| Table.column | Query evidence | Judgement |
|---|---|---|
| `campaign_grants.UserId` | `CampaignRepository.cs:160,173,327,350,380` — five bare `g.UserId == userId` predicates, none of them also constraining `CampaignId`. Includes the per-user grant list and the GDPR export row. | **Re-add.** The only other index touching `UserId` is `(CampaignId, UserId)` unique, whose leading column is `CampaignId` — it cannot serve a `UserId`-only lookup. |
| `budget_audit_logs.ActorUserId` | `BudgetRepository.cs:1013,1029` — both bare `ActorUserId` equality / `Contains`, no other predicate | **Re-add.** The table's indexes are `BudgetYearId`, `OccurredAt` and `(EntityType, EntityId)`; none can serve an actor lookup. |
| `audit_log.ActorUserId` | `AuditLogRepository.cs:171,190,208` | **Do not re-add** — a solo index here is unusable. See below. |
| `board_votes.BoardMemberUserId` | `ApplicationRepository.cs:198,169,110` | **Do not re-add** — already covered. See below. |
| `rotas.TeamId` | `ShiftRepository.Signups.cs:312`; `Management.cs:339,379,412` | **Do not re-add** — already covered. See below. |

#### Why the last three are not exposed

Each looked like a solo predicate and is not. Worth spelling out, because the cost of getting
this wrong is two or three indexes that carry write and storage cost forever while preserving
nothing.

- **`board_votes.BoardMemberUserId`.** The `Any` at `:198` reads
  `!a.BoardVotes.Any(v => v.BoardMemberUserId == boardMemberUserId)`, which looks single-column
  but is a *correlated* subquery: `BoardVoteConfiguration.cs:31-33` binds `a.BoardVotes` through
  `HasForeignKey(bv => bv.ApplicationId)`, so the generated `EXISTS` constrains `ApplicationId`
  **and** `BoardMemberUserId`. That is exactly the unique index at
  `BoardVoteConfiguration.cs:44-45`. The other two sites are covered too — `:169` filters both
  columns explicitly, `:110` filters `ApplicationId` and merely projects the user id. No
  exposure.
- **`rotas.TeamId`.** Every repository predicate on this column sits in a query that already
  pins `EventSettingsId` on the same `Rota`: `Signups.cs:306`+`:312`, `Management.cs:330`+`:339`,
  `:406`+`:412`, and `:379` in one expression. The composite `(EventSettingsId, TeamId)`
  (`RotaConfiguration.cs:36`, the only `TeamId` index on the table) serves all four as a
  leading-equality lookup. Everything else matching `Rota.TeamId` in `src/` is in-memory LINQ in
  the service layer over already-loaded entities, which generates no SQL. No exposure.
- **`audit_log.ActorUserId`.** This column is never queried alone. All three sites are
  three-way disjunctions over `EntityId`, `RelatedEntityId` and `ActorUserId` (`:190` is
  `a.EntityId == userId || a.RelatedEntityId == userId || a.ActorUserId == userId`; `:171` and
  `:208` are the `Contains` forms). Postgres can only use indexes for an `OR` by building a
  bitmap per disjunct, so it needs a usable index on *every* branch. `RelatedEntityId` has none
  — the only index containing it is `(RelatedEntityType, RelatedEntityId)`, which cannot serve
  the bare column — so all three queries seq-scan `audit_log` today, with or without an index on
  `ActorUserId`. Dropping it therefore regresses nothing. If these GDPR-export scans are ever
  worth making index-driven, that needs solo indexes on all three branches and is a separate
  decision from the FK cut, not a consequence of it.

Two more dropping columns are queried, but only from the user-merge path
(`FeedbackRepository.cs:165` on `feedback_messages.SenderUserId`,
`NotificationRepository.cs:478` on `notifications.ResolvedByUserId`). Merging duplicate
accounts is a rare admin action on tables measured in thousands of rows; a sequential scan
there is acceptable. Recorded rather than recommended — say so explicitly in the migration PR
rather than leaving it to be re-derived.

**Recommendation for the migration PR:** add explicit `HasIndex(...)` for exactly **two** columns
— `campaign_grants.UserId` and `budget_audit_logs.ActorUserId`. Leave the other twenty-three
alone (18 unused, 2 merge-path, 3 already covered or unusable) and state in the PR body that they
were checked, so a reviewer does not have to re-derive it.

### What this analysis cannot tell you

Stated plainly rather than guessed:

- **No row counts.** This is static analysis of a repo with no live-database access. At ~500
  users most of these tables are small enough that Postgres would sequential-scan them
  regardless of the index, which is a reason the two recommendations are narrow: an index is
  cheap, but only where a plan could actually use it.
- **No `EXPLAIN`.** The coverage arguments above are read off the index definitions and the
  predicates, not off a query plan. They turn on rules that hold generally — a composite serves
  a leading-column equality; an `OR` needs a usable index on every branch — but the confirmation
  is `EXPLAIN (ANALYZE)` against prod on the cited call sites, not further reading of the
  source. That check is worth running before the migration for the two re-adds and for
  `audit_log`.
- **Partial indexes.** `budget_categories.TeamId` and `budget_line_items.ResponsibleTeamId`
  survive as `WHERE "<col>" IS NOT NULL` partial indexes
  (`HumansDbContextModelSnapshot.cs:401,496`), as does
  `email_outbox_messages.(ShiftSignupId, TemplateName)` at `:1623`. They serve equality lookups
  on non-null values, which is all their call sites do. No action.

---

## Condition 3 — delete behaviors

### Distribution

| Referential action | Count | What the cut changes |
|---|---|---|
| `Restrict` | 24 | The database currently **refuses** to delete the principal while a dependent row references it. After the cut the delete succeeds and leaves an orphan. A guard disappears. |
| `SetNull` | 21 | The column is currently nulled when the principal goes. After the cut it keeps a stale `Guid` pointing at a row that no longer exists. |
| `Cascade` | 10 | The dependent row is currently deleted with its principal. After the cut it survives as an orphan. |

### A referential action only fires if something deletes the principal

That is the question the inventory has to answer first, and it collapses most of the work. The
55 relationships point at six principals:

| Principal | Relationships | Is the row ever hard-deleted, and from where? |
|---|---|---|
| `User` | **42** | **Yes — on three paths.** Right-to-erasure and account-merge both **anonymise in place** — `IUserRepository.Profiles.cs:108,122` (`AnonymizeForMergeByUserIdAsync`, `AnonymizeForDeletionByUserIdAsync`), `IAccountDeletionService.cs:77` (`AnonymizeExpiredAccountAsync`); the merged-away row survives, stamped with `MergedToUserId`. But three paths do hard-delete: the dev-dashboard reset (via the repository) and two signup rollbacks (via ASP.NET Identity). Enumerated below. |
| `Team` | 8 | **Yes, on one path.** The user-facing delete is a soft delete: `TeamService.DeleteTeamAsync` (`TeamService.cs:608-629`) calls `DeactivateTeamAsync` and removes no row; `TeamController.cs:724` is its only caller. The hard delete is `TeamService.PermanentlyDeleteTeamAsync` (`TeamService.cs:1846-1867`) → `TeamRepository.PermanentlyDeleteTeamAsync` (`TeamRepository.cs:1051-1091`, `db.Teams.Remove` at `:1087`). Its only caller today is `DevelopmentDashboardSeeder.cs:472`, but it is admin-authorized production surface. |
| `CampSeason` | 2 | Indirectly — `CampRepository.cs:186` removes a `Camp`, and `CampConfiguration.cs:44` cascades `Camp → CampSeason`. |
| `ShiftSignup` | 1 | **Yes, on four paths.** Rota delete (`ShiftRepository.Management.cs:187`) and shift delete (`:294`); **account-merge dedup** — `ShiftSignupService.ReassignAsync` (`:1366`) → `ShiftRepository.Signups.ReassignToUserAsync`, which drops the source row at `Signups.cs:212` when the target already holds a signup for that shift; and `DeleteAllForUsersAsync` (`Signups.cs:183-186`), whose only caller is the dev seeder (`DevelopmentDashboardSeeder.cs:462`). The first three are ordinary production. |
| `CampaignGrant` | 1 | Yes — `CampaignRepository.cs:364`, the account-merge dedup branch. |
| `GoogleResource` | 1 | No hard-delete path found. |

**So 42 of the 55 are `User`-targeting and fire only on the three user hard-delete paths
below** — including all seven the issue calls out by name (`team_members.UserId`,
`role_assignments.UserId`, `issues.AssigneeUserId`, `issues.ResolvedByUserId`,
`ticket_orders.MatchedUserId`, `ticket_attendees.MatchedUserId`, `issues.ReporterUserId`).

**The remaining 13 are not uniformly live either**, and the difference matters for what
condition 3 has to deliver:

| | Count | Reachability today |
|---|---|---|
| `CampSeason` (2), `ShiftSignup` (1), `CampaignGrant` (1) | **4** | **Ordinary production.** Deleting a camp (`CampAdminController.cs:292` → `ICampService.DeleteCampAsync` → `CampRepository.cs:186`) cascades to its seasons; rota and shift deletes remove signups (`ShiftRepository.Management.cs:187,294`), as does account-merge dedup (`Signups.cs:212`); account-merge dedup also removes grants (`CampaignRepository.cs:364`). |
| `Team` (8) | **8** | **Dev-dashboard reset only.** `PermanentlyDeleteTeamAsync` is admin-authorized service surface, but its only caller is `DevelopmentDashboardSeeder.cs:472` — same gating as user path 1 below. |
| `GoogleResource` (1) | **1** | **Never.** Nothing deletes a `GoogleResource`, so `audit_log.ResourceId`'s `SetNull` cannot fire at all. |

Only **four** of the 55 fire on a path an ordinary production user can reach today, plus
`audit_log.ActorUserId` from the 42 (see path 2). That is the real size of condition 3's live
surface. It does not shrink the *work*, because the `Team` rows still need their replacements
before someone wires `PermanentlyDeleteTeamAsync` to a real admin screen — but it should be
stated rather than left implicit, so nobody reads "13 live" as thirteen production regressions
waiting to happen.

#### The three user hard-delete paths

Two of the three bypass `IUserRepository` entirely and delete through ASP.NET Identity's
`UserStore`, which is why they do not appear in a search of the repository layer.

| # | Path | Delete call | Gating | Cross-section dependents at delete time |
|---|---|---|---|---|
| 1 | Dev-dashboard reset — `UserService.DeleteUsersAsync` (`UserService.cs:1253-1260`) → `UserRepository.DeleteUsersAsync` (`UserRepository.cs:321-341`) | `ctx.Users.Where(...).ExecuteDeleteAsync` at `:335-337` | Admin (`UserService.cs:1257`); only caller is `DevelopmentDashboardSeeder.ResetAsync:478`, reachable only via `POST dev/seed/dashboard/reset`, which is `AdminOnly` **and** 404s outside `ASPNETCORE_ENVIRONMENT=Development` (`DevSeedController.cs:102,114`); seeder not DI-registered in Production (`Program.cs:76-80`) | **Yes** — seeded users have been through a full seed run |
| 2 | OAuth signup rollback — `ExternalLoginService.TryDeleteOrphanUserAsync` (`:331-343`), called from `:230,264,279,288` | `userManager.DeleteAsync(user)` at `:335` | None beyond the signup flow itself — **production** | **One, on one branch** — an `audit_log` row. See below |
| 3 | Magic-link signup rollback — `AccountProvisioningService` | `userManager.DeleteAsync(user)` at `:164` | None beyond the signup flow itself — **production** | **No** |

**Path 3 has nothing to orphan.** `CreateAsync` at `:141`, the only write before the rollback is
`AddVerifiedEmailAsync` at `:155` (a `UserEmail` row — Users-section, not among the 42, and it
writes no audit entry), the delete is at `:164`, and `EnsureStubProfileAsync` runs at `:177`,
after the rollback.

**Path 2 does, on the `CrossUserBlocked` branch.** Three of its four call sites are clean:
`:230` fires when `AddLoginAsync` fails, before reconcile has run at all, and the writes in
between (`AspNetUserLogins`, `UserEmail`) are Users-section tables. But `:264` fires *after*
`ReconcileOAuthIdentityAsync` returns `CrossUserBlocked`, and that branch has already written an
`audit_log` row — `UserEmailService.cs:988-993`, `AuditAction.OAuthRenameCollisionBlocked` with
`actorUserId: userId` set to the user about to be deleted. `audit_log.ActorUserId` is
relationship #1 in this inventory. The two exception branches (`:279`, `:288`) may also have
written one, depending on how far reconcile got before throwing.

##### This relationship is already broken, and the cut fixes it

`audit_log.ActorUserId` is `SetNull` (`AuditLogEntryConfiguration.cs:49-52`; the FK is
`onDelete: ReferentialAction.SetNull`, `20260212152552_Initial.cs:525-529`), and a `SET NULL`
referential action is executed by Postgres as an `UPDATE` on the referencing table. But
`audit_log` carries `prevent_audit_log_update` — `BEFORE UPDATE ... FOR EACH ROW`, raising
*"UPDATE operations are not allowed on audit_log table"* unconditionally
(`20260212152552_Initial.cs:1001-1017`). Referential-action updates fire row-level triggers like
any other.

So today, deleting a user that has any `audit_log` row as actor **fails**. On the
`CrossUserBlocked` path the exception is swallowed by `TryDeleteOrphanUserAsync`'s catch and
logged as *"Failed to clean up orphan user"* (`ExternalLoginService.cs:337-341`) — meaning the
half-provisioned account the code intends to roll back is still there. The `SetNull` and the
immutability trigger have contradicted each other since the initial migration; the config
comment at `:49` (*"null ActorUserId = system action or deleted user"*) describes a behavior the
database has never permitted.

After the cut there is no FK, so no `SET NULL`, so no `UPDATE`, so no trigger: the delete
succeeds and leaves `ActorUserId` as a stale `Guid`. **Accepted orphan, and an improvement.** A
stale actor id on an immutable audit row is a historical fact of the same kind the
`email_outbox_messages` decision accepts — the row records who attempted the action, which
remains true — and it is strictly better than today's outcome, where the rollback silently does
not happen. The migration PR should say so rather than treat it as a new orphan it introduced.

This one is worth confirming against a live database before the migration, since it is inferred
from the trigger and FK definitions rather than observed: delete a user holding an `audit_log`
actor row on a copy of prod and check that it raises. The same reasoning applies to path 1 — the
dev-reset — for any seeded user who has acted.

Paths 2 and 3 still matter for enforcement regardless. They are production callers that
hard-delete a user, and any guard that only covers `IUserService.DeleteUsersAsync` would miss
them entirely. See the
follow-up at the end.

#### What the cut changes on the dev-reset path (path 1)

Today the reset leans on the database to finish the job. It clears the seeded event (`:454`)
and the dev users' shift signups (`:462`) explicitly, then deletes the users and lets the FKs
handle the rest: the **nine `User`-targeting `Cascade` relationships** (`role_assignments.UserId`,
`applications.UserId`, `feedback_reports.UserId`, `notification_recipients.UserId`,
`team_join_requests.UserId`, `team_members.UserId`, `volunteer_event_profiles.UserId`,
`volunteer_tag_preferences.UserId`, `google_sync_outbox.UserId`) delete the dependent rows, the
`SetNull` ones blank their columns, and the `Restrict` ones would refuse the delete outright if
a dev user had picked up an unexpected dependent. The one exception is
`audit_log.ActorUserId`: its `SetNull` cannot fire at all, for the trigger reason above, so a
seeded user who has acted cannot be deleted today either.

After the cut none of that happens. The delete succeeds unconditionally and leaves orphaned
`team_members`, `role_assignments`, `notification_recipients` and the rest pointing at user ids
that no longer exist — in a database that is reseeded on the very next run.

**Required before the cut:** `DevelopmentDashboardSeeder.ResetAsync` must clear the dev users'
dependent rows through the owning section services before calling `DeleteUsersAsync`, extending
the pattern it already uses for shift signups at `:462`. That is a seeder change rather than a
production one, and it is the whole of the replacement work for the 42 — but it is not "no
replacement needed."

That the *ordinary* production lifecycle never hard-deletes a user — erasure and merge anonymise
in place, and the only production deletes are the two signup rollbacks above — is a statement
about today's code, not a permanent guarantee. See the follow-up at the end.

### The 13 non-`User` relationships

The 42 `User`-targeting actions are dealt with above. These are the rest — four reachable in
ordinary production, eight only through the dev-dashboard reset, and one
(`audit_log.ResourceId`) with no delete path at all, per the breakdown above. Each still needs a
decision, because the replacement work is the same whether the trigger is wired up today or
next quarter.

| Principal | Dependent | Action | What breaks after the cut | Replacement |
|---|---|---|---|---|
| `Team` | `calendar_events.OwningTeamId` | `Restrict` | `PermanentlyDeleteTeamAsync` currently throws a raw FK violation if the team owns calendar events. After the cut it succeeds and orphans them. | **Pre-check in `TeamService.PermanentlyDeleteTeamAsync`**, matching the pattern already there for Google resources (`TeamService.cs:1862-1865`). |
| `Team` | `google_resources.TeamId` | `Restrict` | **Already replaced, but not equivalently.** `TeamService.cs:1862-1865` pre-checks and throws a clear message, with a comment saying it exists precisely because the FK is `Restrict`. The check is not atomic with the delete — see *Pre-checks are weaker than the constraint* below. | Template for the other three, with that caveat. |
| `Team` | `legal_documents.TeamId` | `Restrict` | Same as `calendar_events` — orphaned required documents, which would then be missing from every consent dashboard that resolves the team. | Pre-check. |
| `Team` | `rotas.TeamId` | `Restrict` | Same — orphaned rotas, and their shifts and signups behind them. | Pre-check. |
| `Team` | `budget_categories.TeamId` | `SetNull` | Column keeps a stale `Guid`; budget screens resolve a team name that no longer exists. | Null it explicitly inside `PermanentlyDeleteTeamAsync`'s transaction — or accept the orphan, since the delete already requires an admin and the column is nullable. **Needs a call.** |
| `Team` | `budget_line_items.ResponsibleTeamId` | `SetNull` | As above. | As above. |
| `Team` | `feedback_reports.AssignedToTeamId` | `SetNull` | An assigned report keeps a dead team id; the Feedback admin filter at `FeedbackRepository.cs:69` returns it under a team that no longer exists. | As above. |
| `Team` | `google_sync_outbox.TeamId` | `Cascade` | Outbox events for a deleted team survive and are processed by `ProcessGoogleSyncOutboxJob` against a team that is gone. | **Delete *all* of the team's outbox rows** inside the same transaction — not just the pending ones. This one is not merely cosmetic; it is a job that will run. See the note below on why "pending" is the wrong filter. |
| `CampSeason` | `camp_polygons.CampSeasonId` | `Restrict` | Deleting a `Camp` currently throws if any of its seasons has a city-planning polygon. After the cut the polygon is orphaned and `CityPlanningRepository.cs:34,49,96` returns rows for a season that no longer exists. | Pre-check in the Camps delete path, or delete the polygons through `ICityPlanning…` — **cross-section, so it must go through a service, not a repository.** |
| `CampSeason` | `camp_polygon_histories.CampSeasonId` | `Restrict` | As above, for the history table. | As above. |
| `ShiftSignup` | `email_outbox_messages.ShiftSignupId` | `SetNull` | Deleting a rota or shift (`ShiftRepository.Management.cs:187,294`) **and account-merge dedup** (`Signups.cs:212`) leave outbox rows pointing at a dead signup. The dedup index `(ShiftSignupId, TemplateName)` then guards against a signup that cannot exist. | **Orphan is acceptable** — see below. |
| `CampaignGrant` | `email_outbox_messages.CampaignGrantId` | `SetNull` | Account-merge dedup (`CampaignRepository.cs:364`) leaves outbox rows pointing at a dead grant. | **Orphan is acceptable** — see below. |
| `GoogleResource` | `audit_log.ResourceId` | `SetNull` | Nothing today — no code deletes a `GoogleResource`. | None needed; record the constraint. |

### "Pending" is the wrong filter for the outbox cleanup

The `google_sync_outbox` replacement has to match what `Cascade` does today, which is delete
**every** row for the team regardless of state. Cleaning up only pending events leaves a hole:

- `MarkPermanentlyFailedAsync` (`GoogleSyncOutboxRepository.cs:130-142`) sets
  `FailedPermanently = true` **and** a non-null `ProcessedAt`, so a permanently-failed row is not
  pending by any definition and would be skipped.
- `RequeueAllFailedAsync` (`:99-116`) then reactivates *every* failed row —
  `Where(e => e.FailedPermanently)`, with no team filter — clearing `FailedPermanently`,
  nulling `ProcessedAt` and resetting `RetryCount`.

So the skipped row becomes pending again the next time an admin hits requeue, and
`ProcessGoogleSyncOutboxJob` processes it against a team that no longer exists. The window is not
the delete transaction; it is however long the row sits there until someone requeues.

Delete all of the team's rows. It is the same one-line predicate, it matches the cascade exactly,
and it removes the need to reason about which states can be resurrected.

### Explicit "orphan is acceptable" decisions

Recorded here so the migration PR does not have to re-argue them:

- **`email_outbox_messages.ShiftSignupId` and `.CampaignGrantId`.** `email_outbox_messages` is
  an append-only send log that `EmailOutboxRepository.DeleteSentOlderThanAsync`
  (`EmailOutboxRepository.cs:183-190`) prunes on age anyway. A stale id in a sent-mail record
  is a historical fact, not a live reference — the row records that an email *was* sent about a
  signup that has since been deleted, which is true. Neither column is read as a lookup key
  (`ShiftSignupId` appears only in the dedup index, `CampaignGrantId` in no predicate at all).
  Leave both as stale ids; add no replacement.

  **Checked against the account-merge path specifically**, since that is the one case where the
  deleted principal has a live counterpart rather than simply ceasing to exist. When
  `ReassignToUserAsync` drops the source signup (`Signups.cs:212`), the target's signup for that
  shift survives under a *different* `Guid`. The concern would be the partial unique index
  `(ShiftSignupId, TemplateName)`: today `SetNull` blanks the column and the row falls out of the
  index, whereas after the cut it stays in, keyed on a signup id that no longer resolves. That
  cannot collide with anything — the dead id is never reissued, and the surviving signup carries
  its own — so no send is blocked and no dedup decision changes. The same holds for
  `CampaignGrantId` on `CampaignRepository.cs:364`, which is the identical dedup shape. Decision
  stands for both merge and delete paths.
- **The 42 `User`-targeting actions, on the two signup-rollback paths.** Path 3 and three of
  path 2's four call sites delete a user that cannot yet have any of the 42 dependent rows, as
  traced above — nothing to orphan, no replacement. Path 2's `CrossUserBlocked` branch does leave
  one, a stale `audit_log.ActorUserId`; that is accepted for the reasons given above, where the
  alternative today is a rollback that fails outright. This is not a blanket pass for the 42: the
  dev-dashboard reset deletes users that *do* have dependents, and needs the seeder-side cleanup
  specified above.

### Precedent — the pattern is already in the codebase

Two application-level replacements for a `Restrict` FK already exist and both carry a comment
explaining themselves:

- `TeamService.cs:1862-1865` — *"GoogleResource → Team is OnDelete(Restrict); pre-check yields
  a clear message vs raw FK violation."*
- `ShiftRepository.Management.cs:184` — *"ShiftSignup→Shift FK is Restrict, so cascade won't
  handle them — delete explicitly."*

Seven of the eight `Team` rows (all but `google_resources`, already done) and the two
`CampSeason` rows are that same job, nine more times.
That is the whole of condition 3's remaining work; it is a bounded, named list, not an open
question.

### Pre-checks are weaker than the constraint they replace

Worth stating before nine more of them get written, because "replaced" reads as "equivalent" and
it is not.

A `Restrict` FK is enforced by the database at the moment of the delete. A service-layer
pre-check is a separate read, on a separate connection, some time earlier. The existing Google
resource replacement shows the gap: `TeamService.cs:1862-1865` reads through
`TeamResourceService`, then `:1867` calls `repo.PermanentlyDeleteTeamAsync`, which opens **its
own** `DbContext` (`TeamRepository.cs:1053`) and **its own** transaction (`:1061`). A resource
linked to the team between the check and the commit is not seen by either. Today the FK still
catches that — the delete throws. After the cut nothing does, and the delete silently orphans
the row.

The same race applies to all nine proposed pre-checks, and to the cross-section cleanup calls for
the `SetNull` and `Cascade` rows.

**This inventory does not propose closing it.** Permanently deleting a team is an admin-only
operation on a single-server deployment of a few hundred users; the window is a few milliseconds
and the losing outcome is one orphaned row, not corruption. Taking a lock or threading a shared
transaction through the service and repository would be a substantial change to how this codebase
does data access, to buy very little. The point is that the migration PR should record the
narrowing as an accepted trade rather than describe the pre-checks as preserving the guarantee —
and if anyone decides the trade is *not* acceptable for a particular row, that is the row to
argue about, not all nine.

### What this analysis cannot tell you

- **Whether orphans already exist in production.** These constraints are live, so in principle
  they cannot — but a constraint added by a later migration says nothing about rows that
  predate it. Verifying would take an anti-join query per relationship against prod. Not done
  here, and worth doing before the cut for at least the eight `Team` rows.
- **Whether `PermanentlyDeleteTeamAsync` has ever been called in production.** Its only
  in-repo caller is the development seeder, but it is exposed through `ITeamService` and
  admin-authorized. Static analysis cannot answer this; the audit log can.

### Two adjacent findings

Neither is in this document's scope; both are recorded because they bear on the conditions
being tracked and would otherwise be re-derived.

1. **Condition 2's ratchet is already armed — and the bulk cut is what disarms the scaffolding
   around it.** `CrossSectionEfJoinAnalyzer.cs:46` declares HUM0024 with
   `defaultSeverity: DiagnosticSeverity.Error` and `isEnabledByDefault: true`, and `:119` runs
   every configuration through `GrandfatheredCheck.EffectiveSeverity`, which downgrades to
   Warning **only** for types carrying `[Grandfathered(ruleId: "HUM0024", ...)]`. Forty
   configuration classes carried it at the anchor — **39 on `origin/main` at `da45a29a3`**, since
   peterdrier/Humans#1199 deleted `CampLeadConfiguration` with the table
   (`AuditLogEntryConfiguration.cs:13-17` is the pattern). `Directory.Build.props:79` lists
   HUM0024 in `WarningsNotAsErrors`, which keeps those *downgraded* diagnostics non-fatal; it does
   not touch the undowngraded ones.

   So a new cross-section relationship added today, on a class with no annotation, is an
   **Error and fails the build**. The boundary is enforced. What the bulk cut changes is the
   debt side: as each relationship's join goes, its `[Grandfathered]` annotation goes with it,
   and once the last one is removed the `WarningsNotAsErrors` entry becomes removable on the
   exit condition the props comment at `:51-55` already states. That entry is cleanup after the
   fact, not the thing that makes the boundary permanent.
2. **Condition 4's cited example is stale.** The plan
   (the Q3 transition plan, since deleted — historical) and the issue both cite `LegalDocument.Team` as
   an unstripped live nav. It no longer exists: `LegalDocument.cs:25` carries only
   `public Guid TeamId`, and `LegalDocumentRepository.GetActiveRequiredDocumentsForTeamsAsync`
   (`LegalDocumentRepository.cs:87-93`) filters `teamIds.Contains(d.TeamId)` with a comment
   pointing at `memory/architecture/no-cross-section-ef-joins.md`. As a by-product of this
   inventory: **only 10 of the 55 relationships still carry a navigation property at all** —
   `FeedbackMessage.SenderUser`, `FeedbackReport.{User, ResolvedByUser, AssignedToUser,
   AssignedToTeam}`, `TeamJoinRequest.{User, ReviewedByUser}`,
   `TeamJoinRequestStateHistory.ChangedByUser`, `TeamMember.User`,
   `TeamRoleAssignment.AssignedByUser`. All ten are `[Obsolete]`-marked, and no `.Include(...)`
   of them and no member read through them exists anywhere in `src/`. That is not a condition-4
   audit — a consumer can read a nav off a tracked entity without `.Include` — but it bounds
   the search to two sections, Feedback and Teams.

### Follow-up worth filing

The property that keeps the 42 harmless is not "users are never hard-deleted" — they are, three
times — but "nothing hard-deletes a user that has dependents, except the dev seeder." That rests
on convention, not enforcement. Once the FKs are cut, a new delete path added anywhere would
silently orphan rows in sixteen sections with no database backstop.

An analyzer or architecture test is worth filing, but two phrasings that look obvious are both
wrong:

- *"Nothing calls `Remove`/`ExecuteDelete` on the `Users` `DbSet`."* `UserRepository.cs:335-337`
  does, legitimately. Fails on `main` today.
- *"Nothing new calls `IUserService.DeleteUsersAsync`."* Misses paths 2 and 3 entirely — they
  never touch `IUserService`, they go through `UserManager.DeleteAsync` and Identity's
  `UserStore`. This is precisely how the first draft of this document came to claim no
  hard-delete path existed at all.

The enforceable version has to cover **both** doors: `IUserService.DeleteUsersAsync` *and*
`UserManager.DeleteAsync`, allowlisted to the three known call sites
(`DevelopmentDashboardSeeder.cs:478`, `ExternalLoginService.cs:335`,
`AccountProvisioningService.cs:164`) — a small enough list for the `[Grandfathered]` shape the
codebase already uses. A new caller of either is then a build failure rather than a silent
orphan.

---

## Full inventory

Sections are the *dependent* side — the section that owns the table carrying the FK column.
`Config` cites the `HasOne` site that HUM0024 reports.

### AuditLog

| Table | FK column | → | Action | Index after cut | Query filters/sorts on it? | Config |
|---|---|---|---|---|---|---|
| `audit_log` | `ActorUserId` | `User` | `SetNull` | **DROPS** | Yes, but only inside a three-way `OR` — `AuditLogRepository.cs:171,190,208`. No solo index is usable; see *Why the last three are not exposed*. | `AuditLogEntryConfiguration.cs:49` |
| `audit_log` | `ResourceId` | `GoogleResource` | `SetNull` | survives — explicit `HasIndex` | Yes — `AuditLogRepository.cs:43,54,70` | `AuditLogEntryConfiguration.cs:69` |

### Auth

| Table | FK column | → | Action | Index after cut | Query filters/sorts on it? | Config |
|---|---|---|---|---|---|---|
| `role_assignments` | `CreatedByUserId` | `User` | `Restrict` | **DROPS** | No | `RoleAssignmentConfiguration.cs:41` |
| `role_assignments` | `UserId` | `User` | `Cascade` | survives — explicit `HasIndex` | Yes — `RoleAssignmentRepository.cs:43,93,162,266,303` | `RoleAssignmentConfiguration.cs:49` |

### Budget

| Table | FK column | → | Action | Index after cut | Query filters/sorts on it? | Config |
|---|---|---|---|---|---|---|
| `budget_audit_logs` | `ActorUserId` | `User` | `Restrict` | **DROPS** | **Yes** — `BudgetRepository.cs:1013,1029` | `BudgetAuditLogConfiguration.cs:27` |
| `budget_categories` | `TeamId` | `Team` | `SetNull` | survives — explicit `HasIndex` | No direct predicate; `BudgetRepository.cs` projects only | `BudgetCategoryConfiguration.cs:27` |
| `budget_line_items` | `ResponsibleTeamId` | `Team` | `SetNull` | survives — explicit `HasIndex` | No | `BudgetLineItemConfiguration.cs:30` |

### Calendar

| Table | FK column | → | Action | Index after cut | Query filters/sorts on it? | Config |
|---|---|---|---|---|---|---|
| `calendar_events` | `OwningTeamId` | `Team` | `Restrict` | survives — composite `OwningTeamId + StartUtc` (leading) | Yes — `CalendarRepository.cs:42` | `CalendarEventConfiguration.cs:30` |

### Campaigns

| Table | FK column | → | Action | Index after cut | Query filters/sorts on it? | Config |
|---|---|---|---|---|---|---|
| `campaign_grants` | `UserId` | `User` | `Restrict` | **DROPS** | **Yes** — `CampaignRepository.cs:160,173,327,350,380` | `CampaignGrantConfiguration.cs:39` |
| `campaigns` | `CreatedByUserId` | `User` | `Restrict` | **DROPS** | No | `CampaignConfiguration.cs:30` |

### Camps

| Table | FK column | → | Action | Index after cut | Query filters/sorts on it? | Config |
|---|---|---|---|---|---|---|
| ~~`camp_leads`~~ | `UserId` | `User` | `Restrict` | n/a | **Table dropped since the anchor** by peterdrier/Humans#1199 (`20260807155413_DropCampLeadsAndSpecialRoleDefault`); `CampLeadConfiguration` is gone. Row kept as history — out of scope for the cut. | `CampLeadConfiguration.cs:29` (deleted) |
| `camp_seasons` | `ReviewedByUserId` | `User` | `SetNull` | **DROPS** | No | `CampSeasonConfiguration.cs:62` |
| `camps` | `CreatedByUserId` | `User` | `Restrict` | **DROPS** | No | `CampConfiguration.cs:40` |

### CityPlanning

| Table | FK column | → | Action | Index after cut | Query filters/sorts on it? | Config |
|---|---|---|---|---|---|---|
| `camp_polygon_histories` | `CampSeasonId` | `CampSeason` | `Restrict` | survives — composite `CampSeasonId + ModifiedAt` (leading) | Yes — `CityPlanningRepository.cs:67,77` | `CampPolygonHistoryConfiguration.cs:24` |
| `camp_polygon_histories` | `ModifiedByUserId` | `User` | `Restrict` | **DROPS** | No | `CampPolygonHistoryConfiguration.cs:29` |
| `camp_polygons` | `CampSeasonId` | `CampSeason` | `Restrict` | survives — explicit `HasIndex` | Yes — `CityPlanningRepository.cs:34,49,96` | `CampPolygonConfiguration.cs:24` |
| `camp_polygons` | `LastModifiedByUserId` | `User` | `Restrict` | **DROPS** | No | `CampPolygonConfiguration.cs:29` |

### Email

| Table | FK column | → | Action | Index after cut | Query filters/sorts on it? | Config |
|---|---|---|---|---|---|---|
| `email_outbox_messages` | `CampaignGrantId` | `CampaignGrant` | `SetNull` | survives — explicit `HasIndex` | No | `EmailOutboxMessageConfiguration.cs:49` |
| `email_outbox_messages` | `ShiftSignupId` | `ShiftSignup` | `SetNull` | survives — composite `ShiftSignupId + TemplateName` (leading) | No | `EmailOutboxMessageConfiguration.cs:54` |
| `email_outbox_messages` | `UserId` | `User` | `SetNull` | survives — explicit `HasIndex` | Yes — `EmailOutboxRepository.cs:55,63` | `EmailOutboxMessageConfiguration.cs:44` |

### Feedback

| Table | FK column | → | Action | Index after cut | Query filters/sorts on it? | Config |
|---|---|---|---|---|---|---|
| `feedback_messages` | `SenderUserId` | `User` | `SetNull` | **DROPS** | Merge path only — `FeedbackRepository.cs:165` | `FeedbackMessageConfiguration.cs:36` |
| `feedback_reports` | `AssignedToTeamId` | `Team` | `SetNull` | survives — explicit `HasIndex` | Yes — `FeedbackRepository.cs:69,72` | `FeedbackReportConfiguration.cs:101` |
| `feedback_reports` | `AssignedToUserId` | `User` | `SetNull` | survives — explicit `HasIndex` | Yes — `FeedbackRepository.cs:66,72` | `FeedbackReportConfiguration.cs:96` |
| `feedback_reports` | `ResolvedByUserId` | `User` | `SetNull` | **DROPS** | No | `FeedbackReportConfiguration.cs:91` |
| `feedback_reports` | `UserId` | `User` | `Cascade` | survives — explicit `HasIndex` | Yes — `FeedbackRepository.cs` (report-by-user list) | `FeedbackReportConfiguration.cs:86` |

### GoogleIntegration

| Table | FK column | → | Action | Index after cut | Query filters/sorts on it? | Config |
|---|---|---|---|---|---|---|
| `google_resources` | `TeamId` | `Team` | `Restrict` | survives — explicit `HasIndex` | Yes — `GoogleResourceRepository.cs` | `GoogleResourceConfiguration.cs:60` |
| `google_sync_outbox` | `TeamId` | `Team` | `Cascade` | survives — composite `TeamId + UserId + ProcessedAt` (leading) | No | `GoogleSyncOutboxEventConfiguration.cs:38` |
| `google_sync_outbox` | `UserId` | `User` | `Cascade` | **DROPS** | No — only projected, `ProcessGoogleSyncOutboxJob.cs:61` | `GoogleSyncOutboxEventConfiguration.cs:43` |
| `sync_service_settings` | `UpdatedByUserId` | `User` | `SetNull` | **DROPS** | No | `SyncServiceSettingsConfiguration.cs:41` |

### Governance

| Table | FK column | → | Action | Index after cut | Query filters/sorts on it? | Config |
|---|---|---|---|---|---|---|
| `application_state_history` | `ChangedByUserId` | `User` | `Restrict` | **DROPS** | No | `ApplicationStateHistoryConfiguration.cs:33` |
| `applications` | `ReviewedByUserId` | `User` | `Restrict` | **DROPS** | No | `ApplicationConfiguration.cs:55` |
| `applications` | `UserId` | `User` | `Cascade` | survives — explicit `HasIndex` | Yes — `ApplicationRepository.cs` | `ApplicationConfiguration.cs:62` |
| `board_votes` | `BoardMemberUserId` | `User` | `Restrict` | **DROPS** | Yes — `ApplicationRepository.cs:110,169,198` — but all three also constrain `ApplicationId`, so the unique `(ApplicationId, BoardMemberUserId)` covers them | `BoardVoteConfiguration.cs:38` |

### Issues

| Table | FK column | → | Action | Index after cut | Query filters/sorts on it? | Config |
|---|---|---|---|---|---|---|
| `issue_comments` | `SenderUserId` | `User` | `SetNull` | **DROPS** | No | `IssueCommentConfiguration.cs:25` |
| `issues` | `AssigneeUserId` | `User` | `SetNull` | survives — explicit `HasIndex` | Yes — `IssuesRepository.cs:57` | `IssueConfiguration.cs:52` |
| `issues` | `ReporterUserId` | `User` | `Restrict` | survives — explicit `HasIndex` | Yes — `IssuesRepository.cs:56,70,114,124,138` | `IssueConfiguration.cs:49` |
| `issues` | `ResolvedByUserId` | `User` | `SetNull` | **DROPS** | No | `IssueConfiguration.cs:55` |

### Legal

| Table | FK column | → | Action | Index after cut | Query filters/sorts on it? | Config |
|---|---|---|---|---|---|---|
| `consent_records` | `UserId` | `User` | `Restrict` | survives — explicit `HasIndex` | Yes — `ConsentRepository.cs:47,59,75,88,100,120,143` | `ConsentRecordConfiguration.cs:49` |
| `legal_documents` | `TeamId` | `Team` | `Restrict` | survives — composite `TeamId + IsActive` (leading) | Yes — `LegalDocumentRepository.cs` | `LegalDocumentConfiguration.cs:44` |

### Notifications

| Table | FK column | → | Action | Index after cut | Query filters/sorts on it? | Config |
|---|---|---|---|---|---|---|
| `notification_recipients` | `UserId` | `User` | `Cascade` | survives — explicit `HasIndex` | Yes — `NotificationRepository.cs:106,124,336,398,410,427` | `NotificationRecipientConfiguration.cs:21` |
| `notifications` | `ResolvedByUserId` | `User` | `SetNull` | **DROPS** | Merge path only — `NotificationRepository.cs:478` | `NotificationConfiguration.cs:58` |

### Shifts

| Table | FK column | → | Action | Index after cut | Query filters/sorts on it? | Config |
|---|---|---|---|---|---|---|
| `general_availability` | `UserId` | `User` | `Restrict` | survives — composite `UserId + EventSettingsId` (leading) | Yes — `VolunteerTrackingRepository.cs:33,50,62` | `GeneralAvailabilityConfiguration.cs:34` |
| `rotas` | `TeamId` | `Team` | `Restrict` | **DROPS** | Yes — `Signups.cs:312`, `Management.cs:339,379,412` — but every one also pins `EventSettingsId` on the same rota, so `(EventSettingsId, TeamId)` covers them | `RotaConfiguration.cs:44` |
| `shift_signups` | `EnrolledByUserId` | `User` | `SetNull` | **DROPS** | No | `ShiftSignupConfiguration.cs:45` |
| `shift_signups` | `ReviewedByUserId` | `User` | `SetNull` | **DROPS** | No | `ShiftSignupConfiguration.cs:50` |
| `shift_signups` | `UserId` | `User` | `Restrict` | survives — explicit `HasIndex` | Yes — `ShiftRepository.Signups.cs:42,100,158,184,197` | `ShiftSignupConfiguration.cs:35` |
| `volunteer_event_profiles` | `UserId` | `User` | `Cascade` | survives — explicit `HasIndex` | Yes — `ShiftRepository.Management.cs:651,663,673,698` | `VolunteerEventProfileConfiguration.cs:44` |
| `volunteer_tag_preferences` | `UserId` | `User` | `Cascade` | survives — explicit `HasIndex` | Yes — `ShiftRepository.Signups.cs:142` | `VolunteerTagPreferenceConfiguration.cs:27` |

### Teams

| Table | FK column | → | Action | Index after cut | Query filters/sorts on it? | Config |
|---|---|---|---|---|---|---|
| `team_join_request_state_history` | `ChangedByUserId` | `User` | `Restrict` | **DROPS** | No | `TeamJoinRequestStateHistoryConfiguration.cs:33` |
| `team_join_requests` | `ReviewedByUserId` | `User` | `Restrict` | **DROPS** | No | `TeamJoinRequestConfiguration.cs:41` |
| `team_join_requests` | `UserId` | `User` | `Cascade` | survives — explicit `HasIndex` | Yes — `TeamRepository.cs:409,504,534` | `TeamJoinRequestConfiguration.cs:36` |
| `team_members` | `UserId` | `User` | `Cascade` | survives — explicit `HasIndex` | Yes — `TeamRepository.cs:297,308,348,372,380,774` | `TeamMemberConfiguration.cs:30` |
| `team_role_assignments` | `AssignedByUserId` | `User` | `Restrict` | **DROPS** | No | `TeamRoleAssignmentConfiguration.cs:48` |

### Tickets

| Table | FK column | → | Action | Index after cut | Query filters/sorts on it? | Config |
|---|---|---|---|---|---|---|
| `ticket_attendees` | `MatchedUserId` | `User` | `SetNull` | survives — explicit `HasIndex` | Yes — `TicketRepository.cs:167,374,388,461,474,726,739,898` | `TicketAttendeeConfiguration.cs:59` |
| `ticket_orders` | `MatchedUserId` | `User` | `SetNull` | survives — explicit `HasIndex` | Yes — `TicketRepository.cs:400,449,495,640,890` | `TicketOrderConfiguration.cs:64` |

