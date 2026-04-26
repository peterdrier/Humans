<!-- freshness:triggers
  src/Humans.Web/Views/Email/**
  src/Humans.Web/Views/Profile/Emails.cshtml
  src/Humans.Web/Controllers/EmailController.cs
  src/Humans.Web/Controllers/ProfileController.cs
  src/Humans.Application/Services/Email/**
  src/Humans.Application/Services/GoogleIntegration/EmailProvisioningService.cs
  src/Humans.Application/Services/GoogleIntegration/GoogleWorkspaceUserService.cs
  src/Humans.Application/Services/Profile/UserEmailService.cs
  src/Humans.Domain/Entities/UserEmail.cs
  src/Humans.Domain/Entities/EmailOutboxMessage.cs
  src/Humans.Infrastructure/Data/Configurations/Profiles/UserEmailConfiguration.cs
-->
<!-- freshness:flag-on-change
  @nobodies.team mailbox provisioning, group address mechanics, sending-as alias, and Profile Emails surface. Review when email views, provisioning service, or UserEmail entity change.
-->

# Email

## What this section is for

The org runs on Google Workspace under the `@nobodies.team` domain. Two
different things both get called "email" and people mix them up constantly,
so it's worth keeping them straight:

1. **Personal mailboxes** — `yourname@nobodies.team`. A full Workspace account
   with its own inbox, archive, and password. One per [human](Glossary.md#human).
2. **Group emails** — `teamname@nobodies.team`. A shared address that fans
   out to every member of a [team](Glossary.md#team). Not an inbox you log
   into by default (a shared-inbox option is being rolled out).

This guide covers both from the human side: how you get a mailbox, how to
sign in, how group emails work, and how to send "as" your team. The Drive,
Group, and sync mechanics underneath sit in
[Google Integration](GoogleIntegration.md).

## Key pages at a glance

- **Profile → Emails** (`/Profile/Me/Emails`) — your verified addresses, your
  notification target, your Google service email.
- **Team page** (`/Teams/{slug}`) — your team's group address is shown here.
- **Provision Email** (Admin only, on `/Human/{id}/Admin`) — creates a new
  `@nobodies.team` mailbox for a human.
- **mail.google.com** — sign in here with your `@nobodies.team` address to
  reach your inbox.

## As a [Volunteer](Glossary.md#volunteer)

### Why you have a `@nobodies.team` address

Everyone volunteering with Elsewhere gets a personal `@nobodies.team`
mailbox. It's a real Google Workspace account — full inbox, full archive,
yours for as long as you're an active human. It's the official contact
point for anything role-related: emails to other teams, vendors, or the
wider community on behalf of the work you do.

You can keep using your personal Gmail for personal things. Use
`@nobodies.team` for org things so the right people are reachable and the
history lives in one place.

### How you got it (or how to get it if you don't yet)

If you sign up to Humans now, the onboarding flow asks you for the address
you'd like — `firstname@nobodies.team`, `firstname.last@nobodies.team`, or
similar — and provisions it automatically.

If you signed up before that flow existed and don't have one yet, your
[Coordinator](Glossary.md#coordinator) can assign one to you from the
Humans app. Ask in your team chat, or use the feedback button (three dots
in a speech bubble, bottom right of the app), or email
[humans@nobodies.team](mailto:humans@nobodies.team).

### First sign-in

1. Go to [mail.google.com](https://mail.google.com).
2. Sign in with your `@nobodies.team` address and the temporary password
   that was sent to your personal email.
3. Set up two-factor authentication when prompted — this is **required**.
4. Optional: add the account to your phone's mail app (Gmail, Apple Mail,
   Outlook) the same way you'd add any Google account.

### Your team's group address

Every team has a shared address — `volunteers@nobodies.team`,
`barrios@nobodies.team`, `production@nobodies.team`, and so on. When
someone emails the group, every member of the team receives it in their
own personal inbox.

| Situation | What happens |
|---|---|
| Someone emails your team's address | You get it in your `@nobodies.team` inbox along with the rest of the team |
| You reply | By default the reply goes from your personal address — see "Sending from your team's address" below to send as the team instead |
| You want to email the whole team | Send to the group address; everyone receives it |
| You're in a sub-team | You receive emails sent to the sub-team. To also receive the parent department's emails you have to be on the parent team in Humans too |

Find your team's group address on the team's page in Humans, or ask your
Coordinator.

### Sending from your team's group address

If you want a reply to land in everyone's inbox (instead of just yours),
send "as" the group address. There are two ways:

**Option A — Add it as a Gmail send-from alias** (available now):

1. Open your `@nobodies.team` inbox at [mail.google.com](https://mail.google.com).
2. Settings (gear icon) → **See all settings** → **Accounts** tab.
3. Under **Send mail as**, click **Add another email address**.
4. Enter the group address (e.g. `production@nobodies.team`). Leave
   **Treat as an alias** ticked. Click **Next Step**.
5. Click **Send Verification**. A confirmation email arrives in your
   `@nobodies.team` inbox.
6. Open it, click the link, then click **Confirm** on the page that opens.
7. The group address now appears in your **From** dropdown when composing.

**Tip:** in Settings → Accounts, set **When replying to a message** to
**Reply from the same address the message was sent to**. Then if someone
emails `production@nobodies.team` and you reply, the reply goes out from
that address automatically.

**Option B — Sign in directly to the shared group inbox** (rolling out):

A shared-inbox setup is being activated where the whole team can sign in
to the group address itself and respond from a shared view of the inbox.
Ask your Coordinator whether this is live for your team yet.

### Common questions

| Question | Answer |
|---|---|
| I haven't received my `@nobodies.team` details | Ask your Coordinator (they can assign one via Humans), or use the feedback button in the app, or email [humans@nobodies.team](mailto:humans@nobodies.team) |
| I forgot my password | [accounts.google.com](https://accounts.google.com) → forgot password, with your `@nobodies.team` address |
| Can I use my personal Gmail instead? | For personal stuff, yes. For role-related emails (vendors, other teams, the community) please use your `@nobodies.team` address |
| What's my team's group address? | Check your team page in Humans, or ask your Coordinator |
| Can I send from the group address? | Yes — add it as a Gmail alias following the steps above; or use the shared-inbox option if it's live for your team |
| I'm not getting team emails | Check with your Coordinator that you're on the team in Humans. If you are and still not getting them, use the feedback button in the app or email [humans@nobodies.team](mailto:humans@nobodies.team) |
| I don't have a Google account | You don't need one to sign up for Humans — any email works. You'll still get a `@nobodies.team` address as part of the process |

## As a Coordinator

(assumes Volunteer knowledge)

Coordinators should use their `@nobodies.team` address as their main
contact, as should anyone in an externally-facing role (ticketing, comms,
production). It keeps the contact point consistent and survives role
changes — when responsibilities move on, the address stays with the role,
not the person.

### Assign a `@nobodies.team` address to someone on your team

If a member of your team doesn't have a `@nobodies.team` address yet, you
can assign one for them from the Humans app. The flow lives on the human's
profile admin page; if you can't find it, ask in the app's feedback button
(three dots in a speech bubble, bottom right) or email
[humans@nobodies.team](mailto:humans@nobodies.team).

### Group email membership follows team membership

| You do this in Humans | This happens in Google |
|---|---|
| Add a human to your team | They join the team's group email and get Drive folder access |
| Remove a human from your team | They leave the group email and lose Drive access (overnight) |
| Make someone a Coordinator | Their access stays the same as a member, plus they get management tools in the app |

You do **not** manage Google Group membership manually. Manage the team in
Humans; access follows.

> **Don't share Drive links directly with people who aren't on your team
> in Humans.** It creates ungoverned access and a GDPR problem. Add them
> to your team in Humans and Drive access follows automatically.

### Request a new team or sub-team group email

Currently Daniela (production) and Frank (volunteer coordination) create
new teams and group emails. If you need a new team, sub-team, or
sub-team-specific email like `newsletter@nobodies.team`, message either of
them on Discord or email them directly.

## As a Board member / Admin

(assumes Coordinator knowledge)

### Provision a `@nobodies.team` mailbox

From a human's profile admin page (`/Human/{id}/Admin`), use **Provision
Email**. The app:

1. Creates the Google Workspace account.
2. Sets a temporary password.
3. Sends credentials to the human's personal email.
4. Auto-links the new address as their Google service email so future
   sync uses it.

The flow and underlying job behaviour are documented in
[Google Integration](GoogleIntegration.md#manage-nobodiesteam-accounts).

### Audit and link orphans

`/Google/Accounts` lists every `@nobodies.team` mailbox in the domain. Use
it to find accounts not yet linked to a human and connect them.

### Group history is archived

All group email history is archived in Google Groups and visible to
admins. Useful when investigating a "did anyone reply to that?" question.

## Related sections

- [Google Integration](GoogleIntegration.md) — sync mechanics, permissions
  drift, the service account, Drive folder management.
- [Profiles](Profiles.md) — your verified emails and which one is your
  notification target.
- [Teams](Teams.md) — team membership is what drives group email and
  Drive access.
- [Glossary](Glossary.md#service-account) — service account, Shared Drive,
  sync mode.
