# Capacity and care — availability, load and recognition

**Status: draft for discussion — nothing here is implemented.** Items marked **[OPEN]** are undecided. Related specs: [Tasks](2026-10-19-tasks-module-design.md), [Shifts improvements](2026-10-19-shifts-improvements-design.md) and [Build and strike planning](2026-11-01-build-and-strike-planning-design.md).

Inspired by the availability and contribution model in Orchard's spec (capacity-aware fitted asks, contribution tracking), adapted to Humans' tasks and shifts.

## Why: spread the work, avoid burnout

The same willing people take on more and more, and coordinators cannot see who has room. The goal is to **spread work out**: let people say how much they can give, let coordinators see who has room, ask people to think twice before taking on more than they have, and notice gently when someone is not being offered the right work. Nothing here sanctions anyone. Everything is a signal for a conversation, never an automatic decision.

## Availability

- **A declaration once per event year**, per phase. Before the event it is **hours per week**. For the on-site phases (Build, Event, Strike) it is the **day-level availability people already enter** at `/Shifts/Mine/Availability`, which covers the range from build start to strike end. One editor, no second ask.
- **Re-asked at New year**, in the same prompt as slot re-affirmation (see Tasks).
- **On the profile, with work style and preferences.** The existing shift preference wizard at `/Profile/Me/ShiftInfo` (skills, work style, languages, rota tags) and the availability editor become one **"How I like to help"** page. It adds a **size preference** (small / medium / large tasks) and the hours-per-week declaration. Each owner contributes its section and the Shell composes the page. **[OPEN: whether work-style data later moves from Shifts to Users, since tasks' "fits me" wants it too. Tables stay where they are for now.]**
- **Visibility setting** on the declaration: coarse (default) or exact. Exact hours show to others only when the person chose it.
- **Ownership.** Tasks owns the hours-per-week declaration and the size preference. Shifts keeps day-level availability and the volunteer event profile. Tasks reads on-site days through the Shifts contract.

## Load and capacity bands

- **Before the event, load** is the lead hours of held primary-slot tasks, spread across each task's window by overlap, plus confirmed shift hours. Secondary slots do not count. Pending shift signups do not count (as in the existing workload dashboard). A task's **crew hours never count against its lead** (see Build and strike planning); each crew member's load comes from their own shift signups.
- **On site the unit is days**, not hours per week: what matters is consecutive days worked without a rest day. A long run of booked days raises a rest-day nudge when someone signs up, and shows to volunteer coordinators on the existing dashboard. **[OPEN: the threshold.]**
- **Judged by the task's window, not today.** A claim in March for a task that runs in July is measured against July's load.
- **Bands:** *well below capacity · has room · about right · near capacity · over capacity*, computed as declared availability against load. The thresholds are settings **[OPEN]**. Without a declaration there is no band, only a prompt to declare.
- **Tasks without an estimate** are listed as unestimated and counted at a default per effort shape **[OPEN: defaults]**. A rough estimate is why bands, not numbers, are what others see.
- **Display.** A per-person timeline by week or phase against declared availability. Shift hours are exact and drawn solid; task hours are estimates and drawn hatched. This extends the existing `/Shifts/Dashboard` workload view (per-person hours by period) and the Workload lens in Tasks, not a new dashboard.

## Who sees what

- **The person** sees everything about themselves.
- **Staffing holders** looking at a nomination, a pick-up request or their container's workload see the **band only**, never the numbers unless the person chose exact. Because a band covers all containers, it is coarse by design.
- **Nobody else.** A person who has opted out of being suggested (the existing Shifts suggestion opt-out) does not appear in suggestion pools.

## Declines and check-ins

- **Declining a suggestion or nomination is one tap** and costs nothing. A **reason chip** is offered: *busy right now · not my kind of task · too big · not sure*.
- **Reasons update preferences.** "Too big" nudges the size preference; "not my kind" invites updating tags. "Busy right now" updates nothing.
- **Declines stay out of the track record.** Only slot outcomes feed it.
- **A pattern, not a count.** Several "not my kind" or "too big" declines while the person is *well below capacity* raise a check-in. "Busy right now" never counts.
- **The person is asked first**: "Your availability says X. Want to update it, or your preferences?" If the pattern persists, staffing holders of the containers where the person is involved see **"check-in suggested"**. The existing private pick-up call (see Meetings) carries the conversation. **[OPEN: who sees the flag; leaning staffing holders of the person's containers.]**
- **No sanctions and no ranking.** Nothing blocks, hides or reduces anything. No decline counts are shown to anyone. It follows the Responsiveness precedent: persistent silence is a concern, not a metric. It is not routed to Concerns, which handle problems with a person's conduct in a role.
- **Audited.** Every nudge and every raised check-in, so the Board can see what the automation did.

## Suggestions respect load

- **Browse never hides anything.** Get involved always lists every open task, ranked as in Tasks.
- **Suggestion surfaces filter by headroom**: the "fits me" preset, nudges and the nomination pool. Near capacity, they offer only tasks that fit the remaining hours, and smaller ways in: a shadow slot or a subtask.
- **Over capacity, a confirmation appears on every claim path,** including instant claims (holder-open, proven-open, open), because those have no human gate: "You're at about X of Y. This adds about Z. Still take it?" It offers releasing something instead. It **never blocks**, and is softer for Critical gaps.
- **No declaration, no modal**, only a prompt to declare.
- **One load nudge.** The sole-holder flag and the over-capacity confirmation are one family of load nudges.

## My contribution

A personal page, built only from state that already exists: **completed** tasks and shifts (attributed to when they were done), **active** tasks and shifts, and **future signed-up** work, broken down by category, with presence on site as context. Never a single score. The track record (see Tasks) remains the confirmer's view of a request; this page is the person's own picture and links to it. The member-dashboard "My tasks" card links in.

There is no such page today. The track record and `/Shifts/Mine` are the nearest.

## Community recognition (opt-in)

Separate from the track record, which with its drops and bails feeds no leaderboard and no recognition. Recognition is built from **completions and shift hours only**.

- **Opt-in, off by default, revocable at once.** Opting in shares the same breakdown the person sees themselves, to signed-in members, under their display name.
- **No individual ranking.** Ranking by volume rewards exactly what this spec tries to prevent.
- **Barrio challenge.** Members who opted in count toward their barrio, through the self-declared per-season `CampMember` affiliation.
  - **Metric: the share of opted-in members who reached X hours** in the period. It is capped: going past X adds nothing, so nobody is pushed to overwork. A person counts as reaching X at the lower of X and their own declared availability.
  - **X is set by the community per event**, and a phase such as strike can have its own, smaller X. It makes a fun, voluntary alternative to a mandatory strike commitment.
  - **Hours** are exact for shifts and the owner's estimate for tasks (time tracking is rejected in Tasks).
  - **The barrio's total hours** show beside the percentage, since a percentage alone hides big contributors.
  - **Aggregates only, never a breakdown by person.** A barrio's figure is hidden below a minimum number of opted-in members **[OPEN: the minimum]**.
- **Community-wide totals** ("together we did...") need no opt-in beyond the same aggregation and carry the least pressure.
- **Breadth recognition** (a new team, a clean handover, covering a critical gap) as a gentler complement. **[OPEN.]**

## Where this meets the other specs

- Tasks: Workload lens, Default list order, Sole-holder flag, Track record, New year, Ownership (each updated in the same change).
- Shifts improvements: on-site availability read, consecutive-days nudge, the profile page.
- Build and strike planning: crew need and lead hours are separate, so a lead's load stays honest.

## Surfaces checklist

- **Cultures:** the declaration form, the confirmation, the nudges, My contribution and the recognition surface are member-facing, so full resx set, six cultures. Staffing holders' flag screens are exempt only where the route is.
- **Authorization + negatives:** staffing holders see bands only; exact hours only by the person's choice; opted-out people never appear in pools; recognition shows only opted-in people and never per-person within a barrio; nobody sees decline counts.
- **Audit:** nudges, raised check-ins, opt-in changes, declaration visibility changes.
- **GDPR:** export contributors and erasure for the declaration, size preference, decline reasons, check-in flags and opt-in settings.
- **Navigation:** "How I like to help" from the profile; My contribution from the member dashboard card; the recognition page from the community area.
- **Tests:** `tests/Humans.Tasks.Tests` — load by window overlap, secondary slots and crew hours excluded, bands, visibility, decline-reason effects, check-in trigger excluding "busy", modal on every claim path, browse never filtered, barrio metric cap and minimum-members rule, opt-out removal.
- **Invariant docs:** `Tasks.md`, and `Shifts.md` for the availability read.

## Reuse audit

| Considered | Decision |
|---|---|
| A new availability table for the on-site phases | Rejected: the existing day-level availability already covers build to strike. |
| A new workload dashboard | Rejected: extend `/Shifts/Dashboard` and the Tasks Workload lens. |
| Decline counts shown to coordinators | Rejected: reasons feed preferences; only a pattern raises a person-first check-in. |
| Routing check-ins through Concerns | Rejected: this is care, not a complaint about conduct. |
| Individual leaderboard | Rejected: rewards volume. A capped barrio threshold instead. |
| Time tracking for contributed hours | Rejected in Tasks; estimates and shift hours suffice. |
