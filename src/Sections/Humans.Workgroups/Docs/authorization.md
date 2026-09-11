# Workgroups — Authorization

`WorkgroupsController` requires `AppAccess`. `WorkgroupsAdminController` requires
`BoardOrAdmin` for registration, referral, refusal, withdrawal, closing, reactivation,
coordinator overrides, dispositions, bootstrapping and settings.

`WorkgroupAuthorizationHandler` is called by the member controller before member-only
operations and when building page permissions. Its Member requirement allows current
members and Board/Admin on Active groups. Other statuses reject member work. Services
independently enforce lifecycle and document rules; they do not authorize the caller's role.

- Non-members cannot edit a group, its meetings, log, documents or comment responses.
- Draft documents require membership or Board/Admin; other documents require AppAccess.
- Nested meeting, log, document and comment IDs must belong to the route's group.
  A mismatch returns 404 before mutation, including form submissions.
- Join, leave, status requests and posting comments do not require the Member operation.
  Their service rules enforce Active status, membership when leaving, the last-coordinator
  replacement, request cooldown and the comment window.
- Only Board/Admin can reach admin actions. Anonymous callers cannot reach any route.
- Page controls use the same Member authorization result as POST actions.

Tests: `WorkgroupAuthorizationHandlerTests` and `WorkgroupsControllerAuthorizationTests`.
