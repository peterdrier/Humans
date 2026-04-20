# User Guide — Design

**Status:** Approved, ready for implementation plan
**Date:** 2026-04-20
**Topic:** End-user documentation under `docs/guide/`

## Purpose

The Humans app has thorough developer-facing invariants in `docs/sections/` but no
end-user documentation. This spec defines a parallel set of **user guides** under
`docs/guide/` — one file per section — that tell humans (volunteers, coordinators,
board, admin) what each section is for and how to do the things they can do.

Out of scope:

- Translations (English only for the first pass).
- Video or animated walkthroughs.
- Actual screenshots for the first pass — placeholders only.
- Developer-facing architecture content — that stays in `docs/sections/` and
  `docs/architecture/`.

## Audience & voice

- **Audience:** end users of the app, split by role within each section.
- **Voice:** second person ("you can edit…", "to approve a volunteer, …").
- **Vocabulary:**
  - Use "humans" (not "members", "users", or "volunteers") when referring to the
    general population.
  - Use "birthday" (not "date of birth").
  - Never name the event (e.g. "Nowhere") — per project feedback memory.
  - Keep role names consistent across files (Volunteer, Colaborador, Asociado,
    Coordinator, Board member, Admin). Defined in the glossary.

## File layout

All files live under `docs/guide/`.

```
docs/guide/
├── README.md               # index — links to GettingStarted first, then sections
├── GettingStarted.md       # "where to start" — signup → profile → consent → explore
├── Glossary.md             # terms used across the guide
├── Profiles.md
├── Onboarding.md
├── LegalAndConsent.md
├── Teams.md
├── Shifts.md
├── Tickets.md
├── Camps.md
├── Campaigns.md
├── Feedback.md
├── Governance.md
├── Budget.md
├── CityPlanning.md
├── GoogleIntegration.md
├── Admin.md
└── img/                    # screenshots live here (populated over time)
```

Filenames mirror `docs/sections/` exactly so readers cross-referencing dev docs
find the matching user guide immediately.

### TOC order in `README.md`

The `README.md` index lists files in user-journey order (most universal first,
admin-only last), not alphabetically:

1. Getting Started
2. Profiles
3. Onboarding
4. Legal & Consent
5. Teams
6. Shifts
7. Tickets
8. Camps
9. Campaigns
10. Feedback
11. Governance
12. Budget
13. City Planning
14. Google Integration
15. Admin
16. Glossary (appendix)

## Per-section template

Every section file follows this skeleton. Sections that don't apply (e.g., no
coordinator-specific flows) are omitted rather than left empty.

```markdown
# <Section Name>

## What this section is for
(1–2 paragraphs. Purpose of the section, core concepts, and why a user
would come here. Link to the glossary for any defined term the first time
it appears.)

## Key pages at a glance
(A bulleted tour of the main pages/screens a user can reach. One line per
page: the page name, the URL pattern if useful, and what it's for. This
builds shared vocabulary before the task recipes.)

## As a Volunteer
### <Task title, imperative>
(Short recipe: where to go, what to click, what happens. Keep it tight —
a few steps, not a screenplay.)

### <Next task>
…

## As a Coordinator
(Assumes Volunteer knowledge. Only include tasks that require coordinator
privileges.)
### <Task>
…

## As a Board member / Admin
(Assumes Coordinator knowledge. Admin-only and board-only tasks.)
### <Task>
…

## Related sections
- [Other section](OtherSection.md) — one-line reason for the link.
```

### Rules for the template

- **Additive role layering.** Coordinator tasks assume the reader already knows
  the Volunteer tasks. Board/Admin assumes Coordinator. Don't repeat.
- **Omit empty role sections.** If a section has no coordinator-specific
  capabilities (e.g., Legal & Consent), skip "As a Coordinator" entirely.
- **Screenshot placeholders inline.** Use the exact marker
  `![TODO: screenshot — <description>]` so a future pass can grep for them.
- **Length target:** ~400–1,000 words per section file. Brief sections
  (Campaigns, Feedback) stay short; large sections (Profiles, Teams, Shifts,
  Budget) use more of the budget.
- **Linking:** internal links to other guide files are relative
  (`Teams.md`, not `/docs/guide/Teams.md`). Links to the app go to
  path-only URLs (`/Profile/Me`) — no hostnames.
- **No hallucinated content.** Per the project's memory rules, never invent
  copy (policies, benefits, resources) that isn't real in the app. Describe
  what's there; if something is admin-editable, say so rather than naming
  invented content.

## Special files

### `README.md`

Minimal index. Structure:

```markdown
# Humans — User Guide

One-paragraph intro: this is the end-user guide for the Humans app; covers
everything a human can do grouped by site section.

## Start here
- [Getting Started](GettingStarted.md)

## Section guides
- [Profiles](Profiles.md)
- [Onboarding](Onboarding.md)
- …
- [Admin](Admin.md)

## Appendix
- [Glossary](Glossary.md)
```

### `GettingStarted.md`

Role-aware walkthrough of the first few hours in the app:

1. Signing up (Google OAuth, creating the account).
2. Completing your profile.
3. Consenting to legal documents.
4. What happens after consent clears (auto-approval to Volunteers team).
5. Where to go next, split by "if you're a Volunteer / Coordinator / Board".

Length target ~600–900 words.

### `Glossary.md`

Alphabetical list of defined terms. Each entry: one-to-three-sentence
definition plus a link to the section guide where it's most relevant.

Initial term list (non-exhaustive — add as writing surfaces more):

- Admin
- Asociado
- Board
- Colaborador
- Consent Coordinator
- Coordinator
- Facilitated message
- Human
- Section
- Shared Drive
- Team
- Tier
- Volunteer

## Screenshot strategy

First pass writes **placeholders only**. Actual screenshots are filled in over
time by a documented process.

### Placeholder format

Inline in the markdown where a screenshot would help. Exact marker so it's
greppable:

```markdown
![TODO: screenshot — profile edit page showing contact-field visibility controls]
```

### Maintenance process — documented separately

`docs/architecture/screenshot-maintenance.md` (new dev-facing doc, not part of
`docs/guide/`) covers:

- **Capture environment.** Dev server runs at `nuc.home`; seed data includes
  fixtures for common screenshots.
- **Naming convention.** `docs/guide/img/<section>-<slug>.png`
  (e.g., `docs/guide/img/profiles-edit.png`).
- **Cadence.** Screenshot review added to the monthly maintenance log in
  `docs/architecture/maintenance-log.md`.
- **Finding placeholders.** `grep -r "TODO: screenshot" docs/guide/` lists
  every outstanding placeholder.
- **Replacement rule.** When filling a placeholder, replace the marker with
  the real image reference. Keep the alt text descriptive — it doubles as
  a caption.

This file is out of scope for `docs/guide/` content but is part of the
implementation plan so the process exists by the time writers want it.

## Content sourcing

Each section guide is seeded from three sources, in order of priority:

1. **`docs/sections/<Section>.md`** — authoritative on invariants, routes,
   and roles. Translate developer language into user language.
2. **The running app.** Where the sections doc is abstract, confirm the
   actual UI by visiting the page at `nuc.home`.
3. **`docs/features/`** — feature-level specs, for the "why" behind flows.

Do not write anything the app doesn't actually do. If a section has no
coordinator UI yet, the "As a Coordinator" section stays omitted — don't
document aspirational behavior.

## Ordering constraints between files

- Write `Glossary.md` first (it's an index of terms; can start empty and grow
  as each section guide is written).
- Write `Profiles.md` before `Onboarding.md` and `LegalAndConsent.md` — the
  latter two reference profile concepts.
- Write `Teams.md` before `Shifts.md` and `Camps.md` — both reference teams.
- Write `GettingStarted.md` last — it links to everything else, so it can't
  be written until the section guides exist.
- `README.md` is written last (just after `GettingStarted.md`) for the same
  reason.

## Done criteria

- All 14 section files exist and follow the template.
- `README.md`, `GettingStarted.md`, `Glossary.md` exist.
- `docs/architecture/screenshot-maintenance.md` exists and describes the
  process.
- Every section guide has at least one screenshot placeholder in a useful
  location, so the maintenance process has something to act on from day one.
- No section file contains hallucinated content or invented copy.
- All relative links resolve.
- `docs/architecture/maintenance-log.md` updated with the new screenshot-review
  cadence entry.
