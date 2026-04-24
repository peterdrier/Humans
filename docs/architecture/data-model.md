# Data Model — Index and Cross-Section Graph

This file is the **index and cross-cutting rule sheet** for the data model. Per-entity field tables live under `docs/sections/<OwningSection>.md` (each section owns the entities it owns). If you are looking for a specific entity's fields, indexes, or constraints, follow the "Owning section" link for that entity below.

> Rule: each entity has exactly one owning section. That section's doc is the authoritative source for field-level detail, serialization rules, indexes, and cross-domain FK strip status. This file only indexes the landscape and documents rules that cross section boundaries.

## Entity index

| Entity | Owning section | Notes |
|--------|---------------|-------|
| User | Profiles (profile-adjacent fields) | Entity itself owned by Users/Identity; no dedicated section doc yet — extension fields live in [`../sections/Profiles.md`](../sections/Profiles.md). |
| Profile | [Profiles](../sections/Profiles.md) | |
| UserEmail | [Profiles](../sections/Profiles.md) | |
| ContactField | [Profiles](../sections/Profiles.md) | |
| CommunicationPreference | [Profiles](../sections/Profiles.md) | |
| VolunteerHistoryEntry | [Profiles](../sections/Profiles.md) | Sub-aggregate of Profile. |
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
| TeamPage | [Teams](../sections/Teams.md) | |
| GoogleResource | [Teams](../sections/Teams.md) | Team Resources sub-aggregate. |
| Camp / CampSeason / CampLead / CampImage / CampHistoricalName / CampSettings | [Camps](../sections/Camps.md) | |
| CityPlanningSettings | [City Planning](../sections/CityPlanning.md) | |
| CampPolygon | [City Planning](../sections/CityPlanning.md) | |
| CampPolygonHistory | [City Planning](../sections/CityPlanning.md) | Append-only (§12). |
| CalendarEvent / CalendarEventException | [Calendar](../sections/Calendar.md) | |
| EmailOutboxMessage | Email (no dedicated section doc) | See [EmailOutboxMessage below](#emailoutboxmessage). |
| Campaign / CampaignCode / CampaignGrant | [Campaigns](../sections/Campaigns.md) | |
| TicketOrder / TicketAttendee / TicketSyncState | [Tickets](../sections/Tickets.md) | |
| EventSettings / Rota / Shift / ShiftSignup / GeneralAvailability / VolunteerEventProfile | [Shifts](../sections/Shifts.md) | |
| FeedbackReport / FeedbackMessage | [Feedback](../sections/Feedback.md) | |
| BudgetYear / BudgetGroup / BudgetCategory / BudgetLineItem / BudgetAuditLog / TicketingProjection | [Budget](../sections/Budget.md) | `BudgetAuditLog` append-only (§12). |
| SyncServiceSettings / GoogleSyncOutboxEvent | [Google Integration](../sections/GoogleIntegration.md) | |
| SystemSetting | [Admin](../sections/Admin.md) | Per-key ownership — each key is owned by its consuming section's repository. |
| AccountMergeRequest | [Admin](../sections/Admin.md) | |
| AuditLogEntry | Audit Log (no dedicated section doc) | Append-only (§12). See [AuditLogEntry below](#auditlogentry). |
| Notification / NotificationRecipient | Notifications (no dedicated section doc) | |

Sections with no dedicated doc yet (Users/Identity, Email, Audit Log, Notifications) are candidates for new section files — see `docs/sections/SECTION-TEMPLATE.md`.

## Cross-section FK graph

High-level FK topology. Each arrow crosses a section boundary — the FK is scalar only, the navigation property is stripped or `[Obsolete]`-marked per design-rules §6c.

```
Users/Identity
  ← Profile, UserEmail, ContactField, CommunicationPreference (Profiles)
  ← RoleAssignment (Auth)
  ← Application, BoardVote, ApplicationStateHistory (Governance)
  ← ConsentRecord (Legal & Consent)
  ← TeamMember, TeamJoinRequest, TeamRoleAssignment (Teams)
  ← Camp.CreatedByUser, CampLead, CampSeason.ReviewedByUser (Camps)
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

CampSeason (Camps)
  ← CampPolygon, CampPolygonHistory (City Planning)

DocumentVersion (Legal & Consent)
  ← ConsentRecord (Legal & Consent, sibling aggregate — join by DocumentVersionId)

Campaign (Campaigns)
  ← CampaignCode, CampaignGrant (Campaigns, aggregate-local)
CampaignGrant (Campaigns)
  ← EmailOutboxMessage (Email, cross-section — nav stripped)
```

**Aggregate-local FKs** (FKs whose source and target live in the same section) are documented inside the section's own doc and kept as nav properties — they are not part of the cross-section graph.

## Cross-cutting entities

### EmailOutboxMessage

Queued / sent / failed transactional email records. Processed by `ProcessEmailOutboxJob`; cleaned up by `CleanupEmailOutboxJob`. No dedicated section doc yet.

**Table:** `email_outbox_messages`

| Property | Type | Purpose |
|----------|------|---------|
| Id | Guid | PK |
| RecipientEmail | string | Delivery address |
| RecipientName | string? | Display name |
| Subject | string | Email subject line |
| HtmlBody | string | Rendered HTML body |
| PlainTextBody | string? | Optional plain-text alternative |
| TemplateName | string | Template identifier used to render this message |
| UserId | Guid? | FK → User (optional) — FK only |
| CampaignGrantId | Guid? | FK → CampaignGrant (optional) — FK only |
| ReplyTo | string? | Reply-To header value |
| ExtraHeaders | string? | JSON-encoded additional headers (e.g., `List-Unsubscribe`) |
| Status | EmailOutboxStatus | Queued / Sent / Failed |
| CreatedAt | Instant | When queued |
| PickedUpAt | Instant? | When first picked up by the job |
| SentAt | Instant? | When successfully delivered |
| RetryCount | int | Number of delivery attempts |
| LastError | string? | Last delivery error message |
| NextRetryAt | Instant? | Earliest time for next retry attempt |

#### EmailOutboxStatus

| Value | Description |
|-------|-------------|
| Queued | Awaiting delivery |
| Sent | Successfully delivered |
| Failed | Exhausted all retries |

Stored as int.

### AuditLogEntry

Append-only system audit trail (user actions, sync ops). `IAuditLogService` is cross-cutting and called from most sections. Append-only per design-rules §12 — enforced by `AuditLogArchitectureTests.IAuditLogRepository_HasNoUpdateOrDeleteMethods`.

**Table:** `audit_log_entries`

Relationships: `AuditLogEntry.ActorUserId` → User (optional), `AuditLogEntry.RelatedGoogleResourceId` → GoogleResource (optional).

#### AuditAction (cross-section enum)

Action strings are shared across all sections that write audit entries. Representative entries (non-exhaustive):

- `ConsentCheckCleared` — Consent Coordinator cleared a consent check
- `ConsentCheckFlagged` — Consent Coordinator flagged a consent check
- `SignupRejected` — Admin rejected a signup
- `TierApplicationApproved` — Board approved a tier application
- `TierApplicationRejected` — Board rejected a tier application
- `TierDowngraded` — Admin downgraded a member's tier
- `MembershipsRevokedOnDeletionRequest` — GDPR deletion revoked memberships
- `FeedbackResponseSent` — Admin sent an email response to a feedback report
- `CalendarEventCreated`, `CalendarEventUpdated`, `CalendarEventDeleted`, `CalendarOccurrenceCancelled`, `CalendarOccurrenceOverridden` — Calendar mutations

## Cross-cutting serialization rules

- All entities use `System.Text.Json` serialization.
- All dates and times use NodaTime (`Instant`, `LocalDate`, `LocalDateTime`, `OffsetDateTime`) — never `DateTime` or `DateTimeOffset`. See [`coding-rules.md`](coding-rules.md).
- Enums are stored as strings via `HasConversion<string>()` unless otherwise noted on the owning section's doc.
- Entity serialization rules: see [`coding-rules.md`](coding-rules.md) — in particular: never rename serialized fields; never remove "unused" properties (reflection); always include the full set of required fields at serialization time.

## Append-only entities (§12)

The following entities are append-only — no `UpdateAsync` / `DeleteAsync` on their repositories. Enforced either by DB triggers or by architecture tests. Full list, with owning section:

| Entity | Owning section | Enforcement |
|--------|---------------|-------------|
| ConsentRecord | Legal & Consent | DB triggers block UPDATE / DELETE |
| AuditLogEntry | Audit Log (cross-cutting) | Architecture test: `AuditLogArchitectureTests.IAuditLogRepository_HasNoUpdateOrDeleteMethods` |
| BudgetAuditLog | Budget | Repository shape — no update/delete methods |
| CampPolygonHistory | City Planning | Architecture test: `CityPlanningArchitectureTests` pins append-only repo surface |
| ApplicationStateHistory | Governance | Repository shape — no update/delete methods |
| TeamJoinRequestStateHistory | Teams | Repository shape (target; pending #540a) |

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
