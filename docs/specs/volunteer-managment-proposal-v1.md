# Volunteer Management in Humans — Project Definition

**Elsewhere 2026 | Draft v1.0 | March 2026**

---

## 1. The Problem

VIM (FIST) has been the volunteer management system for Elsewhere, and it has real issues. The UX is dated and confusing, there is no aggregate view (coordinators manually copied data into spreadsheets), there is no total volunteer cap, and — most critically — volunteers who buy tickets often don't complete registration. It also lives entirely separately from Humans, meaning people have to register twice and carry two accounts.

The goal is to retire VIM and build volunteer management natively into Humans, so the whole journey — ticket → registration → shift signup → on-site coordination — lives in one place.

---

## 2. What We're Building

A shift management module embedded in Humans, covering:

- **Shift creation** for team leads (build/strike full-day shifts + event-time slots)
- **Volunteer signup** with a clear, modern UI on the Humans homepage
- **Lead approval workflows** for shifts that require it
- **Email notifications** for confirmations, approvals/rejections, and pre-event reminders
- **Early Entry list generation** computed automatically from confirmed build signups
- **NoInfo coordinator dashboard** for real-time gap-filling on site
- **Overall view & CSV export** so coordinators never need to touch a spreadsheet again

This is an MVP scoped for Elsewhere 2026. The cantina dietary export is a future iteration.

---

## 3. What We're NOT Building (Right Now)

- A separate registration flow — volunteers are already in Humans via the existing signup form
- Ticket integration / SSO with the ticketing system (nice-to-have, not blocking)
- Arrival tracking / check-in (the "did they show up" problem — a Phase 2 problem)
- Cantina dietary headcount export (data exists on profiles; just not exporting it yet)
- Year-over-year migration tooling (manual for 2026; automate later)
- A chatbot

---

## 4. Who Uses This and How

### Volunteer (most users)

After registering in Humans they see a **Shifts panel on their homepage** showing:

- Their upcoming confirmed shifts (with bail option)
- Open high-priority shifts across all teams, filterable by period (build / event / strike) and time of day
- Status of any pending signup requests

They can sign up for open shifts directly. For approval-required roles, they get a notification when approved or rejected.

### Team Lead

From their **team page in Humans**, leads can:

- Create shifts grouped by period (build / event / strike) — toggled with a checkbox per team
- Set min/max volunteer counts, priority level (Normal / Important / Essential), and approval policy
- Approve or reject pending signups
- See fill rates for all their shifts at a glance
- Export their team's rota as CSV

### MetaLead (department level)

Same as team lead view but across all teams in their department. Can approve lead applications. Sees department-wide fill rates and an early entry list for their department.

### NoInfo Coordinator

A dedicated dashboard at `/noinfo` showing all unfilled shifts across the entire event, ranked by urgency (priority × remaining capacity). Can assign a volunteer to any shift directly ("voluntell"), sending them an email notification. The volunteer can still bail if they choose.

### Manager / Admin

Global overview: total signups, pending approvals, fill rates by department. Can export all rotas, early entry list, trigger mass reminder emails, and adjust event settings (dates, timezone, early entry cutoff).

---

## 5. Core Data Model

This reuses what Humans already has (profiles, teams, departments, roles, email outbox) and adds the following:

### EventSettings

Single record storing event/build/strike date ranges, timezone, early entry cap, early entry close datetime, and the system open date (when shift browsing opens to all volunteers).

### Rota

A group of shifts belonging to a team. Has a period type (build / event / strike), a priority, and a signup policy (Public = instant confirm / RequireApproval = lead must approve).

### Shift

A single slot within a Rota. Has start/end datetime, min/max volunteer count, and an optional admin-only flag. Duration and early-entry eligibility are computed fields.

### Project *(Phase 2)*

A multi-day commitment (build/strike only). Differs from a Rota+Shift in that staffing targets can vary day-by-day. Deferred — for 2026, model any project-style work as shifts within a rota.

### DutySignup

Links a user to a shift. States: `Pending → Confirmed → Bailed` or `Pending → Refused`. An `Enrolled` flag marks voluntold assignments. Exactly one shift FK per row.

### Email triggers

Reuses the existing `EmailOutboxMessage` + `ProcessEmailOutboxJob` infrastructure. New triggers: signup confirmed, signup refused, voluntell assignment, bail notification to lead, pre-event reminder.

---

## 6. Key Design Decisions

**No double registration.** Volunteers are already in Humans. The existing volunteer signup form feeds directly into the shift system. No separate VIM account.

**Reuse existing hierarchy.** Humans already has Departments → Teams and role-based permissions (Lead, MetaLead, Manager, Admin, NoInfo). No new entities needed for the org structure.

**Volunteer cap is real.** `MaxVolunteers` on a shift is enforced transactionally. Leads cannot exceed it without raising the cap explicitly. A global cap (total volunteers cantina can feed) is surfaced on the manager dashboard as a warning indicator, not a hard block at the shift level.

**Shift switching is automatic.** If a volunteer signs up for a shift that overlaps one they already hold, the system flags the conflict with a clear error message and offers to swap (bail the old one, confirm the new). No manual unassign-then-reassign.

**Early Entry is computed, not managed.** The EE list is generated automatically: anyone with a confirmed build-period shift gets EE, with their arrival date = their first shift start minus one day (in event timezone). No manual gate list spreadsheet.

**Soft delete over hard delete.** Deactivating a shift hides it from volunteers but preserves confirmed signups. Hard deletion is blocked if confirmed signups exist.

**Skills on profiles is Phase 2.** Humans has dietary/allergy fields and ticket ID but not skills/quirks yet. The NoInfo urgency scoring can work on priority + capacity alone for 2026; skills matching is a later enhancement.

---

## 7. Phased Delivery for Elsewhere 2026

### Phase 1 — Core (must ship)

1. EventSettings entity (dates, timezone, EE cutoff, system open date)
2. Rota + Shift entities with soft delete
3. DutySignup with state machine and transactional capacity enforcement
4. Volunteer homepage: booked shifts panel + open shifts browser (filterable)
5. Lead team page: create/edit rotas and shifts, approve/refuse signups, fill rate display
6. Conflict detection and shift-swap UX
7. Basic email notifications (confirm, refuse, voluntell, bail alert to lead, pre-event reminder)

### Phase 2 — Coordination & Exports (target: 6 weeks before event)

8. NoInfo dashboard with urgency ranking and voluntell
9. Overall rota CSV export (team / department / all)
10. Early Entry list generation and export
11. Manager dashboard (global stats, all exports, mass reminder)
12. MetaLead department dashboard

### Phase 3 — Enhancements (post-2026 or if time allows)

13. Project duty type (multi-day with per-day staffing)
14. Skills/quirks on profiles + urgency score matching
15. Cantina dietary headcount export
16. Arrival tracking / check-in
17. Year-over-year migration tooling

---

## 8. Migration from VIM

The existing VIM data (shifts, signups, team structure) is not large. For 2026:

- The org structure (departments, teams, leads) already exists in Humans — no migration needed
- Historical shift data from VIM does not need to be migrated; start fresh for 2026
- Existing VIM users who are also in Humans are already matched by email

---

## 9. Open Questions

1. **System open date** — When does shift browsing open to all volunteers? Is there a date set for 2026?
2. **Early Entry cap** — Is there a fixed total number of EE passes for 2026? Who manages exceptions?
3. **NoInfo scope** — Does NoInfo see all departments, or only their assigned area?
4. **Approval defaults** — Which teams/roles require lead approval vs. auto-confirm? Is this configured per team or per shift?
5. **Total volunteer cap** — Should there be a hard global cap (cantina limit), or just a visible warning when approaching it?
6. **Pre-event reminder timing** — How far in advance should the reminder go out? (e.g., 48h before shift start)

---

## 10. Success Criteria for 2026

- Zero manual spreadsheet exports needed for coordinators to have an aggregate view
- Volunteers can sign up, receive confirmation, and see their shifts without leaving Humans
- Early Entry list is generated automatically from the system (no manual Bruce-table process)
- NoInfo coordinator can find and fill an open shift in under 2 minutes during the event
- No "502 error — user already committed to a shift" — proper conflict messages throughout
