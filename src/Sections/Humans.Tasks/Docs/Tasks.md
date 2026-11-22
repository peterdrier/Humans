<!-- freshness:triggers
  src/Sections/Humans.Tasks/**
-->
<!-- freshness:flag-on-change
  Who sees hidden tasks, who manages (Tasks.Manage on the team, inherited down; TasksAdmin/Admin everywhere; Board read-only), who may set status, and that services are auth-free behind TasksAccess.
-->

# Tasks — Section Invariants

Pieces of work with an end, hanging on a team. A task is to a role (standing responsibility) and a shift (being somewhere at a time) what work-that-gets-done is. Feature spec: [`features/Tasks-feature.md`](features/Tasks-feature.md). Vocabulary: [`CONTEXT.md`](../../../../CONTEXT.md#tasks).

## Concepts

- A **task** has a team (the owner), title, Markdown description, optional event, optional start and due dates, status, priority, slot count, needs-approval flag with reason, and a hidden flag.
- A **date** is fixed (a calendar date) or relative: days from the event's gate opening day, day 0 being opening, negatives before. A relative date needs an event.
- **Status**: to do, in progress, blocked (needs a note), done, cancelled.
- A **slot** is a place for one person. People on a task are **confirmed**, or **requested** (asked to join, no slot taken). Overfilling is allowed.
- **Waiting on**: a link from a task to another it depends on. Shown, never enforced.
- **Effectively hidden**: the task's own flag, or its team being hidden.
- The **listed** tasks are the active event's plus event-less ones, minus done and cancelled.

## Pages

| Route | What | Who |
|---|---|---|
| `GET /Tasks` | All tasks the viewer may see, filters; listed tasks by default. In the main nav. | Signed-in members |
| `GET /Tasks/Team/{slug}` | A team's tasks; add form for managers | Signed-in members |
| `GET /Tasks/{id}` | One task: description, dates, people, waiting-on; managers edit, assign, link, delete | Anyone who can see it; 404 otherwise |
| `GET /Tasks/Admin` | Read-only per-team counts and every blocked task with its note. The admin sidebar's Tasks group. English only (admin page). | TasksAdmin, Board, Admin (`TasksPolicies.Read`) |

Claiming, the open-tasks board, the bulk-load endpoint, the team-page card and the timeline arrive in later PRs.

## Data Model

| Table | Columns |
|---|---|
| `task_items` (`TaskItem`) | id, owner kind, owner id, title, description, event id, start date, start day offset, due date, due day offset, status (string), blocked note, priority (string), slot count, needs approval, approval reason, hidden, created, updated |
| `task_assignments` (`TaskAssignment`) | id, task id, user id, state (Requested/Confirmed), created |
| `task_dependencies` (`TaskDependency`) | id, task id (the waiting one), depends-on task id |

- `(task, user)` is unique on assignments; `(task, depends-on)` unique on dependencies. Assignments and dependencies cascade with the task.
- `event id` is a Settings event record id and `owner id` a team id: no cross-section FK, resolved through `ISettingsService` and `ITeamService`.
- There is no "created by": the audit log is the history.

## Actors & Roles

| Actor | Capabilities |
|-------|--------------|
| Signed-in human | See tasks that are not effectively hidden |
| Confirmed on a task | Set its status to anything but cancelled; sees it even when hidden |
| Manager: `Tasks.Manage` on the team (inherited down sub-teams; team coordinators hold it) | Create, edit, assign, unassign, link, hide, cancel and delete tasks of that team. Moving a task needs `Tasks.Manage` on both teams |
| TasksAdmin, Admin | The manager's rights on every team |
| Board | Sees every task, hidden ones included, and the admin page; changes nothing |

## Invariants

- Every task belongs to an active, non-system team.
- Title is required, at most 200 characters; description 4000; blocked note 500; approval reason 200. Slots are at least 1.
- Blocked needs a note; leaving blocked clears it. The approval reason is dropped when needs-approval is off.
- A due date never precedes the start date (resolved dates).
- A task cannot wait on itself, and a dependency circle is refused.
- A person is on a task at most once. Assigning a requester confirms the request.
- An event is picked only if it exists and is not withdrawn; a task keeps an event withdrawn later.
- Hidden tasks, and every task of a hidden team, show only to managers of the team, people confirmed on them, TasksAdmin, Board and Admin. A hidden task a viewer cannot see is "a hidden task" in waiting-on lists.
- Services are auth-free: controllers build `TasksAccess` (`Authorization/TasksAccess.cs`) from `IPermissionService` and the roles, and check it before every write and filter every read. Tests: `tests/Humans.Tasks.Tests/TasksAccessTests.cs`.
- Every create, update, status change, delete, assign, unassign and dependency change is audited (`TasksAuditActions`, entity type `TaskItem`).
- GDPR: export lists the person's tasks with state and date; erasure removes their places and requests; a merge moves them to the survivor, keeping one row, confirmed winning. Task text is not tied to a person and stays.
- Demo story: `Services/Demo/TasksDemoStory.cs`, after Teams.

## Negative Access Rules

- A member **cannot** see a hidden task, or any task of a hidden team, unless they manage its team, are confirmed on it, or are TasksAdmin, Board or Admin. `/Tasks/{id}` answers 404.
- A member **cannot** create, edit, assign, unassign, link, hide, cancel or delete tasks without `Tasks.Manage` on the task's team.
- A manager **cannot** touch tasks of a parent or sibling team, and **cannot** move a task to a team they do not manage.
- A person confirmed on a task **cannot** cancel it, edit it, or change who is on it.
- A person only requested on a task **cannot** set its status and does not see it if hidden.
- Board **cannot** change any task.
- Only TasksAdmin, Board and Admin **can** open `/Tasks/Admin`; managers cannot. It is read-only.

## Triggers

- A write to a task, its people or its links writes an audit row.
- A merge writes a `TaskAssigned` audit row on the survivor when places moved.
- Erasure of a user removes their places and requests; it writes no audit row.

## Cross-Section Dependencies

- **Auth**: the `TasksAdmin` system role (`ISectionRoles`).
- **Permissions**: declares `Tasks.Manage` (`ISectionPermissions`, owner kind `Team`); `IPermissionService.GetOwnersAsync` in `TasksAccess`.
- **Teams.Contracts**: `ITeamService` for team names, slugs, hidden and active flags; `TeamOwner`, `SlotPriority`.
- **Settings**: `ISettingsService` for event names, gate opening dates and the active event.
- **AuditLog**: `IAuditLogService.LogAsync`, `IAuditActionContributor`.
- **Gdpr / Users**: `IUserDataContributor` and `IUserMerge`, implemented by `TaskService`.

## Architecture

**Owning services:** `TaskService`
**Owned tables:** `task_items`, `task_assignments`, `task_dependencies`
**Status:** (A) Migrated: new section, 2026-11-22 (PR 1 of 5).

### Public contract

| Public interface | Methods | Notes |
|---|---:|---|
| `TasksPolicies` | — | `Read`: the policy name for `/Tasks/Admin` |
| `TasksSystemRoles`, `TasksPermissions` | — | The `TasksAdmin` role and the `Tasks.Manage` key |

Nothing else is public: `ITaskService`, its DTOs and the entities are internal; no other section consumes Tasks yet.

- No caching decorator: low traffic; the dataset loads whole per request.
- `TaskService` is the only repository caller (`ITaskRepository`, `Data/`) and never imports `Microsoft.EntityFrameworkCore`.
- Display stitching: team and event names resolve through `ITeamService` and `ISettingsService`.
- Tests: `tests/Humans.Tasks.Tests/`.
