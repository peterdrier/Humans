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

1. Admin selects one or more teams to target.
2. Service collects all active members of those teams.
3. Exclusions applied automatically:
   - Humans already granted a code for this campaign
   - Humans who have unsubscribed from campaigns (`User.UnsubscribedFromCampaigns = true`)
4. Remaining eligible humans are matched to available codes (one each).
5. A `CampaignGrant` record is created per assignment.
6. An `EmailOutboxMessage` is queued per grant, referencing `CampaignGrantId`.

## Admin UI

Route: `/Admin/Campaigns` — requires Admin role.

Pages:
- Campaign list with status and code/grant counts
- Campaign detail: stats (total codes, assigned, sent, failed), grant table
- Create campaign form (title, description, email subject, email body template)
- Import codes (CSV upload)
- Activate / Send Wave / Complete actions

## Unsubscribe

- Route: `/Unsubscribe/{token}` — public, no auth required
- Token is a URL-safe Base64-encoded user ID
- Sets `User.UnsubscribedFromCampaigns = true` on first visit
- Idempotent: visiting again shows a confirmation page
- All campaign emails include `List-Unsubscribe` headers pointing to this endpoint

## Self-Service Code Lookup

Humans can view their campaign codes on their profile page. The profile page shows a "My Codes" section listing all `CampaignGrant` records for the current user, including the campaign title and the code value.

## Authorization

- All `/Admin/Campaigns` routes: `Admin` role required
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

See [24. Ticket Vendor Integration](24-ticket-vendor-integration.md) for details.
