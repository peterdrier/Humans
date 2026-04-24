# Campaigns — Section Invariants

Bulk code-distribution campaigns: codes imported or generated, assigned to humans, delivered via email waves.

## Concepts

- A **Campaign** is a bulk code distribution effort — discount codes are assigned to humans and delivered via email waves.
- A **Campaign Code** is an individual code belonging to a campaign. Codes are imported in bulk (CSV) or generated via the ticket vendor.
- A **Campaign Grant** records the assignment of a specific code to a specific human.
- A **Wave** is a batch email send targeting a group of humans (typically by team) who have been granted codes but not yet notified.

## Data Model

### Campaign

**Table:** `campaigns`

| Property | Type | Purpose |
|----------|------|---------|
| Id | Guid | PK |
| Title | string | Campaign display name |
| Description | string? | Optional description |
| EmailSubject | string | Subject line template |
| EmailBodyTemplate | string | Liquid/Razor body template |
| Status | CampaignStatus | Draft / Active / Completed |
| CreatedAt | Instant | When created |
| CreatedByUserId | Guid | FK → User — **FK only**, `[Obsolete]`-marked nav |

**Aggregate-local navs:** `Campaign.Codes`, `Campaign.Grants`.

### CampaignCode

One row per individual code belonging to a campaign. Codes are imported in bulk; each is assigned to at most one user via a CampaignGrant.

**Table:** `campaign_codes`

| Property | Type | Purpose |
|----------|------|---------|
| Id | Guid | PK |
| CampaignId | Guid | FK → Campaign |
| Code | string | The code value (unique per campaign) |
| ImportedAt | Instant | When imported |

**Aggregate-local navs:** `CampaignCode.Campaign`, `CampaignCode.Grant`.

### CampaignGrant

Records the assignment of a specific code to a specific user.

**Table:** `campaign_grants`

| Property | Type | Purpose |
|----------|------|---------|
| Id | Guid | PK |
| CampaignId | Guid | FK → Campaign |
| CampaignCodeId | Guid | FK → CampaignCode (unique — one grant per code) |
| UserId | Guid | FK → User — **FK only**, `[Obsolete]`-marked nav |
| AssignedAt | Instant | When assigned |
| LatestEmailStatus | EmailOutboxStatus? | Status of most recent delivery attempt |
| LatestEmailAt | Instant? | Timestamp of most recent delivery attempt |

**Aggregate-local navs:** `CampaignGrant.Campaign`, `CampaignGrant.Code`.
Cross-domain nav `CampaignGrant.OutboxMessages` (Email) is `[Obsolete]`-marked — consumers route through `IEmailOutboxService`.

### CampaignStatus

| Value | Description |
|-------|-------------|
| Draft | Codes can be imported; sending not yet active |
| Active | Sending waves is enabled |
| Completed | Campaign closed |

Stored as int.

## Actors & Roles

| Actor | Capabilities |
|-------|--------------|
| TicketAdmin, Admin | View campaign details, generate discount codes via the ticket vendor |
| Admin | Full campaign management: create, edit, activate, complete campaigns. Import codes. Manage grants. Send campaign email waves |

## Invariants

- Campaign status follows: Draft then Active then Completed.
- Codes can only be generated or imported while the campaign is in Draft status.
- Each code is unique per campaign and can be assigned to at most one human.
- Campaign emails are queued through the email outbox system. Each grant tracks the status and timestamp of the most recent delivery attempt.
- Humans can unsubscribe from campaigns via a link in the email. Unsubscribed humans are excluded from future campaign sends.

## Negative Access Rules

- TicketAdmin **cannot** create, edit, activate, or complete campaigns. They can only view details and generate codes.
- Regular humans and other roles have no access to campaign management.
- There is no self-service view for humans to see their assigned codes (codes are delivered by email).

## Triggers

- When a campaign wave is sent, emails are queued to the outbox for each granted human who has not unsubscribed.
- When a human unsubscribes, their unsubscribe flag is set (`User.UnsubscribedFromCampaigns = true`) and they are excluded from all future campaign sends.

## Cross-Section Dependencies

- **Tickets:** `ITicketVendorService` — TicketAdmin can generate discount codes via the ticket vendor integration.
- **Email:** `IEmailOutboxService` — campaign emails are delivered through the email outbox system.
- **Profiles / Users:** `IUserEmailService.GetNotificationTargetEmailsAsync(IReadOnlyCollection<Guid>)` — resolves notification targets for grant emails; `IUserService` — campaign creator / grantee display names.
- **Teams:** `ITeamService.GetTeamMembersAsync` — team-scoped wave targeting.

## Architecture

**Owning services:** `CampaignService`
**Owned tables:** `campaigns`, `campaign_codes`, `campaign_grants`
**Status:** (A) Migrated (peterdrier/Humans PR for issue nobodies-collective/Humans#546, 2026-04-22).

- `CampaignService` lives in `Humans.Application.Services.Campaigns` and depends only on Application-layer abstractions.
- `ICampaignRepository` (impl `Humans.Infrastructure/Repositories/CampaignRepository.cs`) is the only file that touches this section's tables via `DbContext`.
- **Decorator decision — no caching decorator.** Admin-only, low write/read volume.
- **Cross-section reads** route through `ITeamService.GetTeamMembersAsync`, `IUserEmailService.GetNotificationTargetEmailsAsync`, and `IUserService` for display data.
- **Cross-domain navs `[Obsolete]`-marked:** `Campaign.CreatedByUser`, `CampaignGrant.User`. The `TicketQueryService.GetCodeTrackingDataAsync` code-tracking page still reads `grant.User.DisplayName` inside `#pragma warning disable CS0618` blocks — migration follow-up lands when Tickets moves fully to Application.

### Touch-and-clean guidance

- Do not add new cross-domain navs to `Campaign`, `CampaignCode`, or `CampaignGrant`. When adding fields, keep them scalar or aggregate-local only.
- New cross-section reads must go through the owning service interface; never `_dbContext`.
