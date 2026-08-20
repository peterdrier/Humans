# MailerLite — Data Access

## MailerLite

Folder: `src/Sections/Humans.MailerLite/Services/`. No owned DB tables —
MailerLite is the external system; classifier writes through other
sections' services.

### MailerLiteImportService (Scoped)

No repository. Cross-section calls via `IMailerLiteService` (external),
`IUserEmailService`, `IUserServiceRead`, `IAccountProvisioningService`,
`ICommunicationPreferenceService`, `IAuditLogService`. Inbound import
slice — reads MailerLite subscribers and provisions matching accounts.
No DB access, no cache.

### MailerLiteService (Scoped)

No repository. `MailerLiteClient` is the MailerLite HTTP port, built over
`IHttpClientFactory`: account and group reads (`GetAccountSummaryAsync`,
`ListGroupsAsync`, `CreateGroupAsync`), subscriber reads
(`ListSubscribersAsync`, `GetSubscriberAsync`), and the group-membership
writes the audience sync drives (`AssignSubscriberToGroupAsync`,
`UnassignSubscriberFromGroupAsync`, `BulkImportSubscribersToGroupAsync`,
`RefreshAsync`). Retry timing uses `IClock`. No DB access, no cache.

### MailerLiteAudienceSyncService (Scoped)

No repository. Cross-section calls via `IMailerLiteService`,
`IUserEmailService`, `IAuditLogService`, plus
`IEnumerable<IMailerLiteAudience>` (audience definitions). Outbound slice —
pushes computed audiences back to MailerLite groups. No DB access, no
cache.

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


