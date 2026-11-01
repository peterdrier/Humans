# Shifts — improvements proposal (a starting point)

**Status: draft for discussion — nothing here is implemented.** This starts as a short list and is meant to grow as we look at how Shifts works with the new Teams, Tasks and Meetings model. Items marked **[OPEN]** are undecided. Related specs: [Tasks](2026-10-19-tasks-module-design.md) (its Track record section depends on this), [Teams on tasks](2026-10-19-teams-on-tasks-design.md), [Meetings](2026-10-19-meetings-design.md), [Capacity and care](2026-11-01-capacity-and-care-design.md) and [Build and strike planning](2026-11-01-build-and-strike-planning-design.md).

## What exists today (checked in the code)

- A signup moves through *Pending, Confirmed, Refused, Bailed, Cancelled, NoShow*. Rotas belong to a department or sub-team, and coordinators approve, refuse, remove, voluntell and mark no-shows.
- **Marking a no-show** is a button on the department shift admin page (`/Teams/{slug}/Shifts`): expand a **past** shift's "Manage" panel and press "Mark No-Show" beside a confirmed person. It appears only to people who can approve signups for that department (its coordinators, VolunteerCoordinator, NoInfoAdmin, Admin), only on shifts that have already happened, and only for shifts with at least one confirmed signup. The page works on the active event's rotas, so before the next event has any past shifts there is nothing to mark.
- The page lists past no-shows and bails read-only. There is a post-event dashboard with no-show and completion percentages, and the profile page has a no-show history section **[OPEN: check who can see it]**.
- Voluntold signups are confirmed automatically.

## Gaps

1. **Attendance is assumed, not confirmed.** A past shift nobody marks reads as attended, so the data is only as good as coordinators' habit of marking, one person at a time.
2. **A bail has no time of its own.** The signup carries creation and general update times and a status reason, but nothing that says when someone bailed, so an early bail cannot be told from a last-minute one, and later edits overwrite the update time.
3. **The volunteer isn't told** when they are marked a no-show, and has no way to say "I was there".
4. **Marking is hard to find.** It sits three levels deep: a past shift row, its Manage panel, then a per-person button. It is easy to miss and awkward on a phone.
5. **History is not readable elsewhere.** Other sections cannot ask "has this person turned up for shifts?", which the Tasks track record and nominations need.

## Proposed changes

- **Post-shift prompt.** When a shift ends, its department's `volunteers.manage` holders get a short prompt listing who was confirmed, with "everyone came" and "mark exceptions" as the two quick paths. Unmarked past shifts show as *unconfirmed*, not attended.
- **Bail timestamp and timing.** Record when a bail happened and classify it relative to the shift (early, late, last-minute) with the thresholds as settings **[OPEN: values]**.
- **Tell the volunteer.** Marking a no-show notifies them, and they can answer "I was there" or give a reason, which the coordinator sees. The record keeps both.
- **History through the contract.** A read on the Shifts contract returns a person's attended, bailed (with timing) and no-show counts and recent dated list for a window, for the Tasks track record. Only the person themselves and a confirmer deciding on their own request see it, with the same rules as the Tasks spec.
- **On-site presence through the contract.** A read returns the days a person is available or signed up on site for an event, for the lead nomination pool, the demand-against-supply forecast and capacity bands (see [Build and strike planning](2026-11-01-build-and-strike-planning-design.md)). The existing opt-out from being suggested applies.
- **Rota sizing from tasks.** A team's crew need per day (from Tasks) shows as a suggested min and max in the build/strike grid. Coordinators accept or adjust; nothing is written automatically.
- **Rest days.** Signing up for a long run of consecutive on-site days raises a rest-day nudge, and the volunteer coordinator dashboard shows long runs **[OPEN: threshold]**.
- **Profile.** Day-level availability and the shift preference wizard join the hours-per-week declaration on one "How I like to help" profile page (see [Capacity and care](2026-11-01-capacity-and-care-design.md)).
- **Authority through capabilities.** The Tasks spec replaces coordinator checks with `shifts.manage` (create and edit rotas and shifts) and `volunteers.manage` (confirm, refuse, voluntell, mark attendance). Marking attendance belongs to the second.

## How it meets the new Teams and Tasks model

- **People who did shifts with a team** become a suggestion pool when a coordinator looks for someone to nominate (labelled, for example, "3 shifts with us in 2026"), and the recipients of a thank-you with an invitation to take part. Teams reads this through the Shifts contract; a read may need adding. A profile setting lets someone opt out of being suggested.
- **Rotas and the team model.** Rotas belong to a department or sub-team, so they follow the same home rule as tasks. Whether a rota can be copied to the next event, and whether its period (Build, Event, Strike) should reference production-calendar phases instead of fixed offsets, is not yet examined **[OPEN]**.
- **Workload.** The workload view in the Tasks spec extends the existing `/Shifts/Dashboard` workload with capacity bands; day-level availability is the on-site signal and the yearly hours-per-week declaration covers the rest.

## To explore as a group

- Partial attendance: arriving late, leaving early, covering someone else's slot. Does the record need hours, or only attended / did not?
- Voluntold signups: are they held to the same no-show rules?
- Appeals and fairness: who can reverse a no-show, and does it expire from a track record?
- Bail rules: deadlines, reasons, and whether a bail with a replacement counts like a handover on a task.
- Recognition: how a thank-you is sent and whether it is public.
- Reminders before shifts, and whether a missed reminder changes how a no-show is read.
- Mobile use of the prompt: coordinators will mostly mark attendance from a phone.
- Participation picture: **decided** that shifts and tasks are counted separately, and that both are part of a person's overall participation. Still open: whether attended hours count, and who sees the combined picture.

## Surfaces checklist (to complete as the proposal firms up)

- **Cultures:** the prompt, the volunteer notification and the "I was there" reply need all six cultures; coordinator-only screens are exempt.
- **Authorization + negatives:** only `volunteers.manage` holders mark attendance; a volunteer sees only their own record; the contract read is restricted as described.
- **Audit:** attendance marks, reversals and bail classification changes.
- **GDPR:** attendance and bail history are personal data: export, erasure and retention window decided with the track-record window.
- **Tests:** `tests/Humans.Shifts.Tests`.
- **Invariant doc:** `Shifts.md` updated in the implementing PR.
