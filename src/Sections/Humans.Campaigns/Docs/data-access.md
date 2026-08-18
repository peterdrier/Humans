# Campaigns — Data Access

## Campaigns

Project: `src/Sections/Humans.Campaigns` — services under `Services/`,
repository under `Data/`. **DbContext:** `CampaignsDbContext`.
`CampaignRepository` injects `IDbContextFactory<CampaignsDbContext>`
directly. Owns `Campaigns`, `CampaignCodes`, `CampaignGrants`.

### CampaignService (Scoped)

Repository: `ICampaignRepository`.

| Table | R/W |
|-------|-----|
| Campaigns | R/W |
| CampaignCodes | R/W |
| CampaignGrants | R/W |

Cross-section calls via `ITeamServiceRead`, `IUserEmailService`,
`IUserServiceRead`, `INotificationEmitter`,
`ICommunicationPreferenceService`, `IEmailService`, `IEmailMessageFactory`,
`ITicketDiscountCodes` (Tickets Contracts leaf — grant-wave discount code
generation), plus `IClock`. Implements `ICampaignService` (which extends
`ICampaignServiceRead`), `IUserDataContributor`, `IUserMerge`. No
`IMemoryCache`.

---


