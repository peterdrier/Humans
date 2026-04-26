<!-- freshness:triggers
  src/Humans.Web/Views/Application/**
  src/Humans.Web/Views/OnboardingReview/BoardVoting.cshtml
  src/Humans.Web/Views/OnboardingReview/BoardVotingDetail.cshtml
  src/Humans.Web/Views/Governance/**
  src/Humans.Web/Controllers/ApplicationController.cs
  src/Humans.Web/Controllers/OnboardingReviewController.cs
  src/Humans.Web/Controllers/GovernanceController.cs
  src/Humans.Application/Services/Governance/**
  src/Humans.Application/Services/Auth/RoleAssignmentService.cs
  src/Humans.Domain/Entities/Application.cs
  src/Humans.Domain/Entities/ApplicationStateHistory.cs
  src/Humans.Domain/Entities/BoardVote.cs
  src/Humans.Domain/Entities/RoleAssignment.cs
  src/Humans.Domain/Constants/RoleNames.cs
  src/Humans.Domain/Constants/RoleGroups.cs
  src/Humans.Domain/Constants/SystemTeamIds.cs
-->
<!-- freshness:flag-on-change
  Tier application workflow (Submitted/Approved/Rejected/Withdrawn), Board voting dashboard, finalization, term expiry, and role-assignment management. Review when governance views, services, entities, or role constants change.
-->

# Governance

## What this section is for

Governance handles **tier applications** — applying to become a [**Colaborador**](Glossary.md#colaborador) or [**Asociado**](Glossary.md#asociado) — along with the [**Board vote**](Glossary.md#board-vote) that decides those applications and the **[coordinator](Glossary.md#coordinator) and admin [role assignments](Glossary.md#role-assignment)** that track who can do what. It is **not** how you become a [Volunteer](Glossary.md#volunteer). Volunteer access is a separate, parallel path handled through profile setup, consent, and a safety check by a Consent Coordinator — see [Onboarding.md](Onboarding.md) for that flow. Tier applications never block Volunteer access, and Volunteer access never depends on a [Board](Glossary.md#board) decision.

Both tiers run on synchronized 2-year terms that expire on December 31 of the next appropriate odd year. Terms, votes, and role assignments all leave an audit trail on your profile and on the human detail page.

![TODO: screenshot — the Board Voting dashboard: applications as rows, Board members as columns, each cell showing an individual vote, with Review/Finalize actions on the right]

## Key pages at a glance

- `/Application/Create` — submit a Colaborador or Asociado application.
- `/Application` — your application status and history.
- `/Application/Admin` — admin list of all applications, filterable by status and tier (Board and Admin).
- `/Application/Admin/{id}` — admin detail view of a single application (Board and Admin).
- `/OnboardingReview/BoardVoting` — Board voting dashboard (Board and Admin).
- `/OnboardingReview/BoardVoting/{id}` — application detail and vote form (Board and Admin); the Finalize form is rendered only for Admin.
- `/Governance/Roles` — paginated list of all role assignments, filterable by role (Board and Admin).
- `/Profile/{id}/Admin/Roles/Add` and `/Profile/{id}/Admin/Roles/{roleId}/End` — assign and end role assignments on a specific human (Board, HumanAdmin, and Admin).

## As a Volunteer

### Apply for Colaborador or Asociado

As an active Volunteer you can apply for **Colaborador** (active contributor with project and event responsibilities) or **Asociado** (voting member with governance rights). If you are already a Colaborador, you can apply to upgrade to Asociado. Both require a Board vote and grant a 2-year term on approval.

Go to `/Application/Create`, pick the tier, and fill in a **motivation** (required) and any **additional info** for the Board (optional). Your current tier and access stay the same while the Board reviews. You cannot submit a second application for the same tier while one is pending.

If you applied inline during initial signup, that form was a one-shot. After onboarding, `/Application/Create` is the only way to apply.

### See your application status

Your dashboard and `/Application` show the status of any application you have submitted:

- **Submitted** — waiting on the Board.
- **Approved** — tier granted. You will see the term expiry date, Board meeting date, and decision note.
- **Rejected** — the Board declined. The decision note explains why.
- **Withdrawn** — you cancelled it.

The state history on the detail page records each change with a timestamp.

### Withdraw an application

While your application is still **Submitted**, you can withdraw it from the application detail page. Withdrawing is recorded in the state history and lets you submit a new application for the same tier later.

### Renew your tier

About 90 days before your term expires, a renewal reminder email and in-app notification go out, and a reminder appears on your dashboard. A renewal creates a new application for the same tier and goes through the normal Board vote. Board and Admin see the same upcoming expirations on the Board voting dashboard, so renewals can be prompted or processed proactively. If you do not renew before the term ends, the next nightly system-team sync removes you from the Colaboradors or Asociados system team, so you lose the access tied to that membership. Your profile's stored tier label does not automatically reset — you stay listed at the previous tier on your profile until a new approval changes it. Volunteer access is unaffected.

## As a Board member / Admin

### Vote on tier applications

Open `/OnboardingReview/BoardVoting`. The dashboard is a spreadsheet: applications on the rows, Board members on the columns, each cell showing that member's current vote (or a dash if they have not voted). Filter by tier and click **Review** on a row to open the application.

On the detail page you see the applicant's profile, their motivation, and the votes cast so far. Vote options are **Yay**, **Maybe**, **No**, and **Abstain**. You can add a note and change your vote at any time until the application is finalized. Each Board member gets exactly one vote per application. Admins can view but do not cast individual Board votes (the vote form is gated by the `BoardOnly` policy).

### Finalize the decision

The system does not count votes for you — this is a consensus model. The Finalize form on the detail page is shown only to **Admin** users; Board members coordinate the decision in their meeting and ask an Admin to record it. (The underlying Finalize endpoint will accept submissions from any Board member or Admin, but only Admin sees the form in the UI today.) Finalization requires at least one Board vote to have been cast on the application.

On the detail page, fill in the **meeting date** (required) and a **decision note**, then choose **Approve** or **Reject**. The decision note is required for rejections and optional for approvals.

On **Approve**, the applicant's tier is updated on their profile, their term expiry is set to December 31 of the next appropriate odd year (at least two years out), and they are added to the Colaboradors or Asociados system team. An approval email and an in-app notification are sent. On **Reject**, the applicant stays at their current tier and receives a rejection email plus an in-app notification with the decision note.

Either way, finalization immediately **deletes all individual Board vote records** for that application. Only the collective decision — final status, meeting date, and decision note — is retained, per GDPR data minimization. Finalization is not reversible.

### Assign and revoke coordinator and admin roles

Role assignments live on each human's detail page under the Admin area (`/Profile/{id}/Admin`). Every assignment is temporal — it has a **valid from** date and an optional **valid to** date, and every change is audited. You can also browse all current and historical role assignments at `/Governance/Roles`.

- **Admin** can assign and revoke any role, including Admin itself.
- **Board** can assign and revoke any role **except** Admin.
- **HumanAdmin** can assign and revoke any role **except** Admin (same surface as Board for role management).
- The full set of roles Board and HumanAdmin can manage is `RoleNames.BoardManageableRoles`: Board, HumanAdmin, TeamsAdmin, CampAdmin, TicketAdmin, NoInfoAdmin, FeedbackAdmin, FinanceAdmin, ConsentCoordinator, VolunteerCoordinator.
- Coordinator roles (Consent Coordinator, Volunteer Coordinator) are assigned here too; what those coordinators actually do is described in [LegalAndConsent.md](LegalAndConsent.md) and [Onboarding.md](Onboarding.md).

To end a role, set the **valid to** date. Historical assignments remain on the profile for the audit trail.

### See the governance audit

Application state history, past and present role assignments, and the collective decision notes on finalized applications are visible on the application detail and human detail pages. Individual Board votes are not retained after finalization — this is deliberate.

## Related sections

- [Profiles](Profiles.md) — [membership tier](Glossary.md#membership-tier) lives on the profile and is updated automatically on approval. (It is not auto-reset on term expiry — only the system-team membership changes then.)
- [Legal and Consent](LegalAndConsent.md) — the Consent Coordinator safety check for Volunteer access, independent of Board voting.
- [Onboarding](Onboarding.md) — how a new human becomes a Volunteer. Tier applications do not replace or block this.
- [Teams](Teams.md) — the Colaboradors and Asociados system teams that approved applicants join.
