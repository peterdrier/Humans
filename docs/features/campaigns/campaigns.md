<!-- freshness:triggers
  src/Sections/Humans.Campaigns/**
  src/Sections/Humans.Campaigns.Contracts/**
  src/Humans.Application/Services/Users/UnsubscribeService.cs
  src/Humans.Web/Controllers/UnsubscribeController.cs
-->
<!-- freshness:flag-on-change
  Campaign workflow, wave-send eligibility, unsubscribe behavior, or self-service code lookup may have shifted; verify states/routes/auth still match.
-->

# Feature 22: Campaigns

## Business Context

Campaigns allow admins to distribute individualized codes to humans — for example, presale ticket codes for partner events. Each code is unique and assigned to exactly one human. Codes are sent by email in waves (filtered by team membership), and humans can look up their codes on their profile page at any time.

## Campaign Workflow

```
Draft → Active → Completed
```

| State | Description |
|-------|-------------|
| Draft | Created, codes can be imported, not yet sending |
| Active | Codes have been imported; sending waves is possible |
| Completed | All codes assigned, campaign closed |

Transitions:
- **Activate**: moves from Draft → Active (requires at least one imported code)
- **Complete**: moves from Active → Completed (manual or auto)

## Wave Send

A "send wave" assigns ungranted codes to eligible humans and queues the email delivery:

1. Admin selects one team to target (one wave per team; repeat for more).
2. Service collects all active members of that team.
3. Exclusions applied automatically:
   - Humans already granted a code for this campaign
4. Remaining eligible humans are matched to available codes (one each). (`CampaignCodes` is an always-on message category — there is no opt-out to exclude on; `CampaignService` does not call `ICommunicationPreferenceService`.)
5. A `CampaignGrant` record is created per assignment.
6. An `EmailOutboxMessage` is queued per grant, referencing `CampaignGrantId`.

Commits are per-grant: if the enqueue throws for one human, only that grant flips to Failed and the wave keeps going, so Resend / Retry All Failed can pick it up later. The wave aborts up front if there are fewer available codes than eligible humans.

## Admin UI

Route: `/Campaigns/Admin` — Admin role, except the detail page and API code generation, which also accept TicketAdmin.

Pages:
- Campaign list with status and code/grant counts
- Campaign detail: stats (total codes, assigned, sent, failed), grant table
- Create / Edit campaign form (title, description, email subject, email body template, optional reply-to address)
- Import codes (CSV upload) or generate codes via the ticket vendor API
- Activate / Send Wave / Complete actions
- Per-grant Resend and campaign-wide Retry All Failed for failed email deliveries

## Unsubscribe

- Route: `/Unsubscribe/{token}` — public, no auth required
- **New-format tokens** (signed via `CommunicationPreferences` Data Protection purpose, category-aware): redirect to the public communication-preferences page (`/Guest/CommunicationPreferences?utoken=…`) where the user toggles per-category opt-outs.
- **Legacy tokens** (signed via `CampaignUnsubscribe` time-limited Data Protection purpose, campaign-only): show a confirmation page and POST to confirm. Sets the `Marketing` category preference to opted-out via `ICommunicationPreferenceService.UpdatePreferenceAsync`. `User.UnsubscribedFromCampaigns` is **not** flipped — the per-category `CommunicationPreference` table is the source of truth.
- RFC 8058 one-click POST (`/Unsubscribe/OneClick`) also routes through `UnsubscribeService.ConfirmUnsubscribeAsync`.
- Campaign emails carry no unsubscribe footer link and no `List-Unsubscribe` header: `CampaignCodes` is always-on, so `OutboxEmailService` skips unsubscribe stamping for it (nobodies-collective/Humans#1032). Only opt-outable categories reach this endpoint.

## Self-Service Code Lookup

Humans can view their campaign codes on their profile page. The profile page shows a "My Codes" section listing all `CampaignGrant` records for the current user, including the campaign title and the code value.

## Authorization

- `/Campaigns/Admin` routes: `Admin` role required (`PolicyNames.AdminOnly`), except `GET /Campaigns/Admin/{id}` (detail) and `POST /Campaigns/Admin/{id}/GenerateCodes`, which use `PolicyNames.TicketAdminOrAdmin`
- `/Unsubscribe/{token}`: public (no authentication required)
- Profile code lookup: authenticated user, own profile only

## Data Relationships

```
Campaign 1──n CampaignCode
Campaign 1──n CampaignGrant
CampaignCode 1──1 CampaignGrant (once assigned)
CampaignGrant n──1 User
CampaignGrant 1──n EmailOutboxMessage
```

## Ticket Vendor Integration

CampaignGrant has a `RedeemedAt` (Instant?) field set by the ticket sync job when it discovers a grant's discount code was used in a ticket purchase. This enables:
- Redemption tracking on the Campaign Detail page ("X of Y codes redeemed")
- Code tracking on the `/Tickets/Codes` page

Additionally, Draft campaigns support API-based code generation via `ITicketVendorService.GenerateDiscountCodesAsync()` as an alternative to CSV import.

See [24. Ticket Vendor Integration](../tickets/ticket-vendor-integration.md) for details.

## Related

- [Campaigns section invariants](../../../src/Sections/Humans.Campaigns/Docs/Campaigns.md) — current data model, routing, and architecture status (own project since G5, nobodies-collective/Humans#866; `CampaignsDbContext` owns the `campaign*` tables)
