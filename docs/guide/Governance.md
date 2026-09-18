<!-- freshness:triggers
  src/Sections/Humans.Governance/Views/**
  src/Sections/Humans.Governance/Controllers/GovernanceApplicationsController.cs
  src/Sections/Humans.Governance/Controllers/GovernanceBoardVotingController.cs
  src/Sections/Humans.Governance/Controllers/GovernanceController.cs
  src/Sections/Humans.Governance/Controllers/GovernanceVotesController.cs
  src/Sections/Humans.Governance/Controllers/GovernanceVotesAdminController.cs
  src/Sections/Humans.Governance/Services/**
  src/Sections/Humans.Auth/Services/RoleAssignmentService.cs
  src/Sections/Humans.Governance/Domain/Application.cs
  src/Sections/Humans.Governance/Domain/ApplicationStateHistory.cs
  src/Sections/Humans.Governance/Domain/BoardVote.cs
  src/Sections/Humans.Governance/Domain/AssemblyVote.cs
  src/Sections/Humans.Governance/Domain/AssemblyBallot.cs
  src/Sections/Humans.Auth/Domain/RoleAssignment.cs
  src/Humans.Base/Constants/RoleNames.cs
  src/Humans.Base/Constants/RoleGroups.cs
  src/Humans.Base/Constants/SystemTeamIds.cs
-->
<!-- freshness:flag-on-change
  Tier application workflow (Submitted/Approved/Rejected/Withdrawn), Board voting dashboard, finalization, term expiry, and role-assignment management. Assembly votes: who is on the roster, what the ballot page shows while a vote is open, when the result becomes visible, and who may open, stop, extend, cancel or peek. Review when governance views, services, entities, or role constants change.
-->

# Governance

## What this section is for

Governance handles three things. **Tier applications** — applying to become a [**Colaborador**](Glossary.md#colaborador) or [**Asociado**](Glossary.md#asociado) — together with the [**Board vote**](Glossary.md#board-vote) that decides them. **Assembly votes** — the binding votes the Asociados cast on a motion, in or around a General Assembly. And the **[coordinator](Glossary.md#coordinator) and admin [role assignments](Glossary.md#role-assignment)** that track who can do what. It is **not** how you become a [Volunteer](Glossary.md#volunteer). Volunteer access is a separate, parallel path handled through profile setup and consent — see [Onboarding.md](Onboarding.md) for that flow. Tier applications never block Volunteer access, and Volunteer access never depends on a [Board](Glossary.md#board) decision.

Both tiers run on synchronized 2-year terms that expire on December 31 of the current cycle's odd year (2027, 2029, ...), so a term granted mid-cycle is shorter than two years. An approval from October of an odd year onward, the renewal window, runs to the end of the next cycle. Terms, votes, and role assignments all leave an audit trail on your profile and on the human detail page.

![TODO: screenshot — the Board Voting dashboard: applications as rows, Board members as columns, each cell showing an individual vote, with Review/Finalize actions on the right]

## Key pages at a glance

- `/Governance/Applications/Create` — submit a Colaborador or Asociado application.
- `/Governance/Applications` — your application status and history.
- `/Governance/Applications/Admin` — admin list of all applications, filterable by status and tier (Board and Admin).
- `/Governance/Applications/Admin/{id}` — admin detail view of a single application (Board and Admin).
- `/Governance/BoardVoting` — Board voting dashboard (Board and Admin).
- `/Governance/BoardVoting/{id}` — application detail and vote form (Board and Admin); the Finalize form is rendered only for Admin.
- `/Governance/Votes` — every assembly vote that has been opened, open ones first.
- `/Governance/Votes/{id}` — the motion, the live participation numbers, and your ballot if you are on the roster.
- `/Governance/Votes/{id}/Results` — the result, once the vote has closed. `Results.csv` is the same numbers as a download.
- `/Governance/Votes/Admin` — every vote in every state, including drafts (Board and Admin).
- `/Users/Admin/Roles` — paginated list of all role assignments, filterable by role (Human Admin, Board or Admin).
- `/Users/Admin/{id}/Roles/Add` and `/Users/Admin/{id}/Roles/{roleId}/End` — assign and end role assignments on a specific human (Board, HumanAdmin, and Admin).

## As a Volunteer

### Apply for Colaborador or Asociado

As an active Volunteer you can apply for **Colaborador** (active contributor with project and event responsibilities) or **Asociado** (voting member with governance rights). If you are already a Colaborador, you can apply to upgrade to Asociado. Both require a Board vote and grant a 2-year term on approval.

Go to `/Governance/Applications/Create`, pick the tier, and fill in a **motivation** (required) and any **additional info** for the Board (optional). Your current tier and access stay the same while the Board reviews. While one application is pending you cannot submit another, of any tier.

If you applied inline during initial signup, that form was a one-shot. After onboarding, `/Governance/Applications/Create` is the only way to apply.

### See your application status

Your dashboard and `/Governance/Applications` show the status of any application you have submitted:

- **Submitted** — waiting on the Board.
- **Approved** — tier granted. You will see the term expiry date, Board meeting date, and decision note.
- **Rejected** — the Board declined. The decision note explains why.
- **Withdrawn** — you cancelled it.

The state history on the detail page records each change with a timestamp.

### Withdraw an application

While your application is still **Submitted**, you can withdraw it from the application detail page. Withdrawing is recorded in the state history and lets you submit a new application for the same tier later.

### Renew your tier

About 90 days before your term expires, a renewal reminder email and in-app notification go out, and a reminder appears on your dashboard. A renewal creates a new application for the same tier and goes through the normal Board vote. Board and Admin see the same upcoming expirations on the Board voting dashboard, so renewals can be prompted or processed proactively. If you do not renew before the term ends, the next hourly system-team sync removes you from the Colaboradors or Asociados system team, so you lose the access tied to that membership. Your profile's tier label is updated at the same time — back to another tier you still hold, or to Volunteer if you hold none. Volunteer access is unaffected.

### Vote in an assembly vote

Assembly votes are the association's binding decisions. They are the Asociados' to make, but the Board can invite Colaboradores and sometimes every active Volunteer to cast an *indicative* ballot alongside them, and the list of votes is visible to every logged-in member either way.

When the Board opens a vote, everyone entitled to take part gets an email in their own language with the motion's title, the closing time, whether their ballot is official or indicative, and a link to the ballot page. An open vote also shows up in your things-to-do list and on your dashboard until you have voted.

Who is entitled is decided **at the moment the vote opens**, and never changes afterwards. Every current Asociado and every active Board member is on that roster with an **official** ballot — Board members count as Asociados here whatever their profile tier says. Depending on the motion, Colaboradores and sometimes all active Volunteers are invited too, with an **indicative** ballot that is labelled as such everywhere it appears and is never counted in the official result. If your tier changes while the vote is running, your place on the roster does not.

The motion is written in all six languages. One of them is marked as the **official** text — that is the binding wording, and the others are labelled as translations of it.

A vote is either a straight **yes / no / abstain**, or a **ranked choice** where you put the options in your order of preference. Either way you can come back and change your ballot as many times as you like until the vote closes; the page shows you your own history of changes. Abstaining counts toward turnout but is neither for nor against.

While the vote is open, the page shows everyone how many people are on the roster, how many have voted, the turnout, how many ballots have been changed, and the closing time. It shows **nothing about how anyone voted** — no running tally, no partial rounds, no export. That stays true until the vote closes, for everybody.

Votes close at their announced time. If the Board extends a vote, the deadline only ever moves **later**, never earlier.

### See a result

Once the vote is closed, `/Governance/Votes/{id}/Results` shows the outcome: the tally or the round-by-round ranked count, whether the motion passed under its required majority (simple, or two thirds), and an **acta block** the Secretary can paste straight into the minutes. `Results.csv` is the same figures as a download. The result is computed once when the vote closes and stored — the page shows you that stored result, it does not recount each time you open it.

If a yes/no vote comes out level, the system says so and stops there: under the statutes the President's casting vote decides and the Secretary records it in the acta. Nothing in Humans breaks that tie for you.

Whether individual names appear beside the ballots depends on what the Board chose for that motion when they drafted it.

## As a Board member / Admin

### Run an assembly vote

Drafting is at `/Governance/Votes/Admin` — Board and Admin can create, edit and delete drafts. A draft carries the title and text in each of the six languages, which of them is official, an optional information link, whether it is yes/no or ranked choice, the options for a ranked vote, the majority it needs, who gets an indicative ballot, whether names are shown beside ballots, and the closing time.

**Opening a vote is Admin-only, and it is not reversible.** Opening freezes the roster, mails everybody on it, and writes an audit entry. From then on the motion text cannot be edited. Admin can **stop** a vote early, **extend** its deadline (later only), or **cancel** it with a recorded reason — all audited, all Admin-only. Board members draft and read; they do not open, stop, extend or cancel.

An Admin who genuinely needs the tally before the close can **peek** at `/Governance/Votes/Admin/{id}/Peek`. Every peek is recorded and shown on the results page afterwards, so the association can see who looked early.

If nobody stops a vote, an hourly job closes it at its announced deadline and audits that it did so, and a reminder goes out 24 hours before closing to everyone who has not voted yet. After a vote closes, Board and Admin can see the per-member ballots at `/Governance/Votes/Admin/{id}/Ballots`; that page is audited too.

### Vote on tier applications

Open `/Governance/BoardVoting`. The dashboard is a spreadsheet: applications on the rows, Board members on the columns, each cell showing that member's current vote (or a dash if they have not voted). Click **Review** on a row to open the application.

On the detail page you see the applicant's profile, their motivation, and the votes cast so far. Vote options are **Yay**, **Maybe**, **No**, and **Abstain**. You can add a note and change your vote at any time until the application is finalized. Each Board member gets exactly one vote per application. Admins can view but do not cast individual Board votes (the vote form is gated by the `BoardOnly` policy).

### Finalize the decision

The system does not count votes for you — this is a consensus model. The Finalize form on the detail page is shown only to **Admin** users; Board members coordinate the decision in their meeting and ask an Admin to record it. Only Admin can submit the Finalize form. Finalization requires at least one Board vote to have been cast on the application.

On the detail page, fill in the **meeting date** (required) and a **decision note**, then choose **Approve** or **Reject**. The decision note is required for rejections and optional for approvals.

On **Approve**, the applicant's tier is updated on their profile, their term expiry is set to December 31 of the current cycle's odd year, and they are added to the Colaboradors or Asociados system team. An approval email and an in-app notification are sent. On **Reject**, the applicant stays at their current tier and receives a rejection email plus an in-app notification with the decision note.

Either way, finalization immediately **deletes all individual Board vote records** for that application. Only the collective decision — final status, meeting date, and decision note — is retained, per GDPR data minimization. Finalization is not reversible.

### Assign and revoke coordinator and admin roles

Role assignments live on each human's detail page under the Admin area (`/Users/Admin/{id}`). Every assignment is temporal — it has a **valid from** date and an optional **valid to** date, and every change is audited. You can also browse all current and historical role assignments at `/Users/Admin/Roles`.

- **Admin** can assign and revoke any role, including Admin itself.
- **Board** can assign and revoke any role **except** Admin.
- **HumanAdmin** can assign and revoke any role **except** Admin (same surface as Board for role management).
- The full set of roles Board and HumanAdmin can manage is `RoleNames.BoardManageableRoles`: Board, HumanAdmin, TeamsAdmin, CampAdmin, TicketAdmin, NoInfoAdmin, FeedbackAdmin, FinanceAdmin, EventsAdmin, StoreAdmin, CantinaAdmin, EETeamAdmin, RideshareAdmin, ConsentCoordinator, VolunteerCoordinator.
- Coordinator roles (Consent Coordinator, Volunteer Coordinator) are assigned here too; what those coordinators actually do is described in [LegalAndConsent.md](LegalAndConsent.md) and [Onboarding.md](Onboarding.md).

To end a role, set the **valid to** date. Historical assignments remain on the profile for the audit trail.

### See the governance audit

Application state history, past and present role assignments, and the collective decision notes on finalized applications are visible on the application detail and human detail pages. Individual Board votes are not retained after finalization — this is deliberate.

## Related sections

- [Profiles](Profiles.md) — [membership tier](Glossary.md#membership-tier) lives on the profile and is updated automatically on approval, and again on term expiry (down to another still-active tier, or Volunteer).
- [Legal and Consent](LegalAndConsent.md) — the consent signing flow, independent of Board voting.
- [Onboarding](Onboarding.md) — how a new human becomes a Volunteer. Tier applications do not replace or block this.
- [Teams](Teams.md) — the Colaboradors and Asociados system teams that approved applicants join.
