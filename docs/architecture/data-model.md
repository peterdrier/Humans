# Data Model — Index and Cross-Section Graph

This file is the **index and cross-cutting rule sheet** for the data model. Per-entity field tables live under `docs/sections/<OwningSection>.md` (each section owns the entities it owns). If you are looking for a specific entity's fields, indexes, or constraints, follow the "Owning section" link for that entity below.

> Rule: each entity has exactly one owning section. That section's doc is the authoritative source for field-level detail, serialization rules, indexes, and cross-domain FK strip status. This file only indexes the landscape and documents rules that cross section boundaries.

## Entity index

<!-- freshness:auto id="entity-index" prompt="Walk every class under src/Humans.Domain/Entities/ that has a corresponding configuration under src/Humans.Infrastructure/Data/Configurations/. For each, identify the owning section (find the section doc in docs/sections/ whose Data Model section names the entity). Build the entity index table with columns: Entity | Owning section | Notes. Preserve any per-row Notes column content the existing table already has — only update entity names and section links if they changed." -->

| Entity | Owning section | Notes |
|--------|---------------|-------|
| User | [Users/Identity](../sections/Users.md) | Profile-adjacent extension fields documented in [`Profiles.md`](../sections/Profiles.md#user-identity-extension). |
| EventParticipation | [Users/Identity](../sections/Users.md) | Per-user, per-event participation status. |
| Profile | [Profiles](../sections/Profiles.md) | |
| UserEmail | [Profiles](../sections/Profiles.md) | |
| ContactField | [Profiles](../sections/Profiles.md) | |
| CommunicationPreference | [Profiles](../sections/Profiles.md) | |
| ProfileLanguage | [Profiles](../sections/Profiles.md) | |
| VolunteerHistoryEntry | [Profiles](../sections/Profiles.md) | Sub-aggregate of Profile. |
| AccountMergeRequest | [Profiles](../sections/Profiles.md) | `AccountMergeService` + `DuplicateAccountService` live in `Humans.Application.Services.Profiles/`. |
| Application | [Governance](../../src/Sections/Humans.Governance/Docs/Governance.md) | |
| ApplicationStateHistory | [Governance](../../src/Sections/Humans.Governance/Docs/Governance.md) | Append-only (§12). |
| BoardVote | [Governance](../../src/Sections/Humans.Governance/Docs/Governance.md) | Transient — deleted on finalization. |
| RoleAssignment | [Auth](../sections/Auth.md) | |
| LegalDocument / DocumentVersion | [Consent](../sections/Consent.md) | |
| ConsentRecord | [Consent](../sections/Consent.md) | Append-only via DB triggers (§12). |
| Team | [Teams](../sections/Teams.md) | |
| TeamMember | [Teams](../sections/Teams.md) | |
| TeamJoinRequest | [Teams](../sections/Teams.md) | |
| TeamJoinRequestStateHistory | [Teams](../sections/Teams.md) | Append-only (§12). |
| TeamRoleDefinition | [Teams](../sections/Teams.md) | |
| TeamRoleAssignment | [Teams](../sections/Teams.md) | |
| TeamEarlyEntryGrant | [Teams](../sections/Teams.md) | Per-team Early Entry grant (gated by `Team.EarlyEntryEnabled`). Cross-section `UserId` FK — nav stripped, resolved via `IUserServiceRead`. |
| GoogleResource | [Teams](../sections/Teams.md) | Team Resources sub-aggregate. |
| Camp / CampSeason / CampImage / CampHistoricalName / CampSettings | [Camps](../sections/Camps.md) | |
| CampMember | [Camps](../sections/Camps.md) | Per-season, post-hoc human/camp affiliation (Pending/Active/Removed). Partial unique on `(CampSeasonId, UserId) WHERE Status <> 'Removed'`. |
| CampRoleDefinition / CampRoleAssignment | [Camps](../sections/Camps.md) | Per-camp role catalogue + per-season assignments. Owned by `CampRoleService`. Unique on `(CampSeasonId, CampRoleDefinitionId, CampMemberId)`. |
| Container / ContainerPlacement | [Containers](../../src/Sections/Humans.Containers/Docs/Containers.md) | Camp-owned (`CampId` → `camps.Id`, non-nullable). |
| CityPlanningSettings | [City Planning](../../src/Sections/Humans.CityPlanning/Docs/CityPlanning.md) | |
| CampPolygon | [City Planning](../../src/Sections/Humans.CityPlanning/Docs/CityPlanning.md) | |
| CampPolygonHistory | [City Planning](../../src/Sections/Humans.CityPlanning/Docs/CityPlanning.md) | Append-only (§12). |
| CalendarEvent / CalendarEventException | [Calendar](../../src/Sections/Humans.Calendar/Docs/Calendar.md) | |
| EmailOutboxMessage | [Email](../sections/Email.md) | |
| Campaign / CampaignCode / CampaignGrant | [Campaigns](../../src/Sections/Humans.Campaigns/Docs/Campaigns.md) | |
| TicketOrder / TicketAttendee / TicketSyncState / TicketTransferRequest | [Tickets](../sections/Tickets.md) | |
| GateScanEvent / GateSettings / GateStaffPin | [Gate](../../src/Sections/Humans.Gate/Docs/Gate.md) | `GateScanEvent` is the append-only gate admission log (retention-purged by `GateRetentionJob`; user ids re-pointed on merge). Cross-section refs (`ScannedByUserId`, `GuestUserId`, `OverrideByUserId`, `TicketAttendeeId`, `GateStaffPin.UserId`) are bare Guid columns — no navs, no cross-section EF FK constraints. |
| EventSettings / Rota / Shift / ShiftSignup / GeneralAvailability / VolunteerEventProfile / VolunteerBuildStatus / ShiftTag / VolunteerTagPreference | [Shifts](../sections/Shifts.md) | |
| Event / EventCategory / EventVenue / EventGuideSettings / EventModerationAction / EventFavourite / EventPreference | [Events](../../src/Sections/Humans.Events/Docs/Events.md) | Event Guide submissions, moderation, categories, shared venues, per-user favourites/preferences. `EventModerationAction` append-only (§12 — Restrict on delete). |
| FeedbackReport / FeedbackMessage | [Feedback](../../src/Sections/Humans.Feedback/Docs/Feedback.md) | |
| BudgetYear / BudgetGroup / BudgetCategory / BudgetLineItem / BudgetAuditLog / TicketingProjection | [Budget](../../src/Sections/Humans.Budget/Docs/Budget.md) | `BudgetAuditLog` append-only (§12). `BudgetGroup.Slug` and `BudgetCategory.Slug` are the Holded-tag-safe identifiers consumed by Finance. |
| ExpenseReport / ExpenseLine / ExpenseAttachment / HoldedExpenseOutboxEvent | [Expenses](../../src/Sections/Humans.Expenses/Docs/Expenses.md) | Expense reports and Holded sync outbox. |
| HoldedExpenseDoc / HoldedCategoryMap / HoldedSyncState / HoldedLedgerLine / HoldedCreditorContact | [Finance](../../src/Sections/Humans.Finance/Docs/Finance.md) | Holded actuals cache (Feature 1) + creditor daybook ledger cache + member→account binding (Feature 2). |
| Product / Order / OrderLine / Payment / Invoice / TreasurySyncState | [Store](../../src/Sections/Humans.Store/Docs/Store.md) | |
| Issue / IssueComment | [Issues](../../src/Sections/Humans.Issues/Docs/Issues.md) | |
| AgentConversation / AgentMessage / AgentSettings | [Agent](../../src/Sections/Humans.Agent/Docs/Agent.md) | |
| SyncServiceSettings / GoogleSyncOutboxEvent | [Google Integration](../sections/GoogleIntegration.md) | |
| Survey / SurveyQuestion / SurveyQuestionOption / SurveyResponse / SurveyAnswer / SurveyInvitation | [Survey](../../src/Sections/Humans.Surveys/Docs/Surveys.md) | Cross-domain refs are bare `Guid` FK columns only — no nav properties, no cross-section EF FK constraints. |
| SystemSetting | System Settings section | Owned by `SystemSettingsRepository` (exposed via `ISystemSettingsService`); consuming sections read/write keys through it. See [SystemSetting below](#systemsetting-system-settings-section). |
| AuditLogEntry | [Audit Log](../sections/AuditLog.md) | Append-only (§12). |
| Notification / NotificationRecipient | [Notifications](../../src/Sections/Humans.Notifications/Docs/Notifications.md) | |

<!-- /freshness:auto -->

Every major section in the app now has a dedicated section doc.

- **Admin Shell** — frame only, no entities. See [`docs/sections/admin-shell.md`](../sections/admin-shell.md).

## DbContext ownership

Since the per-section split (nobodies-collective/Humans#858) the model is partitioned across several EF contexts, all against the same database and connection. Each context below owns its tables outright: its own `__EFMigrationsHistory_<Section>` table, its own migrations folder under `src/Humans.Infrastructure/Migrations/<Section>/`, and its own model snapshot. An entity mapped by two contexts — or by none — is a build failure (`DbContextEntityOwnershipTests`).

| Context | Tables |
|---------|--------|
| `SystemSettingsDbContext` | `system_settings` |
| `ContainersDbContext` | `containers`, `container_placements` |
| `AgentDbContext` | `agent_conversations`, `agent_messages`, `agent_settings` |
| `ExpensesDbContext` | `expense_reports`, `expense_lines`, `expense_attachments`, `holded_expense_outbox_events` |
| `FinanceDbContext` | `holded_expense_docs`, `holded_category_map`, `holded_creditor_contacts`, `holded_doc_sync_state` |
| `HoldedDbContext` | `holded_ledger_lines`, `holded_accounts`, `holded_api_calls`, `holded_sync_states` |
| `SurveysDbContext` | `surveys`, `survey_questions`, `survey_question_options`, `survey_invitations`, `survey_responses`, `survey_answers` |
| `EventGuideDbContext` | `events`, `event_categories`, `event_venues`, `event_guide_settings`, `event_moderation_actions`, `event_favourites`, `event_preferences` |
| `StoreDbContext` | `store_products`, `store_orders`, `store_order_lines`, `store_payments`, `store_invoices`, `store_treasury_sync_state` |
| `AuthDbContext` | `role_assignments` |
| `EmailDbContext` | `email_outbox_messages` |
| `CalendarDbContext` | `calendar_events`, `calendar_event_exceptions` |
| `NotificationsDbContext` | `notifications`, `notification_recipients` |
| `IssuesDbContext` | `issues`, `issue_comments` |
| `GovernanceDbContext` | `applications`, `application_state_history`, `board_votes` |
| `CampaignsDbContext` | `campaigns`, `campaign_codes`, `campaign_grants` |
| `GoogleIntegrationDbContext` | `google_resources`, `google_sync_outbox`, `sync_service_settings` |
| `TicketsDbContext` | `ticket_orders`, `ticket_attendees`, `ticket_sync_state`, `ticket_transfer_requests` |
| `FeedbackDbContext` | `feedback_reports`, `feedback_messages` |
| `CityPlanningDbContext` | `city_planning_settings`, `camp_polygons`, `camp_polygon_histories` |
| `BudgetDbContext` | `budget_years`, `budget_groups`, `budget_categories`, `budget_line_items`, `budget_audit_logs`, `ticketing_projections` |
| `CampsDbContext` | `camps`, `camp_seasons`, `camp_historical_names`, `camp_images`, `camp_settings`, `camp_members`, `camp_role_definitions`, `camp_role_assignments` |
| `GateDbContext` | `gate_scan_events`, `gate_settings`, `gate_staff_pins` |
| `SystemDbContext` | `DataProtectionKeys` — the platform context for framework-owned tables no section can own; adding a table to it is Peter's call |
| `LegalDbContext` | `legal_documents`, `document_versions`, `consent_records` — the `consent_records` immutability trigger lives as raw SQL in the baseline (Peter-authorized `migrationBuilder.Sql`, 2026-08-10 on #858), not in the EF model |
| `AuditLogDbContext` | `audit_log` — its immutability trigger likewise lives as raw SQL in the baseline |
| `ShiftsDbContext` | `event_settings`, `rotas`, `shifts`, `shift_signups`, `shift_tags`, `rota_shift_tags`, `volunteer_event_profiles`, `general_availability`, `volunteer_build_statuses`, `volunteer_tag_preferences` |
| `TeamsDbContext` | `teams`, `team_members`, `team_join_requests`, `team_join_request_state_history`, `team_role_definitions`, `team_role_assignments`, `team_early_entry_grants` |
| `HumansDbContext` | everything else, including the Identity tables (which come from the framework base class) |

What is left in `HumansDbContext`: Users/Identity and Profiles. Profiles is blocked by its three surviving `→ User` model relationships (`Profile`, `UserEmail`, `CommunicationPreference`); Users peels last by design. See the design doc's §10.1.

## Cross-section FK graph

High-level FK topology. Each arrow crosses a section boundary — the FK is scalar only, the navigation property is stripped or `[Obsolete]`-marked per design-rules §6c.

```
Users/Identity
  ← Profile, UserEmail, ContactField, CommunicationPreference (Profiles)
  ← RoleAssignment (Auth)
  ← Application, BoardVote, ApplicationStateHistory (Governance)
  ← ConsentRecord (Consent)
  ← TeamMember, TeamJoinRequest, TeamRoleAssignment (Teams)
  ← Camp.CreatedByUser, CampSeason.ReviewedByUser, CampRoleAssignment.AssignedByUser (Camps)
  ← CampPolygon.LastModifiedByUser, CampPolygonHistory.ModifiedByUser (City Planning)
  ← CalendarEvent.CreatedByUser, CalendarEventException.CreatedByUser (Calendar)
  ← EmailOutboxMessage.User (Email)
  ← Campaign.CreatedByUser, CampaignGrant (Campaigns)
  ← TicketOrder.MatchedUser, TicketAttendee.MatchedUser (Tickets)
  ← ShiftSignup.User / EnrolledByUser / ReviewedByUser, GeneralAvailability, VolunteerEventProfile (Shifts)
  ← FeedbackReport.User / ResolvedByUser / AssignedToUser, FeedbackMessage.SenderUser (Feedback)
  ← BudgetAuditLog.ActorUser, BudgetCategory.Team.* (Budget)
  ← SyncServiceSettings.UpdatedByUserId, GoogleSyncOutboxEvent (Google Integration)
  ← AccountMergeRequest.TargetUser / SourceUser / ResolvedByUser (Admin)
  ← Survey.CreatedByUserId, SurveyInvitation.UserId, SurveyResponse.UserId (Survey — bare Guid FKs, no navs, no cross-section EF FK constraints)
  ← GateScanEvent.ScannedByUserId / GuestUserId / OverrideByUserId, GateStaffPin.UserId (Gate — bare Guid FKs, no navs, no cross-section EF FK constraints)

Team (Teams)
  ← Rota.Team (Shifts)
  ← BudgetCategory.Team, BudgetLineItem.ResponsibleTeam (Budget)
  ← CalendarEvent.OwningTeam (Calendar)
  ← LegalDocument.Team (Consent)
  ← FeedbackReport.AssignedToTeam (Feedback)
  ← Survey.AudienceTeamId (Survey — bare Guid FK, no nav, no cross-section EF FK constraint)

BudgetCategory (Budget)
  ← HoldedCategoryMap.BudgetCategoryId (Finance — FK only, no nav)
  ← HoldedExpenseDoc.BudgetCategoryId (Finance — FK only, no nav, null = unmatched)

TicketAttendee (Tickets)
  ← GateScanEvent.TicketAttendeeId (Gate — bare Guid FK, no nav, no cross-section EF FK constraint)

CampSeason (Camps)
  ← CampPolygon, CampPolygonHistory (City Planning)

Camp (Camps)
  ← Container.CampId (Containers — bare Guid FK, non-nullable)

DocumentVersion (Consent)
  ← ConsentRecord (Consent, sibling aggregate — join by DocumentVersionId)

CampSeason (Camps)
  ← CampMember (Camps, aggregate-local — partial unique on (CampSeasonId, UserId) WHERE Status <> 'Removed')
  ← CampRoleAssignment (Camps, aggregate-local — unique on (CampSeasonId, CampRoleDefinitionId, CampMemberId))

CampRoleDefinition (Camps)
  ← CampRoleAssignment (Camps, aggregate-local — OnDelete Restrict)

CampMember (Camps)
  ← CampRoleAssignment (Camps, aggregate-local — OnDelete Cascade; soft-delete cleared in service)

Campaign (Campaigns)
  ← CampaignCode, CampaignGrant (Campaigns, aggregate-local)
CampaignGrant (Campaigns)
  ← EmailOutboxMessage (Email, cross-section — nav stripped)
```

**Aggregate-local FKs** (FKs whose source and target live in the same section) are documented inside the section's own doc and kept as nav properties — they are not part of the cross-section graph.

## SystemSetting (System Settings section)

`system_settings` is a cross-cutting key/value table owned by the **System Settings section** (`SystemSettingsRepository`), exposed across sections via `ISystemSettingsService`. Consuming sections that need runtime-flag state add a key here and read/write it through `ISystemSettingsService` rather than touching the table directly.

| Key | Consuming section | Purpose |
|-----|-------------------|---------|
| `IsEmailSendingPaused` | [Email](../sections/Email.md) | When `"true"`, `ProcessEmailOutboxJob` skips processing |
| `DriveActivityMonitor:LastRunAt` | [Google Integration](../sections/GoogleIntegration.md) | Last-run timestamp for drive-activity monitor |

| Property | Type | Purpose |
|----------|------|---------|
| Key | string | PK |
| Value | string | Setting value |

## Cross-cutting serialization rules

- All entities use `System.Text.Json` serialization.
- All dates and times use NodaTime (`Instant`, `LocalDate`, `LocalDateTime`, `OffsetDateTime`) — never `DateTime` or `DateTimeOffset`. See [`memory/code/nodatime-for-dates.md`](../../memory/code/nodatime-for-dates.md).
- Enums are stored as strings via `HasConversion<string>()` unless otherwise noted on the owning section's doc.
- Entity serialization rules — never rename serialized fields ([`memory/code/no-rename-serialized-fields.md`](../../memory/code/no-rename-serialized-fields.md)); never remove "unused" properties because they may be reflection-bound ([`memory/code/no-remove-unused-properties.md`](../../memory/code/no-remove-unused-properties.md)); private setters need `[JsonInclude]` and polymorphic types need `[JsonPolymorphic]` + `[JsonDerivedType]` ([`memory/code/json-serialization.md`](../../memory/code/json-serialization.md)).

## Account merge fold + chain-follow reads

Account merges are folded into the target via `IAccountMergeService.AcceptAsync` (Profiles section). The orchestrator re-FKs every owning section's user-scoped rows from source to target via per-section `Reassign…ToUserAsync` methods, then tombstones the source `User` row by setting `User.MergedToUserId` + `User.MergedAt` (`IUserService.AnonymizeForMergeAsync`). The source row is NOT deleted — it stays as a redirect. The self-referential `User.MergedToUserId` FK is `OnDelete(Restrict)` so deleting a target cannot cascade-delete its source tombstones.

Append-only sections (§12) cannot rewrite their `UserId` / `ActorUserId` columns to point at the target — the rows stay at source by design (DB triggers, repository shape, or both). Per-user reads on append-only entities therefore **chain-follow** merge tombstones: callers union the result of `IUserService.GetMergedSourceIdsAsync(targetUserId)` with the target id before querying. Sections that implement chain-follow:

| Section | Owning entity | Read paths that chain-follow |
|---------|---------------|------------------------------|
| [Audit Log](../sections/AuditLog.md) | `AuditLogEntry` | `GetByUserAsync`, `GetUserAuditLogPageAsync`, per-entity history when entity is User, `ContributeForUserAsync` |
| [Consent](../sections/Consent.md) | `ConsentRecord` | `GetUserConsentsAsync`, `HasAllRequiredConsentsAsync`, consent dashboard, `ContributeForUserAsync` |
| [Budget](../../src/Sections/Humans.Budget/Docs/Budget.md) | `BudgetAuditLog` | `ContributeForUserAsync` (GDPR) |

When adding a new append-only entity that carries a `UserId` / `ActorUserId` column, decide at design time whether per-user reads need chain-follow and add the union explicitly — `IUserService.GetMergedSourceIdsAsync` is the only sanctioned primitive.

## Append-only entities (§12)

The following entities are append-only — no `UpdateAsync` / `DeleteAsync` on their repositories. Enforced either by DB triggers or by architecture tests. Full list, with owning section:

| Entity | Owning section | Enforcement |
|--------|---------------|-------------|
| ConsentRecord | [Consent](../sections/Consent.md) | DB triggers block UPDATE / DELETE |
| AuditLogEntry | [Audit Log](../sections/AuditLog.md) | Architecture test: `AuditLogArchitectureTests.IAuditLogRepository_HasNoUpdateOrDeleteMethods` |
| BudgetAuditLog | [Budget](../../src/Sections/Humans.Budget/Docs/Budget.md) | Repository shape — no update/delete methods |
| CampPolygonHistory | [City Planning](../../src/Sections/Humans.CityPlanning/Docs/CityPlanning.md) | Architecture test: `CityPlanningArchitectureTests` pins append-only repo surface |
| ApplicationStateHistory | [Governance](../../src/Sections/Humans.Governance/Docs/Governance.md) | Repository shape — no update/delete methods |
| TeamJoinRequestStateHistory | [Teams](../sections/Teams.md) | Repository shape (target; pending sub-task nobodies-collective/Humans#540a) |

## Constants

### SystemTeamIds

See [`../sections/Teams.md`](../sections/Teams.md#systemteamids-constants) for the authoritative list.

### RoleNames

See [`../sections/Auth.md`](../sections/Auth.md#rolenames-constants) for the authoritative list.

## Where to add a new entity

1. Decide which section owns it per design-rules §8. If a new section is warranted, copy `docs/sections/SECTION-TEMPLATE.md` into a new file.
2. Add the field table under the owning section's `## Data Model` heading.
3. Add a row to the [Entity index](#entity-index) above.
4. If the entity participates in a cross-section FK, update the [Cross-section FK graph](#cross-section-fk-graph) above.
5. If the entity is append-only, add a row to [Append-only entities](#append-only-entities-12) above.
6. If the entity owns user-scoped data, make the owning service implement `IUserDataContributor` per design-rules §8a and wire the GDPR export.
7. If the owning section has its own DbContext ([DbContext ownership](#dbcontext-ownership) above), add the `DbSet<>` and an explicit `ApplyConfiguration` call to that context — `HumansDbContext`'s assembly scan deliberately skips peeled namespaces, so a configuration left unregistered is mapped by nothing at all.

Do **not** add field tables to this file. This file is an index; the section doc is the source of truth.
