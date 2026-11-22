# Tasks section: design

**Status: agreed design, nothing built.** Worked out by Peter and Claude on 22 Nov 2026, starting from Cupcake's first-version plan (`docs/superpowers/specs/2026-11-21-tasks-module-mvp-plan.md` on `DustyCupcake/Humans`, branch `tasks-mvp`). Cupcake builds it. Where this doc and that plan differ, this doc wins.

## For Cupcake

Your plan was the right size and most of it is still here. Two things changed around it.

First, main moved. Roles and Permissions landed right after you checked, so the permission parts of the plan stood on things that are being retired. This doc lives on main on purpose: to read it you have to update the fork, and you need the update anyway (list below).

Second, Peter and Claude went through the design question by question. What changed from your plan:

| Your plan | Now | Why |
|---|---|---|
| Production holds a temporary global role, recorded as debt | `TasksAdmin`, a normal system role, plus one permission `Tasks.Manage` on teams | This is how every section does it now. Nothing temporary, no debt rows |
| Coordinator checked with `IsUserCoordinatorOfTeamAsync` | `IPermissionService` with `Tasks.Manage` | That Teams method is being retired |
| Optional team | Every task belongs to a team | A task nobody owns has nobody to manage it or to get it filled |
| Extra rights on "tasks they created", and a production lock | Neither. The team that holds the task manages it | No rule ties a task to the person who made it |
| "Places", "openness", "criticality" | Slots, needs approval, priority | Places read as locations. Screen wording is yours, code wording is this |
| Critical tasks need confirmation by default | A plain switch per task, off by default, with an optional reason | Approval is about skill and experience, not priority |
| Stored status "blocked" and a computed blocked | Stored **Blocked** is picked by hand with a note. The computed one is **Waiting on** | They are different things |
| Warn, then add a place when over the limit | A manager can simply overfill | Less ceremony |
| Tasks per year | Tasks belong to an event, or to none | Other instances run several events a year |
| Fixed dates | Each date is fixed or relative to the event | Relative dates are what makes a plan copyable to the next event |
| Bulk create through Backdoor and an import page | Backdoor only | Everyone has an agent. The page comes if someone needs it |
| Get involved links to `/Teams/Roster` | The open-roles board is `/Roles` | Roles moved |
| Team slot modelled on `ICampPart` | Copy `ITeamRolesPart` | One named interface per contributor is the Teams pattern |
| Core PR includes the timeline | Timeline is the last PR | Core stays its own world first |

### Update the fork first

Your branch starts from #2502. What landed since that matters here:

1. **Permissions section** (#2527): permissions on owners, declared by each section. Shifts and Budget already check through it (#2559, #2562). Read how Shifts declares `Shifts.Manage` in its `Section.cs`.
2. **Roles section** (#2531, #2536, #2551): roles on owners, the open-roles board at `/Roles`, the roles card on the team page, a Roles admin page. Tasks is built the same way and should feel the same.
3. **Teams answers Permissions for today's coordinators** (#2553), a temporary bridge while departments re-create their roles.
4. **Design direction** (#2532, #2533): `docs/design/DESIGN.md`, a rule for views, and the `design-page` and `look-check` skills.
5. **Demo world** (#2552, #2554): every section with data ships a demo story.
6. **AGENTS.md** gained two checks from 4 and 5: the look, and demo data.
7. **Governance took over** the Asociados, Colaboradores and Board groups (#2510). They are no longer teams.
8. **`role_assignments` is now `auth_role_assignments`** (#2403). The free name belongs to Roles.
9. **Account merges are self-service** (#2492), and Users things are keyed by user id, not profile id (#2522, #2524, #2526).
10. **A glossary grew in `CONTEXT.md`** (#2571). The Tasks words below go there in your first PR.

## The design

### Three kinds of work

| | Role | Task | Shift |
|---|---|---|---|
| What | A standing responsibility | A piece of work with an end | Being somewhere for a time slot |
| Ends when | It is handed over | It is done | The slot passes |
| Lives in | Roles | Tasks | Shifts |

Tasks is its own section, `Humans.Tasks`, with its own tables. It is built like Roles: things that hang on a team, managed by whoever holds a permission on that team.

### Words

Code uses these words. What the screen says is Cupcake's choice, except where noted.

| Word | Meaning |
|---|---|
| **Task** | A piece of work with an end. In code the type is `TaskItem`, because `Task` is taken by .NET |
| **Team** | What a task belongs to. The screen never says "owner", which reads as a person |
| **Slot** | One person needed. A task with 4 slots needs 4 people |
| **Assign / unassign** | A manager puts a person on a task, or takes them off |
| **Claim** | A person takes a free slot themselves. The button says something like "I'll do this" |
| **Release** | A person gives their own slot back |
| **Needs approval** | A claim on this task waits for a manager's yes. `NeedsApproval` in code |
| **Request** | A claim that is waiting for that yes |
| **Priority** | How much the task matters. Same levels as roles. Not "criticality" |
| **Blocked** | A status picked by hand: the task waits on something outside, such as a permit |
| **Waiting on** | Shown by itself when a task this one depends on is not done |
| **Manager** | Anyone holding `Tasks.Manage` on the task's team |

### What a task holds

Tables: `task_items`, `task_assignments`, `task_dependencies`, in Tasks' own context. No other section reads them.

A task has:

- **Title** and a Markdown **description**, shown with the shared Markdown renderer.
- **Team**: stored as owner kind plus id (`Owner` in Base). `Team` is the only kind in the first version. Required. There are no tasks without one.
- **Event**: optional. It is the event record in Settings, the same one shift rotas point at. A task without an event is year-round work.
- **Start** and **due**: each optional. Each is either a fixed date, or a number of days before or after the event's gate opening day (day 0, the same day shifts count from). Relative dates need an event.
- **Status**: to do, in progress, blocked, done, cancelled. Blocked carries a required note saying what it waits on.
- **Priority**: the levels roles use (`SlotPriority`).
- **Slots**: how many people it needs. 1 by default.
- **Needs approval**: off by default. When on, an optional free-text reason ("chainsaw licence") shown beside the claim button.
- **Hidden**: off by default.

An assignment is a task, a person, and whether it is a request or confirmed. A dependency is a link from one task to the task it waits on.

No "created by" field drives any rule. Who did what is in the audit log, which is also the task's history. There is no second history table.

### Who can do what

| Who | Can |
|---|---|
| Any signed-in member | See every task that is not hidden. Claim a free slot. Release their own slot |
| A person assigned to a task | Change its status, including marking it done or blocked |
| A manager (`Tasks.Manage` on the team) | Everything on that team's tasks: create, edit, assign, unassign, answer requests, set dependencies, hide, cancel, delete |
| `TasksAdmin` | The same on every team |
| Board | See everything, change nothing |
| Admin | Everything |

`Tasks.Manage` is declared by Tasks through `ISectionPermissions`, on owner kind `Team`, with `TasksAdmin` as its admin role. A team's coordinator holds every permission on the team, so coordinators manage their team's tasks with no setup. A coordinator can also put `Tasks.Manage` on another role. All checks go through `IPermissionService`.

Each team manages its own tasks. Production's tasks sit on production's team. No team can lock another team's tasks.

### Claiming and approval

- A member sees the claim button while a task has a free slot. When the slots are full, the button is gone.
- On a task that does not need approval, claiming assigns the person at once.
- On a task that needs approval, claiming makes a request. A manager says yes or no. A no can carry an optional reason.
- The person gets an email on a yes and on a no, because the answer can come days later.
- Waiting requests show in the manager's to-do list on Home.
- A manager can assign more people than there are slots. It shows as 5 of 4. Nothing warns and nothing changes the slot count.
- Taking a task does not make anyone a member of the team.

### Dependencies

- A dependency says "this task waits on that one". The two tasks can belong to different teams.
- The manager of the waiting task adds and removes the link. The other team does nothing.
- Dependencies are shown, never enforced. A task that is waiting can still be started and marked done.
- A link that would make a circle is refused.

### Hidden

A hidden task is not shown to ordinary members. Its team's managers, `TasksAdmin`, Board and Admin see it as normal.

### Cancel and delete

- **Cancel**: we are not doing it this time. The task stays, marked cancelled, and is still there to be copied to the next event.
- **Delete**: the task is gone and will not be copied. The audit log keeps what happened.

### Pages

| Route | What | Who |
|---|---|---|
| `/Tasks` | All tasks, with filters | Signed-in members |
| `/Tasks/Team/{slug}` | One team's tasks, and the add form for managers | Signed-in members |
| `/Tasks/{id}` | One task: description, dates, people, what it waits on | Anyone who can see the task |
| `/Tasks/Admin` | One row per team: tasks, open slots, blocked tasks. Below it, every blocked task with its note. In the admin sidebar. English only | `TasksAdmin`, Board, Admin |
| `/Tasks/Open` | The open-tasks board: tasks with free slots, most needed first, claim on each row. Works like the `/Roles` board | Signed-in members |
| `/Tasks/Timeline` | Tasks over time with their dependencies, and the dependency graph | Signed-in members |

Tables use the shared table component. Pages follow `docs/design/DESIGN.md`. There is one "Tasks" link in the main nav and no search box: the header search is the only search.

### Bulk load

One Backdoor endpoint takes a whole plan, so an agent can load it with its person's key.

- The plan is a list of tasks. Each has a key that only means something inside the plan, and dependencies name those keys, so no ids are needed up front. Each task names its team by slug.
- All or nothing. The whole plan is checked first and the answer lists every problem. A dry-run flag checks without creating.
- The caller needs `Tasks.Manage` on every team in the plan.
- The rules live in the Tasks service. Backdoor gets one controller that calls it, like its Store and Surveys controllers.

### How Tasks touches other sections

| Section | What |
|---|---|
| Permissions | Tasks declares `Tasks.Manage` and asks `IPermissionService` |
| Auth | Tasks declares the `TasksAdmin` system role |
| Teams | Tasks reads team names, slugs and links from `ITeamService`. Later, Teams gets one named slot for a tasks card on the team page, copied from `ITeamRolesPart` |
| Settings | Tasks reads the event record for names and gate opening dates |
| Email | The yes and no emails |
| Backdoor | The bulk-load controller |
| Home | Waiting requests in the to-do list |
| Users, Gdpr, AuditLog | Names, export and erasure for assignments, merge, audit entries for every change |

Coming, and not Cupcake's to build: the list of names and links per owner will move to one shared place that Roles and Tasks both read. It only matters when tasks can belong to something other than a team. Until then, read Teams directly.

### PR order

1. **Tasks as its own world.** The section, tables, `/Tasks`, the team page, the task page, managers create, edit, assign and set dependencies, `/Tasks/Admin`, the `TasksAdmin` role and `Tasks.Manage` permission, audit, GDPR, demo story, seven cultures, the glossary words in `CONTEXT.md`. Nothing in another section changes except the project wiring.
2. **Bulk load** through Backdoor.
3. **Claiming**: claim, release, requests, the two emails, the Home to-do entry, the open-tasks board.
4. **Tasks card on the team page.**
5. **Timeline and dependency graph.**

Fork PRs get no preview deploy. Peter deploys each one by hand when asked.

### Not in the first version

Tasks on workgroups, camps or the organisation itself. Copying a plan to the next event. Offering team membership to someone who took a task. One combined board of roles and tasks. A paste-in import page. Sub-tasks and projects. Tasks that grant permissions. Tasks in the header search.
