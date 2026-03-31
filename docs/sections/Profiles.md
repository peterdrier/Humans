# Profiles — Section Invariants

## Concepts

- A **Profile** holds a human's personal information: name, city, country, birthday (month and day only — never year), profile picture, and admin notes.
- **Contact Fields** are per-field contact details (phone, Signal, Telegram, WhatsApp, Discord, custom) with per-field visibility controls.
- **Visibility Levels** determine who can see each contact field: BoardOnly (most restrictive), CoordinatorsAndBoard, MyTeams (shared team members), or AllActiveProfiles (least restrictive).
- **Membership Tier** is tracked on the profile: Volunteer (default), Colaborador, or Asociado.
- **Communication Preferences** control per-category email opt-in/opt-out (System, EventOperations, CommunityUpdates, Marketing).

## Actors & Roles

| Actor | Capabilities |
|-------|-------------|
| Any authenticated human | View and edit own profile, manage own emails, manage own contact fields, upload profile picture, set notification and communication preferences, request data export (GDPR Article 15), request account deletion |
| Any active human | View other active humans' profiles (contact fields restricted by per-field visibility). Send facilitated messages to other humans |
| HumanAdmin, Board, Admin | View any profile with full detail. Manage humans via admin pages (suspend, unsuspend, update tier, view audit log) |

## Invariants

- Every authenticated human can edit their own profile regardless of membership status (available during onboarding).
- Contact field visibility is enforced per-field: a human viewing their own profile sees everything. Board members see everything. Coordinators see CoordinatorsAndBoard-level and below. Shared-team members see MyTeams-level and below. Other active members see only AllActiveProfiles fields.
- Birthday stores month and day only — never year. UI text uses "birthday", not "date of birth".
- Membership tier (Volunteer, Colaborador, Asociado) is tracked on the profile, not as a role assignment.
- Consent check status on the profile gates Volunteer activation: unset until all consents are signed, then Pending, Cleared, or Flagged.
- Profile deletion request sets a flag. Memberships and team memberships are revoked immediately. Actual data purge is deferred to a background job.
- Data export returns all personal data as a JSON download (GDPR compliance).
- Profile pictures are stored on disk. Uploaded images are validated for allowed types and size.

## Negative Access Rules

- Regular humans **cannot** view suspended profiles.
- Regular humans **cannot** edit another human's profile.
- Regular humans **cannot** see contact fields above their access level on other humans' profiles.
- Non-active humans (still onboarding) **cannot** view other humans' profiles or send messages.

## Triggers

- When all required legal documents are consented to, consent check status transitions to Pending.
- When consent check status is Cleared, the human is auto-approved as a Volunteer and added to the Volunteers system team.
- When a human requests account deletion, team memberships are revoked immediately. The actual data purge runs in a background job.

## Cross-Section Dependencies

- **Legal & Consent**: Consent check status depends on all required document versions having consent records.
- **Teams**: Active membership equals membership in the Volunteers system team. Profile activation triggers addition.
- **Onboarding**: Profile completion is a prerequisite step in the onboarding pipeline.
- **Google Integration**: A human's Google service email determines which email is used for Google Groups and Drive sync.
