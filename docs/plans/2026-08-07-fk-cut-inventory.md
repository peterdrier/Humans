# Cross-section FK cut — index and delete-behavior inventory

**Issue:** nobodies-collective/Humans#992, tasks 1 and 3.
**Anchor:** `origin/main` `37eb40d99`, analysed 2026-08-07.
**Scope:** read-only inventory. No migration is written here. Conditions 2 and 4 of the
FK-cut carve-out (`2026-06-13-q3-transition-plan.md`, *FK-cut carve-out*) gate the migration
and are not this document's subject.

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
| less `camp_leads.UserId` — table dropped whole by nobodies-collective/Humans#774, in flight as peterdrier/Humans#1199 | −1 |
| **Net, in scope for the bulk FK-cut migration** | **54** across **38 tables** in **17 sections** |

`camp_leads` is the only G2 table drop that removes an inventoried relationship. Camps keeps
`camps.CreatedByUserId` and `camp_seasons.ReviewedByUserId`, so the section count stays at 17.
If peterdrier/Humans#1199 has not merged when the bulk migration is authored, re-check — the
scope rule is the plan's: *a relationship is out of scope if its table is dropped by that
table's own G2 demolition item*.

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
speculative. The five that do carry query traffic:

| Table.column | Query evidence | Judgement |
|---|---|---|
| `audit_log.ActorUserId` | `AuditLogRepository.cs:171` (`userIds.Contains(e.ActorUserId.Value)`), `:190` (`a.ActorUserId == userId`), `:208` | **Highest risk.** `audit_log` is append-only and the fastest-growing table in the system; two of the three call sites are GDPR export contributors that scan by actor. Re-add. |
| `campaign_grants.UserId` | `CampaignRepository.cs:160,173,327,350,380` — five `UserId ==` predicates, including the per-user grant list and the GDPR export row | **Re-add.** The only other index touching `UserId` is `(CampaignId, UserId)` unique, whose leading column is `CampaignId` — it cannot serve a `UserId`-only lookup. |
| `budget_audit_logs.ActorUserId` | `BudgetRepository.cs:1013,1029` | Re-add. Small table, but the predicate is a bare equality with nothing else to cover it. |
| `board_votes.BoardMemberUserId` | `ApplicationRepository.cs:198` — `!a.BoardVotes.Any(v => v.BoardMemberUserId == boardMemberUserId)`, a solo predicate. `:169` is covered by the unique `(ApplicationId, BoardMemberUserId)`. | Partial exposure. Re-add — one index on a table of a few hundred rows is not worth reasoning about twice. |
| `rotas.TeamId` | `ShiftRepository.Signups.cs:312` — `s.Shift.Rota.TeamId == deptId`, a solo join-side predicate. `Management.cs:379` is covered by the explicit `(EventSettingsId, TeamId)`. | Partial exposure. Re-add. |

Two more dropping columns are queried, but only from the user-merge path
(`FeedbackRepository.cs:165` on `feedback_messages.SenderUserId`,
`NotificationRepository.cs:478` on `notifications.ResolvedByUserId`). Merging duplicate
accounts is a rare admin action on tables measured in thousands of rows; a sequential scan
there is acceptable. Recorded rather than recommended — say so explicitly in the migration PR
rather than leaving it to be re-derived.

**Recommendation for the migration PR:** add explicit `HasIndex(...)` for the five rows above.
Leave the other twenty alone (18 unused + 2 merge-path) and state in the PR body that they were
checked, so a reviewer does not have to re-derive it.

### What this analysis cannot tell you

Stated plainly rather than guessed:

- **No row counts.** This is static analysis of a repo with no live-database access. Whether
  losing `IX_audit_log_ActorUserId` actually degrades anything depends on how large `audit_log`
  has grown in production. At ~500 users most of these tables are small enough that Postgres
  would sequential-scan them regardless of the index. The five recommendations above are made
  on the conservative side: an index is cheap and the cost of guessing wrong is a silent
  production regression that no test catches.
- **No `EXPLAIN`.** Whether the planner uses each of these indexes *today* is unverified. If
  anyone wants to trim the five down, the check is `EXPLAIN (ANALYZE)` against prod on the
  cited call sites — not further reading of the source.
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
| `ShiftSignup` | 1 | Yes — `ShiftRepository.Management.cs:187,294`, when a rota or shift is deleted. |
| `CampaignGrant` | 1 | Yes — `CampaignRepository.cs:364`, the account-merge dedup branch. |
| `GoogleResource` | 1 | No hard-delete path found. |

**So 13 of the 55 referential actions are live in ordinary production flow. The other 42 —
every `User`-targeting one — fire only on the three user hard-delete paths below.** That
includes all seven the issue calls out by name (`team_members.UserId`,
`role_assignments.UserId`, `issues.AssigneeUserId`, `issues.ResolvedByUserId`,
`ticket_orders.MatchedUserId`, `ticket_attendees.MatchedUserId`, `issues.ReporterUserId`).

#### The three user hard-delete paths

Two of the three bypass `IUserRepository` entirely and delete through ASP.NET Identity's
`UserStore`, which is why they do not appear in a search of the repository layer.

| # | Path | Delete call | Gating | Cross-section dependents at delete time |
|---|---|---|---|---|
| 1 | Dev-dashboard reset — `UserService.DeleteUsersAsync` (`UserService.cs:1253-1260`) → `UserRepository.DeleteUsersAsync` (`UserRepository.cs:321-341`) | `ctx.Users.Where(...).ExecuteDeleteAsync` at `:335-337` | Admin (`UserService.cs:1257`); only caller is `DevelopmentDashboardSeeder.ResetAsync:478`, reachable only via `POST dev/seed/dashboard/reset`, which is `AdminOnly` **and** 404s outside `ASPNETCORE_ENVIRONMENT=Development` (`DevSeedController.cs:102,114`); seeder not DI-registered in Production (`Program.cs:76-80`) | **Yes** — seeded users have been through a full seed run |
| 2 | OAuth signup rollback — `ExternalLoginService.TryDeleteOrphanUserAsync` (`:331-343`), called from `:230,264,279,288` | `userManager.DeleteAsync(user)` at `:335` | None beyond the signup flow itself — **production** | **No** — see below |
| 3 | Magic-link signup rollback — `AccountProvisioningService` | `userManager.DeleteAsync(user)` at `:164` | None beyond the signup flow itself — **production** | **No** — see below |

**Paths 2 and 3 need no replacement, and that is verified rather than assumed.** Both delete a
user created moments earlier in the same request, on a failure branch, before onboarding has
written anything. In `ExternalLoginService.CreateUserAsync` the only writes between
`userManager.CreateAsync` (`:223`) and the last rollback point are `AddLoginAsync` (`:227`,
`AspNetUserLogins`) and `ReconcileOAuthIdentityAsync` (`UserEmail` rows) — both Users-section
tables, neither among the 42. `EnsureStubProfileAsync` runs at `:238`, *after* the final
rollback. `AccountProvisioningService` has the same shape: `CreateAsync` at `:141`, the only
write before the rollback is `AddVerifiedEmailAsync` at `:155` (a `UserEmail` row), the delete is
at `:164`, and `EnsureStubProfileAsync` runs at `:177`. So none of the 42 dependent rows can
exist when these deletes fire — today or after the cut.

That does not make them irrelevant. They are production callers that hard-delete a user, and any
enforcement that only guards `IUserService.DeleteUsersAsync` would miss them entirely. See the
follow-up at the end.

#### What the cut changes on the dev-reset path (path 1)

Today the reset leans on the database to finish the job. It clears the seeded event (`:454`)
and the dev users' shift signups (`:462`) explicitly, then deletes the users and lets the FKs
handle the rest: the **nine `User`-targeting `Cascade` relationships** (`role_assignments.UserId`,
`applications.UserId`, `feedback_reports.UserId`, `notification_recipients.UserId`,
`team_join_requests.UserId`, `team_members.UserId`, `volunteer_event_profiles.UserId`,
`volunteer_tag_preferences.UserId`, `google_sync_outbox.UserId`) delete the dependent rows, the
`SetNull` ones blank their columns, and the `Restrict` ones would refuse the delete outright if
a dev user had picked up an unexpected dependent.

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

### The 13 live in production flow

The 42 `User`-targeting actions are dealt with above. These are the ones ordinary production
flow can trigger.

| Principal | Dependent | Action | What breaks after the cut | Replacement |
|---|---|---|---|---|
| `Team` | `calendar_events.OwningTeamId` | `Restrict` | `PermanentlyDeleteTeamAsync` currently throws a raw FK violation if the team owns calendar events. After the cut it succeeds and orphans them. | **Pre-check in `TeamService.PermanentlyDeleteTeamAsync`**, matching the pattern already there for Google resources (`TeamService.cs:1862-1865`). |
| `Team` | `google_resources.TeamId` | `Restrict` | **Already replaced, but not equivalently.** `TeamService.cs:1862-1865` pre-checks and throws a clear message, with a comment saying it exists precisely because the FK is `Restrict`. The check is not atomic with the delete — see *Pre-checks are weaker than the constraint* below. | Template for the other three, with that caveat. |
| `Team` | `legal_documents.TeamId` | `Restrict` | Same as `calendar_events` — orphaned required documents, which would then be missing from every consent dashboard that resolves the team. | Pre-check. |
| `Team` | `rotas.TeamId` | `Restrict` | Same — orphaned rotas, and their shifts and signups behind them. | Pre-check. |
| `Team` | `budget_categories.TeamId` | `SetNull` | Column keeps a stale `Guid`; budget screens resolve a team name that no longer exists. | Null it explicitly inside `PermanentlyDeleteTeamAsync`'s transaction — or accept the orphan, since the delete already requires an admin and the column is nullable. **Needs a call.** |
| `Team` | `budget_line_items.ResponsibleTeamId` | `SetNull` | As above. | As above. |
| `Team` | `feedback_reports.AssignedToTeamId` | `SetNull` | An assigned report keeps a dead team id; the Feedback admin filter at `FeedbackRepository.cs:69` returns it under a team that no longer exists. | As above. |
| `Team` | `google_sync_outbox.TeamId` | `Cascade` | Pending outbox events for a deleted team survive and are processed by `ProcessGoogleSyncOutboxJob` against a team that is gone. | Delete pending events for the team inside the same transaction. This one is not merely cosmetic — it is a job that will run. |
| `CampSeason` | `camp_polygons.CampSeasonId` | `Restrict` | Deleting a `Camp` currently throws if any of its seasons has a city-planning polygon. After the cut the polygon is orphaned and `CityPlanningRepository.cs:34,49,96` returns rows for a season that no longer exists. | Pre-check in the Camps delete path, or delete the polygons through `ICityPlanning…` — **cross-section, so it must go through a service, not a repository.** |
| `CampSeason` | `camp_polygon_histories.CampSeasonId` | `Restrict` | As above, for the history table. | As above. |
| `ShiftSignup` | `email_outbox_messages.ShiftSignupId` | `SetNull` | Deleting a rota (`ShiftRepository.Management.cs:187`) leaves outbox rows pointing at a dead signup. The dedup index `(ShiftSignupId, TemplateName)` then guards against a signup that cannot exist. | **Orphan is acceptable** — see below. |
| `CampaignGrant` | `email_outbox_messages.CampaignGrantId` | `SetNull` | Account-merge dedup (`CampaignRepository.cs:364`) leaves outbox rows pointing at a dead grant. | **Orphan is acceptable** — see below. |
| `GoogleResource` | `audit_log.ResourceId` | `SetNull` | Nothing today — no code deletes a `GoogleResource`. | None needed; record the constraint. |

### Explicit "orphan is acceptable" decisions

Recorded here so the migration PR does not have to re-argue them:

- **`email_outbox_messages.ShiftSignupId` and `.CampaignGrantId`.** `email_outbox_messages` is
  an append-only send log that `EmailOutboxRepository.DeleteSentOlderThanAsync`
  (`EmailOutboxRepository.cs:183-190`) prunes on age anyway. A stale id in a sent-mail record
  is a historical fact, not a live reference — the row records that an email *was* sent about a
  signup that has since been deleted, which is true. Neither column is read as a lookup key
  (`ShiftSignupId` appears only in the dedup index, `CampaignGrantId` in no predicate at all).
  Leave both as stale ids; add no replacement.
- **The 42 `User`-targeting actions, on the two signup-rollback paths.** Both delete a user that
  cannot yet have any of the 42 dependent rows, as traced above. Nothing to orphan, so no
  replacement. This is not a blanket pass for the 42: the dev-dashboard reset deletes users that
  *do* have dependents, and needs the seeder-side cleanup specified above.

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

1. **Condition 2's ratchet is not armed.** `Directory.Build.props:79` lists `HUM0024` in
   `WarningsNotAsErrors` globally, and **zero** configurations carry
   `[Grandfathered("HUM0024", ...)]`. The props comment at `:51-55` describes a per-site
   grandfathering scheme that is not in use, and states its exit condition as *"remove this
   entry once the last `[Grandfathered("HUM0024", ...)]` is gone"* — a condition already
   vacuously true, so nothing will ever trigger it. A new cross-section FK added today produces
   a non-blocking warning. Removing `HUM0024` from `WarningsNotAsErrors` in the bulk cut's PR
   is what actually makes the boundary permanent, which is the thing condition 2 is for.
2. **Condition 4's cited example is stale.** The plan
   (`2026-06-13-q3-transition-plan.md:209-217`) and the issue both cite `LegalDocument.Team` as
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
| `audit_log` | `ActorUserId` | `User` | `SetNull` | **DROPS** | **Yes** — `AuditLogRepository.cs:171` (`userIds.Contains(e.ActorUserId.Value)`), `:190` (`a.ActorUserId == userId`), `:208` | `AuditLogEntryConfiguration.cs:49` |
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
| `camp_leads` | `UserId` | `User` | `Restrict` | **DROPS** | n/a — table dropped | `CampLeadConfiguration.cs:29` |
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
| `board_votes` | `BoardMemberUserId` | `User` | `Restrict` | **DROPS** | **Partly** — `ApplicationRepository.cs:198` (solo predicate inside `Any`); `:169` is covered by the unique composite | `BoardVoteConfiguration.cs:38` |

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
| `rotas` | `TeamId` | `Team` | `Restrict` | **DROPS** | **Partly** — `ShiftRepository.Signups.cs:312` (`s.Shift.Rota.TeamId == deptId`, solo); `Management.cs:379` is covered by `EventSettingsId+TeamId` | `RotaConfiguration.cs:44` |
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

