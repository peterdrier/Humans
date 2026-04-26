<!-- freshness:triggers
  src/Humans.Application/Services/Campaigns/**
  src/Humans.Domain/Entities/Campaign.cs
  src/Humans.Domain/Entities/CampaignCode.cs
  src/Humans.Domain/Entities/CampaignGrant.cs
  src/Humans.Infrastructure/Data/Configurations/Campaigns/**
  src/Humans.Infrastructure/Repositories/CampaignRepository.cs
  src/Humans.Web/Controllers/CampaignController.cs
-->
<!-- freshness:flag-on-change
  Campaign lifecycle, code import/grant rules, wave email triggers, and unsubscribe handling — review when Campaign service/entities/controller change.
-->

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
| EmailSubject | string | Subject line template (supports `{{Name}}`) |
| EmailBodyTemplate | string | Markdown body template (supports `{{Code}}` and `{{Name}}`) |
| ReplyToAddress | string? | Optional Reply-To header for campaign emails |
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
| ImportOrder | int | Monotonic per-campaign sequence assigned at import time; wave allocation orders by this for stable batch order |
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
| RedeemedAt | Instant? | When the granted code was redeemed in a ticket purchase; null if unused. Set by `TicketSyncService` via `MarkGrantsRedeemedAsync` |

**Indexes:** unique `(CampaignCodeId)` (one grant per code) and unique `(CampaignId, UserId)` (one grant per user per campaign).

**Aggregate-local navs:** `CampaignGrant.Campaign`, `CampaignGrant.Code`.
Cross-domain nav `CampaignGrant.OutboxMessages` (Email) is `[Obsolete]`-marked — consumers route through `IEmailOutboxService`.

### CampaignStatus

| Value | Description |
|-------|-------------|
| Draft | Codes can be imported; sending not yet active |
| Active | Sending waves is enabled |
| Completed | Campaign closed |

Stored as string (`HasConversion<string>()`, max length 20).

## Actors & Roles

| Actor | Capabilities |
|-------|--------------|
| TicketAdmin, Admin | View campaign details, generate discount codes via the ticket vendor |
| Admin | Full campaign management: create, edit, activate, complete campaigns. Import codes. Manage grants. Send campaign email waves |

## Invariants

- Campaign status follows: Draft then Active then Completed. `ActivateAsync` requires Draft + at least one code; `CompleteAsync` requires Active; `SendWaveAsync` requires Active.
- Vendor-generated codes can only be created while the campaign is in Draft status (controller enforces). CSV code import has no service-side status guard — the Campaign Detail view exposes the import form in both Draft and Active.
- Each code is unique per campaign (DB-enforced via unique `(CampaignId, Code)` index) and can be assigned to at most one human (DB-enforced via unique `CampaignCodeId` on grants).
- Each human can hold at most one grant per campaign (DB-enforced via unique `(CampaignId, UserId)` on grants).
- Wave allocation pulls available codes ordered by `CampaignCode.ImportOrder` so batch order is stable and reproducible.
- Campaign emails are queued through the email outbox system. Each grant tracks the status and timestamp of the most recent delivery attempt; failed enqueues flip the single grant to `Failed` (the loop persists/enqueues one grant at a time so a mid-loop throw cannot orphan grants).
- Humans opt out of campaign emails by setting `MessageCategory.CampaignCodes = opted-out` via the unsubscribe / communication-preferences flow. Opted-out humans are excluded from future wave sends. (Today `CampaignCodes` is an always-on category, so the gate is a no-op guard for a future opt-outable state.)

## Negative Access Rules

- TicketAdmin **cannot** create, edit, activate, or complete campaigns. They can only view details and generate codes.
- Regular humans and other roles have no access to campaign management.
- Humans see only their own grants on `/Profile/Me` (the "My Codes" card, sourced from `ICampaignService.GetActiveOrCompletedGrantsForUserAsync`); no human can see another human's grants outside the admin views.

## Triggers

- When a campaign wave is sent (`SendWaveAsync`), emails are queued to the outbox via `IEmailService.SendCampaignCodeAsync` for each eligible human, and a `CampaignReceived` in-app notification is dispatched (best-effort) to every recipient who actually received a grant.
- When a human unsubscribes (legacy campaign-only token or new category-aware token), `ICommunicationPreferenceService.UpdatePreferenceAsync` flips their `MessageCategory.CampaignCodes` (legacy tokens map to `MessageCategory.Marketing`) preference to opted-out; they are then excluded from future wave sends. (The legacy `User.UnsubscribedFromCampaigns` boolean still exists on the entity for GDPR export but is no longer the active gate.)
- When `TicketSyncService` detects a granted code redeemed in a ticket purchase, it calls `ICampaignService.MarkGrantsRedeemedAsync` to set `CampaignGrant.RedeemedAt`.
- When an enqueue throws during `SendWaveAsync` or `RetryAllFailedAsync`, the single offending grant is flipped to `Failed` so the next pass of `RetryAllFailedAsync` can pick it up.

## Cross-Section Dependencies

- **Tickets:** `ITicketVendorService` — TicketAdmin can generate discount codes via the ticket vendor integration. Generation is invoked from the Campaign Detail page, not from the Tickets section.
- **Email:** `IEmailService.SendCampaignCodeAsync` — composes and queues the campaign-code email through the outbox.
- **Profiles / Users:** `IUserEmailService.GetNotificationTargetEmailsAsync(IReadOnlyCollection<Guid>)` — resolves notification targets for grant emails; `IUserService.GetByIdAsync` / `GetByIdsAsync` — recipient `DisplayName` for the email payload and code-tracking display; `ICommunicationPreferenceService.IsOptedOutAsync(MessageCategory.CampaignCodes)` — opt-out gate; `IUnsubscribeService` (in `Humans.Application.Services.Users`) processes the public `/Unsubscribe/{token}` endpoint, validating both new category-aware tokens and legacy campaign-only tokens before delegating opt-out to `ICommunicationPreferenceService.UpdatePreferenceAsync`.
- **Notifications:** `INotificationService.SendAsync` — `CampaignReceived` in-app notifications for wave recipients.
- **Teams:** `ITeamService.GetActiveTeamOptionsAsync` (Send Wave team picker) and `ITeamService.GetTeamMembersAsync` (team-scoped wave targeting).

## Architecture

**Owning services:** `CampaignService`
**Owned tables:** `campaigns`, `campaign_codes`, `campaign_grants`
**Status:** (A) Migrated (peterdrier/Humans PR for issue nobodies-collective/Humans#546, 2026-04-22).

- `CampaignService` lives in `Humans.Application.Services.Campaigns` and depends only on Application-layer abstractions.
- `ICampaignRepository` (impl `Humans.Infrastructure/Repositories/CampaignRepository.cs`) is the only file that touches this section's tables via `DbContext`.
- **Decorator decision — no caching decorator.** Admin-only, low write/read volume.
- **Cross-section reads** route through `ITeamService.GetActiveTeamOptionsAsync` / `GetTeamMembersAsync`, `IUserEmailService.GetNotificationTargetEmailsAsync`, `IUserService.GetByIdAsync` / `GetByIdsAsync` for display data, and `ICommunicationPreferenceService.IsOptedOutAsync` for opt-out filtering. Outbound email queueing goes through `IEmailService.SendCampaignCodeAsync` (the outbox service owns the email_outbox_messages table).
- **Cross-domain navs `[Obsolete]`-marked:** `Campaign.CreatedByUser`, `CampaignGrant.User`. Both are kept solely so EF can model the FK constraint (configured under `#pragma warning disable CS0618` in `CampaignConfiguration` / `CampaignGrantConfiguration`). All callers — including `TicketQueryService.GetCodeTrackingDataAsync` via `ICampaignService.GetCodeTrackingAsync` — resolve display names through `IUserService`, never the obsolete navs.

### Touch-and-clean guidance

- Do not add new cross-domain navs to `Campaign`, `CampaignCode`, or `CampaignGrant`. When adding fields, keep them scalar or aggregate-local only.
- New cross-section reads must go through the owning service interface; never `_dbContext`.
