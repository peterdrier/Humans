# Build and strike planning — tasks as a planning tool for on-site work

**Status: draft for discussion — nothing here is implemented.** Items marked **[OPEN]** are undecided. Related specs: [Tasks](2026-10-19-tasks-module-design.md), [Shifts improvements](2026-10-19-shifts-improvements-design.md) and [Capacity and care](2026-11-01-capacity-and-care-design.md).

Prompted by the strike guide compiled by past strike leads, which shows what a strike lead needs to know and when.

## Why

Build and strike are task based and run on site, where nobody wants to use the platform. So **tasks here are a planning tool**: they say who leads each area, and how many people each area needs on each day. Coordinators use that on site to size shifts and brief crews. Nobody is expected to act in the platform while on site.

**Everything about leads is settled before the first person reaches site for build**, strike leads included. There are no completion prompts, notifications or assignments that depend on someone being online mid-event.

## Model

- **Normal tasks.** An on-site task is an ordinary task with a window. Its **slots are leads**: needed is usually one; capacity is one or two (the guide's multiple-strike-lead model). Claiming, confirmation, shadow slots and openness work as in Tasks. A shadow slot on a strike lead task lets newcomers learn alongside.
- **Crew need.** A separate field: people needed per day over the task's window, flat by default with an optional per-day override. It sizes daily shifts. It creates no slots and appears nowhere as understaffing. **[OPEN: whether the per-day override is needed from the start (cantina or forklift work vary by day).]**
- **Two effort numbers.** *Lead hours* are the lead's own time and count toward that person's load (see Capacity and care). *Crew person-hours* are derived: people × days × productive hours per on-site day (a setting). They count against nobody's load; each crew member's load comes from their own shift signups.
- **Where the number comes from.** Last year's plan clones with its tasks. Once someone claims the task, the lead confirms or adjusts it. Each number shows whether it is *inherited*, *confirmed by the lead* or *set by a planner*, so planners see how firm the total is.
- **Strike as a project and a team.** Strike is a **project** (a lens, with a milestone) across the teams that do the work: Power strikes its electrics, Cantina its structure. A standing **Strike team** is the home for cross-cutting lead tasks such as greeting walk-ins, the MOOP work and vehicles. Event-scoped tasks in a team clone next year; the project carries its tasks across teams (see Tasks: Projects, working groups and cloning).
- **Ordering.** Finish-to-start dependencies give the order on the day sheet (Power before Gate, teardown before container loading). **[OPEN: a lag on a dependency, for "needs 24 hours' notice before a structure goes".]**
- **Handover notes, written before the event.** A structure's build task carries a *strike handover* note: tools and bits needed, teardown order, container loading preferences. It shows on the strike counterpart and is written while build is planned, never on the day. **[OPEN: whether the counterpart is a dependency or a plain link, and whether the note is a required checklist item.]**

## Lead-by date

- Every on-site task has a **lead-by date**, by default **the start of build minus a buffer** (a Calendar reference, so it moves if build moves). It applies to build and strike tasks alike: a strike lead is needed as early as a build lead.
- A task without a lead as that date approaches enters the needs-attention queue automatically (the gap-cover cascade applies). A task may set its own lead-by.
- A **readiness milestone** ("every on-site task has a lead by the lead-by date") shows computed progress, and the production view shows a countdown of leaderless tasks per team.
- Criticality marks the essentials; those surface first. The guide names the recurring bottleneck: Power, Cantina and Toolhaus leads, and the Gate, MoN, NoInfo and Participant Wellness teams.

## Nomination pool for leads

For a task with no lead, staffing holders see suggested leads. A person appears if all three hold:

1. **Present.** Declared availability or shift signups cover the task's window. Full coverage ranks above partial.
2. **Involved in the department.** A team member, a holder of tasks in the container, or someone with past shifts with the team (the Shifts improvements suggestion pool).
3. **Free.** No other lead task overlaps those days. The concurrent-lead limit is a setting, **default one for on-site tasks**. **[OPEN: whether one fits short, non-overlapping tasks.]**

**Ranking:** who led this structure's build, or last year's strike counterpart, comes first; then department involvement; then skill tags; then capacity band. The nominator decides and the person confirms, as in the existing nominate flow, with the notice sent by email well before the event.

**Reuse.** The Shifts volunteer-tracking view already finds people who declared participation and availability but have no signups. The pool is a sibling of that cohort. Tasks needs a read on the Shifts contract for on-site days; new public surface needs Peter's approval.

**Presence earns its keep.** Declaring when you are on site makes you appear in the pool, count in the forecast and count toward barrio hours; the profile editor says so.
- **Claim check.** Claiming a lead task whose window runs past your declared stay gives a soft warning and offers to update your availability.
- **Lead holders without a declaration** get a nudge to declare.
- **[OPEN: availability is often declared late.]** Whether the lead-by buffer should sit after the usual declaration deadline, or the pool should also use last year's presence as a weaker signal.

**Guardrails.** Presence is whereabouts data. Only staffing holders looking at a specific vacancy see it, only for people in the suggestions, and only for that window. The existing opt-out from being suggested applies. Nominations are audited.

## Planning outputs

- **Demand grid:** days across, areas down, crew need in each cell. Build and strike both use it.
- **Rota sizing.** A team's crew need for a day becomes a **suggested min and max** for that day on its build or strike rota. Coordinators accept or adjust it in the existing build/strike grid. The link is one-way and per team per day, so tasks and shifts never record one commitment twice. Progress and done stay on the task; attendance stays on the shift.
- **Demand against supply.** Supply is day-level availability plus shift signups. The gap view tells leads where to recruit before the event.
- **Walk-ins** are not in supply. The forecast shows last year's actual headcount per day as a calibration line, and the planner can add a margin.
- **Day sheet.** A read-only, printable page per day for coordinators: each area with its headcount, order ("after: Power"), notes and tools, and the lead's name and contact. An area with no lead is marked, so coordinators know it before arriving.
- **Next year.** The plan clones with its tasks. The year-end report compares planned against actual crew per day.

## Not here

- Claiming, notifications or lead recording on site.
- Tooling for walk-ins without accounts; holding a slot requires an account (see Tasks: Hosts).
- Shared resources (forklifts, vehicles, radios) and certifications as a modelled concept. A requirement type for certifications is a later question.
- Container contents and key inventory: they belong to the Containers section, if anywhere.

## Surfaces checklist

- **Cultures:** the nomination notice and claim warning are member-facing, so six cultures. The planning, day-sheet and forecast screens are for coordinators, and exempt where the route is.
- **Authorization + negatives:** only staffing holders see the pool and a person's on-site days, only for the vacancy they are working; opted-out people never appear; crew need is editable by the container's holders, never by outsiders.
- **Audit:** nominations, lead claims, crew-need changes with their provenance, generated rota suggestions accepted.
- **GDPR:** presence is read from data already in export and erasure; nothing new is stored about people.
- **Navigation:** the planning views from the container page and the production view; the day sheet from the strike project page.
- **Tests:** `tests/Humans.Tasks.Tests` — lead-by escalation and milestone, pool tests (present, involved, free, opt-out), concurrent-lead limit, crew hours excluded from lead load, rota-size suggestion per team per day, clone with inherited provenance. `tests/Humans.Shifts.Tests` for the availability read.
- **Invariant docs:** `Tasks.md`, `Shifts.md`.

## Reuse audit

| Considered | Decision |
|---|---|
| Crew members as task slots | Rejected: two records of one commitment. Crew labour is shifts; tasks hold leads. |
| A separate planning concept for headcount | Rejected: normal tasks with leads plus a crew-need field. |
| Build-complete prompts to nominate strike leads | Rejected: nobody is on the platform mid-event; leads are settled before build. |
| Day sheet as an interactive on-site tool | Rejected: read-only and printable. |
| A new availability source for presence | Rejected: day-level availability and shift signups already exist. |
