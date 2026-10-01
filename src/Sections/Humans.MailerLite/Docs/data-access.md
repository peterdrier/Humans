# MailerLite — Data Access

## MailerLite

Folder: `src/Sections/Humans.MailerLite/Services/`. One owned table,
`mailerlite_sync_states` (`MailerLiteDbContext`, `Data/Repository.cs`
behind `IMailerLiteRepository`) — everything else about a subscriber
lives in MailerLite, and classifier writes go through other sections'
services.

### MailerLiteImportService (Scoped)

`IMailerLiteRepository` for the `import-reconciliation` sync-state row
(upsert after apply, read for the dashboard). Cross-section calls via
`IMailerLiteService` (external), `IUserEmailService`, `IUserServiceRead`,
`IAccountProvisioningService`, `ICommunicationPreferenceService`,
`IAuditLogService`. Inbound import slice — reads MailerLite subscribers
and provisions matching accounts. No cache.

### MailerLiteService (Singleton, class `MailerLiteClient`)

No repository. `MailerLiteClient` is the MailerLite HTTP port, built over
`IHttpClientFactory`: reads (`GetAccountSummaryAsync`, `ListGroupsAsync`,
`ListSubscribersAsync`) plus `RefreshAsync` (re-fetches the snapshot); writes
(`CreateGroupAsync`, `AssignSubscriberToGroupAsync`,
`UnassignSubscriberFromGroupAsync`, `BulkImportSubscribersToGroupAsync`,
`DeleteSubscriberAsync` for GDPR erasure). Retry timing uses `IClock`. No DB access, no `IMemoryCache`
(the client holds its own in-process subscriber/group state as a Singleton).

### MailerLiteGdprContributor (Scoped)

No repository, no cache. Implements `IUserDataContributor` — the section owns
no user-scoped tables, so `ContributeForUserAsync` (Article 15) always
returns empty; `EraseForUserAsync` (Article 17) deletes the MailerLite
subscriber under every verified address plus the primary via `IMailerLiteService`. Depends on
`IMailerLiteService`, `IUserEmailService`.

### MailerLiteAudienceSyncService (Scoped)

`IMailerLiteRepository` for the per-audience sync-state rows (upsert after
each push, one read for the dashboard's last-sync column). Cross-section
calls via `IMailerLiteService`, `IUserEmailService`, `IAuditLogService`,
plus `IEnumerable<IMailerLiteAudience>` (audience definitions). Outbound
slice — pushes computed audiences back to MailerLite groups. No cache.

### Audience definitions (`IMailerLiteAudience`)

Audience-membership computation classes under `Services/Audiences/`:
`HasShiftAudience`, `HasShiftSetupAudience`, `HasShiftEventAudience`,
`HasShiftStrikeAudience`, `HasTicketAudience`, `MarketingAudience`,
`MarketingNoTicketAudience`, `TicketNoShiftsAudience`, `MailerLiteAudienceBase`,
`ShiftViewAudienceBase`, `CurrentEventTicketHolders`.
No repository; compute over read-split / section service interfaces
(`ITicketServiceRead`, `IShiftView`, `IUserServiceRead`). No direct DB
access, no cache.

---


