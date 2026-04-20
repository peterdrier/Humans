# Campaigns

## What this section is for

Campaigns distribute individualised codes to humans — for example, presale ticket codes for a partner event. Each code is unique, belongs to exactly one campaign, and is assigned to at most one human.

A campaign bundles three things: the **codes** (imported in bulk or generated through the ticket vendor), the **grants** that link a code to a human, and the **email waves** that deliver those codes. You can always look up your codes on your own profile, and anyone can opt out of campaign emails via the unsubscribe link in every send.

## Key pages at a glance

- **My codes** — a section on your profile (`/Profile/Me`) listing every campaign grant assigned to you
- **Unsubscribe** (`/Unsubscribe/{token}`) — public, no-login page to opt out of all future campaign emails
- **Campaigns list** (`/Admin/Campaigns`) — every campaign with its status and code and grant counts
- **Campaign detail** (`/Admin/Campaigns/{id}`) — stats (total codes, assigned, sent, failed, redeemed) and the grant table
- **Create campaign** — form for title, description, email subject, and email body template
- **Import codes** — CSV upload for code values on a Draft campaign
- **Send wave** — targets one or more teams, assigns free codes to eligible humans, and queues the emails

## As a Volunteer

### See the codes you have been granted

Open your profile at `/Profile/Me` and look for the My Codes section. It lists every campaign grant on your account, with the campaign and the code value. Codes stay visible here even after you have used the one in the email, so if you lose the message, this is where to look.

![TODO: screenshot — My Codes section on `/Profile/Me` showing a campaign grant with its title and code value]

### Receive a campaign email

When an Admin sends a wave that includes you, the code arrives by email to your notification address. The message carries the code, the campaign's description, and an unsubscribe link (plus a `List-Unsubscribe` header).

### Unsubscribe from campaign emails

Click the unsubscribe link in any campaign email. It takes you to `/Unsubscribe/{token}` — public, no login needed. The first visit flags your account and excludes you from all future campaign sends; later visits show a confirmation. Unsubscribing does not remove codes already granted to you, and it only affects campaign emails.

## As a Board member / Admin

Full campaign management requires the **Admin** role. **TicketAdmin** can view campaign details and generate discount codes through the ticket vendor, but cannot create, edit, activate, or complete campaigns.

### Create a campaign

From `/Admin/Campaigns`, start a new campaign. Fill in the title, description, email subject, and email body template. It begins in **Draft** — no emails can be sent yet, and this is the only status in which you can add codes.

### Load codes into a Draft campaign

Two options while Draft: **CSV import** of code values, or **ticket vendor generation** through the API. Each code must be unique within the campaign.

### Activate

Once at least one code is loaded, activate the campaign. It moves from **Draft** to **Active** and becomes eligible for wave sends. Codes can no longer be imported or generated after activation.

### Send a wave

On an Active campaign, choose Send Wave and pick one or more target [teams](Glossary.md#team). The system collects active members of those teams, excludes anyone already granted a code or unsubscribed, and matches the rest one-to-one with free codes. Each match creates a grant and queues a delivery through the email outbox. Run multiple waves as more humans become eligible.

### Watch delivery and redemption

The campaign detail page shows how many codes are assigned, sent, failed, and — for campaigns tied to ticket purchases — redeemed. Redemption is updated by the ticket sync job when it sees a granted code used in a purchase.

### Complete

When a campaign is done, mark it Completed. No further waves can be sent.

## Related sections

- [Profiles](Profiles.md) — the My Codes list and the campaign unsubscribe flag both live on your profile
- [Onboarding](Onboarding.md) — only active, non-suspended humans on targeted teams are eligible for waves
