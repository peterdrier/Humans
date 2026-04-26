<!-- freshness:triggers
  src/Humans.Web/Views/Profile/**
  src/Humans.Web/Controllers/ProfileController.cs
  src/Humans.Web/Controllers/ProfileApiController.cs
  src/Humans.Web/Controllers/ContactsController.cs
  src/Humans.Web/ViewComponents/ProfileCardViewComponent.cs
  src/Humans.Web/ViewComponents/UserAvatarViewComponent.cs
  src/Humans.Application/Services/Profile/**
  src/Humans.Application/Services/Users/UserService.cs
  src/Humans.Application/Services/Gdpr/**
  src/Humans.Domain/Entities/Profile.cs
  src/Humans.Domain/Entities/ProfileLanguage.cs
  src/Humans.Domain/Entities/ContactField.cs
  src/Humans.Domain/Entities/UserEmail.cs
  src/Humans.Domain/Entities/CommunicationPreference.cs
  src/Humans.Domain/Entities/User.cs
  src/Humans.Infrastructure/Data/Configurations/Profiles/**
-->
<!-- freshness:flag-on-change
  Personal profile, contact-field visibility, email management, communication preferences, search, GDPR export/deletion, and admin profile actions (suspend/approve/reject/roles). Review when profile views, services, or entities change.
-->

# Profiles

## What this section is for

Your profile is how the organization knows you and how other humans reach you. It holds your name, city, country, birthday (month and day only), profile picture, contact handles, email addresses, shift preferences, and communication settings. Every authenticated human has a profile, and you can edit yours at any time — including while onboarding.

Each contact field has its own visibility setting, so you can share your Signal handle with every active human while keeping your phone number visible only to the [Board](Glossary.md#board). Your [membership tier](Glossary.md#membership-tier) (Volunteer, Colaborador, or Asociado) is recorded here, and this is where you go to export your data or request deletion under GDPR.

## Key pages at a glance

- **My profile** (`/Profile/Me`) — view your own profile exactly as you see it
- **Edit profile** (`/Profile/Me/Edit`) — update personal info, contact fields, picture, birthday
- **Emails** (`/Profile/Me/Emails`) — add, verify, and manage your email addresses
- **Shift info** (`/Profile/Me/ShiftInfo`) — preferences and availability info used for shift planning
- **Notifications** (`/Profile/Me/Notifications`) — communication preferences by category
- **Privacy** (`/Profile/Me/Privacy`) — data export and account deletion
- **Outbox** (`/Profile/Me/Outbox`) — the emails the system has sent to you
- **View another human** (`/Profile/{id}`) — another human's profile, filtered by visibility
- **Search** (`/Profile/Search`) — find other humans by name

## As a Volunteer

### View your profile

Go to `/Profile/Me`. You see every field you have filled in, including anything marked board-only — on your own profile, visibility limits do not hide things from you. If you have a pending tier application, a banner links to its status page.

### Edit your profile

From your profile, click Edit (or go to `/Profile/Me/Edit`). The edit page is organised into cards: general information, contributor information, and private information (legal name, emergency contact, board notes). Fill in what you want and save — changes take effect immediately.

### Manage your contact fields and their visibility

On the edit page, add contact fields for Phone, Signal, Telegram, WhatsApp, Discord, or an "Other" type with a custom label. For each field, pick a visibility level:

- **Board only** — only Board members can see it
- **Coordinators and Board** — Coordinators of any team plus the Board
- **My teams** — humans who share a team with you
- **All active humans** — any active human in the system (the default)

![TODO: screenshot — `/Profile/Me/Edit` contact fields section, showing the per-field visibility dropdown with all four levels]

### Upload a profile picture

On the edit page, choose an image (JPEG, PNG, or WebP, up to 2 MB). Your custom picture takes precedence over your Google avatar everywhere in the app. You can remove it later to revert to the Google avatar.

### Manage your email addresses

Go to `/Profile/Me/Emails`. Your OAuth login email is always there and cannot be deleted. Add other addresses — each is verified via a link. Exactly one verified email is your notification target (where system emails go). Each verified email has its own visibility setting, using the same levels as contact fields. If you have been provisioned a `@nobodies.team` address, it is auto-selected as your Google services email.

### Set communication preferences

Go to `/Profile/Me/Notifications`. Categories like Facilitated Messages, Volunteer Updates, Team Updates, Governance, and Marketing can each be toggled for email and in-app alerts. System and Campaign Codes are always on. If you have a matched ticket, Ticketing is locked on. Opting out of Facilitated Messages hides the Send Message button on your profile for other humans.

### Set shift preferences

Go to `/Profile/Me/ShiftInfo` to record preferences and availability info used by shift planners.

### View another human's profile

Click any name or avatar, or go to `/Profile/{id}`. You see their basic info plus only the contact fields and emails whose visibility matches your access level. A popover variant (`/Profile/{id}/Popover`) powers hover previews.

### Send a facilitated message

On another human's profile, use Send Message (`/Profile/{id}/SendMessage`). The system relays your message via email without revealing either address. If the recipient has opted out of Facilitated Messages, the button is hidden.

### Search for humans

Go to `/Profile/Search` and type a name. Results respect your access level and exclude suspended humans.

### Export your data

Go to `/Profile/Me/Privacy` and choose Download My Data. You get a JSON file containing your profile, contact fields, emails, team memberships, consent records, applications, and role assignments.

### Request account deletion

Also on `/Profile/Me/Privacy`. Requesting deletion revokes your team memberships and role assignments immediately and starts a 30-day grace period during which you can still log in and cancel. After 30 days your personal data is anonymised automatically in the background. Google permissions are removed through the normal sync cycle once your team memberships end.

## As a Board member / Admin (Human Admin)

### View any profile in full

Go to `/Profile/{id}/Admin` (or the admin list at `/Profile/Admin`). All contact fields, emails, admin notes, emergency contact, legal name, and role history are visible regardless of per-field visibility.

### See a human's outbox

From the admin detail page, open `/Profile/{id}/Admin/Outbox` to see what emails the system has sent them — useful when debugging notifications or confirming a consent reminder went out.

### Suspend or unsuspend a human

Use `/Profile/{id}/Admin/Suspend`. Suspended humans cannot be seen by regular humans and lose active access. Unsuspending reverses the flag.

### Approve or reject a new signup

New humans whose consent check is pending wait for a decision at `/Profile/{id}/Admin/Approve` or `/Profile/{id}/Admin/Reject`. Approving adds them to the Volunteers team; rejecting stops onboarding. These manual buttons are for edge cases where automatic activation didn't happen — for example, a Consent Coordinator flagged the consent status, or the human otherwise requires admin review. The normal happy-path activation is automatic; see [Onboarding](Onboarding.md).

### Manage a human's roles

Use `/Profile/{id}/Admin/Roles` to assign or end [role assignments](Glossary.md#role-assignment) (Coordinator, Board, Admin, domain-specific admins). Role changes are recorded in the audit trail.

## Related sections

- [Legal and Consent](LegalAndConsent.md) — consent clearance gates Volunteer activation and flows through the profile
- [Teams](Teams.md) — sharing a team with another human raises what you can see on their profile
- [Onboarding](Onboarding.md) — completing your profile is a required step in the signup pipeline
