# Workgroups — Authorization

| Controller | Scope | Roles | Source |
|---|---|---|---|
| `WorkgroupsController` | Class | Any active human | `PolicyNames.AppAccess` (register, group page, join/leave, member work, documents, comments) |
| `WorkgroupsAdminController` | Class | Board, Admin | `PolicyNames.BoardOrAdmin` (`/Workgroups/Admin/*` — queue, register/refer/refuse/withdraw/close/reactivate, coordinator override, bootstrapping, disposition, settings) |

`WorkgroupsController` additionally gates member-work actions (edit register, meetings,
log, documents, comments-management, coordinator handover, done) on membership in the
service layer: a group must be `Active` (`RequireAcceptsMemberWork`) and the actor a
current member, or `BoardOrAdmin`. The controller's own `MayDoMemberWork` check
(`workgroup.AcceptsMemberWork() && (workgroup.IsMember(userId) || IsBoardOrAdmin())`) is
the page-level copy of this rule, used to 403/404 before rendering a form; the service
enforces it again independently and is authoritative. Reading the register, a group page,
and a Published/Delivered document requires only `AppAccess`; a Draft document additionally
requires membership or `BoardOrAdmin` (checked in `WorkgroupsController.Document`).
Adding a comment (`AddCommentAsync`) requires no membership at all — only an open comment
window gates it, per any signed-in human.

## Resource-based handler

`WorkgroupAuthorizationHandler` + `WorkgroupOperationRequirement` (Read, Member,
Administer) exist per design-rules §11 and are registered in DI
(`services.AddScoped<IAuthorizationHandler, WorkgroupAuthorizationHandler>()`), resolving
against a `WorkgroupInfo` resource:

- **Read** — any authenticated human.
- **Administer** — `RoleNames.Admin` or `RoleNames.Board`.
- **Member** — a current member (`resource.IsMember(userId)`) of a group that
  `AcceptsMemberWork()` (`Status == Active`); a Dormant (or not-yet-Active) group denies
  the Member operation to everyone, coordinators included.

As of this doc, no controller or view calls `IAuthorizationService.AuthorizeAsync` with
this requirement — `Views/` under Workgroups is still empty stubs, and the controller's
manual `MayDoMemberWork`/`IsBoardOrAdmin` checks cover the same ground today. The handler
is registered and ready for when views need the resource-based check (e.g. to hide
buttons a Dormant group would 403 on); it is not dead code to remove, but it is not yet
load-bearing.

## Negative cases

- Anonymous requests **cannot** reach any `/Workgroups*` route — no public access.
- A non-member **cannot** read a Draft document, post a log entry, create a meeting, edit
  register fields, or reach any document-mutation route (design §5).
- A member **cannot** register, refer, refuse, withdraw, close, reactivate, or record a
  disposition — those are `BoardOrAdmin` only, and the service throws
  `WorkgroupRuleException`/`UnauthorizedAccessException` if attempted.
- Nobody **cannot** comment outside an open comment window, or on a Draft.
- A Dormant group **cannot** accept any member mutation (log, meetings, documents,
  comments, register edits) from anyone but `BoardOrAdmin` — `RequireAcceptsMemberWork`
  throws `WorkgroupErrorKeys.Frozen` for every member write once `Status != Active`.
- A member **cannot** leave as the last coordinator without naming a replacement
  (`WorkgroupErrorKeys.LastCoordinatorNeedsReplacement`); only `BoardOrAdmin` may leave a
  group coordinatorless.
- A non-`BoardOrAdmin` human **cannot** reach `/Workgroups/Admin/*` — the class-level
  `BoardOrAdmin` policy refuses before any action runs.
