# Campaigns — Data Access

## Campaigns

Project: `src/Sections/Humans.Campaigns` — services under `Services/`,
repository under `Data/`. **DbContext:** `CampaignsDbContext`.
`CampaignRepository` (Singleton) injects `IDbContextFactory<CampaignsDbContext>`
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
`IEmailService`, `IEmailMessageFactory`,
`ITicketDiscountCodes` (Tickets Contracts leaf — vendor discount code
generation), plus `IClock`. Implements `ICampaignService` (which extends
`ICampaignServiceRead`), `IUserDataContributor`, `IUserMerge`. No
`IMemoryCache`.

---


