# Per-Section DbContext Split — Design (nobodies-collective/Humans#858)

_2026-07-15. Phase 1 deliverable of the stacked-PR sequence for nobodies-collective/Humans#858._

## 1. Goal and constraints

Split the single 98-entity `HumansDbContext` into per-section DbContexts, each mapping only its
section's tables, with its own `__EFMigrationsHistory_<Section>` table and a **real-up baseline
migration** (full `CreateTable`/index/FK operations generated from the current model). One Postgres
database, same connection string, same deploys; tables do not move or rename; **zero physical schema
changes anywhere in this stack**.

Decisions fixed before this design (not relitigated here):

- **Real-up baselines** with an idempotent, code-only, per-context detection path: fresh DB
  (section tables absent) → baseline executes normally; existing DB (tables present, section history
  table missing/empty) → baseline recorded as applied WITHOUT executing. One shared helper, tested
  for both paths. No hand-run SQL against any live DB, ever.
- **Old migrations in `src/Humans.Infrastructure/Migrations/` are not deleted or edited.**
  Retroactive history shrink is a separate future effort; straggler DBs mid-chain need the old
  chain intact.
- **Peel-off strategy**: clean sections only, least complicated first. `HumansDbContext` keeps
  everything dirty (the main pile).
- Each peel's `HumansDbContext` removal migration has **hand-emptied `Up()`/`Down()` bodies**
  (snapshot-only change). This is a Peter-authorized, per-instance exception to
  `memory/architecture/no-hand-edited-migrations.md`, granted in the #858 execution brief for this
  stack only. A **non-empty** operation body in any `HumansDbContext` migration in this stack is a
  stop-the-line wall.

## 2. Preflight verification (2026-07-15)

- origin/main quiet: peterdrier#1111 merged, no open PRs; upstream/main == origin/main tip
  (`17c1f9021`), so the prod promotion boundary is current.
- `/Debug/DbVersion` parity: **prod** (humans.nobodies.team), **QA** (humans.n.burn.camp), and
  **local dev** all report `lastApplied = 20260713192905_AddSurveyAudienceLoggedInSince` — the tip
  of the 122-migration chain in code — with 0 pending. Preview DBs clone QA, so QA covers them.
- Benign anomaly, recorded for the future history-shrink effort: QA and local dev have **123**
  history rows; prod has **122**, exactly matching code. The extra row is
  `20260322172854_AddDrivePermissionLevel`, applied on QA/dev in March and later removed from code
  before it ever reached prod. Harmless today (EF ignores unknown history rows); it becomes relevant
  only when history is rewritten/squashed.
- Only non-model FK constraints in the live DB are Hangfire's own (`hangfire` schema, not
  EF-managed). **The `public` schema has zero FK-constraint drift against the EF model** — every
  constraint the DB has, the model describes, and vice versa.

## 3. Partition map

Authoritative source: the EF model snapshot (`HumansDbContextModelSnapshot.cs`) cross-checked
against `Data/Configurations/<Section>/` folders and the repository tree. 115 mapped entities /
tables. The §8 table-ownership map in `design-rules.md` is directionally right but stale in spots
(missing `camp_members`, `camp_role_definitions`, `camp_role_assignments`, `team_early_entry_grants`,
`profile_languages`; still lists the removed `team_pages`) — this audit is from code.

### 3.1 Sections and their tables

| Section | Tables | Cross-section rels (out / in) | Verdict |
|---------|--------|------------------------------|---------|
| **SystemSettings** | system_settings | 0 / 0 | **CLEAN — peel 1** |
| **Containers** | containers, container_placements | 0 / 0 | **CLEAN — peel 2** |
| **Gate** | gate_scan_events, gate_settings, gate_staff_pins | 0 / 0 | **CLEAN — peel 3** |
| **Agent** | agent_conversations, agent_messages, agent_settings | 0 / 0 | **CLEAN — peel 4** |
| **Expenses** | expense_reports, expense_lines, expense_attachments, holded_expense_outbox_events | 0 / 0 | **CLEAN — peel 5** |
| **Finance** | holded_expense_docs, holded_category_map, holded_ledger_lines, holded_creditor_contacts, holded_sync_states | 0 / 0 | **CLEAN — peel 6** |
| **Store** | store_products, store_orders, store_order_lines, store_payments, store_invoices, store_treasury_sync_state | 0 / 0 | **CLEAN — peel 7** |
| **Surveys** | surveys, survey_questions, survey_question_options, survey_invitations, survey_responses, survey_answers | 0 / 0 | **CLEAN — peel 8** |
| **EventGuide** | events, event_categories, event_venues, event_guide_settings, event_moderation_actions, event_favourites, event_preferences | 0 / 0 | **CLEAN — peel 9** |
| Users/Identity | users, roles, user_roles, user_claims, user_logins, role_claims, user_tokens | 0 / 20+ | main pile (hub; peels last, after drain) |
| Teams | teams, team_members, team_join_requests, team_join_request_state_history, team_role_definitions, team_role_assignments, team_early_entry_grants | 5 / 7 | main pile |
| Profiles | profiles, contact_fields, user_emails, volunteer_history_entries, communication_preferences, profile_languages, account_merge_requests | 6 / 0 | main pile |
| Auth | role_assignments | 2 / 0 | main pile |
| Governance | applications, application_state_history, board_votes | 4 / 0 | main pile |
| Legal | legal_documents, document_versions, consent_records | 2 / 0 | main pile |
| Camps | camps, camp_seasons, camp_leads, camp_images, camp_historical_names, camp_settings, camp_members, camp_role_definitions, camp_role_assignments | 3 / 2 | main pile |
| CityPlanning | city_planning_settings, camp_polygons, camp_polygon_histories | 4 / 0 | main pile |
| Calendar | calendar_events, calendar_event_exceptions | 1 / 0 | main pile |
| Shifts | rotas, shifts, shift_signups, event_settings, general_availability, volunteer_event_profiles, volunteer_build_statuses, shift_tags, rota_shift_tags, volunteer_tag_preferences, event_participations | 8 / 1 | main pile |
| Budget | budget_years, budget_groups, budget_categories, budget_line_items, budget_audit_logs, ticketing_projections | 3 / 0 | main pile |
| Tickets | ticket_orders, ticket_attendees, ticket_sync_state, ticket_transfer_requests | 2 / 0 | main pile |
| Campaigns | campaigns, campaign_codes, campaign_grants | 2 / 1 | main pile |
| GoogleIntegration | google_resources, google_sync_outbox, sync_service_settings | 4 / 1 | main pile |
| Email | email_outbox_messages | 3 / 0 | main pile |
| Feedback | feedback_reports, feedback_messages | 5 / 0 | main pile |
| Issues | issues, issue_comments | 4 / 0 | main pile |
| Notifications | notifications, notification_recipients | 2 / 0 | main pile |
| AuditLog | audit_log | 2 / 0 | main pile (horizontal) |
| Agent-adjacent framework | DataProtectionKeys | 0 / 0 | stays in HumansDbContext permanently (framework-owned: `IDataProtectionKeyContext`), as do the Identity tables (`IdentityDbContext` base) |

Scanner and Onboarding own no tables. Hangfire owns its own schema outside EF.

### 3.2 Nav/FK audit vs the issue's dirty list

The issue estimated "~20 obsolete navs across 15 entities". Measured from the snapshot:

- **29 cross-section navigation properties** across 19 entities. Not on the issue's list:
  `AccountMergeRequest → User` ×3, `AuditLogEntry → GoogleResource` (a horizontal→vertical
  reference — doubly rule-violating), `EmailOutboxMessage → CampaignGrant` and `→ ShiftSignup`,
  `Campaign → User`, `BudgetAuditLog → User`, `CampPolygon/CampPolygonHistory → CampSeason`,
  `TeamJoinRequestStateHistory → User`, `BoardVote`-adjacent items. On the issue's list but
  actually nav-free: `GeneralAvailability` (constraint-only).
- **33 additional nav-less cross-section FK constraints** (`HasOne(..., null)` in the snapshot —
  physical constraints with no C# nav). These block peeling exactly like navs do: the dependent's
  model can't describe the constraint without the principal entity.
- **Total: 62 cross-section model relationships.** Full list in §10 (drain backlog).

**Cleanness criterion used:** a section peels only when it has zero cross-section relationships in
either direction — no outbound (its model would need another section's entity) and no inbound (the
main pile's model would need its entity). All 62 rels sit entirely within the main pile after the
9 clean peels; **no peeled baseline contains any cross-section FK**, so the fake-applied path can
never try to recreate one on existing DBs (§6).

## 4. Baseline-detection mechanism (the shared helper)

### 4.1 The central subtlety: mark-applied is the *only* live path while the old chain exists

The old `HumansDbContext` chain keeps its historical `CreateTable`s for peeled sections (old
migrations are not edited). Startup migrates `HumansDbContext` **first**, so on a **genuinely fresh
DB** the old chain creates every table — including peeled sections' tables — before any section
context runs. By the time `<Section>DbContext` migrates, its tables always exist:

- **Every real environment (fresh or existing) takes the mark-applied branch** for as long as the
  old chain remains intact.
- The **baseline-executes branch** is reachable only when a section context runs in *isolation*
  (empty DB, that context only). That is exactly what CI Layer 2 and the helper's tests do (§7),
  and it becomes the live fresh-DB path after the future history shrink removes the old
  `CreateTable`s.

Corollary: startup order is deterministic and load-bearing — `HumansDbContext` first, then section
contexts (any order among themselves; they share nothing).

### 4.2 Helper design

One implementation in `Humans.Infrastructure.Hosting`, used by `DatabaseMigrationHostedService`
for every section context:

```csharp
// Registration (per peel PR):
services.AddSectionDbContext<AgentDbContext>(sentinelTable: "agent_conversations");

// DatabaseMigrationHostedService.StartingAsync, after HumansDbContext migrates:
//   foreach section context descriptor (registration order):
//     await SectionMigrationRunner.MigrateAsync(context, sentinelTable, logger, ct);

static async Task MigrateAsync(DbContext db, string sentinelTable, ILogger log, CancellationToken ct)
{
    var applied = await db.Database.GetAppliedMigrationsAsync(ct);       // [] if history table absent
    if (!applied.Any())
    {
        var tablesExist = await SentinelTableExistsAsync(db, sentinelTable, ct); // SELECT to_regclass(...)
        if (tablesExist)
        {
            // Existing DB: record the baseline as applied WITHOUT executing it.
            var baselineId = db.Database.GetMigrations().First();        // baseline = earliest
            var history = db.GetService<IHistoryRepository>();
            await db.Database.ExecuteSqlRawAsync(history.GetCreateIfNotExistsScript(), ct);
            await db.Database.ExecuteSqlRawAsync(
                history.GetInsertScript(new HistoryRow(baselineId, ProductInfo.GetVersion())), ct);
        }
        // Fresh DB (sentinel absent): fall through — MigrateAsync executes the baseline for real.
    }
    await db.Database.MigrateAsync(ct);  // applies baseline (fresh) and/or post-baseline migrations
}
```

Properties:

- **Idempotent**: once the history row exists, every later boot sees `applied` non-empty and goes
  straight to `MigrateAsync` (no-op or pending post-baseline migrations).
- **Only the baseline is ever fake-applied.** Post-baseline section migrations always run normally
  — mark-applied happens only when the history table is empty, which is only true before the
  baseline is recorded.
- **Not manual DB surgery**: the insert uses EF's own `IHistoryRepository` script generation —
  the same mechanism `MigrateAsync` itself uses to record migrations — executed by the tested
  startup code path on every environment identically. No operator ever types SQL.
- **New sections born after the split** (tables never in the old chain) work unchanged: sentinel
  absent → baseline executes. The helper needs no knowledge of the old chain.
- Sentinel = one stable table per section (its first table, e.g. `agent_conversations`). A
  half-created section state is impossible: the old chain created all-or-nothing per its own
  transactional migrations, and baselines run in one migration transaction.

### 4.3 Context wiring

Per section context (all identical to `HumansDbContext`'s wiring except the history table):

- `internal sealed class <Section>DbContext(DbContextOptions<<Section>DbContext>) : DbContext` —
  plain `DbContext` (Identity/DataProtection base classes stay on `HumansDbContext` only).
- `OnModelCreating` applies **only that section's** `IEntityTypeConfiguration`s (explicit
  `ApplyConfiguration` calls — not assembly scanning, which would drag in every section).
- Same `ConfigureNpgsql` options (NodaTime, `MigrationsAssembly("Humans.Infrastructure")`,
  split-query) **plus** `MigrationsHistoryTable("__EFMigrationsHistory_<Section>")`.
- Same interceptor set as `HumansDbContext` (QueryMonitoring, UserInfoSaveChanges,
  LegalDocumentSaveChanges). The latter two watch only main-pile entities today, so they're no-ops
  on section contexts, but a single shared options-configuration path means zero divergence risk
  and query monitoring keeps covering everything.
- Registered via one new generic helper in `InfrastructureServiceCollectionExtensions`
  (`AddSectionDbContext<TContext>`) that does `AddDbContext` + `AddDbContextFactory` + records the
  (contextType, sentinelTable) descriptor for the migration hosted service.
- One `IDesignTimeDbContextFactory<TContext>` per context, mirroring `HumansDbContextFactory`.
- Migrations live in `src/Humans.Infrastructure/Migrations/<Section>/`
  (`--output-dir Migrations/<Section>`), giving each context its own folder + its own
  `<Section>DbContextModelSnapshot.cs`.

Repositories switch constructor generic only: `IDbContextFactory<HumansDbContext>` →
`IDbContextFactory<<Section>DbContext>` (8 of 9 candidates; `AgentRepository` takes the scoped
context directly and switches the same way). Nothing above the repository layer changes.

## 5. Historical-migration content audit (per candidate)

Method: every `migrationBuilder.Sql(` / `InsertData` / `UpdateData` / `DeleteData` in all 122
migrations (32 files contain at least one), mapped to owning section. Dispositions for the nine
candidates — **(a)** re-expressed in the baseline, **(b)** dead, with justification:

| Section | Historical op | Disposition |
|---------|--------------|-------------|
| SystemSettings | `AddSystemSettings`: InsertData `IsEmailSendingPaused=false` | **(a) via model**: `SystemSetting` has `HasData` — the generated baseline re-emits it as `InsertData` automatically |
| Containers | — none — | n/a |
| Gate | — none — | n/a |
| Agent | `AddAgentSection`: InsertData agent_settings singleton | **(a) via model**: `AgentSettings.HasData` — baseline re-emits (current model values, which supersede the historical row's stale `claude-sonnet-4-6` defaults by design: `HasData` rows were kept current in the snapshot) |
| Expenses | `HoldedLedgerSingleSource`: `UPDATE expense_reports SET Status='Approved' WHERE Status IN ('SepaSent','Paid')` | **(b) dead**: one-time status-collapse of rows that can no longer be written (the enum values were removed); a fresh DB has no such rows |
| Finance | `HoldedActuals`: InsertData holded_sync_states singleton | **(a) via model**: `HoldedSyncState.HasData` |
| Store | — none — | n/a |
| Surveys | — none — | n/a |
| EventGuide | `AddEventsSection`: InsertData event_categories (11 rows) | **(a) via model**: `EventCategory.HasData` |

Every seed a candidate section owns is **model-level `HasData`**, so real-up baselines regenerate
them with zero hand work; on existing DBs the fake-applied baseline never re-inserts (no duplicate
seed risk). No candidate has raw-SQL indexes, triggers, functions, or extensions:
the `consent_records`/`audit_log` immutability triggers and the `team_role_definitions` functional
unique index belong to main-pile sections and are untouched by this stack. Postgres partial/unique
indexes used by candidates (e.g. `ix_gate_scan_events_admit_dedupe_key`) are declared in the model
and appear in generated baselines. Identity-by-default int PKs (agent_settings, holded_sync_states,
store_treasury_sync_state, ticket_sync_state) are model-declared — baselines regenerate them.

**Non-candidate future note (drain backlog):** when Legal/AuditLog eventually peel, their
plpgsql immutability triggers exist only as raw SQL in the old chain. That is a from-scratch gap
to solve *then* (e.g. EF `Sql` in that section's baseline with explicit authorization, or moving
enforcement into the model). Explicitly out of scope now; recorded so it isn't silently lost.

### 5.1 Second audit class discovered during peel 3: scaffolded physical column defaults

**Found the hard way** (Gate peel, 2026-07-15): the content audit above only covered
`Sql`/`InsertData`/`UpdateData`/`DeleteData`. A second class silently diverges fresh-baseline
schema from old-chain schema: **`AddColumn` with a scaffolded `defaultValue:`**. When EF adds a
non-nullable column to an existing table it emits `DEFAULT <clr-default>` so existing rows can be
backfilled — and never drops the default. The physical default persists in every old-chain DB, but
the model (correctly, per `memory/feedback_ef_sentinel`) declares no default, so a model-generated
baseline omits it. The schema-equivalence test (§7.2.iii) catches exactly this.

Authoritative physical audit (information_schema of a chain-built DB vs the model snapshot),
all nine candidate sections:

| Section | Physical default | In model? | Verdict |
|---------|-----------------|-----------|---------|
| Gate | `gate_staff_pins.AdminEnrolled DEFAULT false` (from `20260701005052_AddGateStaffPinAdminEnrolled`) | **no** | **DEFERRED** |
| Store | `store_orders.Year DEFAULT 0` | **no** | **DEFERRED** |
| Expenses | `expense_lines.LineType DEFAULT 'Receipt'` | yes (`HasDefaultValue`) | clean |
| Surveys | 7 jsonb `DEFAULT '{}'` columns | yes (`HasDefaultValueSql`) | clean |
| SystemSettings, Containers, Agent, Finance, EventGuide | none | n/a | clean |

**Gate and Store are deferred out of this run.** Options for unblocking them (Peter's call — all
were rejected as in-run improvisations): (a) a realignment `AlterColumn` migration on
`HumansDbContext` dropping the stray physical default (model is source of truth; EF cannot
auto-generate it because model==snapshot already — it would need a deliberate, explicitly-authorized
schema change); (b) declaring the default in the model — forbidden for the bool
(`HasDefaultValue(false)` sentinel trap), merely wrong-direction for `Year`; (c) weakening the
equivalence check to ignore defaults — rejected, it guards the guarantee that makes real-up
baselines trustworthy.

## 6. Cross-section FK constraint inventory

All 62 cross-section constraints (list in §10) have **both endpoints in the main pile** after the
nine peels — that is precisely why these nine are the clean set. Consequences:

- No peeled baseline contains a cross-section FK ⇒ the fake-applied path never risks recreating
  one on existing DBs, and the baseline-executes path never needs another section's tables.
- `HumansDbContext` continues to describe all 62 (unchanged model relationships among main-pile
  entities). Nothing in this stack changes any physical constraint.
- The peeled sections' own **intra**-section FKs (e.g. `expense_lines → expense_reports`) move
  wholesale into their section model/baseline.

## 7. Fresh-schema consumers

Audited consumers that build schema from migrations today:

1. **Docker integration tests** (`HumansWebApplicationFactory`): starts a fresh Testcontainers
   Postgres and boots the real app; schema comes from `DatabaseMigrationHostedService` in
   `StartingAsync`. **Post-split this is automatically correct**: the same hosted service migrates
   `HumansDbContext` (old chain creates everything) then each section context (helper takes the
   mark-applied branch). Every integration-test run therefore proves boot-on-fresh-DB *and* the
   mark-applied branch end-to-end. No fixture changes required.
2. **New helper tests** (added with peel 1): Testcontainers-based tests of the runner itself —
   (i) empty DB + section context alone → baseline executes, tables + history row exist;
   (ii) DB pre-built by the old `HumansDbContext` chain, empty section history → helper records the
   baseline without executing (boot succeeds, no DDL errors, history row present, subsequent boot
   idempotent); (iii) schema equivalence — section tables from path (i) vs path (ii) compared on
   columns/types/nullability/defaults/indexes/constraints (ignoring ordinal position, which
   legitimately differs: old chain evolved columns incrementally, baselines inline them).
3. **CI `build.yml`** — two single-context assumptions to update with peel 1 and keep green each
   peel:
   - `dotnet ef migrations has-pending-model-changes` (Layer 1 and Layer 2 post-apply): errors as
     soon as a second context exists. Becomes a loop over every context (`--context <C>`).
   - Layer 2 `verify-migrations-apply` (`dotnet ef database update` from scratch): stays as-is for
     `HumansDbContext` (full historical replay), and gains a per-section isolated step — for each
     section context, `database update --context <C>` against a **separate empty database** — which
     executes the real-up baselines for real on every migration-touching PR. (The CLI can't invoke
     the in-app helper; the two-database shape is what makes both paths CLI-verifiable.)
4. **Local dev / preview / QA / prod**: existing DBs — mark-applied branch via normal app boot.
   Preview deploys clone QA (which by then includes any earlier peels' history tables — also fine:
   `applied` non-empty ⇒ straight to `MigrateAsync`).

## 8. Peel order and per-peel scope

Order (criteria: navs = 0 for all, so ranked by table count, then seeds/PK quirks, then repo shape):

| # | Branch | Section | Tables | Notes |
|---|--------|---------|--------|-------|
| 1 | `858/01-systemsettings` | SystemSettings | 1 | Carries the shared mechanism (helper + hosted-service loop + `AddSectionDbContext` + analyzer generalization + ratchet scan fix + CI loop). Smallest possible section: 1 table, 0 FKs, 1 `HasData` row, 1 repo |
| 2 | `858/02-containers` | Containers | 2 | intra-section only |
| 3 | `858/03-agent` | Agent | 3 | `HasData`, identity int PK, scoped-context repo |
| 4 | `858/04-expenses` | Expenses | 4 | dead data-op disposition (§5); `LineType` default is model-declared |
| 5 | `858/05-finance` | Finance | 5 | `HasData`, identity int PK |
| 6 | `858/06-surveys` | Surveys | 6 | owned/JSON survey config; jsonb defaults model-declared |
| 7 | `858/07-eventguide` | EventGuide | 7 | `HasData` (11 seed rows) |
| — | *(deferred)* | Gate | 3 | **§5.1 wall**: `AdminEnrolled` physical default not in model |
| — | *(deferred)* | Store | 6 | **§5.1 wall**: `Year` physical default not in model |

Each peel PR (branch `858/NN-<section>` off the previous branch; PR base = previous branch):

1. `<Section>DbContext` + design-time factory + `AddSectionDbContext` registration (context,
   sentinel table).
2. Real-up baseline: `dotnet ef migrations add Baseline<Section> --context <Section>DbContext
   --output-dir Migrations/<Section>` — reviewed by the EF migration reviewer agent
   (`memory/process/ef-migration-review-gate.md`) before commit.
3. Section repositories switch to `<Section>DbContext`; DI updated; that section's repo unit tests
   switch their in-memory context type. Nothing above the repository layer changes.
4. `HumansDbContext` stops mapping the section: DbSets removed, configurations no longer applied;
   removal migration generated then **hand-emptied `Up()`/`Down()`** (snapshot-only; §1
   authorization). Shared snapshot shrinks accordingly.
5. CI `build.yml` context lists updated.
6. Verification (definition of done, all before opening the PR): clean build; full test suite
   including Docker integration tests; `has-pending-model-changes` clean for **every** context;
   both helper branches proven (tests of §7.2 + schema equivalence); throwaway-migration proof
   (add a migration in the peeled section, confirm its Designer contains only that section's
   model, `migrations remove` cleanly); NoDestructiveMigrationOps ratchet green with zero baseline
   additions.
7. After opening: preview deploy (`{pr_id}.n.burn.camp`, DB cloned from QA) boots and
   `/api/version` responds — the real-database proof of the mark-applied path — result reported on
   the PR.

## 9. Enforcement updates (peel 1)

- **Analyzers**: `ControllerDbContextInjectionAnalyzer`, `ApplicationServiceDbContextInjectionAnalyzer`,
  `OrchestratorRepositoryInjectionAnalyzer`, `SingleRepositoryPerTableAnalyzer` all match the
  literal type `Humans.Infrastructure.Data.HumansDbContext`. They generalize to "any
  `DbContext`-derived type in `Humans.Infrastructure.Data`" so peeled tables stay policed
  (one-table-one-repository, no context injection outside repositories, orchestrator bans).
  `HumansDbContext` remains a match, so existing baselines don't move.
- **NoDestructiveMigrationOpsRule**: scans `Migrations/*.cs` top-directory-only and excludes only
  `HumansDbContextModelSnapshot.cs` by exact name. Extends to `SearchOption.AllDirectories` and a
  `*ModelSnapshot.cs` suffix exclusion, so section baselines and future section migrations are in
  scope. Baselines contain only `Create`/`AddColumn`/index ops in `Up()` ⇒ zero baseline-file
  additions expected.
- **Pre-commit hook** (wrong-directory guard) already permits `Migrations/<Section>/`.

## 10. Drain backlog — the 62 cross-section relationships that keep the main pile dirty

These are the nav/constraint removals that must land (each as bare-Guid decoupling per
`memory/architecture/no-cross-section-ef-joins.md`, constraint drops sequenced per
`no-drops-until-prod-verified`) before further sections can peel. Grouped by dependent section —
navs marked ★, the rest are nav-less physical constraints:

- **Teams → Users** (7): TeamMember★, TeamJoinRequest★×2, TeamJoinRequestStateHistory★,
  TeamRoleAssignment★ — plus inbound: BudgetCategory★, CalendarEvent★, FeedbackReport★,
  GoogleResource, GoogleSyncOutboxEvent, LegalDocument, Rota, BudgetLineItem (constraints on their
  side).
- **Profiles → Users** (6): AccountMergeRequest★×3, Profile, UserEmail, CommunicationPreference.
- **Shifts → Users** (8): ShiftSignup×3, GeneralAvailability, VolunteerEventProfile,
  VolunteerTagPreference, EventParticipation (+ Rota → Teams).
- **Governance → Users** (4): Application×2, ApplicationStateHistory, BoardVote.
- **Issues → Users** (4): Issue★×3, IssueComment★.
- **Feedback → Users/Teams** (5): FeedbackReport★×4, FeedbackMessage★.
- **CityPlanning → Camps/Users** (4): CampPolygon★/CampPolygonHistory★ → CampSeason,
  ×2 → Users.
- **GoogleIntegration → Teams/Users** (4): GoogleResource, GoogleSyncOutboxEvent×2,
  SyncServiceSettings.
- **Camps → Users** (3): Camp, CampLead, CampSeason.
- **Email → Campaigns/Shifts/Users** (3): EmailOutboxMessage★ → CampaignGrant,
  ★ → ShiftSignup, → User.
- **Budget → Users/Teams** (3): BudgetAuditLog★, BudgetCategory★, BudgetLineItem.
- **Auth → Users** (2): RoleAssignment★×2.
- **Campaigns → Users** (2): Campaign★, CampaignGrant★.
- **Tickets → Users** (2): TicketOrder, TicketAttendee (MatchedUserId).
- **Legal → Users/Teams** (2): ConsentRecord, LegalDocument.
- **Notifications → Users** (2): Notification, NotificationRecipient.
- **AuditLog → Users/GoogleIntegration** (2): AuditLogEntry → User, ★ → GoogleResource (the
  only horizontal→vertical model reference in the codebase; highest-priority drain item).
- **Calendar → Teams** (1): CalendarEvent★.

Unblock order fallout: Tickets, Notifications, Legal, Governance, Auth, Issues, Feedback, Calendar,
Budget, Campaigns, Email, CityPlanning become peelable as their outbound sets drain (most only
reference Users/Teams); Camps needs CityPlanning's inbound navs removed; Shifts needs Email's
inbound nav removed; Teams needs 7 inbound removals; Users unblocks last.

## 11. `dotnet ef` per-context usage (migration-discipline docs)

With >1 context in the assembly, **every** `dotnet ef` invocation needs `--context`:

```bash
# main pile (unchanged commands + explicit context):
dotnet ef migrations add <Name> --context HumansDbContext \
  --project src/Humans.Infrastructure --startup-project src/Humans.Web

# a peeled section:
dotnet ef migrations add <Name> --context AgentDbContext --output-dir Migrations/Agent \
  --project src/Humans.Infrastructure --startup-project src/Humans.Web

dotnet ef migrations has-pending-model-changes --context <C> ...   # per context
```

A `memory/process/ef-multi-context-commands.md` atom ships with peel 1 (the PR that makes
`--context` mandatory), updating `INDEX.md` in the same commit.

## 12. Rollback story

Rollback of a peel = **revert the PR** (code-only: context, baseline files, DI, repo constructors,
snapshot-only removal migration; the `__EFMigrationsHistory_<Section>` table left behind is inert
and ignored — a re-peel later regenerates a fresh baseline id). Migrate-**down** below a section
baseline is gone by design: the baseline's `Down()` would drop the section's tables, which is never
acceptable on a shared DB — exactly why the fake-applied path exists on the way in and why there is
deliberately no symmetric path out. Physical schema is untouched by the entire stack, so no
rollback scenario involves data.

## 13. Open questions for Peter

1. **`/Debug/DbVersion` scope** — it reports `HumansDbContext`'s history only. Post-split that
   remains true (main-pile chain), which stays correct for the squash-boundary checks you use it
   for. Extending it to enumerate per-section histories is a small follow-up. Options: leave as-is
   (my default), or I add per-section rows to the payload in a later peel. Implementer-decides is
   fine here.
2. **QA/dev orphan history row** (`20260322172854_AddDrivePermissionLevel`, §2) — leave in place
   (my default; EF ignores it) or note it for cleanup during the future history shrink?
3. **CI Layer 2 cost** — the per-section isolated-apply step adds one empty-DB `database update`
   per peeled context to migration-touching PRs (~seconds each on the self-hosted runner).
   Acceptable? Alternative is trusting the Testcontainers helper tests alone (they run in
   `dotnet test` anyway); I prefer both since Layer 2 is the explicit from-scratch gate.
4. **`AuditLogEntry → GoogleResource` nav** (§10) — flagging that the horizontal AuditLog section
   holds a model-level reference into a vertical section; want a drain issue opened for it now?
5. **Gate/Store realignment (§5.1)** — to unblock their peels, my recommendation is a deliberate,
   explicitly-authorized `AlterColumn` migration on `HumansDbContext` dropping the two stray
   physical defaults (`gate_staff_pins.AdminEnrolled`, `store_orders.Year`) so the DB matches the
   model, soaked through QA→prod before the peels. EF tooling cannot generate it (model and
   snapshot already agree), so it needs your sign-off on the mechanism as well as the change.
