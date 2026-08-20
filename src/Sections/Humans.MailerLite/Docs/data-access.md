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

### MailerLiteService (Scoped)

No repository. `MailerLiteClient` is the MailerLite HTTP port, built over
`IHttpClientFactory`: account and group reads (`GetAccountSummaryAsync`,
`ListGroupsAsync`, `CreateGroupAsync`), subscriber reads
(`ListSubscribersAsync`, `GetSubscriberAsync`), and the group-membership
writes the audience sync drives (`AssignSubscriberToGroupAsync`,
`UnassignSubscriberFromGroupAsync`, `BulkImportSubscribersToGroupAsync`,
`RefreshAsync`). Retry timing uses `IClock`. No DB access, no cache.

### MailerLiteAudienceSyncService (Scoped)

`IMailerLiteRepository` for the per-audience sync-state rows (upsert after
each push, one read for the dashboard's last-sync column). Cross-section
calls via `IMailerLiteService`, `IUserEmailService`, `IAuditLogService`,
plus `IEnumerable<IMailerLiteAudience>` (audience definitions). Outbound
slice — pushes computed audiences back to MailerLite groups. No cache.

### Audience definitions (`IMailerLiteAudience`)

Audience-membership computation classes under `MailerLite/Audiences/`:
`HasShiftAudience`, `HasShiftSetupAudience`, `HasShiftEventAudience`,
`HasShiftStrikeAudience`, `HasTicketAudience`, `MarketingAudience`,
`MarketingNoTicketAudience`, `TicketNoShiftsAudience`, `MailerLiteAudienceBase`,
`HasShiftInPeriodAudienceBase`.
No repository; compute over read-split / section service interfaces
(`ITicketServiceRead`, `IShiftSignupService`, `IShiftManagementService`,
etc.). No direct DB access, no cache.

---


