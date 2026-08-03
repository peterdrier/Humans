# G0 Demolition Inventory

> Generated 2026-08-03 against commit `5a9bbe198` (fork `main`). Feeds the
> [Q3 Transition Plan](2026-06-13-q3-transition-plan.md) G0 gate ("Demolition inventory: per-section
> list of dead columns/tables, cross-section FK constraints, and non-conforming table names —
> feeds G2 work items") and the Section tracker table in that doc.
>
> **Method:** code-derived, not doc-derived. Every claim below cites a `file:line` or a
> `[Grandfathered(...)]`/`ToTable(...)` call actually in the tree. Where the plan doc or
> `docs/architecture/design-rules.md` §8 disagreed with what the code does, the code wins and the
> drift is noted.
>
> **On the plan's "§10 cross-section FK list":** `design-rules.md` has no §10 cross-section FK
> list — §10 is "Cross-Cutting Services", unrelated. The nearest existing catalog is the
> `[Grandfathered(ruleId: "HUM0024", ...)]` attribute the HUM0024 analyzer already requires on
> every configuration class with a live cross-section `HasOne`/`HasForeignKey`/`HasMany` (see
> `memory/architecture/no-cross-section-ef-joins.md`).
>
> **Method corrected 2026-08-03 — the HUM0024 attribute list is NOT ground truth.** This doc
> originally described itself as a transcription of the 34 grandfathered configuration classes.
> Its own later corrections falsify that premise: Governance contributes four cross-section FKs
> and GoogleIntegration four more, and **none of those eight carries a HUM0024 marker**. Treating
> the attribute list as complete would drop all eight on any future regeneration — and it also
> means the analyzer is not firing where it should, which is a finding in its own right (raised
> in both sections' scorecards). The source of truth for this inventory is a **repo-wide scan of
> every EF configuration class for cross-section `HasOne`/`HasForeignKey`/`HasMany`**, with the
> `[Grandfathered(HUM0024)]` attribute recorded as an *attribute of* each relationship rather
> than as the enumeration mechanism. Regenerate it that way.

## Prior art

nobodies-collective/Humans#866 (G5 project-split issue) carries a live status comment
(2026-08-02) referencing a 31-scaffolded-physical-default inventory, produced by a new
`PhysicalDefaultParityTests` integration test that diffs model-declared vs physical column
defaults. That inventory was **realigned in fork PR #1150 (commit `d2eb5c27b`)** — it is
closed demolition work, not open, and is cited here only as (a) prior art for this inventory's
format and (b) evidence the schema-audit machinery (`PhysicalDefaultParityTests`) already exists
and could plausibly grow a companion test for §2/§3 below (e.g. "no cross-section FK constraint
without a `[Grandfathered(HUM0024)]`", "every `ToTable` name matches its section prefix").

---

## AuditLog (horizontal)

### Cross-section FK
`AuditLogEntryConfiguration.cs:49-52` — typed `HasOne<User>()` (no nav) on `ActorUserId` →
`AspNetUsers` (Users/Identity). `AuditLogEntryConfiguration.cs:69-72` — live nav
`HasOne(e => e.Resource)` on `ResourceId` → `google_resources` (**GoogleIntegration** — corrected
2026-08-03, was mislabeled Teams; see the ownership correction below); `AuditLogEntry.Resource`
is an un-obsoleted `GoogleResource?` nav property (`AuditLogEntry.cs:69`). Grandfathered
HUM0024, `AuditLogEntryConfiguration.cs:13-17`.

### Non-conforming table name
`AuditLogEntryConfiguration.cs:22` maps to physical table **`audit_log`**, not `audit_log_entries`
as `design-rules.md` §8's table-ownership map claims — a doc/code drift, not a code problem.
Propose renaming the physical table to `audit_log_entries` to match the entity name and fix the
doc in the same PR.

---

## Auth (horizontal)

### Cross-section FK
`RoleAssignmentConfiguration.cs:43-46` — nav `CreatedByUser` (`[Obsolete]`,
`RoleAssignment.cs:67-68`) on `CreatedByUserId` → `AspNetUsers` (Users). `:53-56` — nav `User`
(`[Obsolete]`, `RoleAssignment.cs:29-30`) on `UserId` → `AspNetUsers` (Users). Grandfathered
HUM0024, `RoleAssignmentConfiguration.cs:8-12`.

### Non-conforming table name
`role_assignments` (`RoleAssignmentConfiguration.cs:17`) — no `auth_` prefix. Propose
`auth_role_assignments`.

---

## Governance

### Cross-section FK
**Added 2026-08-03** — the original pass recorded Governance as having none, which also made its
G1.4 PASS wrong. Four typed `HasOne<User>()` relationships → `AspNetUsers` (Users) across three
Governance-owned tables: `ApplicationConfiguration.cs:49-51` (`ReviewedByUserId`) and `:56-58`
(`UserId`); `ApplicationStateHistoryConfiguration.cs:27-29` (`ChangedByUserId`);
`BoardVoteConfiguration.cs:32-34` (`BoardMemberUserId`). None carry a `[Grandfathered(HUM0024)]`
marker. All four are G2 FK cuts.

### Non-conforming table names
`ApplicationConfiguration.cs:12` → `applications`; `ApplicationStateHistoryConfiguration.cs:11`
→ `application_state_history`; `BoardVoteConfiguration.cs:11` → `board_votes`. None carry a
`governance_` prefix and they're inconsistent with each other (`application_*` vs bare
`board_votes`). Propose `governance_applications`, `governance_application_state_history`,
`governance_board_votes`.

---

## Legal & Consent

### Cross-section FK
`LegalDocumentConfiguration.cs:44-46` — typed `HasOne<Team>()` (no nav) on `TeamId` → `teams`
(Teams). `ConsentRecordConfiguration.cs:49-51` — `HasForeignKey(cr => cr.UserId)` → `AspNetUsers`
(Users), typed FK. Grandfathered HUM0024: `LegalDocumentConfiguration.cs:8-12`,
`ConsentRecordConfiguration.cs:13-17`.

### Non-conforming table names
`legal_documents` conforms. `DocumentVersionConfiguration.cs:13` → `document_versions` and
`ConsentRecordConfiguration.cs:22` → `consent_records` both lack the `legal_` prefix their
sibling table carries. Propose `legal_document_versions`, `legal_consent_records`.

---

## Profiles (shared contract)

### Dead columns
- `Profile.ProfilePictureData` — `Profile.cs:96-97`, `[Obsolete(..., DiagnosticId =
  "HUM_PROFILE_PICTUREDATA")]` citing issue #702 ("DB→FS migration complete (PR #576); column
  reserved for prod-soak drop"). Matches plan item #528 ("Remove ProfilePictureData column from
  DB after filesystem migration", still OPEN) — the two issues are the same storage-chain item
  under different numbers; #528 is the umbrella, #702 the landed-migration tracker.
- `Profile.IsSuspended` — `Profile.cs:121-123`, `[Obsolete(..., DiagnosticId =
  "HUM_PROFILE_ISSUSPENDED")]` citing issue #635, superseded by `Profile.State`
  (`ProfileState.Suspended`). Narrower than plan item #844 (which targets the whole
  `ProfileState` column/classifier) — this is a specific redundant boolean flag, safe to drop
  independently once #635's write-sites are ported.

### Queued, not yet actionable
`Profile.State` (`ProfileState`) itself is **not dead** — actively read/written throughout
`UserService.cs` (`:349,425-426,466,527,529,559`), `UserStateClassifier.cs:54-55`, and
`TeamController.cs:290,369`. Plan item #844 ("Retire the ProfileState column (suspension →
User.State, then drop)", OPEN) is real but blocked on #834 (stored `User.State` becoming the
sole access-state source) — do not queue for G2 yet; re-check when #834 lands.

`contact_fields.ProfileId` (`ContactField.cs:19`) and `volunteer_history_entries.ProfileId`
(`VolunteerHistoryEntry.cs:18`) are plan item #516's target columns, but #516 is explicitly
**blocked by #515** ("Do not start until #515 has shipped to production and the data-validation
query has passed for at least one full business cycle"). `Profile.cs` already carries both `Id`
(`:11`) and `UserId` (`:13`) — the dual-key exists on the anchor table, but the FK propagation to
these two dependent tables (a `UserId` shadow column alongside `ProfileId`) has not happened yet.
`CampLead` (`CampLead.cs:13`) already carries only `UserId`, no `ProfileId` — that table's #516
step is done (moot, since `camp_leads` is itself queued for full deletion under #774, see Camps
below).

### Cross-section FK
None — Profiles' own configs (`ContactFieldConfiguration`, `ProfileLanguageConfiguration`,
`VolunteerHistoryEntryConfiguration`, `AccountMergeRequestConfiguration`,
`CommunicationPreferenceConfiguration`, `UserEmailConfiguration`) all FK into `profiles`, not
another section — none carry `[Grandfathered(HUM0024)]`.

### Non-conforming table names
`profiles` conforms (root noun). `profile_languages` conforms. `contact_fields`
(`ContactFieldConfiguration.cs:11`), `user_emails` (`UserEmailConfiguration.cs:11`),
`communication_preferences` (`CommunicationPreferenceConfiguration.cs:10`),
`volunteer_history_entries` (`VolunteerHistoryEntryConfiguration.cs:11`), and
`account_merge_requests` (`AccountMergeRequestConfiguration.cs:10`) all lack a `profile_`
prefix.

**Reassess before queueing (added 2026-08-03).** The obvious proposals are
`profile_contact_fields`, `profile_user_emails`, `profile_communication_preferences`,
`profile_volunteer_history_entries`, `profile_account_merge_requests` — but the confirmed section
inventory folds **Profiles into the canonical `Users` shared-contract section**, and G2's rename
rule is "prefix with the owning *section*". Spending a destructive migration to stamp `profile_`
onto tables whose section is being eliminated would be immediately self-defeating. Re-derive these
under the Users boundary (`user_…`? keep bare? some stay `profile_` because the *entity* is
`Profile` even though the section is Users?) before any of them becomes a G2 work item. This also
subsumes the `user_emails` naming question already parked in the Unmapped tail.

### Misfiled configuration (not a table-ownership violation, but pre-G5 drift)
`AccountMergeRequestConfiguration.cs` lives under `Configurations/Profiles/`, but
`design-rules.md` §8 lists `account_merge_requests` under **Users/Identity**, and the only
repository touching `AccountMergeRequest` is `Repositories/Users/AccountMergeRepository.cs`
(confirmed by grep — zero other repository references). The table is genuinely Users-owned; the
configuration class is just in the wrong folder. Flag for a mechanical move at G1, not G2.

---

## Users/Identity (shared contract)

### Dead / obsolete-for-drop columns
- `User.DisplayName` (`User.cs:14`) — `[Obsolete(..., DiagnosticId = "HUM_USER_DISPLAYNAME")]`
  citing #691. **Not a drop target** — still the actively-written source column; the
  obsoletion restricts *rendering reads* (must go through `UserInfo.BurnerName`), not the
  column's existence. Listed here for completeness, not as a G2 item.
- `User.NormalizedEmail` (`User.cs:58`) — `[Obsolete(..., DiagnosticId =
  "HUM_USER_NORMALIZEDEMAIL")]` citing #635, `[Architecture.ExpiresOn("2026-09-01", ...)]`.
  Shadow-populated by Identity; matches plan item #603.
- `User.Email`, `User.EmailConfirmed`, `User.UserName`, `User.NormalizedUserName` (`User.cs:36,
  60, 68, 79`) — live virtual overrides (computed from `UserEmails`/`Id`), **not yet
  `[Obsolete]`-tagged**, but explicitly targeted by issue #603's still-open scope (§3
  `UserConfiguration.Ignore()` + §4 migration dropping all five `AspNetUsers` columns +
  `EmailIndex`/`UserNameIndex`). #603 is blocked on porting the remaining `u.Email` LINQ callers
  (`DuplicateAccountService`, `ProfileService` admin search, `DriveActivityMonitorRepository`,
  `DevLoginController`, one Feedback test, `UserRepository.RewritePrimaryEmailAsync`) — real
  G1-shaped prerequisite work before this is a safe G2 column drop.
- `User.GoogleEmailStatus` (`User.cs:123-124`) — `[Obsolete("Moved to UserEmail.GoogleEmailStatus
  (per-address) — #687. Do not read or write; retained for the DB column until a drop
  migration.")]`.

### Cross-section FK
None — `UserConfiguration.cs` and `AspNetUser*` configs are the *target* of every cross-section
FK listed elsewhere in this doc, not a source. No `[Grandfathered(HUM0024)]` on any Users config.

### Misfiled configuration
`EventParticipationConfiguration.cs` lives under `Configurations/Shifts/`, and
`VolunteerEventProfileConfiguration.cs`/`GeneralAvailabilityConfiguration.cs` etc. genuinely are
Shifts-owned — but `event_participations` itself is read/written exclusively by
`Repositories/Users/UserRepository.cs` (confirmed by grep — the only repository reference) and
`design-rules.md` §8 lists it under Users/Identity. `EventParticipationConfiguration.cs:41-44`
even wires the full two-way nav (`HasOne<User>().WithMany(u => u.EventParticipations)`) unlike
every genuinely-Shifts-owned config in the same folder, which strip navs per §6c. Table is
Users-owned; the configuration class is in the wrong folder (same class of drift as
`AccountMergeRequestConfiguration` above). Flag for a mechanical move at G1.

---

## Camps

### Dead table
**`camp_leads`** (`CampLeadConfiguration.cs:17`) + the `CampLead` entity + its
`camp_leads.UserId → AspNetUsers` FK — plan item #774 ("Drop legacy camp_leads table — physical
cleanup (follow-up to #753)", OPEN). `CampRepository.cs:320-322`: "legacy camp_leads — only the
seed-migration snapshot + role-backed team-sync reads remain; mutation/query methods retired with
the Camp Lead role move. Entity/table kept until nobodies-collective/Humans#774." Only two read
paths remain, both explicitly transitional: `GetAllLeadAssignmentsForUserAsync`
(`CampRepository.cs:354-364`, "GDPR export of legacy camp_leads rows until #774 drops the table")
and the historical seed path. `GetActiveLeadUserIdsAsync`/`IsLeadAnywhereAsync`
(`CampRepository.cs:324-352`) already read from `CampRoleAssignments` instead (post-#753
retirement). This is the cleanest full-table demolition in the inventory — no live writes, one
GDPR-export reader to re-point (into `CampRoleAssignment` GDPR contributor) before the drop.

### Dead DB default (not a column drop — a constraint drop)
`CampRoleDefinitionConfiguration.cs:36-40` — `.HasDefaultValueSql("'None'")` on
`CampRoleDefinition.SpecialRole`. Plan item #787 ("Remove obsolete SQL default on
CampRoleDefinition.SpecialRole", OPEN): the backfill this default served (migration
`20260519173900_AddSpecialRoleToCampRoleDefinition`) is complete; the constraint now only
produces an EF sentinel-ambiguity warning at startup. Pure `AlterColumn` dropping the default,
no data change.

### Cross-section FK
`CampConfiguration.cs:40-42` — typed `HasOne<User>()` on `CreatedByUserId` → `AspNetUsers`
(Users). `CampLeadConfiguration.cs:29-32` — typed `HasOne<User>()` on `UserId` → `AspNetUsers`
(Users) (dies with the table under #774). `CampSeasonConfiguration.cs:62-65` — typed
`HasOne<User>()` on `ReviewedByUserId` → `AspNetUsers` (Users). Grandfathered HUM0024:
`CampConfiguration.cs:11-15`, `CampLeadConfiguration.cs:8-12`, `CampSeasonConfiguration.cs:12-16`.

### Non-conforming table names
None — `camps`, `camp_seasons`, `camp_leads`, `camp_images`, `camp_historical_names`,
`camp_settings`, `camp_members`, `camp_role_definitions`, `camp_role_assignments` all carry the
`camp_` prefix cleanly.

---

## City Planning

### Cross-section FK
`CampPolygonConfiguration.cs:24-27` — live nav `HasOne(p => p.CampSeason)` on `CampSeasonId` →
`camp_seasons` (**Camps**). `:29-32` — typed `HasOne<User>()` on `LastModifiedByUserId` →
`AspNetUsers` (Users). `CampPolygonHistoryConfiguration.cs:24-27` — live nav
`HasOne(h => h.CampSeason)` on `CampSeasonId` → `camp_seasons` (Camps); `:29-32` — typed
`HasOne<User>()` on `ModifiedByUserId` → `AspNetUsers` (Users). Note: `design-rules.md` §15i
already documents `CampPolygonHistories.ModifiedByUser` as migrated off `.Include()` to a batched
`IUserService.GetByIdsAsync` lookup at the *query* layer — but the DB-level FK constraint (typed,
no nav) is still live and still HUM0024-grandfathered; the query-side fix didn't remove the
schema-level cross-section coupling. Grandfathered HUM0024: `CampPolygonConfiguration.cs:8-12`,
`CampPolygonHistoryConfiguration.cs:8-12`.

### Non-conforming table names
`city_planning_settings` conforms. `camp_polygons` (`CampPolygonConfiguration.cs:17`) and
`camp_polygon_histories` (`CampPolygonHistoryConfiguration.cs:17`) carry the **Camps** section's
prefix despite being City Planning-owned — actively misleading, not just missing a prefix.
Propose `city_planning_polygons`, `city_planning_polygon_histories` (or a shorter
`cityplanning_*` if the long form reads badly).

---

## Calendar

### Cross-section FK
`CalendarEventConfiguration.cs:33-35` — nav `OwningTeam` (`[Obsolete]`, `CalendarEvent.cs:24-25`)
on `OwningTeamId` → `teams` (Teams). Grandfathered HUM0024, `CalendarEventConfiguration.cs:8-12`.

### Non-conforming table names
None — `calendar_events`, `calendar_event_exceptions` both conform.

---

## Shifts

### Dead columns — added 2026-08-03
Six `VolunteerEventProfile` dietary/medical columns (`VolunteerEventProfile.cs:39-61`):
`DietaryPreference`, `Allergies`, `Intolerances`, `AllergyOtherText`, `IntoleranceOtherText`,
`MedicalConditions`. Class doc comment: "Dietary + medical moved to Profile... These
columns are RETAINED but unused — the data was backfilled to Profile and all code now
reads/writes Profile... Do NOT read or write these." Per-property doc comments: "RETAINED
for prod-soak drop. Use Profile.\<X\>." Grep of `src/Humans.Application/Services/Shifts`
for all six property names → zero live reads/writes, confirming the class comment. Same
"retained for prod-soak drop" pattern as `Profile.ProfilePictureData`/`Profile.IsSuspended`
above — a G2 drop candidate once soaked, not yet tracked as a numbered issue here.

### Cross-section FK
`GeneralAvailabilityConfiguration.cs:34-37` — typed `HasOne<User>()` on `UserId` →
`AspNetUsers`. `RotaConfiguration.cs:44-47` — typed `HasOne<Team>()` on `TeamId` → `teams`.
`ShiftSignupConfiguration.cs:35-38,45-53` — **three** typed `HasOne<User>()`: `UserId`,
`EnrolledByUserId`, `ReviewedByUserId`, all → `AspNetUsers`. `VolunteerEventProfileConfiguration.cs:44-47`
— typed `HasOne<User>()` on `UserId` → `AspNetUsers`. `VolunteerTagPreferenceConfiguration.cs:27-30`
— typed `HasOne<User>()` on `UserId` → `AspNetUsers`. That is **7 Shifts cross-section
relationships across 5 tables** (corrected 2026-08-03 — see below).

`EventParticipationConfiguration.cs:41-44` — live two-way nav on `UserId` → `AspNetUsers`.
**Not counted as a Shifts relationship (corrected 2026-08-03, hence 7 not 8):** this file's own
ownership determination above establishes that `event_participations` is Users-owned and the
configuration class is merely misfiled into `Configurations/Shifts/`. A Users-owned table with a
FK to the Users-owned `AspNetUsers` is an *internal* Users relationship, so counting it here both
inflated Shifts and would have scheduled an FK cut that isn't a cross-section cut at all. It stays
listed in this section only because the configuration class currently sits in the folder — the
fix is the mechanical class move already flagged at G1.

Grandfathered HUM0024 on
all six configuration classes in the folder: `GeneralAvailabilityConfiguration.cs:9-12`, `RotaConfiguration.cs:8-11`,
`ShiftSignupConfiguration.cs:8-11`, `VolunteerEventProfileConfiguration.cs:9-12`,
`VolunteerTagPreferenceConfiguration.cs:8-11`, `EventParticipationConfiguration.cs:8-11`.

### Non-conforming table names
`shift_signups`, `shift_tags`, `rota_shift_tags` conform. `rotas`
(`RotaConfiguration.cs:17`), `general_availability` (`GeneralAvailabilityConfiguration.cs:18`),
`volunteer_event_profiles` (`VolunteerEventProfileConfiguration.cs:19`),
`volunteer_build_statuses` (`VolunteerBuildStatusConfiguration.cs:24`),
`volunteer_tag_preferences` (`VolunteerTagPreferenceConfiguration.cs:17`), and — the sharpest
one — `event_settings` (`EventSettingsConfiguration.cs:13`) all lack a `shift_` prefix. Propose
`shift_rotas`, `shift_general_availability`, `shift_volunteer_event_profiles`,
`shift_volunteer_build_statuses`, `shift_volunteer_tag_preferences`, and **`shift_event_settings`**
— the last rename also resolves the `event_` prefix collision below.

**`event_` prefix collision (cross-section naming ambiguity, not owned by one section):**
`event_settings` (Shifts) and `events`/`event_categories`/`event_venues`/
`event_moderation_actions`/`event_favourites`/`event_preferences`/`event_guide_settings`
(EventGuide) and `event_participations` (Users) all share the `event_` root but belong to three
different sections. This is very likely *why* `event_guide_settings` breaks EventGuide's own
otherwise-clean `event_` convention (`EventGuideSettingsConfiguration.cs:11`) — `event_settings`
was already taken by Shifts. Renaming Shifts' table to `shift_event_settings` frees `event_` for
EventGuide exclusively and lets `event_guide_settings` shorten to `event_settings` in the same
pass if desired (optional, cosmetic). `event_participations` (Users) should also lose the
`event_` prefix to avoid re-colliding — propose `user_event_participations`.

---

## Budget

### Cross-section FK
`BudgetCategoryConfiguration.cs:28` — live nav `HasOne(c => c.Team)` on `TeamId` → `teams`
(Teams). `BudgetLineItemConfiguration.cs:30-33` — typed `HasOne<Team>()` on
`ResponsibleTeamId` → `teams` (Teams). `BudgetAuditLogConfiguration.cs:28` — live nav
`HasOne(a => a.ActorUser)` on `ActorUserId` → `AspNetUsers` (Users). Grandfathered HUM0024:
`BudgetCategoryConfiguration.cs:8-12`, `BudgetLineItemConfiguration.cs:8-12`,
`BudgetAuditLogConfiguration.cs:8-12`.

### Non-conforming table names
All `budget_*` except `TicketingProjectionConfiguration.cs:11` → `ticketing_projections`, which
lacks the `budget_` prefix its owning section (`BudgetGroupConfiguration.cs:23` — one-to-one FK
from `BudgetGroup`) uses everywhere else. Propose `budget_ticketing_projections`.

---

## Expenses

### Non-conforming table name (ownership-naming confusion, not missing a prefix)
`HoldedExpenseOutboxEventConfiguration.cs:12` → `holded_expense_outbox_events` — carries the
**Finance** section's `holded_` prefix despite being Expenses-owned (`design-rules.md` §8 lists
it under Expenses' `ExpenseReportService`, and the table sits alongside `expense_reports`/
`expense_lines`/`expense_attachments` in the same `Configurations/Expenses/` folder). Propose
`expense_holded_outbox_events` to make the ownership unambiguous — this table is an outbox
Expenses writes to and Finance's sync job drains, not a Finance-owned table.

No cross-section FK — not `[Grandfathered(HUM0024)]`.

---

## Tickets

### Dead columns — added 2026-08-03
`TicketTransferRequest.VendorStepsJson` (`TicketTransferRequestConfiguration.cs:56-59`,
`DefaultValue("[]")`). Config comment (`:43-46`): "Vendor-writeback columns written by the
automated transfer path (ProcessTransferAsync / RetryReissueAsync) and read by the admin
queue/detail. `VendorStepsJson` is the one genuinely dormant column — unread, dropped in a
follow-up PR after prod soak." Already noted in the G0 first-audit Tickets scorecard's G2
queue notes but was missing from this inventory's per-section list and summary total.

### Cross-section FK
`TicketOrderConfiguration.cs:64-67` — typed `HasOne<User>()` on `MatchedUserId` →
`AspNetUsers` (Users). `TicketAttendeeConfiguration.cs:59-62` — typed `HasOne<User>()` on
`MatchedUserId` → `AspNetUsers` (Users). Grandfathered HUM0024:
`TicketOrderConfiguration.cs:8-12`, `TicketAttendeeConfiguration.cs:8-12`.

### Non-conforming table name (doc drift only)
`TicketSyncStateConfiguration.cs:12` maps to `ticket_sync_state` (singular); `design-rules.md`
§8 lists it as `ticket_sync_states` (plural). Code prefix is fine either way — just fix the doc
or pick one and rename, not a real demolition item.

---

## Teams

### Cross-section FK
`TeamMemberConfiguration.cs:30-32` — nav `User` (`[Obsolete]`, `TeamMember.cs:43-44`) on
`UserId` → `AspNetUsers`. `TeamRoleAssignmentConfiguration.cs:48-50` — nav `AssignedByUser`
(`[Obsolete]`, `TeamRoleAssignment.cs:59-60`) on `AssignedByUserId` → `AspNetUsers`.
`TeamJoinRequestConfiguration.cs:36-38,41-43` — nav `User` and nav `ReviewedByUser` (both
`[Obsolete]`, `TeamJoinRequest.cs:23-24,36-37`) → `AspNetUsers` ×2.
`TeamJoinRequestStateHistoryConfiguration.cs:33-35` — nav `ChangedByUser` (`[Obsolete]`,
`TeamJoinRequestStateHistory.cs:50-51`) on `ChangedByUserId` → `AspNetUsers`. Grandfathered
HUM0024 on all four: `TeamMemberConfiguration.cs:8-12`, `TeamRoleAssignmentConfiguration.cs:8-12`,
`TeamJoinRequestConfiguration.cs:8-12`, `TeamJoinRequestStateHistoryConfiguration.cs:8-12`.

### `google_resources` — ownership correction, added 2026-08-03
~~Previously listed here as "owned by Teams per `design-rules.md` §8 (`TeamResourceService`),
named for Google Integration."~~ **Wrong — `google_resources` is GoogleIntegration-owned, not
Teams-owned.** `design-rules.md` §8's table-ownership map is stale on this point; per this
doc's own stated methodology ("where the plan doc or `design-rules.md` §8 disagreed with what
the code does, the code wins"): `TeamResourceService.cs` physically lives at
`src/Humans.Application/Services/GoogleIntegration/TeamResourceService.cs` (not a Teams path),
with its own doc comment "TeamService owns TeamMembers/Teams; **we** own GoogleResources" —
"we" being GoogleIntegration. `reforge.surface-score.json`'s `GoogleIntegration` entry maps
`src/Humans.Domain/Entities/Google*` (covers the `GoogleResource` entity) and
`src/Humans.Infrastructure/Repositories/GoogleIntegration/**` (covers
`GoogleResourceRepository.cs`, confirmed at that exact path) to GoogleIntegration. See the
Google Integration section below for the corrected entry. Moved here to Teams: nothing — this
was a misfiled item, not a Teams finding. The `GoogleResourceConfiguration.cs` class itself
(directly under `Configurations/`, no section subfolder) is a misfiled-configuration case, same
class of drift as `AccountMergeRequestConfiguration`/`EventParticipationConfiguration` above —
flag for a mechanical move to `Configurations/GoogleIntegration/` at G1.

All `team_*` tables conform.

---

## Google Integration

### Cross-section FK — added 2026-08-03 (moved from Teams, see correction there)
`GoogleResourceConfiguration.cs:54-57` — typed `HasOne<Team>()` (no nav) on `TeamId` →
`teams` (Teams). `google_resources` is GoogleIntegration-owned (see the ownership
correction under Teams above), so this is a genuine cross-section FK, not an internal one.
**Not `[Grandfathered(HUM0024)]`** — the full config file (`GoogleResourceConfiguration.cs`)
carries no `[Grandfathered]` attribute at all, unlike every other cross-section FK in this
inventory. Either an under-the-radar HUM0024 gap (the analyzer/its baseline should be
catching this and isn't) or the attribute was never added when the FK landed — worth a
follow-up beyond just the rename.

**Three further cross-section FKs — added 2026-08-03** (this section originally inventoried
only `google_resources.TeamId`, undercounting the section 1 → 4 across three tables):
`GoogleSyncOutboxEventConfiguration.cs:32-34` — typed `HasOne<Team>()` on `TeamId` → `teams`
(Teams); `:37-39` — typed `HasOne<User>()` on `UserId` → `AspNetUsers` (Users);
`SyncServiceSettingsConfiguration.cs:35-37` — typed `HasOne<User>()` on `UpdatedByUserId` →
`AspNetUsers` (Users). Like `google_resources.TeamId`, **none carries a
`[Grandfathered(HUM0024)]` marker**, so all four sit outside the attribute-based allowlist that
§2 treats as the ground-truth FK catalog — the same under-the-radar gap noted above, now known
to span the whole section. Note the `SyncServiceSettings` case is not a contradiction of the
audit's "`UpdatedByUser` nav was already fully removed" finding: the nav is gone (good), but the
physical FK constraint remains and is what G2 cuts.

### Non-conforming table name
`SyncServiceSettingsConfiguration.cs:15` → `sync_service_settings` — its sibling table
(`GoogleSyncOutboxEventConfiguration.cs:11` → `google_sync_outbox`) carries the `google_`
prefix; this one doesn't. Propose `google_sync_service_settings`. Separately,
`google_resources` (`GoogleResourceConfiguration.cs:11`) is misleadingly Teams-prefixed for
a GoogleIntegration-owned table — propose `google_team_resources` (keeping the ownership
signal on the section that actually owns the repository/entity).

---

## Email

### Cross-section FK
`EmailOutboxMessageConfiguration.cs:46` — `HasForeignKey(e => e.UserId)` → `AspNetUsers`
(Users). `:49-51` — live nav `HasOne(e => e.CampaignGrant)` on `CampaignGrantId` →
`campaign_grants` (**Campaigns**). `:54-56` — live nav `HasOne(e => e.ShiftSignup)` on
`ShiftSignupId` → `shift_signups` (**Shifts**). Three distinct cross-section targets from one
table — the widest fan-out in this inventory. Grandfathered HUM0024,
`EmailOutboxMessageConfiguration.cs:8-12`.

### Non-conforming table names
None — `email_outbox_messages` conforms.

---

## Notifications

### Cross-section FK
`NotificationConfiguration.cs:60` — `HasForeignKey(n => n.ResolvedByUserId)` → `AspNetUsers`
(Users). `NotificationRecipientConfiguration.cs:21-24` — typed `HasOne<User>()` on `UserId` →
`AspNetUsers` (Users). Grandfathered HUM0024: `NotificationConfiguration.cs:8-12`,
`NotificationRecipientConfiguration.cs:8-12`.

### Non-conforming table names
None — `notifications`, `notification_recipients` both conform (root-noun convention, same
pattern as `camps`, `teams`, `issues`).

---

## Feedback

### Cross-section FK
`FeedbackReportConfiguration.cs:86-88,91-93,96-98,101-103` — **four** navs, all `[Obsolete]`:
`User`/`ResolvedByUser`/`AssignedToUser` (`FeedbackReport.cs:21-22,62-63,72-73`) → `AspNetUsers`
×3, and `AssignedToTeam` (`FeedbackReport.cs:81-82`) → `teams` (Teams) ×1.
`FeedbackMessageConfiguration.cs:36-38` — nav `SenderUser` (`[Obsolete]`,
`FeedbackMessage.cs:19-20`) on `SenderUserId` → `AspNetUsers`. That is **5 relationships across
2 tables** (corrected 2026-08-03 — the summary row said 4, dropping the message-sender FK).
Grandfathered HUM0024: `FeedbackReportConfiguration.cs:9-13`, `FeedbackMessageConfiguration.cs:8-12`.

### Non-conforming table names
None — `feedback_reports`, `feedback_messages` both conform.

---

## Issues

### Cross-section FK
`IssueConfiguration.cs:50,53,56` — **three** navs, all `[Obsolete]`: `Reporter`, `Assignee`,
`ResolvedByUser` (`Issue.cs:17-18,44-45,53-54`) → `AspNetUsers` ×3.
`IssueCommentConfiguration.cs:27` — nav `SenderUser` (`[Obsolete]`, `IssueComment.cs:13-14`) on
`SenderUserId` → `AspNetUsers`. Grandfathered HUM0024: `IssueConfiguration.cs:8-12`,
`IssueCommentConfiguration.cs:8-12`.

### Non-conforming table names
None — `issues`, `issue_comments` both conform (root-noun convention).

---

## Campaigns

### Cross-section FK
`CampaignConfiguration.cs:31-33` — nav `CreatedByUser` (`[Obsolete]`, `Campaign.cs:28-29`) on
`CreatedByUserId` → `AspNetUsers`. `CampaignGrantConfiguration.cs:40-42` — nav `User`
(`[Obsolete]`, `CampaignGrant.cs:29-30`) on `UserId` → `AspNetUsers`. Grandfathered HUM0024:
`CampaignConfiguration.cs:8-12`, `CampaignGrantConfiguration.cs:8-12`.

### Non-conforming table names
None — `campaigns`, `campaign_codes`, `campaign_grants` all conform.

---

## Sections audited with no findings

No dead columns/tables, no cross-section FK, no non-conforming table names surfaced in:
**Containers**, **Finance**, **System Settings**, **Agent**, **Event Guide** (aside
from the `event_` collision noted under Shifts), **Survey**, **Gate**, **Scanner** (no owned
tables), **Onboarding** (no owned tables), **Mailer** (no owned tables).

**Store is not in that list (corrected 2026-08-03)** — it has no cross-section FK and no
non-conforming table name, but it does carry one dead column, so a Store demolition pass is
still owed. The summary table's `Store | 1 (self-contained)` row is the authoritative count:
`StoreOrder.Label` (`StoreOrder.cs:36`,
`[Obsolete("Order labels were removed from the UI (#816)...")]`, suppressed at
`StoreOrderConfiguration.cs:13`) — self-contained within Store, not cross-section, not
plan-tracked, but a legitimate G2 drop candidate whenever Store's demolition batch runs.

---

## Notes that don't belong to a single section

*(This was a per-section count table with a Total row. Every number in it was derived from the
per-section findings above, and it drifted from them repeatedly — the Shifts row alone was
corrected twice and the Users row once, each time as pure arithmetic reconciliation. The
per-section sections above are the inventory; count them there if a count is wanted.)*

**`EventParticipation` is not a Shifts cross-section FK.** `event_participations` is Users-owned
per this file's own ownership finding, so its `UserId → AspNetUsers` is internal to Users. It must
not be queued as a Shifts FK cut — an earlier revision did exactly that.

**#603 §4 drops five `AspNetUsers` columns**, not four: `NormalizedEmail` alongside
`Email`/`EmailConfirmed`/`UserName`/`NormalizedUserName`. `NormalizedEmail` carries `[Obsolete]`
but its drop is owned by #603, so it belongs in that migration's scope rather than this
inventory's own tagged-dead-column work.

**Two misfiled EF configurations** — `AccountMergeRequestConfiguration.cs` and
`EventParticipationConfiguration.cs` sit in the wrong `Configurations/<Section>/` folder relative
to who actually owns their table. Not table-ownership violations, just cheap G1 fixes ahead of
G5's project split.

## Unmapped / unclear

- **`user_emails` rename** — proposing `profile_user_emails` is defensible by ownership
  (`design-rules.md` §8: Profiles owns it) but reads badly next to `AspNetUsers`/`UserEmail`
  elsewhere in the codebase; an argument exists for leaving it bare or renaming the *section*
  boundary instead of the table. Punt to Peter.
- **`google_resources` (GoogleIntegration-owned — corrected 2026-08-03, was mislabeled
  Teams-owned; Team-named, AuditLog-consumed)** — still a naming question even after the
  ownership correction: the table/repository/entity are GoogleIntegration's, but the FK
  target is Teams and the physical name reads as a Teams concern. Renaming to
  `google_team_resources` is the mechanical fix (see Google Integration section above); no
  ownership transfer needed, ownership is already settled by `reforge.surface-score.json` +
  code layout — just the ambiguous name.
- **`event_guide_settings` vs `event_settings` shortening** — cosmetic follow-on to the
  `shift_event_settings` rename; not required, listed as optional in the Shifts section.
- **Unaudited sections (corrected 2026-08-03.** The original note claimed six sections but
  named two, and said `Settings` "doesn't exist yet" — it does: `SystemSetting`,
  `SystemSettingConfiguration`, `SystemSettingsRepository` and a dedicated
  `SystemSettingsDbContext` are all in the tree, and this inventory's own no-findings list
  above records **System Settings** as audited. What's pending for `Settings` is the rename
  and the #864 absorption, not the code.) Against the frozen tracker, the rows carrying no
  audit result are `Gate`, `Settings`, `Development`, `Gdpr` and `Search` — all admitted
  at the 2026-08-03 freeze, after this inventory's audit pass. `Shortlinks` (#810) is separate:
  it is `n/a`/`n/a` in the tracker because it genuinely does not exist yet.

  **Missing G1/G3 scorecards ≠ missing demolition coverage (corrected 2026-08-03).** Those five
  rows lack *audit scorecards*; that is not the same as lacking a demolition sweep. Two of the
  five were in fact swept by this inventory and appear in the no-findings list above:
  **`Gate`** (no owned tables) and **`Settings`** (under the *System Settings* heading). Calling
  them unchecked for G2 contradicts this document's own findings and would schedule duplicate
  audit work. So for G2 purposes the genuinely unchecked surfaces are `Development`,
  `Gdpr`, `Search` — plus Shortlinks-when-built. `Gate` and `Settings` still owe a G1/G3
  scorecard, but their demolition coverage is complete.
- This inventory did not attempt a full grep for every `[Obsolete]` property outside
  `Humans.Domain/Entities` (e.g. Application/Web-layer obsolete DTOs, view models) — scope was
  held to entities + EF configurations per the task brief. A follow-up pass could widen to
  DTOs/ViewModels if G2 wants a fuller picture, but those aren't DB-level demolition items.
