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
   into by default; some teams may also have a single shared inbox they all
   sign into — ask your Coordinator whether yours does.

This guide covers both from the human side: how you get a mailbox, how to
sign in, how group emails work, and how to send "as" your team. The Drive,
Group, and sync mechanics underneath sit in
[Google Integration](GoogleIntegration.md).

## Key pages at a glance

- **Profile → Emails** (`/Profile/Me/Emails`) — your verified addresses, your
  notification target, your Google service email.
- **Team page** (`/Teams/{slug}`) — your team's group address is shown here.
- **Provision Email** (Admin only, on `/Profile/{id}/Admin`) — creates a new
  `@nobodies.team` mailbox for a human.
- **mail.google.com** — sign in here with your `@nobodies.team` address to
  reach your inbox.

## As a [Volunteer](Glossary.md#volunteer)

The day-to-day, plain-language how-to for your own mailbox lives in the Common
questions pages — why you have a `@nobodies.team` address and how to get one,
signing in the first time, using your team's shared (group) address, and making
a reply reach the whole team:

- **[Your `@nobodies.team` email](EmailAccount.md)** — the everyday guide.
- **[Two-step verification (2FA)](TwoStepVerification.md)** — the required extra
  sign-in step on your Google account.

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

From a human's profile admin page (`/Profile/{id}/Admin`), use **Provision
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
