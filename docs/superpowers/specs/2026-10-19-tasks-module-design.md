# Tasks — a module for organising work

**Status: draft for discussion — nothing here is implemented.** Items marked **[OPEN]** are undecided. Related specs: [Teams on tasks](2026-10-19-teams-on-tasks-design.md) (the first host), [Concerns](2026-10-19-concerns-design.md), [Capacity and care](2026-11-01-capacity-and-care-design.md) (availability, load, recognition) and [Build and strike planning](2026-11-01-build-and-strike-planning-design.md) (on-site tasks as a planning tool).

Inspired by Orchard's task-centred model (`Local/Orchard/docs/spec.md`), scaled down: no branches, packs, input rounds or modules.

UI mock-ups of the main screens, in Humans' own styling, are in [`prototypes/tasks/`](2026-10-19-tasks-teams-navigation-prototypes/tasks/index.html) (static, fake data).

## Why: engagement

Today a person who wants to help meets a role list, a Join button and a coordinator they must guess how to approach. Willing people bounce for lack of an obvious place to start, and coordinators end up doing everything because handing work out is manual and invisible. The goal is **engagement**: turn interest into contribution and keep the work spread across many hands.

What we gain:

1. **A way in.** What needs doing, when, how big, and how well it fits you — visible without being a member of anything.
2. **Work people can say yes to.** Tasks are outcomes with a size, a date and a priority, ranked by urgency and by fit.
3. **Gentle ramps.** A standing "Take part" task, shadow slots, holder-open tasks that need no approval for people already involved, a needs-help flag for stuck work, following a task or container to hear about it without committing, and proposing a task you would like to take on.
4. **Work spreads by design.** Every task has a needed and a capacity count; people who coordinate see the tasks only they hold; a curated Get involved page and a needs-attention queue put gaps in front of people who can fill them; critical gaps raise themselves.
5. **Tools go to the people doing the work.** Permissions are held through the tasks that need them, not reserved for a rank. A coordinator's job shrinks to one thing: making sure every task has someone on it.
6. **Visible progress.** Milestones, timelines and a production view across the org let people see that their work matters and let gaps show early.
7. **Continuity.** Yearly roll-forward, re-affirmation instead of re-recruitment, shadows carried forward, task notes — knowledge stops evaporating when someone steps back.
8. **Trust stays intact.** Anything that grants access is confirmed or endorsed, every grant is audited, nothing acts invisibly.
9. **Sustainable load.** People declare availability once per event year; coordinators see who has room; suggestions fit what someone can take on; a confirmation appears before anyone goes over capacity. No sanctions, only signals (see [Capacity and care](2026-11-01-capacity-and-care-design.md)).

How we'll know it works, all computed from existing state: the share of tasks with two or more holders; how long critical tasks stay understaffed; how many pick-ups come from people with no prior involvement in that container.

## Concepts

- **Task** — a unit of work. Outcome-phrased title, Markdown description, criticality, effort, slots, openness, optional window / milestone / event year, optional capabilities.
- **Container and host.** Every task belongs to exactly one *container* — its **home**, a team or a working group — supplied by a *host* section. The host decides what holding a slot means; Tasks never references a host.
- **Slot** — a place on a task. **Primary** slots carry the work; **secondary** slots are learn-alongside/backup (shadow, deputy). Secondary slots never count toward capacity and grant no capability, unless the host says otherwise for its staffing task. A slot records how it ended: *completed*, *handed over*, *released*, *removed*, *expired* (not re-affirmed or inactive), or *task cancelled*.
- **Needed / Capacity** — at least *Needed* holders (the staffing floor; criticality applies to it), up to *Capacity*. Same shape as Shifts' `MinVolunteers`/`MaxVolunteers`.
- **Availability** — a person's declaration, once per event year: hours per week before the event, and day-level availability for the on-site phases. It drives capacity bands, never assignment (see Capacity and care).
- **Crew need** — for on-site tasks, people needed per day. A planning number that sizes daily shifts; it creates no slots and counts toward no one's load (see Build and strike planning).
- **Lead-by date** — when an on-site task must have a lead, by default the start of build minus a buffer.
- **Standing vs one-off** — effort shape *ongoing* (owns a thing, never completes) is standing; *one-off* completes.
- **Subtask** — a piece broken off a task, one level deep. Own slots, window and status; an unclaimed subtask is a concrete, grabbable ask. The parent's progress rolls up from its subtasks. A task may also carry a **checklist** of simple micro-steps with no assignee.
- **Dependency** — a finish-to-start link: a task *waits on* another, possibly in another container. It shows as blocked until the predecessor is done.
- **Follower** — a signed-in human who follows a task or container for updates without responsibility. Following never creates membership or involvement.
- **Staffing task** — each container designates one task whose holders make sure every task in it has someone: they confirm pick-ups, nominate and remove holders, and see gaps. Nothing else is implicit. Teams calls it coordination.
- **Capability** — a named permission a task grants to its holders.
- **Milestone** — a dated goal with computed progress; may be flagged as reporting.
- **Phase** — a named span of the event year, owned by the production calendar (Calendar section).
- **Event year** — tasks and milestones are optionally scoped to an `EventSettings` id (one event per year). Unscoped = standing.
- **Date shape** (windows and milestones): *absolute*; *offset* (signed days from the event's gate-opening date); or *calendar reference* (a production-calendar item, or a phase's start or end). Offsets and references resolve live. The interface asks whether a date is *absolute* or *relative* and offers a date picker; for a relative date the tool works out the offset, so nobody types one. A task must use a relative or calendar-referenced date to be cloned into next year; absolute dates do not carry. Most views simply show plain dates.

## Projects, working groups and cloning

Every task has **one home** — a team or a working group — and any number of **lenses**: phase, project, tags, department affiliation. The home decides who confirms pick-ups and what holding the task means. A lens groups, filters, reports and invites; it never carries authority. A task tagged across a team, a workgroup and a project still has exactly one confirmer.

| Kind | What it is | People and landing page | Tasks cloned next year |
|---|---|---|---|
| **Team** | Standing home for people and tasks | Yes | Event-scoped ones are |
| **Working group** | One-off effort on something new, with a goal and an end date | Yes | No, by default |
| **Project** | Recurring, time-bounded effort across teams, such as site design or "ready to sell tickets" | No | Yes |

- **A project is a lens, not a container.** It has a name, an event year and a milestone (usually a calendar reference), and no people, page or tasks of its own. Every project task lives in the team that does the work. For site design: Power makes a form for departments' power needs, departments send theirs, City planning opens the placement tool — three tasks, three homes, one project. A project page lists its tasks by team with owners and progress, and its meetings (see Meetings); people join the work through those tasks, which already show on Get involved with the project as a filter. Projects are most useful in the production view.
- **A lead is optional.** A project may designate one of its tasks, homed in a team, as its lead task; its holders are then the project's contact and see its gaps. A project without one simply shows its gaps in the production view.
- **Projects replace recurring working groups.** What was run as a new working group each year (for example a finance group named for the year) is a project. Working groups remain for one-off efforts.
- **Cloning follows the home.** Event-scoped tasks homed in a team, and any task carrying a project lens, carry into the next year; tasks homed in a working group do not.
- **Year-end review.** Before the All Leads Meeting (the meeting a couple of months after the event that plans next year's direction) each container gets a **report**: its tasks and how the year went (done, dropped, still open, who held what, notes), generated from task data. The container works through it before the meeting and flags only what needs discussion, per task: *keep* (carries on; the default for event-scoped team tasks), *drop*, *move* (propose another home) or, for a working group, *adopt* (name the team or project that takes it, this year or next). Unflagged items need no meeting time. A move **notifies the target's staffing holders at once**, so it is no surprise: if they accept before the meeting it simply happens, and if not it goes on the meeting's agenda for discussion and decision. The agenda gets a "Year-end review: flagged items" item (see Meetings) listing only what is flagged or unresolved. The clone runs **after** the meeting, started by a `production.manage` holder, and carries the kept and adopted tasks and nothing else. **[OPEN: the screens for the report and the follow-up; to look at further.]**
- **A working group cannot simply end.** At its end date the group must submit proposals for what carries into the event, this year or next, even if the proposal is "nothing". Anything not proposed is not perennial and is archived with the group, which stays in the year it ran in. A group's members go their separate ways and may not be at the meeting, so these proposals are the hand-over: the receiving teams see them, are notified, and accept or decline like any move. Outcomes are *done*, *adopted* (tasks given a team or project as their new home), *promoted to a project*, or *promoted to a team* through normal team creation.
- **Moving a task.** A holder or staffing holder of a task's home can propose a new home. The target container's staffing holders are notified immediately and accept or decline; on acceptance the home changes (history is kept and the holders' involvement moves with the task), on decline it stays. An unresolved move goes to the year-end meeting's agenda. Moves are audited.
- **Affiliation, a way in.** Any task may name a team it connects to. Holding it makes the person an **associate** of that team (see Teams on tasks): invited to its meetings and shown a "this work connects to *team* — meet the people" nudge. It grants no membership, no access and no authority, so someone who takes a task in a working group is invited to the team that works with that kind of thing, without tagging the whole group.

## Hosts

A host section declares itself to Tasks through a seam discovered in DI (the pattern Issues uses for queues), so Tasks holds no list of sections:
- **Containers** — id, name, parent, and visibility facts (hidden, sensitive).
- **Staffing task** — which task is the container's staffing task, and the cascade (parent container).
- **Standing task templates** — tasks every container gets at creation (Teams: coordination, contact point, Take part, panel representative).
- **Reactions** — callbacks when a slot is taken or ends (Teams creates and ends `TeamMember` rows; a workgroup host would do something of its own).
- Nothing a host does with slots reaches another host. Workgroup activity never creates team membership, access or Coordinators-team entries. The one bridge is the opt-in affiliation lens, which makes the holder an *associate* of a team (invitations only).
- Holding a slot requires a Humans account, as being involved in the community already does (a ticket needs one). Nothing here supports tasks held by people outside the system.

**Working groups as a host.** Members are anyone who holds an active task in the group or has completed any task in it. A group is bounded in time, so there is no contributor window and nothing expires. Pick-up requests replace join requests and roster management; the group keeps its register fields, deliverable and documents. This changes the Workgroups section's own model and doc **[OPEN: needs that section's agreement]**.

Teams is the first host; working groups are the second (one-off containers; **[OPEN: pending a check with other community members that they want tasks there]**). Projects are not hosts: they are a lens on tasks homed in hosts.

## Tasks

**Fields:** container, title, description, criticality (reuse `SlotPriority`), effort shape + estimated hours, needed, capacity, secondary slots, requirements, openness, status, tags, parent task, checklist, predecessors, public flag, optional **window**, optional milestone / `eventSettingsId`, optional project(s) and department affiliation, optional capabilities, optional origin (production-calendar item), `featured` and `needsAttention` flags, and for on-site tasks optional **crew need** and **lead-by date**. Estimated hours mean the **lead's own time**; crew person-hours are derived from crew need and never count toward a holder's load.

**Window** — when the task is live: optional start and end in the date shape. Choosing a phase fills in references to its start and end; a task can span several phases; phase filters match any overlapping window. No window = standing / whole year.

**Status** — five categories the platform understands: *To do → In progress → Waiting → Done / Cancelled*. *Waiting* carries a "waiting on" note and a check-in date; on that date the holder is nudged (update, done, snooze, release). **[OPEN: containers may relabel or add statuses within a category, as ClickUp does, so a production team can say "Awaiting quote"; core logic reads only the category.]** *Open* is derived: not Done/Cancelled with holders < capacity. *Understaffed*: holders < needed.

**Tags** are free-form labels on a task, used for filtering and, with profile skills, for compatibility **[OPEN: tag vocabulary]**.

**Openness** (a request is always "pick up this task", with an optional note to the confirmer, limited like the old join message):
- *Confirmed* — **default.** A holder of the container's staffing task approves each pick-up.
- *Holder-open* — anyone already holding an active slot in this container claims instantly. Current holders only.
- *Proven-open* — anyone with a good enough track record claims instantly (see Track record). Not available on capability, endorsed or staffing tasks.
- *Open* — any eligible human claims instantly.
- *Endorsed* — candidacy and an endorsement threshold, with a vacancy notice and a meeting agenda item (see Selection).

The host may add a confirmation of its own (Teams: sensitive teams keep the deterrent modal on every claim path).

**Requirements:** typed individual-gate predicates — tier, language, **consent / legal document** (e.g. a confidentiality document before a data-access capability).

**Default list order:** understaffed first → Critical → compatibility → soonest due. Compatibility ranks, never filters or hides; v1 uses requirements, language and tier from Users contracts only. **Suggestion surfaces** ("fits me", nudges, nomination pools) additionally fit a person's remaining capacity, offering only small tasks near capacity; the browse list itself never hides anything (see Capacity and care). Filters: criticality, window/phase, due-before, effort, "fits me", include filled/done.

**Sole-holder flag.** A person holding a staffing task sees a flag on any other task they hold alone, nudging them to spread the work. It belongs to the family of load nudges, with the over-capacity confirmation on every claim path (see Capacity and care).

**Audited:** created, slot requested/approved/taken/released, completed, cancelled, flags changed.

## Track record

When someone asks to pick up a task that needs confirmation, the confirmer sees their **track record** beside the request, built only from what the platform already records:
- **Tasks:** completed, **handed over**, dropped, and currently held, plus the last few outcomes in order, within a window **[OPEN: default 24 months]**. *Handed over* is its own outcome with its own count and is never a drop: a release that names a successor who accepts, or that leaves the task's notes up to date for whoever follows. A plain release counts as *released*; a drop is a release without a handover, an expiry, or a removal. Tasks cancelled or ended by their container are neutral.
- **Shifts**, through the Shifts contract: attended, bailed (early or at the last minute) and no-shows, in the same window.
- **Tasks and shifts stay separate counts.** Both feed a person's overall picture of participation, but neither is folded into the other.
- **Context, not a score.** Counts and a short dated history, with a reason where one was given. No rating, ranking or combined number. A run of drops with no completion is shown as plain fact ("last four outcomes: dropped, dropped, dropped, dropped").
- **A reason to talk, never an automatic rejection.** The confirmer can schedule a call from the request (below). Nothing about a track record rejects anyone.
- **A ground for approval.** *Proven-open* is a per-task or per-container setting: anyone with at least N completed tasks (default 2, in any container) and no run of K or more consecutive drops (default 3) is confirmed instantly; everyone else goes to the confirmer as usual. The thresholds are settings. **[OPEN — to settle as a group: the window, N, K, and exactly what counts as a handover.]**
- **Pick-up call.** One action from a request opens a private meeting between the confirmer and the requester, scheduled through the Meetings poll (see Meetings: private calls), linked to the request. It replaces an ad hoc interview.
- **Visibility.** People see their own track record in full. The confirmer sees it only for requests to their container. Nobody else does. The track record, with its drops and bails, feeds no leaderboard and no recognition; the opt-in recognition in Capacity and care counts completions and shift hours only. Declining a suggestion is not a slot outcome and never enters it.
- **Data protection.** The track record is derived from slot and signup records, so existing export and erasure cover it. Automation only ever approves; a negative outcome always involves a person.

**Needs from Shifts.** The track record depends on better attendance data than Shifts keeps today (an unmarked past shift reads as attended, and a bail records no time of its own). Those changes are proposed in [Shifts improvements](2026-10-19-shifts-improvements-design.md).

## Subtasks, dependencies and checklists

**[OPEN — proposal from the ClickUp review.]**
- **Subtasks** — a holder can break off a piece as its own task (one level). Left unclaimed it is a precise "I need help with this", more honest than releasing the whole task. A parent cannot be marked Done with open subtasks without confirming. The parent's progress rolls up from its subtasks.
- **Checklists** — simple tick items inside a task for micro-steps; no assignee, no slots.
- **Dependencies** — finish-to-start only, between any two visible tasks, across containers. A task is *blocked* until its predecessors are Done. If a predecessor's window end moves past its successor's window start, both show *at risk* (computed, soft). Completing a predecessor notifies the successor's holders. This is how "Team B's power plan waits on Team A's placement" is expressed. **[OPEN: whether production items can declare dependencies between the tasks they generate.]**

## Proposing a task

Any signed-in human can propose a task to a container: a title and rough description, plus optional "I'd like to take this" and "I'd suggest this person". It lands in a proposals queue for the container's staffing holders (the gap-cover cascade applies if unanswered), who edit and activate it, or decline with a reason the proposer can see. Audited.

## Following, comments and notifications

**[OPEN — proposal from the ClickUp review.]**
- **Follow** a task or a container. Followers are notified when a slot opens, needs-attention is raised, status or dates change, a milestone slips, or a comment is added. The follower list is visible to staffing holders; everyone else sees a count.
- **Comments** — a short thread per task for signed-in humans, with @mentions that notify. Sensitive containers limit comments to holders and staffing. Distinct from task notes (summary and links) and from [Concerns](2026-10-19-concerns-design.md). **[OPEN: open to all signed-in humans, or holders and followers only.]**
- Notifications go through the Notifications crosscut with per-person preferences. **[OPEN: immediate vs digest defaults.]**

## Views

**[OPEN — proposal from the ClickUp review.]** One dataset, several lenses; all read the same task state.
- **List** — the default, with the ordering and filters above. Filters are remembered per viewer, with a "fits me" preset.
- **Board** — a container's tasks as columns by status; holders and staffing holders drag to change status.
- **Timeline** — windows and milestones in phase lanes with dependencies drawn, per container, and across containers on the production view.
- **Calendar** — tasks and milestones by date. Tasks you hold with dates join your personal iCal feed through Calendar's contributor seam.
- **Workload** — for staffing holders: per person, estimated hours of held tasks against declared availability, as a coarse capacity band unless the person chose to show numbers. The availability signal is the yearly declaration, and the view extends the existing `/Shifts/Dashboard` workload rather than adding a second one (see Capacity and care).
- **My tasks** — everything I hold or follow across containers, as a card on the member dashboard through the existing dashboard-card seam.
- Not adopted: table, mind map, map.

## Selection

An *Endorsed* task — the coordination task, and any task a container wants filled with care — is filled by **endorsement**, using the tools around it. There is no separate process engine; the design follows a community proposal for onboarding coordinators (tell everyone, meet, let the candidates and the people who know the work decide together).

- **Vacancy.** An empty slot on an Endorsed task opens a vacancy: the task is featured on Get involved and on the container's page, its description is the job description, and the platform adds a **role briefing** compiled from data it already holds (the task's notes, the container's handover note, open tasks and gaps, upcoming reporting milestones, who held it before). Followers of the container and all staffing-task holders are notified. A community-wide email is a deliberate step through the campaign path, never automatic **[OPEN: who may send it]**.
- **Candidacy.** Anyone can put themselves forward, with an optional short note, until an optional closing date. A candidacy confirms when the task's endorsement threshold is met.
- **The meeting is where endorsements are earned.** A vacancy adds a "Filling: *task*" item to the next meeting agenda in the host (from the shared Meetings capability, with the container or project as owner), showing the candidates, their notes and the briefing, with an endorse action beside each. Candidates earn endorsements by talking to people, above all at that meeting. The invitation goes to the people who can endorse and care: the outgoing holder (not if they left through a Concerns decision), the container's involved pool, and staffing-task holders of this and other containers. The meeting is about understanding the role, not judging skill. Nothing else about it is special.
- **Several candidates.** Rare, and by default they work together: everyone who clears the threshold takes a slot, and the container raises its slot counts if needed. Primary and secondary are labels for internal communication (Teams gives both the same powers). Candidates usually settle it in the meeting. If a slot is truly contested the platform picks nobody; the staffing holders or the gap-cover cascade facilitate **[OPEN: leaning first-to-clear takes the slot; later cleared candidates wait or take a secondary slot]**.
- **Endorsement.** Endorsers come from classes, which are settings: the container's involved pool, staffing-task holders of other containers of the same host, and holders of any designated org-level task. The community can add or remove classes (the Board too, if it chooses) and sets each required count (zero allowed). Two routes: the ordinary threshold, or **appointment** by holders of any task that grants the `tasks.appoint` capability. Nothing in the route is tied to the Board. No self-endorsement. Endorsements are point-in-time; confirmation stands if endorsers later leave. **Visibility:** everyone, including the candidate, sees only the count; who endorsed is visible only in audit. **[OPEN: exact thresholds, expiry (leaning 30 days).]**
- **Appointing is a task, not a rank.** `tasks.appoint` is an org-scoped capability. It is seeded on a **Recruitment** task, whose holders (people who help find people) are confirmed like any capability holders. The community decides who holds it and can attach the capability to other tasks, or detach it. The Get involved curators, who already nominate people for needs-attention tasks, are a natural fit but it is a separate choice.
- **Decision published.** Confirmation is announced on the container's page. Notes of a meeting where endorsements were given are published by default, and must be when two or fewer people attended (a setting), so a decision never rests on a private two-person conversation.
- Every vacancy, candidacy, endorsement, confirmation and revocation is audited.
- **Removal** of a confirmed holder from a capability or staffing task happens by release, by not re-affirming, or by a decision record from a Concerns panel. Admin stays the emergency backstop.

## Gap cover

A container can lack a confirmer. Proposal **[OPEN — thresholds undecided]**:
- **Cascade.** The container's staffing-task holders → the parent container's → the org fallback: holders of `tasks.appoint` (Admin stays the emergency backstop). Capability tasks are never opened up because nobody is home.
- **Unanswered requests escalate.** Untouched after N days (default 7), a request moves one step down the cascade and enters the needs-attention queue.
- **Unfilled staffing task** opens a vacancy notice, is Critical, needs-attention and featured, and the container shows on the production view under "without staffing coordination". Any staffing-task holder of the same host may endorse a candidate.
- **Critical understaffing raises itself.** A Critical task below *needed* for N days (default 14) enters the queue automatically.
- **On-site tasks are judged by their lead-by date,** not their own window: a task with no lead as that date approaches enters the queue, and a readiness milestone and the production view count the leaderless ones (see Build and strike planning).

### Responsiveness

A contact point or coordinator who does not answer must not silently block work.
- **Requests show their status.** A pick-up request and a message to the contact point each show the sender *Waiting / Seen / Answered*, when it was sent and when it moved up the cascade. The sender can press "ask someone else", which sends it to the next level without waiting.
- **Messages to a contact point go through the tool.** "Message the contact point" notifies every holder (Notifications already records who has read it) and emails per their preferences. The sender and the container's staffing holders see Seen and Answered; an answer given elsewhere can be marked answered. The direct profile contact link remains available. **[OPEN: whether to also keep a plain mailto]**
- **Overdue shows on the container, not as a leaderboard.** Pages and the production view flag containers with requests or messages waiting longer than N days (default 7, the same as escalation). They list requests, not people. Per-person response statistics are neither kept nor shown.
- **Persistent silence is a concern, not a metric.** A container whose requests keep escalating shows in the needs-attention queue, and anyone can raise an "inactivity" concern (see Concerns), which routes to the container's staffing holders first.

## Capabilities (task-gated permissions)

A task may grant capabilities to its **primary-slot holders**. Holding the slot is the permission; ending it (released, task done/cancelled, container deactivated) revokes immediately.

- **Catalog.** Each section declares the capabilities it exposes through the same DI seam: key, localized label, scope.
  - *Container-scoped* — effect confined to the holding container's own data. Any container may attach it.
  - *Org-scoped* — effect reaches beyond. Hosted by a container an Admin designates once; after that holders self-organise. **[OPEN: later, who Admin is moves to a task too — a `system.admin` capability held through an Endorsed task with its own threshold, seeded with today's admins and never fewer than one holder. The process is a community decision.]**
- **Floor.** A capability-granting task is at least *Confirmed*: *Open* and *Holder-open* are refused at create and edit; attaching a capability to a held task puts current holders into reconfirmation. **Staffing holders take team-scoped capability tasks in their own container (and its sub-containers) directly**, with no second confirmation: they are already endorsed, and blocking them from getting work done is a cost. Every pick-up and release is audited and shows in the who-holds-what matrix, and requirements such as a consent document still apply. Org-scoped capability tasks are not covered: nobody confirms their own claim on those.
- **Nothing implicit.** Holding a staffing task grants no capability by itself; a staffing holder can take a team-scoped one on demand (see Floor). On migration current coordinators receive slots on the capability tasks they effectively held; the sole-holder flag then prompts sharing.
- **Checking.** Consumers ask Tasks through its read contract (`user holds capability C [for container X]`), replacing direct coordinator checks. Today's consumers: Shifts, Budget, Expenses, GoogleIntegration, Teams; Camps' compliance matrix reads coordinator status too.
- **Catalog, first set** (each owning section defines the meaning):

  | Key | Gates | Scope | Owner |
  |---|---|---|---|
  | `team.contact` | Shown as the team's contact point | team | Teams |
  | `team.page` | Edit the team page and calls to action | team | Teams |
  | `meetings.organise` | Organise meetings for the owner (team, workgroup or project): agenda, poll, notes | container | Meetings |
  | `meetings.community` | Organise community-wide meetings | org | Meetings |
  | `team.email` | Membership of the team's Google Group | team | Teams / GoogleIntegration |
  | `team.resources` | Link/manage and receive Drive/Group resource access | team | GoogleIntegration |
  | `earlyentry.manage` | Grant/edit/revoke early entry for the team | team | EarlyEntry / Teams |
  | `budget.manage` | Edit the team's budget lines | team | Budget |
  | `expenses.manage` | See and manage the team's expense reports | team | Expenses |
  | `shifts.manage` | Create and modify the team's rotas and shifts | team | Shifts |
  | `volunteers.manage` | Confirm/refuse/voluntell sign-ups, without editing shifts | team | Shifts |
  | `tasks.curate` | Feature tasks on Get involved; work the needs-attention queue | org | Tasks |
  | `tasks.appoint` | Appoint holders to Endorsed tasks when endorsement does not settle it | org | Tasks |
  | `production.manage` | Edit the production calendar | org | Calendar |
  | `concerns.handle`, `.mediate`, `.consent-response`, `.configure` | See [Concerns](2026-10-19-concerns-design.md) | org | Concerns |
  | `barrios.manage`, `tickets.manage`, … | One per current global role (see Migration) | org | owning section |

- **Visibility.** A "who holds what" matrix per container and the Shell access matrix; every grant and revocation is audited.
- Granting or changing capabilities is a privilege change — implementation needs explicit sign-off (`privilege-changes-need-explicit-approval`).

## Get involved

`/Tasks` — listed in the *Get involved* group of the [navigation spec](2026-10-19-navigation-design.md), not as a top-level link — open to every member: open tasks across all visible containers, same ordering and filters as a container's list plus container and phase. The successor to `/Teams/Roster`'s open-slot view. Shift rotas keep their own browse for now.

- **Featured** — set by `tasks.curate` holders; featured tasks lead.
- **Needs attention** — raised by a task's holders or a staffing-task holder ("help me find someone"), or automatically per Gap cover; lands in a queue worked by `tasks.curate` holders, who can feature it, nominate people, or hand it back. Cleared when staffed.
- Curator actions are audited with the curator as actor.

## Milestones & reporting

A **milestone** has title, description, date (date shape), optional event, status.
- **Progress** is computed from linked tasks (done ÷ total, effort-weighted **[OPEN]**), never typed in. A milestone with no tasks is completed explicitly with a note/link.
- **Two reporting layers**, both always visible on the container's timeline:
  1. **Production reporting** — defined in the production calendar as an org-level ask of the containers it targets (e.g. "define placement and power needs by 1 May"). Read-only to the container; Not started / In progress / Done / Overdue, marked by staffing-task holders or the milestone's task holders.
  2. **Team reporting** — a container flags its own milestone as a key item. Authored and edited by the container.
  Plain milestones show on the timeline but may collapse.

## Production calendar integration

Calendar owns **phases** (name, order, span) and **production items** (title, description, date, phase, targeted containers, optional task template, reporting flag); editing is gated by `production.manage`.
- Tasks reads Calendar through a read contract; Calendar never writes Tasks' tables.
- On production-item create/change, Tasks reconciles automatically: it ensures a generated task + milestone per targeted container (idempotent per item and container), updates referenced dates live, cancels generated tasks of removed items. Calendar publishes a change notification (the `IEventSettingsChangeListener` pattern).
- Generated tasks are attributed to the production calendar in audit; their dates and reporting fields are read-only to the container.
- A production item can reference a community meeting such as the general assembly (see Meetings). When that meeting's date is finalised it becomes the item's date and everything referencing it follows, so a budget deliverable that must be voted there moves with it.

## Requests (first proposal)

**[OPEN — a first proposal, much less explored than the rest of this spec. "Request" is a placeholder name, not a decision.]**

When one team needs specific pieces of information from other teams — power needs, placement needs, ticket inputs — it is not a survey: nothing is being aggregated, someone has to read each answer. The same chain recurs every year, so it should be one pattern built from tasks, with nothing redone by hand.

- **A task with a linked item and a completion rule.** A task may link to something that can finish: a form, a meeting, a vote. It is done when the item reaches a state (form submitted, meeting notes published, vote concluded), and is manual otherwise. This is the one new idea; it also serves debriefs, "hold the session" and "the assembly vote concluded".
- **The chain.** A production item of kind *request* generates, at offsets from the event: (1) a task to define the request, with a deadline; (2) a **sign-off** task for each team that owns a section ("check the power questions ask what Power needs"), each a dependency of sending; (3) the send; (4) a response task in every recipient team ("answer by the deadline") that completes when someone holding it submits, one answer per team; (5) review and follow-up.
- **Sections and ownership.** A request has sections; a section can be owned by a team, which signs it off and sees that section's answers from every team. The requester sees everything; each recipient sees its own.
- **Oversight.** A grid of recipients by status (not started, in progress, submitted, overdue) on the request and as a row in the production view. Reminders and escalation follow the Responsiveness rules. A section owner can ask a team to clarify an answer, which creates a task there.
- **Answers read as a table per section,** not as charts, and can be exported.
- **Next year.** A request clones with its production item, and last year's answers prefill, so a team confirms or updates instead of starting again.
- **Engine.** The lean is to extend the Surveys section (authoring, question types, translations, named responses, completion ledger) with a mode where a team is the respondent, not to add a third form system. Today running a survey needs Board or Admin approval; that gate would move to a capability held through a task (for example `forms.run`), team-scoped for people in your own container and org-scoped for community-wide sends. It needs audiences for "a team's involved people" and "holders of tasks in a project or production item", and a completion read for tasks. **[OPEN: extend Surveys or build a small separate module. The test from Orchard: does the platform act on the answers, or does a human just read them? Here a human reads.]**
- **Debriefs** use the same pattern: a short standard form per team, plus an optional survey for everyone involved. Results feed the year-end report and are hidden below a minimum number of answers in small teams. A "suggest a task for next year" question creates a task proposal in that team. **[OPEN: sign-off blocks sending or only advises; a team answers once, submitted by any holder or only the contact point.]**

## Production view

`/Tasks/Production` — every member: phases as the spine (and, as a second grouping, projects with their tasks across teams), each showing production reporting milestones across visible containers with per-phase progress (done ÷ applicable, overdue list) and containers without staffing coordination. **Team reporting** is a second, toggleable layer, off by default. Read only. Calendar links to it; Calendar owns the data. *(Moved here from Teams now that milestones are Tasks'.)*

## New year

When a new event year is created (Settings change notification):
- **Standing tasks** — each holder is asked to **re-affirm**. Re-affirming keeps the slot and any capability, including staffing, with no endorsement or confirmation. Declined or unanswered after the grace period **[OPEN: default 30 days, with reminders]** releases the slot; the task shows understaffed and staffing holders are told.
- **One-off event tasks** roll forward with empty slots.
- **Availability** is asked again in the same prompt, since last year's declaration is stale.

**Roll-forward.** A staffing-task holder (or a `production.manage` holder) picks event-scoped tasks and milestones from a past year and rolls them into a target event: preview, then confirm.
- Tasks copy with empty primary slots, Active; secondary holders carry as suggestions. Standing tasks are not cloned, and cloning follows the home (see Projects, working groups and cloning). Generated production tasks regenerate from the production calendar.
- Offset milestones and windows recompute against the target's gate-opening date; absolute dates are listed for new values; calendar references re-point to the target year's items.
- The target `EventSettings` row must already exist.

## Continuity & documentation

**[OPEN — proposal.]** If someone vanishes or leaves, the work and its knowledge must survive.
- **Task notes** — a wiki-style summary per task (revision history, holders edit) and a short list of resource links. Done and release prompt "anything worth capturing?".
- **Handover** — releasing a standing task, or declining at re-affirmation, prompts finishing the notes and suggests a successor (the shadow, if any). Those notes feed the vacancy's role briefing.
- **Vanished holders** — a capability holder with no activity for N months **[OPEN]** gets a check-in; unanswered, the slot is released, the capability revoked, and the task shows understaffed. Audited.
- Reference pages and case logs belong to the same family of records (see the team handbook in [Teams on tasks](2026-10-19-teams-on-tasks-design.md) and [Concerns](2026-10-19-concerns-design.md)).

## Migration

- Teams' role definitions and assignments convert to tasks and slots (details in the Teams spec).
- **Global roles → capabilities.** Each work-oriented global role (ConsentCoordinator, VolunteerCoordinator, NoInfoAdmin, CampAdmin, TicketAdmin, EventsAdmin, FinanceAdmin, StoreAdmin, CantinaAdmin, EETeamAdmin, RideshareAdmin) becomes an org-scoped capability hosted by a container, via a built, audited **adopt** action: pick the role, pick the hosting container, preview the holders who move onto the new task, confirm. Sections accept capability or (transitionally) role until adoption completes; then the role stops being assignable. Admin and Board stay as system and legal backstops; FeedbackAdmin (already inert) retires. **[OPEN: TeamsAdmin and HumanAdmin — they create teams and grant roles, the top of the structure.]** NoInfoAdmin already is `volunteers.manage` in role form.
- One consolidated migration per affected section's context; old tables dropped only with Peter's approval.

## Ownership

- **Tasks** (new section, `Humans.Tasks` + `Humans.Tasks.Contracts`) owns tasks, slots, candidacies/endorsements, milestones, projects (the lens, its milestone and event year), capability grants, notes and flags, and the availability declaration, size preference, decline reasons, check-in flags and recognition opt-ins (Capacity and care). On-site days are read from Shifts through its contract. **Calendar** owns phases and production items; **Meetings** (its own spec) owns meetings, agendas, notes and polls. Each section owns its capability declarations. Hosts own what holding means.
- Public surface kept narrow: `ITasksService` reads (task/milestone summaries, involvement, capability checks); a narrow write contract for slot removal carrying a decision record (Concerns) and for the host-driven standing-task creation. No entity leaves a section.
- Dependency direction: hosts and consumers → Tasks.Contracts; Tasks → Calendar read contract, Settings, Users.Contracts, Audit, Notifications. Tasks references no host.

## Surfaces checklist

- **Cultures:** Get involved, task lists, timelines and the production view are member-facing → full resx set, six cultures. Admin screens exempt.
- **Authorization + negatives:** hidden/sensitive containers excluded from org views for non-admins; no self-endorse; no self-confirm on org-scoped capability tasks; capability tasks never Open/Holder-open; staffing holders have no implicit capability; only `production.manage` edits the calendar; only `tasks.curate` features; endorsement identities hidden except in audit; endorsement entries are kept out of per-entity audit cards (such as the Teams members page) and carry no names in their descriptions.
- **Consent:** capability tasks can require a legal document before a claim confirms; documents targeted at the Coordinators system team now reach only coordination-slot holders — review which rely on that.
- **Audit:** every task, slot, candidacy, endorsement, capability grant/revoke, flag, generated task, adopt, re-affirmation and roll-forward, attributed to actor or "production calendar".
- **GDPR:** export contributor for slots (with how each ended), candidacies (with their interest notes), endorsements given/received, meeting attendance, notes, comments, proposals and follows authored; erasure path **[OPEN: join-request precedent (hard delete) vs retain anonymised history]**.
- **Navigation:** `/Tasks` and `/Tasks/Production` from the main nav; each container's page links in and out.
- **Tests:** `tests/Humans.Tasks.Tests` — ordering, endorsement thresholds incl. negatives, capability floor and revocation, windows, roll-forward and re-affirmation, calendar-reference recompute, sole-holder flag, cascade escalation, project progress across homes, cloning follows the home, working-group adoption, associate gets invitations but no access, dependency blocking and at-risk flags, subtask roll-up, status categories; consumer sections test their capability checks.
- **Invariant docs:** a new `Tasks.md`, plus `Calendar.md`, `Teams.md` and each capability-consuming section's doc, in the implementing PRs.

## Reuse audit

| Considered | Decision |
|---|---|
| Tasks inside Teams | Rejected: Workgroups may host tasks too; Tasks is its own section with hosts. |
| Keep role definitions beside tasks | Rejected: no roles remain. |
| Shifts rotas as tasks | Rejected: rotas are recurring and never "done". |
| Calendar events as milestones | Rejected: milestones carry progress and reporting. Calendar supplies phases and production dates. |
| Orchard packs | Rejected: roll-forward is a direct in-app copy. |
| New capability registry in Tasks | Rejected: sections declare capabilities via DI, as Issues does for queues. |
| Extend `/Teams/Roster` | Accepted: becomes Get involved. |
| ClickUp subtasks, dependencies, followers, forms-to-tasks, views, custom statuses | Adapted: one subtask level, finish-to-start only, follow without membership, forms become "propose a task", statuses fixed categories with optional labels. |
| Voting, scoring or a separate selection workflow | Rejected: candidates earn endorsements by talking to people, mostly at the meeting. The endorsement count is the only tally. |
| ClickUp milestones as a task type | Rejected: our milestones compute progress from linked tasks. |
| ClickUp goals, sprints | Not needed: milestones with computed progress, and phases / the event year, cover them. |
| ClickUp custom fields, recurring tasks | Deferred: custom fields need six-culture labels and are opaque to the platform; recurrence (e.g. a monthly report) comes later. |
| ClickUp automation builder | Rejected for now: automations stay built in (gap cover, generation, re-affirmation) with configurable thresholds. |
| ClickUp time tracking | Rejected: volunteer surveillance and GDPR cost; self-reported contributed hours may come later. |
| Reuse `SlotPriority`, Shifts' min/max shape, offset-from-gate-opening dates, change-listener pattern | Accepted. |
