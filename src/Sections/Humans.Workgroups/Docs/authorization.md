<!-- freshness:triggers
  src/Sections/Humans.Workgroups/**
  tests/Humans.Workgroups.Tests/**
  src/Sections/Humans.Auth/Contracts/IRoleAssignmentServiceRead.cs
  src/Sections/Humans.Teams.Contracts/ITeamServiceRead.cs
  src/Humans.Base/Constants/SystemTeamIds.cs
-->

# Workgroups — Authorization

`WorkgroupsController` requires `AppAccess`. `WorkgroupsAdminController` requires
`BoardOrAdmin` for registration, referral, refusal, withdrawal, closing, reactivation,
coordinator overrides, dispositions, bootstrapping, settings and the budget.

`WorkgroupAuthorizationHandler` is called by the member controller before member-only
operations and when building page permissions. Its Member requirement allows current
members and Board/Admin on Active groups. Other statuses reject member work. Services
independently enforce lifecycle and document rules; they do not authorize the caller's role.

- Non-members cannot edit a group, its meetings, log, documents or comment responses.
- Draft documents require membership or Board/Admin; other documents require AppAccess.
- Meetings and minutes on the group page require AppAccess, regardless of `IsPublic`.
  That flag controls listing in the community calendar, not privacy on the register.
- Nested meeting, log, document and comment IDs must belong to the route's group.
  A mismatch returns 404 before mutation, including form submissions.
- Join, leave, status requests and posting comments do not require the Member operation.
  Their service rules enforce Active status, membership when leaving, the last-coordinator
  replacement and the comment window. A status request is rate-limited by nothing.
- Join is a request. Approve, Decline and Add (`ManageMembersAsync`) require a current
  coordinator of an Active group or Board/Admin; the service re-checks the coordinator role
  unless the controller passed `asAdmin`. A plain member gets 403.
- A sensitive group's roster, documents, meetings and history require membership or
  Board/Admin; the Document route returns 404 to everyone else, and `AddCommentAsync`
  refuses non-members.
- Only Board/Admin can reach admin actions. Anonymous callers cannot reach any route.
- A member or coordinator cannot set the budget; the route lives on the admin controller.
- Page controls use the same Member authorization result as POST actions.

Tests: `WorkgroupAuthorizationHandlerTests` and `WorkgroupsControllerAuthorizationTests`.
