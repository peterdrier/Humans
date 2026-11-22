# Tasks section: design

**Status: agreed design, nothing built.** Worked out by Peter and Claude on 22 Nov 2026, starting from Cupcake's first-version plan (`docs/superpowers/specs/2026-11-21-tasks-module-mvp-plan.md` on `DustyCupcake/Humans`, branch `tasks-mvp`). Cupcake builds it.

Where this doc and Cupcake's plan differ, this doc wins. Something in Cupcake's plan that this doc does not mention is not agreed: small things (field lengths, list filters) are Cupcake's call, anything else gets asked. The computed "at risk" flag is dropped.

## For Cupcake

Your plan was the right size and most of it is still here. What changed around it:

Main moved. Roles and Permissions landed right after you checked, so the permission parts of your plan stood on things that are on their way out. This doc goes to main ahead of the code on purpose, as a one-off, because you build on a fork: to read it you have to update the fork, and you need the update anyway (list below).

Peter and Claude also went through the design question by question. What changed from your plan:

| Your plan | Now | Why |
|---|---|---|
| Production holds a temporary global role, recorded as debt | `TasksAdmin`, a normal system role, plus one permission `Tasks.Manage` on teams | This is how Shifts and Budget do it now. Nothing temporary, no debt rows |
| Coordinator checked with `IsUserCoordinatorOfTeamAsync` | `IPermissionService` with `Tasks.Manage` | Do not add callers to that Teams method. It goes with the old team roles |
| Optional team | Every task belongs to a team | A task with no team has nobody to manage it or to get it filled |
| Extra rights on "tasks they created", and a production lock | Neither. The task's team manages it | No rule ties a task to the person who made it |
| "Places", "openness", "criticality" | Slots, needs approval, priority | Places read as locations. Screen wording is yours, code wording is this |
| Critical tasks need confirmation by default | A plain switch per task, off by default, with an optional reason | Approval is about skill and experience, not priority |
| Stored status "blocked" and a computed blocked | Stored **Blocked** is picked by hand with a note. The computed one is **Waiting on** | They are different things |
| Warn, then add a place when over the limit | A manager can simply overfill | Less ceremony |
| Tasks per year | Tasks belong to an event, or to none | Other groups running Humans hold several events a year |
| Fixed dates | Each date is fixed or relative to the event | Relative dates are what makes a set of tasks copyable to the next event |
| Bulk create through Backdoor and an import page | Backdoor only | Agents are the easy way in. The page comes if someone needs it |
| Get involved links to `/Teams/Roster` | Open roles are on `/Roles` now | Roles got its own section |
| Team slot modelled on `ICampPart` | Copy `ITeamRolesPart` | One named interface per contributor is the Teams pattern |
| Core PR includes the timeline | Timeline is the last PR | The first PR stays inside Tasks |

### Update the fork first

Your branch starts from peterdrier/Humans#2502. What landed since that matters here:

- **Permissions section** (peterdrier/Humans#2527): permissions on owners, declared by each section. Shifts and Budget already check through it (peterdrier/Humans#2559, peterdrier/Humans#2562). Read how Shifts declares `Shifts.Manage` in its `Section.cs`.
- **Roles section** (peterdrier/Humans#2531, peterdrier/Humans#2536, peterdrier/Humans#2551): roles on owners, the open-roles board at `/Roles`, the roles card on the team page, a Roles admin page. Tasks looks and feels like it.
- **Teams answers Permissions for today's coordinators** (peterdrier/Humans#2553), a temporary stand-in while departments re-create their roles.
- **Design direction** (peterdrier/Humans#2532, peterdrier/Humans#2533): `docs/design/DESIGN.md`, a rule for views, and the `design-page` and `look-check` skills.
- **Demo world** (peterdrier/Humans#2552, peterdrier/Humans#2554): sections with data ship a demo story. Tasks ships one.
- **AGENTS.md** gained checks for the look and for demo data.
- **Governance took over** the Asociados, Colaboradores and Board groups (peterdrier/Humans#2510). Those teams are switched off.
- **`role_assignments` is now `auth_role_assignments`** (peterdrier/Humans#2403). The free name belongs to Roles.
- **Account merges are self-service** (peterdrier/Humans#2492), and Users things are keyed by user id, not profile id (peterdrier/Humans#2522, peterdrier/Humans#2524, peterdrier/Humans#2526).
- **A glossary grew in `CONTEXT.md`** (peterdrier/Humans#2571). The Words table below goes there, copied as it stands, in your first PR.

## The design

### Kinds of work

| | Role | Task | Shift |
|---|---|---|---|
| What | A standing responsibility | A piece of work with an end | Being somewhere for a set time |
| Ends when | It is handed over | It is done | The time passes |
| Lives in | Roles | Tasks | Shifts |

Tasks is its own section, `Humans.Tasks`, with its own tables. Its pages look and feel like the Roles pages: things that hang on a team.

Do not copy these from the Roles code, where Tasks differs:

- Roles adds a person to the team when they get a role. Tasks never does.
- Roles refuses fewer slots than holders. Tasks allows more people than slots.
- Roles decides who manages with its own coordinator role. Tasks asks Permissions.
- Roles' owner route takes only the key. New routes also take the id (below).
- Roles has no demo story. Tasks has one.

### Words

Code uses these words. What the screen says is Cupcake's choice, within `memory/product/humans-terminology.md`, except where noted.

| Word | Meaning |
|---|---|
| **Task** | A piece of work with an end. In code the type is `TaskItem`, because `Task` is taken by .NET |
| **Team** | What a task belongs to. The screen never says "owner", which reads as a person |
| **Slot** | One person needed. A task with 4 slots needs 4 people |
| **Assign / unassign** | A manager puts a person on a task, or takes them off |
| **Claim** | A person takes a free slot themselves. The button says something like "I'll do this" |
| **Release** | A person gives their own slot back |
| **Needs approval** | A claim on this task waits for a manager's answer. `NeedsApproval` in code |
| **Request** | A claim that is waiting for that answer. A manager approves or declines it |
| **Priority** | How much the task matters. Same levels as roles. Not "criticality" |
| **Blocked** | A status picked by hand: the task is held up by something outside, such as a permit |
| **Waiting on** | Shown automatically when a task this one depends on is not finished |
| **Manager** | Anyone who holds `Tasks.Manage` for the task's team. Not Teams' sub-team manager |

### What a task holds

Tables: `task_items`, `task_assignments`, `task_dependencies`, in Tasks' own context. No other section reads them.

A task has:

- **Title** and a Markdown **description**, shown with the shared Markdown renderer.
- **Team**: stored as owner kind plus id (`Owner` in Base). `Team` is the only kind in the first version. Required. Only an active team that is not a system team can have tasks. When a team is switched off, its tasks stay as they are.
- **Event**: optional. It is the event record in Settings, the one shift rotas point at, stored by id. A task without one is not tied to an event. The add form starts on the active event.
- **Start** and **due**: each optional. Each is either a fixed date, or a number of days from the event's gate opening day. Day 0 is gate opening and days before it are negative, as in shifts. A relative date needs an event, so the event cannot be removed while a date is relative. Changing the event keeps the day numbers.
- **Status**: to do, in progress, blocked, done, cancelled. Blocked carries a required note saying what holds it up. The note is cleared when the task leaves Blocked.
- **Priority**: `SlotPriority`, which lives in `Humans.Teams.Contracts` today. Tasks does its own "most needed first" ordering.
- **Slots**: how many people it needs. 1 by default.
- **Needs approval**: off by default. When on, an optional free-text reason ("chainsaw licence") shown beside the claim button.
- **Hidden**: off by default.

An assignment is a task, a person, and whether it is a request or confirmed. A person has at most one per task. A dependency is a link from one task to the task it waits on.

There is no "created by" column. Who did what is in the audit log, which is also the task's history. There is no second history table.

### Who can do what

| Who | Can |
|---|---|
| Any signed-in human | See every task that is not hidden. Claim a free slot. Release their own slot |
| A person confirmed on a task | Also set its status to to do, in progress, blocked or done. Never cancelled. If the task is hidden, they still see that one task |
| A manager | Everything on that team's tasks: create, edit, assign, unassign, answer requests, set dependencies, hide, cancel, delete |
| `TasksAdmin` | The same on every team |
| Board | Sees everything, hidden tasks included. Being on the Board gives no rights to change anything |
| Admin | Everything |

How that is built:

- Tasks declares `Tasks.Manage` through `ISectionPermissions`, the way Shifts declares `Shifts.Manage`: owner kind `Team`, not top-level only, admin role `TasksAdmin`.
- Per-team checks ask `IPermissionService` for `Tasks.Manage`. Controllers do the asking. The service holds no permission checks.
- Seeing hidden tasks and opening `/Tasks/Admin` use `TasksPolicies.Read` (`TasksAdmin`, Board, Admin), as Roles does.
- A team's coordinator holds every permission on the team, so coordinators manage their team's tasks with no setup. Whoever manages a team's roles in Roles can put `Tasks.Manage` on another role.
- Permissions reaches down: a department's managers also manage the tasks of its sub-teams.
- Moving a task to another team needs `Tasks.Manage` on both.

Each team manages its own tasks. Production's tasks sit on production's team. No team can lock another team's tasks.

### Claiming and approval

- A human sees the claim button while a task has a free slot and is not done or cancelled. The service refuses a claim on a full task. Two claims at the same moment may both land, and it then shows as 5 of 4.
- On a task that does not need approval, claiming confirms the person at once.
- On a task that needs approval, claiming makes a request. A request does not take a slot. The person can take it back.
- A manager approves or declines. Approving when the task has filled up in the meantime overfills it. Declining removes the request, and the person may ask again. A decline can carry an optional reason, which goes in the email and the audit log and nowhere else.
- The person gets an email when approved and when declined, because the answer can come days later. Nothing else in Tasks sends email. The emails use the `VolunteerUpdates` category. Tasks owns the templates, their translations and the preview. Email only sends.
- Waiting requests show in the manager's things to do on Home. `TasksAdmin` and Admin see every team's.
- A manager can assign more people than there are slots, or lower the slots below the people on it. It shows as 5 of 4. Nothing warns and nothing changes the slot count.
- Being on a task, claimed or assigned, does not make anyone a member of the team.

### Dependencies

- A dependency says "this task waits on that one". The tasks can belong to different teams.
- The manager of the waiting task adds and removes the link. The other team does nothing.
- Dependencies are shown, never enforced. A task that is waiting can still be started and marked done.
- A task is waiting while something it depends on is not done. A cancelled task holds nothing up.
- A link that would make a circle is refused.
- Deleting a task removes its links. A link to a task the viewer cannot see shows as "a hidden task" with no link.

### Hidden

A hidden task is shown only to its team's managers, the people confirmed on it, `TasksAdmin`, Board and Admin. Every task of a hidden team counts as hidden, and that team's tasks page is not found for anyone who cannot see the team.

### Cancel and delete

- **Cancel**: we are not doing it this time. The task stays, marked cancelled, and is still there to be copied to the next event.
- **Delete**: the task is gone and will not be copied. The audit log keeps what happened.

### Pages

| Route | What | Who |
|---|---|---|
| `/Tasks` | All tasks, with filters | Signed-in humans |
| `/Tasks/Team/{slug}` | The team's tasks page, with the add form for managers. Takes the slug, the custom slug or the team id | Signed-in humans |
| `/Tasks/{id}` | One task: description, dates, people, what it waits on | Anyone who can see the task |
| `/Tasks/Admin` | Read-only. One row per team that has tasks: tasks, open slots, blocked tasks. Below it, every blocked task with its note. In the admin sidebar, in its own controller. English only | `TasksPolicies.Read` |
| `/Tasks/Open` | The open-tasks board: tasks with free slots, most needed first, claim on each row. Works like the `/Roles` board | Signed-in humans |
| `/Tasks/Timeline` | Tasks over time with their dependencies, and the dependency graph | Signed-in humans |

- Lists show the active event's tasks plus tasks with no event, and leave out done and cancelled tasks, unless a filter asks otherwise. The admin page counts the same set. Open slots are slots minus confirmed people, never below zero.
- The open-tasks board shows tasks that are to do, in progress or blocked.
- Tables use the shared table component. Pages follow `docs/design/DESIGN.md`. Manager forms on these pages are translated like the rest.
- People are shown with `<vc:human>` and picked with `<vc:human-search>`, as on the role page. Tasks stores user ids, never a name.
- There is one "Tasks" link in the main nav and no search box. The header search is the only search.

### Bulk load

One Backdoor endpoint takes a whole set of tasks, so an agent can load it with its person's key. Backdoor keys go to Admin and Board today. Whether `TasksAdmin` can get one is Peter's decision and not part of this design.

- The payload is a list of tasks. Each has a key that only means something inside the payload, and dependencies name those keys, so no ids are needed up front. A task names its team by slug or id, its event by id, and each date as a fixed date or a day number. It carries no people and no status.
- All or nothing. The whole payload is checked first and the answer lists every problem. A dry-run flag checks without creating.
- Loading the same payload twice makes the tasks twice.
- The caller needs `Tasks.Manage` on every team in the payload. The Backdoor controller checks that before it calls Tasks.
- The rules live in the Tasks service. Backdoor gets one controller that calls one Tasks method, like its Issues controller, the one that writes.

### How Tasks touches other sections

| Section | What |
|---|---|
| Permissions | Tasks declares `Tasks.Manage` and asks `IPermissionService` |
| Auth | Tasks declares the `TasksAdmin` system role |
| Teams | Tasks reads team names and slugs from `ITeamService`. The link to a team is `/Teams/{slug}`. No new `ITeamService` method. Later, Teams gets one named slot for a tasks card on its team page, copied from `ITeamRolesPart` |
| Settings | Tasks reads the event record for names and gate opening dates |
| Email | Sends the approved and declined emails |
| Backdoor | The bulk-load controller |
| Home | Waiting requests in things to do |
| Gdpr, Users | Export lists a person's assignments and requests. Erasure deletes them. Free text on a task (description, blocked note, approval reason) is not tied to a person and stays. When two accounts merge and both are on one task, one row is kept, and confirmed wins |
| AuditLog | An entry for every change |

New public surface, each needing Peter's yes in its PR: `TasksPolicies`, the `TasksAdmin` role name, the `Tasks.Manage` key, the method Backdoor calls, and the tasks-card slot in `Humans.Teams.Contracts`.

Coming from Peter's side: the names and links of whatever a task can belong to will move to one shared place that Roles and Tasks both read. It only matters when tasks can belong to something other than a team. Until then, read Teams directly.

### PR order

Each PR is cut from main after the one before it has merged. No stacked branches.

1. **Tasks by itself.** The section, tables, `/Tasks`, the team's tasks page, the task page, managers create, edit, assign and set dependencies, `/Tasks/Admin`, the `TasksAdmin` role and `Tasks.Manage` permission, audit, GDPR, the demo story, every supported culture, the Words table in `CONTEXT.md`, and the section's docs (`Docs/Tasks.md` from the section template, a feature spec made from this doc, `Docs/data-access.md`, the dependency graph). Needs approval is stored and on the form here, and takes effect with claiming. No other section's code changes. Outside Tasks it touches the project wiring, the nav label in Base, and the role name in `SectionRolesTests`.
2. **Bulk load** through Backdoor.
3. **Claiming**: claim, release, requests, the approved and declined emails, the Home entry, the open-tasks board.
4. **Tasks card on Teams' team page.** What the card shows is Cupcake's call.
5. **Timeline and dependency graph.** How tasks without dates show is Cupcake's call.

Fork PRs get no preview deploy. A maintainer can deploy one by hand.

### Not in the first version

Tasks on workgroups, camps or the organisation itself. Copying tasks to the next event. Offering team membership to someone who is on a task. One page for open roles and open tasks together. A paste-in import page. Sub-tasks and projects. Tasks that grant permissions. Tasks in the header search.
