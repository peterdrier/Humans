# Glossary

Terms used across the Humans user guide. Each entry: a short definition plus
a link to the section guide where it's most relevant.

## Admin

A human with the `Admin` role — global superset privilege over the whole app.
Domain admins like Teams Admin, Camp Admin, and Ticket Admin are separate,
narrower roles scoped to their section. See [Admin](Admin.md).

## Asociado

A voting human with governance rights (assemblies, elections). Requires an
application and a Board vote. 2-year term. See [Governance](Governance.md).

## Barrio

A camp — the collective's word for a self-organizing themed community that
registers each year. Used interchangeably with "camp" across the app and
guide. See [Camps](Camps.md) and [City Planning](CityPlanning.md).

## Board

The governance body of Nobodies Collective. Approves tier applications and
votes on governance matters. See [Governance](Governance.md).

## Board vote

The decision a Board member casts on a Colaborador or Asociado application —
Yay, Maybe, No, or Abstain. Individual votes are deleted when the application
is finalized; only the collective decision is retained. See
[Governance](Governance.md).

## Camp Season

A camp's per-year record — name, blurb, vibes, space needs, placement, and
status (Pending, Active, Full, Withdrawn). Placement on the barrio map
requires an approved season for the current year. See [Camps](Camps.md).

## Colaborador

An active contributor with project or event responsibilities. Requires an
application and a Board vote. 2-year term. See [Governance](Governance.md).

## Consent Coordinator

The coordinator role that reviews a human's signed consents and clears them
for activation as a Volunteer. See [Legal & Consent](LegalAndConsent.md) and
[Onboarding](Onboarding.md).

## Coordinator

A human assigned to a department's management role, with full authority over
the department and every sub-team under it. See [Teams](Teams.md).

## Department

A top-level team (Build, Kitchen, and so on). Rotas, Google Groups, and
Shared Drive folders live at the department level; sub-teams inherit access.
See [Teams](Teams.md).

## Facilitated message

A message sent between humans through the app so personal contact details
stay private. Humans can opt out from notifications. See
[Profiles](Profiles.md).

## Human

The standard term for any person in the system. Used in place of "member",
"user", or "volunteer" when referring to the general population, independent
of role. Individual capabilities depend on role.

## Membership tier

Your governance status — Volunteer (default), Colaborador, or Asociado.
Tracked on the profile and changed via tier applications. See
[Governance](Governance.md).

## Primary Lead

The human responsible for a camp. A camp has exactly one Primary Lead and
optional Co-Leads; Primary can be transferred from the Edit page. See
[Camps](Camps.md).

## Reconciliation

The nightly Google sync job (`GoogleResourceReconciliationJob`, 03:00) that
realigns Shared Drive and Group membership with your team memberships. Runs
only for services whose sync mode is not `None`. See
[Google Integration](GoogleIntegration.md).

## Role assignment

A temporal grant of a coordinator or admin role to a human — with a valid-from
date and an optional valid-to date. Every change is audited. See
[Governance](Governance.md).

## Section

A vertical area of the app (Profiles, Teams, Shifts, Camps, Budget, Admin,
etc.). Each section has its own guide file.

## Service account

The Google identity the app uses to manage Shared Drive folders and Groups.
To link a resource, that account must already be an Editor on the Drive
folder or a Manager on the Group. See
[Google Integration](GoogleIntegration.md).

## Shared Drive

A Google Drive shared with a team. The app manages only Shared Drives — not
personal My Drive folders — and touches only direct permissions, not
inherited ones. See [Google Integration](GoogleIntegration.md).

## Sound zone

A noise-level classification assigned to a camp (quiet, medium, loud, and so
on). Color-codes the barrio map and feeds camp directory filters. See
[Camps](Camps.md) and [City Planning](CityPlanning.md).

## Sub-team

A team that lives under exactly one department. Managed by sub-team managers,
who have the same tools as a department Coordinator but scoped to the single
sub-team. See [Teams](Teams.md).

## Sync mode

The per-service Google sync setting — `None` (off), `AddOnly` (grant access
only, never revoke), or `AddAndRemove` (full bidirectional sync). Flipping a
service to `None` is the fast kill switch. See
[Google Integration](GoogleIntegration.md).

## System team

A team the app manages automatically — Volunteers, Coordinators, Board,
Asociados, Colaboradores. You can't join or leave a system team by hand;
membership follows role assignments and tier status. See [Teams](Teams.md).

## Team

A group of humans organized around a shared purpose. Teams have members,
coordinators, and optionally a Google Group and Shared Drive.
See [Teams](Teams.md).

## Volunteer

The default membership tier — the standard human. Everyone starts here after
completing signup, profile setup, consent, and Consent Coordinator clearance.
See [Onboarding](Onboarding.md).
