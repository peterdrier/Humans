# Mailer — Data Access

## Mailer

Folder: `src/Sections/Humans.Mailer/Services/`. No owned DB tables —
MailerLite is the external system; classifier writes through other
sections' services.

### MailerImportService (Scoped)

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

### MailerAudienceSyncService (Scoped)

No repository. Cross-section calls via `IMailerLiteService`,
`IUserEmailService`, `IAuditLogService`, plus
`IEnumerable<IMailerAudience>` (audience definitions). Outbound slice —
pushes computed audiences back to MailerLite groups. No DB access, no
cache.

### Audience definitions (`IMailerAudience`)

Audience-membership computation classes under `Mailer/Audiences/`:
`HasShiftAudience`, `HasShiftSetupAudience`, `HasShiftEventAudience`,
`HasShiftStrikeAudience`, `HasTicketAudience`, `MarketingAudience`,
`MarketingNoTicketAudience`, `TicketNoShiftsAudience`, `MailerAudienceBase`,
`HasShiftInPeriodAudienceBase`.
No repository; compute over read-split / section service interfaces
(`ITicketServiceRead`, `IShiftSignupService`, `IShiftManagementService`,
etc.). No direct DB access, no cache.

---


