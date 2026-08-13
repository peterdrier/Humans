<!-- freshness:triggers
  src/Sections/Humans.Campaigns/Controllers/**
  src/Sections/Humans.Campaigns/Services/**
  src/Sections/Humans.Campaigns/Domain/**
  src/Sections/Humans.Campaigns/Data/Configurations/**
  src/Sections/Humans.Campaigns/Views/**
  src/Humans.Web/Views/Unsubscribe/**
  src/Humans.Web/Controllers/UnsubscribeController.cs
  src/Humans.Application/Services/Users/UnsubscribeService.cs
-->
<!-- freshness:flag-on-change
  Campaign lifecycle (Draft/Active/Completed), code import, wave send, grant assignment, unsubscribe flow, and My Codes profile section. Review when campaign views, services, or entities change.
-->

# Campaigns

## What this section is for

Campaigns distribute individualised codes to humans — for example, presale ticket codes for a partner event. Each code is unique, belongs to exactly one campaign, and is assigned to at most one human.

A campaign bundles three things: the **codes** (imported in bulk or generated through the ticket vendor), the **grants** that link a code to a human, and the **email waves** that deliver those codes. You can always look up your codes on your own profile. `Campaign Codes` is an always-on category — a code you have been granted always reaches you — so campaign emails carry no unsubscribe link.

## Key pages at a glance

- **My codes** — a section on your profile (`/Profile/Me`) listing every campaign grant assigned to you
- **Campaigns list** (`/Campaigns/Admin`) — every campaign with its status and code and grant counts
- **Campaign detail** (`/Campaigns/Admin/{id}`) — stats (total codes, available, sent, failed, redeemed) and the grant table; entry point for Import Codes, Generate Codes (vendor), Activate, Send Wave, Complete, Resend, and Retry All Failed
- **Create campaign** (`/Campaigns/Admin/Create`) — form for title, description, email subject, email body template (markdown, supports `{{Code}}` and `{{Name}}`), and optional Reply-To address
- **Edit campaign** (`/Campaigns/Admin/Edit/{id}`) — the same fields as Create, editable at any status
- **Import codes** — CSV upload for code values, available on Draft and Active campaigns
- **Generate codes** — vendor-issued discount codes (count, type, value), Draft campaigns only; available to TicketAdmin and Admin
- **Send wave** (`/Campaigns/Admin/{id}/SendWave`) — targets a single team, assigns free codes to eligible humans, and queues the emails

## As a Volunteer

### See the codes you have been granted

Open your profile at `/Profile/Me` and look for the My Codes section. It lists every campaign grant on your account, with the campaign and the code value. Codes stay visible here even after you have used the one in the email, so if you lose the message, this is where to look.

![TODO: screenshot — My Codes section on `/Profile/Me` showing a campaign grant with its title and code value]

### Receive a campaign email

When an Admin sends a wave that includes you, the code arrives by email to your notification address. The message carries the code and the campaign's description — no unsubscribe footer link and no `List-Unsubscribe` header.

### Unsubscribe from campaign emails

You cannot. `Campaign Codes` is an always-on category, like `System` — a code granted to you is always delivered. Because there is nothing an unsubscribe could do, campaign emails don't advertise one: no footer link, no RFC 8058 `List-Unsubscribe` header.

Your Communication Preferences page (`/Guest/CommunicationPreferences`) still shows `Campaign Codes` as locked on, alongside everything else — Marketing, Governance, Ticketing, and so on — that you can toggle. Other email types still carry a working unsubscribe link and `List-Unsubscribe` header for their own (opt-outable) category.

## As a Board member / Admin

Full campaign management requires the **Admin** role. **TicketAdmin** can view campaign details and generate discount codes through the ticket vendor, but cannot create, edit, activate, or complete campaigns.

### Create a campaign

From `/Campaigns/Admin`, start a new campaign. Fill in the title, description, email subject, email body template (markdown, with `{{Code}}` for the code and `{{Name}}` for the human's name), and an optional Reply-To address. It begins in **Draft** — no emails can be sent yet.

### Load codes into a campaign

Two options: **CSV import** of code values (allowed in both Draft and Active), or **ticket vendor generation** through the API (Draft only — pick count, discount type, and value). Each code must be unique within the campaign. CSV imports skip duplicates of codes already in the campaign.

### Activate

Once at least one code is loaded, activate the campaign. It moves from **Draft** to **Active** and becomes eligible for wave sends. Vendor-generated codes can no longer be added after activation, but additional CSV codes can still be imported.

### Send a wave

On an Active campaign, choose Send Wave and pick a target [team](Glossary.md#team). Picking a team shows a **Preview** first — eligible humans, how many were excluded as already granted, codes available, and codes left after the send — and the confirm button only appears when there is at least one eligible human and enough codes to cover them all. Confirming collects everyone currently on that team, excludes anyone already granted a code on this campaign, and matches the rest one-to-one with free codes ordered by import order so the batch is reproducible. Each match creates a grant and queues a delivery through the email outbox; recipients also receive an in-app `Campaign received` notification. Run multiple waves as more humans become eligible.

### Watch delivery and redemption

The campaign detail page shows how many codes are imported, available, sent, failed, and — for campaigns tied to ticket purchases — redeemed. Redemption is updated by the ticket sync job when it sees a granted code used in a purchase. From the grants table you can **Resend** a single grant's email, and a **Retry All Failed** button appears whenever any grant's most recent send is in `Failed` state.

### Complete

When a campaign is done, mark it Completed. No further waves can be sent.

## Related sections

- [Profiles](Profiles.md) — the My Codes list lives on your profile; Communication Preferences shows `Campaign Codes` locked on (the legacy `UnsubscribedFromCampaigns` flag is retained for GDPR export but is not read by any active gate)
- [Teams](Teams.md) — a wave targets one team; everyone who has not left it is eligible
