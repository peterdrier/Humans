# Governance (Applications & Board Voting) — Section Invariants

## Concepts

- **Volunteer** is the standard membership tier. Nearly all humans are Volunteers. Becoming a Volunteer happens through the onboarding process — not through the application/voting workflow described here.
- **Colaborador** is an active contributor with project and event responsibilities. Requires an application and Board vote. 2-year term.
- **Asociado** is a voting member with governance rights (assemblies, elections). Requires an application and Board vote. 2-year term. A human must first be an approved Colaborador before applying for Asociado.
- **Application** is a formal request to become a Colaborador or Asociado. Never used for becoming a Volunteer.
- **Board Vote** is an individual Board member's vote on a tier application. Board votes are transient working data — they are deleted when the application is finalized, and only the collective decision note and meeting date are retained (GDPR data minimization).
- **Role Assignment** is a temporal assignment of a governance or admin role to a human, with valid-from and valid-to dates.
- **Term** — Colaborador and Asociado memberships have synchronized 2-year terms expiring on December 31 of odd years (2027, 2029, 2031...).

## Actors & Roles

| Actor | Capabilities |
|-------|-------------|
| Any authenticated human | View own governance status (tier, active applications). Submit a Colaborador or Asociado application |
| Board | View all pending applications and role assignments. Cast individual votes on applications. View Board voting detail |
| Board, Admin | Approve or reject tier applications with a decision note and meeting date. Manage role assignments (all roles except Admin) |
| Admin | Assign the Admin role. All Board capabilities |

## Invariants

- Application status follows: Submitted then Approved, Rejected, or Withdrawn. No other transitions.
- Each Board member gets exactly one vote per application.
- On approval, the term expiry is set to the next December 31 of an odd year that is at least 2 years from the approval date.
- On approval, the human's membership tier is updated and they are added to the corresponding system team (Colaboradors or Asociados).
- On finalization (approval or rejection), all individual Board vote records for that application are deleted. Only the collective decision note and Board meeting date survive.
- Admin can assign all roles. Board and HumanAdmin can assign all roles except Admin.
- Role assignments track temporal membership with valid-from and optional valid-to dates.
- Volunteer onboarding is never blocked by tier applications — they are separate, parallel paths.

## Negative Access Rules

- Regular humans **cannot** view other humans' applications, cast Board votes, or manage role assignments.
- Board **cannot** assign the Admin role.
- HumanAdmin **cannot** assign the Admin role.
- Humans who already have a pending (Submitted) application for a tier cannot submit another for the same tier until the first is resolved.

## Triggers

- When an application is approved: the human's tier is updated on their profile, and they are added to the Colaboradors or Asociados system team.
- When an application is approved or rejected: all Board vote records for that application are deleted.
- A renewal reminder is sent 90 days before term expiry.
- On term expiry without renewal: the human reverts to Volunteer tier and is removed from the tier system team.

## Cross-Section Dependencies

- **Profiles**: Membership tier lives on the profile. Approval updates the profile.
- **Teams**: Tier approval or expiry adds or removes the human from Colaboradors/Asociados system teams.
- **Onboarding**: Tier applications are a separate, optional path — never blocks Volunteer onboarding.
- **Legal & Consent**: Consent checks are reviewed alongside (but independently of) tier applications.
