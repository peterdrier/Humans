# Tasks feature

Pieces of work with an end, hanging on a team: a **Task** is to a Role (standing responsibility) and a Shift (being somewhere at a time) what work-that-gets-done is. Vocabulary: [`CONTEXT.md`](../../../../../CONTEXT.md#tasks). Invariants: [`../Tasks.md`](../Tasks.md). Built in five PRs; **PR 1** marks what ships first.

## What a task holds (PR 1)

- Title, Markdown description, one team (required; active, non-system teams only), optional event.
- Start and due dates, each fixed or relative to the event's gate opening day (day 0, negatives before). The form offers a calendar either way; pages show real dates. A relative date needs an event.
- Status: to do, in progress, blocked (required note), done, cancelled. Priority (`SlotPriority`). Slots (default 1). Needs approval (stored and on the form in PR 1, takes effect with claiming in PR 3: a claim becomes a request), optional reason. Hidden.
- Assignments: person plus confirmed or request, at most one per task. Dependencies: "waits on" links, shown never enforced, circles refused.
- History is the audit log. No "created by".

## Who can do what (PR 1)

| Who | Can |
|---|---|
| Signed-in human | See non-hidden tasks |
| Confirmed on a task | Set status to do/in progress/blocked/done; still see it if hidden (so does someone asking to join) |
| Manager (`Tasks.Manage` on the team, inherited down sub-teams; coordinators hold it) | Create, edit, assign, unassign, set dependencies, hide, cancel, delete; move a task needs both teams |
| `TasksAdmin` / Admin | The same on every team |
| Board | See everything, hidden included; no change rights |

Hidden tasks (and every task of a hidden team) show only to managers, people on them or asking to join, `TasksAdmin`, Board and Admin.

## Pages

| Route | PR | What |
|---|---|---|
| `/Tasks` | 1 | All tasks, filters; active event plus event-less, open only by default |
| `/Tasks/Team/{slug}` | 1 | A team's tasks; add form for managers |
| `/Tasks/{id}` | 1 | One task: description, dates, people, waiting-on |
| `/Tasks/Admin` | 1 | Read-only per-team counts and every blocked task with its note |
| `/Tasks/Open` | 3 | Open-tasks board: free slots, most needed first, claim per row |
| `/Tasks/Timeline` | 5 | Tasks over time, dependency graph |
| `POST /api/backdoor/tasks/bulk` | 2 | Backdoor key; loads a whole set of tasks, or checks it with `dryRun` |

## Later PRs

- **PR 2, bulk load** (built): `POST /api/backdoor/tasks/bulk` takes a set of tasks (payload-local keys, dependencies by key, dates fixed, a day number, or a date Tasks converts). All or nothing, dry-run flag, every problem listed, caller needs `Tasks.Manage` on every team. No people, no status. Loading a payload twice makes the tasks twice. The Backdoor opening-up (keys for people with `Tasks.Manage` or `TasksAdmin`) is Peter's and had not landed when this was built.
- **PR 3, claiming** (built): claim/release; without approval a claim confirms at once, with it a request (takes no slot) a manager approves or declines (optional reason). Overfilling is allowed. Approved/declined emails (`VolunteerUpdates`); waiting requests in Home's things to do. Being on a task never adds team membership. The open-tasks board `/Tasks/Open` lists every visible task with a free slot, each row showing the viewer's place; the task page shows the viewer's state (claim, waiting with withdraw, on it with leave); managers answer requests in a requests card on the task and team pages, which only they see.
- **PR 4**: tasks card on the team page via a named slot in `Humans.Teams.Contracts` (copied from `ITeamRolesPart`).
- **PR 5**: timeline and dependency graph.

## Cancel and delete (PR 1)

Cancel keeps the task, marked cancelled, copyable later. Delete removes it and its links.

## Cross-section

Permissions (`Tasks.Manage`), Auth (`TasksAdmin`), Teams (names, slugs), Settings (event, gate day), AuditLog (every change), Gdpr/Users (assignments exported and erased; free text stays; merge keeps one row, confirmed wins). Backdoor's bulk-load controller calls `ITaskWrite` (PR 2). Email (the approved and declined emails, `VolunteerUpdates`, templates and preview owned by Tasks) and Home (waiting requests in the manager's things to do) were added in PR 3.

## Not in the first version

Tasks on workgroups, camps or the organisation; copying to the next event; team-membership offers; combined open roles/tasks page; import page; sub-tasks; tasks that grant permissions; header search.
