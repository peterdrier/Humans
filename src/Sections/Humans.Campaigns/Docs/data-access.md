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
`IEmailService`, the section's own `CampaignsEmails` builder,
`ITicketDiscountCodes` (Tickets Contracts leaf — vendor discount code
generation), plus `IClock`. Implements `ICampaignService` (which extends
`ICampaignServiceRead`), `IUserDataContributor`, `IUserMerge`. No
`IMemoryCache`.

### CampaignsEmails (Scoped, internal)

No repository. Pure builder — substitutes the campaign's own subject and
markdown body, reads no resource set and writes nothing. Returns
`EmailMessage` values for `CampaignService` to pass to
`IEmailService.SendAsync`. No DB access, no cache.

### CampaignsEmailPreviews (Scoped)

No repository. Read-only gallery contributor (`IEmailPreviewContributor`,
registered in `Section.Register`) — builds one sample per template via
`CampaignsEmails` for `/Email/EmailPreview`. No DB access, no cache.

---


