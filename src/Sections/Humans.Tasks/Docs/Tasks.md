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
- The **listed** tasks are the active event's plus event-less ones, minus done and cancelled. The list filters (any event, one event, include finished) are a `TaskListScope` on `ITaskService.GetListedTasksAsync`, so the rule lives only in the service.

## Pages

| Route | What | Who |
|---|---|---|
| `GET /Tasks` | All tasks the viewer may see, filters; listed tasks by default. Links a manager to each team they manage. In the main nav. | Signed-in members |
| `GET /Tasks/Team/{slug}` | A team's tasks; add form for managers, starting on the active event | Signed-in members |
| `GET /Tasks/{id}` | One task: description, dates, people, waiting-on, the viewer's claim state; managers edit, assign, link, delete and see the requests card | Anyone who can see it; 404 otherwise |
| `GET /Tasks/Open` | The open-tasks board: every listed task the viewer can see with a free slot, most needed first, then title; open-slot and critical tiles; each row shows the viewer's place (claim button, waiting, or on it). Linked from `/Tasks` and team pages | Signed-in members |
| `POST /Tasks/{id}/Claim`, `/Release`, `/Withdraw` | The caller's own claim (or request), release, withdrawal. The controller checks only that the task is visible (404 otherwise); the service refuses every state case in words | Anyone who can see the task |
| `POST /Tasks/{id}/Approve`, `/Decline` (`userId`, decline `reason`) | Answer a request | `Tasks.Manage` on the task's team; 403 otherwise, 404 first when hidden |
| `GET /Tasks/Admin` | Read-only per-team counts and every blocked task with its note. The admin sidebar's Tasks group. English only (admin page). | TasksAdmin, Board, Admin (`TasksPolicies.Read`) |

`POST /api/backdoor/tasks/bulk` (Backdoor's key-authed API) loads a whole set of tasks; see Bulk load below. Every claiming POST takes an optional `returnUrl`, followed only when `Url.IsLocalUrl` accepts it (else back to the task page). The team-page card and the timeline arrive in later PRs.

## Bulk load

`ITaskWrite.BulkLoadAsync` (`Services/TaskService.BulkLoad.cs`) is the one public write. It takes a `BulkLoadRequest`: a list of tasks and a dry-run flag.

- A task carries a payload-local **key**, its **team id**, title, description, optional **event id**, optional start and due, priority, slot count, needs-approval and reason, hidden, and **depends-on** keys. No people, no status: every task starts as to do with nobody on it.
- A date is exactly one of `fixed` (a calendar date), `day` (days from the event's gate opening day) or `relativeDate` (a calendar date Tasks turns into a day number from the task's event). Both relative kinds need an event, and a `day` must land on a date the form can show (years 1 to 9999).
- Each task is checked against the same rules as a task made on the form (team, field lengths, slots, event, dates, due not before start). Dependencies name keys in the payload only: an unknown key, a task waiting on itself and a circle are problems. A key repeated within one task's list counts once.
- All or nothing. The whole payload is planned first and every problem is returned (`BulkLoadProblem`: position, key, field, message), in plain English whatever the request culture. The dependency check walks with an explicit stack, so a long chain cannot overflow the call stack. Only when there are none are the tasks and links saved, in one repository transaction (`ITaskRepository.AddManyAsync`). A dry run stops before saving and answers without ids.
- Loading the same payload twice creates the tasks twice: there is no identity beyond the key, and the key means nothing outside one payload.
- Audited like normal creates: a `TaskCreated` row per task (its text ends "by bulk load") and a `TaskDependencyAdded` row per link. A refused payload is logged at Warning once and writes no audit rows.
- Auth-free, like the rest of the service. The only caller is Backdoor's `BackdoorTasksController`, which turns each team reference (slug, custom slug, else id) into a team id through `ITeamService` and checks `Tasks.Manage` on it (`IPermissionService`) before calling, so rights are checked on exactly the team the task lands on. A team reference that matches nothing gets the same 403 as a team the caller does not manage, so a key never learns whether a team exists; Tasks still refuses a team id that is unknown, retired or a system team.

## Claiming and requests

`ITaskService` (all internal, auth-free; controllers ask `TasksAccess` first):

- `ClaimAsync(taskId, actor)`: the actor takes a free slot. On a task that does not need approval they are **confirmed** at once; on one that does, a **request** is made. A request takes no slot, and many people may request the same last slot. Refused on a done or cancelled task, when the actor is already on it or already requested it, and when every slot is taken (confirmed people only; a needs-approval task that is full refuses requests too). Two claims at the same moment may both land (no locking), which shows as 5 of 4.
- `ReleaseAsync`: the actor gives back their own **confirmed** place. A request is not released; it is withdrawn.
- `WithdrawRequestAsync`: the actor takes back their own request. Never touches a confirmed place.
- `ApproveRequestAsync(taskId, userId, actor)`: confirms the request. A task that has since filled up is **overfilled**, not refused. Emails the person.
- `DeclineRequestAsync(taskId, userId, reason?, actor)`: deletes the request; the person may ask again. The optional reason (200 characters at most, trimmed, blank counts as none) is carried only into the email and the audit entry; no column holds it. Emails the person.
- Approve and decline need a request row (state Requested) and never remove or change a confirmed place. A manager who assigns someone who already asked is told they are already on the task; approving is the way.
- Claiming, assigning or approving never makes anyone a team member: Tasks calls no write on Teams.
- Needs-approval takes effect only here, at claim time. Turning it off later does not confirm existing requests.
- A manager's unassign removes only a confirmed place; a request is answered with approve or decline, so its email is never skipped.

### Claim states on the pages

`TasksAccess.PlaceOn(task)` gives the viewer's state (`MyPlace`): **CanClaim** shows "I'll do this", or "Ask to join" with the approval reason when the task needs approval (on list rows too); **Requested** shows "waiting for approval" and Withdraw; **Confirmed** shows "you're on this task" and Leave. No state (full, finished, or not visible) shows nothing: no disabled button. The task page has the card; `/Tasks`, team pages and the board have a Join column. Leaving a hidden task goes back to `/Tasks`, since the task may drop out of sight.

### The requests card

The waiting requests (state Requested, `TaskInfo.WaitingRequests`), oldest first, done and cancelled tasks included since they still owe an answer, each with `<vc:human>`, Approve and Decline (optional reason, 200 characters). On the task page and on the team page (`#requests`, with the task named on each row), and only for viewers who manage the task's team: TasksAdmin and Admin through `Tasks.Manage` everywhere. Home's entry counts the same set and links to the team page. The People list and every slot count show confirmed people only.

### The two emails

`TasksEmails` (`Services/TasksEmails.cs`) builds the approved and the declined email, category `VolunteerUpdates`, templates `task_request_approved` and `task_request_declined`, in the recipient's preferred language (English when unsupported), with HTML-encoded substitutions and a link to `/Tasks/{id}`. Copy lives in `TasksResource` under `Tasks_Email_*` in all seven cultures. `TasksEmailPreviews` puts the samples in the gallery at `/Email/EmailPreview`. Email only sends: `TaskService` calls `IEmailService.SendAsync` after the change is saved and audited. After the answer is committed, recipient lookup and outbox handoff finish independently of browser cancellation. Preflight reads and persistence keep the caller token. Missing or tombstoned accounts and blank recipient addresses skip the email with a Warning; the saved answer and its audit remain. A failed send is logged and never undoes the answer. Nothing else in Tasks sends email: claim, release, request and withdraw send none.

### Home

`Section` implements `ISectionThingsToDo`: one entry per team with waiting requests (`TaskInfo.WaitingRequests`), for people who hold `Tasks.Manage` on that team (`IPermissionService.GetOwnersAsync`; that already includes every team for TasksAdmin and Admin, and a team's coordinators). Key `tasks-requests-{teamId}`, linking to `/Tasks/Team/{slug}`. Requests on done, cancelled and hidden tasks are counted: they still owe an answer, and the viewer manages them.

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
| Signed-in human | See tasks that are not effectively hidden; claim a free slot (or ask to join), leave their own place, withdraw their own request |
| Confirmed on a task | Set its status to anything but cancelled; sees it even when hidden |
| Manager: `Tasks.Manage` on the team (inherited down sub-teams; team coordinators hold it) | Create, edit, assign, unassign, answer requests, link, hide, cancel and delete tasks of that team. Moving a task needs `Tasks.Manage` on both teams |
| TasksAdmin, Admin | The manager's rights on every team |
| Board | Sees every task, hidden ones included, and the admin page; changes nothing |

## Invariants

- Every task belongs to an active, non-system team.
- Title is required, at most 200 characters; description 4000; blocked note 500; approval reason 200. Slots are at least 1.
- Blocked needs a note; leaving blocked clears it. The approval reason is dropped when needs-approval is off.
- A due date never precedes the start date (resolved dates).
- A task cannot wait on itself, and a dependency circle is refused.
- A person is on a task at most once (confirmed or requested). Assignment rejects missing or tombstoned accounts and resolves archived ids to the surviving account before checking duplicates, storing the assignment and linking its audit entry.
- Only confirmed people fill slots. Open slots are slots minus confirmed, never below zero; overfilling by claiming races, approving or assigning is allowed and shows as 5 of 4.
- An event is picked only if it exists and is not withdrawn; a task keeps an event withdrawn later.
- Hidden tasks, and every task of a hidden team, show only to managers of the team, people confirmed on them or asking to join them, TasksAdmin, Board and Admin. A hidden task a viewer cannot see is "a hidden task" in waiting-on lists, without its status.
- A done or cancelled task is never shown as waiting.
- A hidden team's tasks page (`/Tasks/Team/{slug}`) is found only by TasksAdmin, Board, Admin, the team's managers and people confirmed on one of its tasks; 404 for everyone else.
- Services are auth-free: controllers build `TasksAccess` (`Authorization/TasksAccess.cs`) from `IPermissionService` and the `TasksPolicies.Read` policy, and check it before every write and filter every read. Tests: `tests/Humans.Tasks.Tests/TasksAccessTests.cs`.
- Every create (bulk or form), update, status change, delete, assign, unassign, claim, release, request, withdrawn request, approval, decline and dependency change is audited (`TasksAuditActions`, entity type `TaskItem`; claim, release, request, withdraw, approve and decline carry the person as the related subject; a decline's reason is in the description). Every refusal is logged at Warning.
- Add/edit forms require explicit priority, slot count, date modes, approval and visibility flags; status changes require an explicit status. Missing required values and malformed fields refuse the write, log a Warning and show the form again with what was typed. Explicit false flags and the None priority remain valid. A refused status change preserves the existing status and blocked note.
- GDPR: export lists the person's tasks with state (Confirmed or Requested) and date; erasure removes their places and requests; a decline reason is stored nowhere but the email and the audit entry; a merge moves them to the survivor, keeping one row, confirmed winning. Task text is not tied to a person and stays.
- Demo story: `Services/Demo/TasksDemoStory.cs`, after Teams.

## Negative Access Rules

- A member **cannot** see a hidden task, or any task of a hidden team, unless they manage its team, are on it or asking to join it, or are TasksAdmin, Board or Admin. `/Tasks/{id}` answers 404.
- A member **cannot** create, edit, assign, unassign, link, hide, cancel or delete tasks without `Tasks.Manage` on the task's team.
- A key holder **cannot** bulk-load a payload that names a team where they lack `Tasks.Manage`, or a team that does not exist: the same 403 for both, and nothing is checked or created, even for the other teams.
- A bulk load **cannot** put people or a status on a task, or link to a task outside the payload.
- A manager **cannot** touch tasks of a parent or sibling team, and **cannot** move a task to a team they do not manage.
- A person confirmed on a task **cannot** cancel it, edit it, or change who is on it.
- A person only requested on a task **cannot** set its status; they still see it when hidden, so they can withdraw.
- A member **cannot** claim a task they cannot see, a done or cancelled task, a full one, or one they are already on or have asked to join (`TasksAccess.CanClaim` for the button, the service for the state rules).
- A member **cannot** approve or decline requests, release someone else's place, or withdraw someone else's request: approve and decline need `Tasks.Manage` on the task's team; release and withdraw act only on the caller's own row.
- A manager **cannot** approve or decline a request that does not exist, and approving or declining never touches a confirmed place.
- The waiting-requests entries on Home appear only for holders of `Tasks.Manage` on the team; nobody else, and not a person for their own request.
- Board, plain members, people confirmed on a task and requesters themselves **cannot** see who has asked to join: the requests card is for the team's managers only, and lists and the board show counts of confirmed people.
- Board **cannot** change any task.
- Only TasksAdmin, Board and Admin **can** open `/Tasks/Admin`; managers cannot. It is read-only.

## Triggers

- A write to a task, its people or its links writes an audit row.
- A claim, release, request, withdrawal, approval or decline writes one audit row; an approval or decline also queues one email to the requester.
- A merge writes a `TaskAssigned` audit row on the survivor when places moved.
- Erasure of a user removes their places and requests; it writes no audit row.

## Cross-Section Dependencies

- **Users**: `IUserService.GetUserInfoAsync`, to check a person exists before assigning, and for the address and language of an answer email.
- **Email**: `IEmailService.SendAsync` and `EmailMessage`/`MessageCategory` from `Humans.Email.Contracts`; `IEmailPreviewContributor` for the gallery.
- **Home**: `ISectionThingsToDo`, the waiting-requests entries.
- **Permissions**: declares `Tasks.Manage` (`ISectionPermissions`, owner kind `Team`); `IPermissionService.GetOwnersAsync` in `TasksAccess`.
- **Teams.Contracts**: `ITeamService` for team names, slugs, hidden and active flags; `TeamOwner`, `SlotPriority`.
- **Backdoor** (consumer): calls `ITaskWrite.BulkLoadAsync` from its bulk-load controller.
- **Settings**: `ISettingsService` for event names, gate opening dates and the active event.
- **AuditLog**: `IAuditLogService.LogAsync`, `IAuditActionContributor`.
- **Gdpr / Users**: `IUserDataContributor` and `IUserMerge`, implemented by `TaskService`.

## Architecture

**Owning services:** `TaskService`
**Owned tables:** `task_items`, `task_assignments`, `task_dependencies`
**Status:** (A) Migrated: new section, 2026-11-22; bulk load; claiming, requests, emails, Home entries and the open-tasks board.

### Public contract

| Public interface | Methods | Notes |
|---|---:|---|
| `TasksPolicies` | — | `Read`: the policy name for `/Tasks/Admin` |
| `TasksSystemRoles`, `TasksPermissions` | — | The `TasksAdmin` role and the `Tasks.Manage` key |
| `ITaskWrite` | 1 | `BulkLoadAsync`, with `BulkLoadRequest`, `BulkLoadTask`, `BulkLoadDate`, `BulkLoadResult`, `BulkLoadProblem`, `BulkLoadedTask`. Consumed by Backdoor only |

Nothing else is public: `ITaskService` (including claim, release, withdraw, approve and decline), `TasksEmails`, its DTOs and the entities are internal. Claiming adds no public surface.

- No caching decorator: low traffic; the dataset loads whole per request.
- `TaskService` is the only repository caller (`ITaskRepository`, `Data/`) and never imports `Microsoft.EntityFrameworkCore`.
- Display stitching: team and event names resolve through `ITeamService` and `ISettingsService`.
- Tests: `tests/Humans.Tasks.Tests/`.
