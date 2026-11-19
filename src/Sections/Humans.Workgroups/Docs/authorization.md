<!-- freshness:triggers
  src/Sections/Humans.Workgroups/**
  tests/Humans.Workgroups.Tests/**
  src/Sections/Humans.Auth/Contracts/IRoleAssignmentService.cs
  src/Sections/Humans.Teams.Contracts/ITeamService.cs
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
- Join, leave and posting comments do not require the Member operation.
  Their service rules enforce Active status, membership when leaving, the last-coordinator
  replacement and the comment window.
- Requesting a status update requires the `BoardOrAdmin` policy; anyone else gets 403 and
  the page hides the form. It is rate-limited by nothing.
- Join is a request. Approve, Decline and Add (`ManageMembersAsync`) use the handler's
  ManageMembership requirement: a current coordinator of an Active group or Board/Admin.
  A plain member gets 403. Services retain Active-group and request/target validation,
  without receiving privilege flags or re-checking the caller's role.
- Member coordinator handover (`SetCoordinatorsAsync`) requires an Active group. The existing
  Board/Admin route calls `AppointCoordinatorsAsync` and may appoint on frozen groups.
  Both commands share membership/count validation and persistence; authorization stays at the routes.
- A sensitive group's roster, documents, meetings and history require membership or
  Board/Admin; the Document route returns 404 to everyone else, and `AddCommentAsync`
  refuses non-members.
- Only Board/Admin can reach admin actions. Anonymous callers cannot reach any route.
- A member or coordinator cannot set the budget; the route lives on the admin controller.
- Page controls use the same Member and ManageMembership authorization results as POST actions.

Tests: `WorkgroupAuthorizationHandlerTests` and `WorkgroupsControllerAuthorizationTests`.
