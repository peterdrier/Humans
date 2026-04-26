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
| AccountMergeRequest | [Profiles](../sections/Profiles.md) | `AccountMergeService` + `DuplicateAccountService` live in `Humans.Application.Services.Profile/`. |
| Application | [Governance](../sections/Governance.md) | |
| ApplicationStateHistory | [Governance](../sections/Governance.md) | Append-only (§12). |
| BoardVote | [Governance](../sections/Governance.md) | Transient — deleted on finalization. |
| RoleAssignment | [Auth](../sections/Auth.md) | |
| LegalDocument / DocumentVersion | [Legal & Consent](../sections/LegalAndConsent.md) | |
| ConsentRecord | [Legal & Consent](../sections/LegalAndConsent.md) | Append-only via DB triggers (§12). |
| Team | [Teams](../sections/Teams.md) | |
| TeamMember | [Teams](../sections/Teams.md) | |
| TeamJoinRequest | [Teams](../sections/Teams.md) | |
| TeamJoinRequestStateHistory | [Teams](../sections/Teams.md) | Append-only (§12). |
| TeamRoleDefinition | [Teams](../sections/Teams.md) | |
| TeamRoleAssignment | [Teams](../sections/Teams.md) | |
| GoogleResource | [Teams](../sections/Teams.md) | Team Resources sub-aggregate. |
| Camp / CampSeason / CampLead / CampImage / CampHistoricalName / CampSettings | [Camps](../sections/Camps.md) | |
| CampMember | [Camps](../sections/Camps.md) | Per-season, post-hoc human/camp affiliation (Pending/Active/Removed). Partial unique on `(CampSeasonId, UserId) WHERE Status <> 'Removed'`. |
| CampRoleDefinition / CampRoleAssignment | [Camps](../sections/Camps.md) | Per-camp role catalogue + per-season assignments. Owned by `CampRoleService`. Unique on `(CampSeasonId, CampRoleDefinitionId, CampMemberId)`. |
| CityPlanningSettings | [City Planning](../sections/CityPlanning.md) | |
| CampPolygon | [City Planning](../sections/CityPlanning.md) | |
| CampPolygonHistory | [City Planning](../sections/CityPlanning.md) | Append-only (§12). |
| CalendarEvent / CalendarEventException | [Calendar](../sections/Calendar.md) | |
| EmailOutboxMessage | [Email](../sections/Email.md) | |
| Campaign / CampaignCode / CampaignGrant | [Campaigns](../sections/Campaigns.md) | |
| TicketOrder / TicketAttendee / TicketSyncState | [Tickets](../sections/Tickets.md) | |
| EventSettings / Rota / Shift / ShiftSignup / GeneralAvailability / VolunteerEventProfile / ShiftTag / VolunteerTagPreference | [Shifts](../sections/Shifts.md) | |
| FeedbackReport / FeedbackMessage | [Feedback](../sections/Feedback.md) | |
| BudgetYear / BudgetGroup / BudgetCategory / BudgetLineItem / BudgetAuditLog / TicketingProjection | [Budget](../sections/Budget.md) | `BudgetAuditLog` append-only (§12). `BudgetGroup.Slug` and `BudgetCategory.Slug` are the Holded-tag-safe identifiers consumed by Finance. |
| HoldedTransaction / HoldedSyncState | [Finance](../sections/Finance.md) | Holded purchase invoices and sync singleton. `HoldedTransaction.BudgetCategoryId` → `BudgetCategory` (Budget) — FK only, no nav. |
| SyncServiceSettings / GoogleSyncOutboxEvent | [Google Integration](../sections/GoogleIntegration.md) | |
| SystemSetting | per-key ownership | Each key belongs to its consuming section's repository. See [SystemSetting below](#systemsetting-per-key-ownership). |
| AuditLogEntry | [Audit Log](../sections/AuditLog.md) | Append-only (§12). |
| Notification / NotificationRecipient | [Notifications](../sections/Notifications.md) | |

<!-- /freshness:auto -->

Every major section in the app now has a dedicated section doc. `/Admin/*` is a controller/nav holder, not a section — its services belong to the sections they act on (Email, Profiles, Google Integration, Auth, Legal & Consent).

## Cross-section FK graph

High-level FK topology. Each arrow crosses a section boundary — the FK is scalar only, the navigation property is stripped or `[Obsolete]`-marked per design-rules §6c.

```
Users/Identity
  ← Profile, UserEmail, ContactField, CommunicationPreference (Profiles)
  ← RoleAssignment (Auth)
  ← Application, BoardVote, ApplicationStateHistory (Governance)
  ← ConsentRecord (Legal & Consent)
  ← TeamMember, TeamJoinRequest, TeamRoleAssignment (Teams)
  ← Camp.CreatedByUser, CampLead, CampSeason.ReviewedByUser, CampRoleAssignment.AssignedByUser (Camps)
  ← CampPolygon.LastModifiedByUser, CampPolygonHistory.ModifiedByUser (City Planning)
  ← CalendarEvent.CreatedByUser, CalendarEventException.CreatedByUser (Calendar)
  ← EmailOutboxMessage.User (Email)
  ← Campaign.CreatedByUser, CampaignGrant (Campaigns)
  ← TicketOrder.MatchedUser, TicketAttendee.MatchedUser (Tickets)
  ← ShiftSignup.User / EnrolledByUser / ReviewedByUser, GeneralAvailability, VolunteerEventProfile (Shifts)
  ← FeedbackReport.User / ResolvedByUser / AssignedToUser, FeedbackMessage.SenderUser (Feedback)
  ← BudgetAuditLog.ActorUser, BudgetCategory.Team.* (Budget)
  ← SyncServiceSettings.UpdatedByUser, GoogleSyncOutboxEvent (Google Integration)
  ← AccountMergeRequest.TargetUser / SourceUser / ResolvedByUser (Admin)

Team (Teams)
  ← Rota.Team (Shifts)
  ← BudgetCategory.Team, BudgetLineItem.ResponsibleTeam (Budget)
  ← CalendarEvent.OwningTeam (Calendar)
  ← LegalDocument.Team (Legal & Consent)
  ← FeedbackReport.AssignedToTeam (Feedback)

BudgetCategory (Budget)
  ← HoldedTransaction.BudgetCategory (Finance — FK only, no nav)

CampSeason (Camps)
  ← CampPolygon, CampPolygonHistory (City Planning)

DocumentVersion (Legal & Consent)
  ← ConsentRecord (Legal & Consent, sibling aggregate — join by DocumentVersionId)

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

## SystemSetting (per-key ownership)

`system_settings` is a cross-cutting key/value table, but **each key is owned by the consuming section's repository**. There is no single cross-cutting owner. Sections that need runtime-flag state add the key here and expose reads/writes through their own service surface; no other section touches the key.

| Key | Owning section | Purpose |
|-----|----------------|---------|
| `email_outbox_paused` | [Email](../sections/Email.md) | When `"true"`, `ProcessEmailOutboxJob` skips processing |
| `DriveActivityMonitor:LastRunAt` | [Google Integration](../sections/GoogleIntegration.md) | Last-run timestamp for drive-activity monitor |

| Property | Type | Purpose |
|----------|------|---------|
| Key | string | PK |
| Value | string | Setting value |

## Cross-cutting serialization rules

- All entities use `System.Text.Json` serialization.
- All dates and times use NodaTime (`Instant`, `LocalDate`, `LocalDateTime`, `OffsetDateTime`) — never `DateTime` or `DateTimeOffset`. See [`coding-rules.md`](coding-rules.md).
- Enums are stored as strings via `HasConversion<string>()` unless otherwise noted on the owning section's doc.
- Entity serialization rules: see [`coding-rules.md`](coding-rules.md) — in particular: never rename serialized fields; never remove "unused" properties (reflection); always include the full set of required fields at serialization time.

## Append-only entities (§12)

The following entities are append-only — no `UpdateAsync` / `DeleteAsync` on their repositories. Enforced either by DB triggers or by architecture tests. Full list, with owning section:

| Entity | Owning section | Enforcement |
|--------|---------------|-------------|
| ConsentRecord | [Legal & Consent](../sections/LegalAndConsent.md) | DB triggers block UPDATE / DELETE |
| AuditLogEntry | [Audit Log](../sections/AuditLog.md) | Architecture test: `AuditLogArchitectureTests.IAuditLogRepository_HasNoUpdateOrDeleteMethods` |
| BudgetAuditLog | [Budget](../sections/Budget.md) | Repository shape — no update/delete methods |
| CampPolygonHistory | [City Planning](../sections/CityPlanning.md) | Architecture test: `CityPlanningArchitectureTests` pins append-only repo surface |
| ApplicationStateHistory | [Governance](../sections/Governance.md) | Repository shape — no update/delete methods |
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

Do **not** add field tables to this file. This file is an index; the section doc is the source of truth.
