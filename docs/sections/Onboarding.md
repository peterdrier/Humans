# Onboarding — Section Invariants

## Concepts

- **Onboarding** is the process a new human goes through to become an active Volunteer: sign up via Google OAuth, complete their profile, consent to all required legal documents, and pass a profile review by a Consent Coordinator. The last two steps can happen in any order.
- The **Membership Gate** restricts most of the application to active Volunteers. Humans still onboarding are limited to their profile, consent, feedback, legal documents, camps (public), and the home dashboard. All admin and coordinator roles bypass this gate entirely.
- **Profileless accounts** are authenticated users with no Profile record (e.g., ticket holders, newsletter subscribers created by imports). They are redirected to the Guest dashboard instead of the Home dashboard and see a reduced nav: Guest Dashboard, Camps, Teams (public), Legal. They can create a profile to enter the standard onboarding flow.
- The **Profile Review** (consent check) and **Legal Document Signing** are independent, parallel tracks. A Consent Coordinator can clear the profile review before or after legal documents are signed. Admission to the Volunteers team only happens when both are complete.

## Actors & Roles

| Actor | Capabilities |
|-------|-------------|
| Unauthenticated visitor | Sign up via Google OAuth (or magic link) |
| Authenticated human (pre-approval) | Complete profile, sign legal documents, submit a tier application (optional), submit feedback |
| ConsentCoordinator | Clear or flag consent checks in the onboarding review queue |
| VolunteerCoordinator | Read-only access to the onboarding review queue (cannot clear/flag or reject) |
| Board, Admin | All ConsentCoordinator capabilities. Reject signups. Manage Board voting on tier applications |

## Invariants

- Onboarding steps: (1) complete profile, (2a) consent to all required global legal documents, (2b) profile review by a Consent Coordinator — these two can happen in any order, (3) auto-approval as Volunteer when both 2a and 2b are complete.
- Volunteer onboarding is never blocked by tier applications — they are separate, parallel paths.
- The ActiveMember status is derived from membership in the Volunteers system team.
- All admin and coordinator roles bypass the membership gate entirely — they can access the full application regardless of membership status.
- OAuth login checks verified UserEmails, unverified UserEmails, and User.Email before creating a new account — preventing duplicate accounts when the same email exists on another user in any form.

## Negative Access Rules

- VolunteerCoordinator **cannot** clear or flag consent checks, and **cannot** reject signups. They have read-only access to the review queue.
- ConsentCoordinator **cannot** reject signups. Rejection requires Board or Admin.
- Regular humans still onboarding **cannot** access most of the application (teams, shifts, budget, tickets, governance, etc.) until they become active Volunteers.
- Profileless accounts **cannot** access the Home dashboard, City Planning, Budget, Shifts, Governance, or any member-only features. They are redirected to the Guest dashboard.

## Triggers

- When a human completes their profile and signs all required documents: their consent check status becomes Pending.
- When a profile review is cleared: the profile is approved. If all legal documents are also signed, the human is added to the Volunteers system team and a welcome email is sent. If documents are still pending, admission happens automatically when the last document is signed.
- When a legal document is signed: the system checks if the profile is also approved. If both conditions are met, the human is added to the Volunteers team.
- When a consent check is flagged: onboarding is blocked. Board or Admin must review.
- When a signup is rejected: the rejection reason and timestamp are recorded on the profile. The human is notified.

## Cross-Section Dependencies

- **Profiles**: Profile completion is step 1. Consent check status and membership tier live on the profile.
- **Legal & Consent**: Consent to all required global documents is step 2.
- **Teams**: Volunteer activation adds the human to the Volunteers system team.
- **Governance**: Tier applications are optional and independent of Volunteer onboarding.
- **Feedback**: Feedback submission is available during onboarding, before the human is active.
