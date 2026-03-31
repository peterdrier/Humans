# Onboarding — Section Invariants

## Concepts

- **Onboarding** is the process a new human goes through to become an active Volunteer: sign up via Google OAuth, complete their profile, consent to all required legal documents, and pass a safety review by a Consent Coordinator.
- The **Membership Gate** restricts most of the application to active Volunteers. Humans still onboarding are limited to their profile, consent, feedback, legal documents, camps (public), and the home dashboard. All admin and coordinator roles bypass this gate entirely.
- The **Consent Check** is the final gate before activation. A Consent Coordinator reviews and either clears (auto-approves the human as a Volunteer) or flags the check (blocks activation until Board or Admin review).

## Actors & Roles

| Actor | Capabilities |
|-------|-------------|
| Unauthenticated visitor | Sign up via Google OAuth |
| Authenticated human (pre-approval) | Complete profile, sign legal documents, submit a tier application (optional), submit feedback |
| ConsentCoordinator | Clear or flag consent checks in the onboarding review queue |
| VolunteerCoordinator | Read-only access to the onboarding review queue (cannot clear/flag or reject) |
| Board, Admin | All ConsentCoordinator capabilities. Reject signups. Manage Board voting on tier applications |

## Invariants

- Onboarding steps: (1) complete profile, (2) consent to all required global legal documents, (3) consent check review by a Consent Coordinator, (4) auto-approval as Volunteer.
- Volunteer onboarding is never blocked by tier applications — they are separate, parallel paths.
- The ActiveMember status is derived from membership in the Volunteers system team.
- All admin and coordinator roles bypass the membership gate entirely — they can access the full application regardless of membership status.

## Negative Access Rules

- VolunteerCoordinator **cannot** clear or flag consent checks, and **cannot** reject signups. They have read-only access to the review queue.
- ConsentCoordinator **cannot** reject signups. Rejection requires Board or Admin.
- Regular humans still onboarding **cannot** access most of the application (teams, shifts, budget, tickets, governance, etc.) until they become active Volunteers.

## Triggers

- When a human completes their profile and signs all required documents: their consent check status becomes Pending.
- When a consent check is cleared: the human becomes an active Volunteer, is added to the Volunteers system team, and gains access to the full application. A welcome email is sent.
- When a consent check is flagged: onboarding is blocked. Board or Admin must review.
- When a signup is rejected: the rejection reason and timestamp are recorded on the profile. The human is notified.

## Cross-Section Dependencies

- **Profiles**: Profile completion is step 1. Consent check status and membership tier live on the profile.
- **Legal & Consent**: Consent to all required global documents is step 2.
- **Teams**: Volunteer activation adds the human to the Volunteers system team.
- **Governance**: Tier applications are optional and independent of Volunteer onboarding.
- **Feedback**: Feedback submission is available during onboarding, before the human is active.
