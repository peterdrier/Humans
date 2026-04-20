# User Guide Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Create end-user documentation under `docs/guide/` — one file per section plus index, getting-started, glossary, and a separate dev-facing screenshot-maintenance process.

**Architecture:** Markdown docs. One file per section, mirroring `docs/sections/` filenames. Role-layered (Volunteer → Coordinator → Board/Admin ascending). Hybrid style: purpose & tour, then task recipes per role. English only. Screenshot placeholders with an external maintenance process.

**Tech Stack:** Markdown. Bash (grep, wc, ls) for verification. Git for commits.

**Spec:** `docs/superpowers/specs/2026-04-20-user-guide-design.md` (commit `167caf25`)

---

## File Structure

New files:

```
docs/guide/
├── README.md
├── GettingStarted.md
├── Glossary.md
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
└── Admin.md

docs/architecture/screenshot-maintenance.md   (new)
docs/architecture/maintenance-log.md          (modified — add cadence entry)
```

No `docs/guide/img/` directory is created in this plan — screenshots are
placeholders only. The directory comes into existence when someone fills
the first placeholder.

---

## Per-Section Template

**Every section file uses this exact skeleton.** Copy it, then fill the parts.
Omit role sections that don't apply (don't leave them empty).

```markdown
# <Section Name>

## What this section is for

<1–2 paragraphs. Purpose of the section, core concepts, why a user comes
here. Link to the Glossary the first time a defined term appears, e.g.,
`[Coordinator](Glossary.md#coordinator)`.>

## Key pages at a glance

- **<Page name>** (`<route>`) — <one-line purpose>
- **<Page name>** (`<route>`) — <one-line purpose>
- …

## As a Volunteer

### <Task title, imperative verb>

<Short recipe: where to go, what to click, what happens next. 2–6 steps or
a short paragraph. Keep it tight.>

![TODO: screenshot — <description>]

### <Next task>

…

## As a Coordinator

<Assumes Volunteer knowledge. Only coordinator-specific tasks.>

### <Task>

…

## As a Board member / Admin

<Assumes Coordinator knowledge. Admin-only and board-only tasks.>

### <Task>

…

## Related sections

- [<Other section>](<OtherSection>.md) — <one-line reason for the link>
```

### Template rules

- **Placement of role sections** — keep them in ascending privilege even when
  one is omitted. Order is always Volunteer → Coordinator → Board/Admin.
- **Screenshot placeholders** — every section file must have at least one
  `![TODO: screenshot — <description>]` marker. Put them where a screenshot
  would genuinely help (the main landing page of the section, a complex
  flow, a non-obvious UI control). The description is concrete enough that
  a future contributor knows what to capture.
- **Internal links** — relative (`Teams.md`, not `/docs/guide/Teams.md`).
- **App links** — path-only (`/Profile/Me`), no hostnames.
- **Word count target** — 400–1,000 words per file. Brief sections stay
  short; large ones use more of the budget.

### Vocabulary rules

- Use "humans" (not "members", "users", "volunteers") for the population.
- Use "birthday" (not "date of birth").
- Never name the event (e.g. "Nowhere").
- Use second person ("you can…").
- Role names: Volunteer, Colaborador, Asociado, Coordinator, Board member, Admin.

---

## Source Crosswalk

For every section guide, read these sources **in order** before writing:

| Guide file            | Primary source (`docs/sections/`) | Feature specs (`docs/features/`)                      |
|-----------------------|-----------------------------------|--------------------------------------------------------|
| Profiles.md           | Profiles.md                       | 02-profiles.md, 10-contact-fields.md, 11-preferred-email.md, 14-profile-pictures-birthdays.md, 15-membership-tiers.md, communication-preferences.md |
| Onboarding.md         | Onboarding.md                     | 16-onboarding-pipeline.md, 30-magic-link-auth.md, 01-authentication.md |
| LegalAndConsent.md    | LegalAndConsent.md                | 04-legal-documents-consent.md, gdpr-export.md         |
| Teams.md              | Teams.md                          | 06-teams.md, 17-coordinator-roles.md, 36-hidden-teams.md |
| Shifts.md             | Shifts.md                         | 25-shift-management.md, 26-shift-signup-visibility.md, 33-shift-preference-wizard.md |
| Tickets.md            | Tickets.md                        | 24-ticket-vendor-integration.md                        |
| Camps.md              | Camps.md                          | 20-camps.md                                            |
| Campaigns.md          | Campaigns.md                      | 22-campaigns.md                                        |
| Feedback.md           | Feedback.md                       | 27-feedback-system.md                                  |
| Governance.md         | Governance.md                     | 03-asociado-applications.md, 18-board-voting.md, 17-coordinator-roles.md |
| Budget.md             | Budget.md                         | 31-budget.md                                           |
| CityPlanning.md       | CityPlanning.md                   | 38-city-planning.md                                    |
| GoogleIntegration.md  | GoogleIntegration.md              | 07-google-integration.md, 32-workspace-account-provisioning.md, 13-drive-activity-monitoring.md |
| Admin.md              | Admin.md                          | 09-administration.md, 12-audit-log.md, 37-notification-inbox.md |

If a feature doc doesn't exist for a row, use only the sections/ doc.

---

## Task Ordering

Dependency rules from the spec:

- `Glossary.md` first (skeleton — grown over time).
- `Profiles.md` before `Onboarding.md` and `LegalAndConsent.md`.
- `Teams.md` before `Shifts.md` and `Camps.md`.
- `GettingStarted.md` and `README.md` last (they link to everything else).
- Final task updates `Glossary.md` with terms that surfaced during writing.
- Final task updates `docs/architecture/maintenance-log.md`.

---

### Task 1: Screenshot-maintenance process doc

**Files:**
- Create: `docs/architecture/screenshot-maintenance.md`

- [ ] **Step 1: Write `docs/architecture/screenshot-maintenance.md`**

Contents:

```markdown
# Screenshot Maintenance

Process for keeping screenshots in `docs/guide/` current. User guides ship with
placeholders (`![TODO: screenshot — <description>]`); real screenshots get
filled in over time.

## Capture environment

- The dev server runs on `nuc.home`. Start it locally with `dotnet run --project src/Humans.Web` if needed.
- Seed data via the dev seed login gives you a reliable fixture set for common screenshots.
- Use a consistent browser (Chrome/Edge at 1440×900 is the default) so screenshots feel uniform.
- Capture at 2× for retina clarity, but save as PNG at standard resolution.

## Naming convention

- Store images at `docs/guide/img/<section>-<slug>.png`.
  - `<section>` is the lowercased section filename (e.g., `profiles`, `teams`, `legalandconsent`).
  - `<slug>` is a short kebab-case description of what's in the shot (e.g., `edit`, `contact-field-visibility`).
- Examples:
  - `docs/guide/img/profiles-edit.png`
  - `docs/guide/img/shifts-signup-page.png`
  - `docs/guide/img/admin-audit-log.png`

## Replacing a placeholder

1. Find a TODO marker: `grep -rn "TODO: screenshot" docs/guide/`.
2. Capture the screenshot and save it at the naming convention above.
3. Replace the placeholder in the markdown:
   - Before: `![TODO: screenshot — profile edit page showing contact-field visibility controls]`
   - After: `![Profile edit page showing contact-field visibility controls](img/profiles-edit.png)`
4. The alt text doubles as a caption — keep it descriptive.

## Cadence

Screenshot review is part of monthly maintenance. Each month:

1. List outstanding placeholders: `grep -rn "TODO: screenshot" docs/guide/`.
2. Open the app at `nuc.home` and spot-check existing screenshots against the live UI.
3. Replace outdated images (UI has changed) and fill in any placeholders that have become important.
4. Log the review in `docs/architecture/maintenance-log.md`.

Don't chase perfection — a placeholder in a low-traffic guide is fine. Priorities:

1. `GettingStarted.md` and `Profiles.md` (every user sees these).
2. Sections with complex UI flows (Shifts, Budget, Camps).
3. Everything else.
```

- [ ] **Step 2: Verify file exists**

```bash
ls -l docs/architecture/screenshot-maintenance.md
```

Expected: the file is present and > 1 KB.

- [ ] **Step 3: Commit**

```bash
git add docs/architecture/screenshot-maintenance.md
git commit -m "Add screenshot maintenance process doc"
```

---

### Task 2: Glossary.md skeleton

**Files:**
- Create: `docs/guide/Glossary.md`

- [ ] **Step 1: Write `docs/guide/Glossary.md` with seed terms**

Contents:

```markdown
# Glossary

Terms used across the Humans user guide. Each entry: a short definition plus
a link to the section guide where it's most relevant.

## Admin

A human with the `Admin` role — global superset privilege over the whole app.
See [Admin](Admin.md).

## Asociado

A voting member with governance rights (assemblies, elections). Requires an
application and a Board vote. 2-year term. See [Governance](Governance.md).

## Board

The governance body of Nobodies Collective. Approves tier applications, votes
on governance matters. See [Governance](Governance.md).

## Colaborador

An active contributor with project or event responsibilities. Requires an
application and a Board vote. 2-year term. See [Governance](Governance.md).

## Consent Coordinator

A coordinator role responsible for reviewing a volunteer's consent status
and clearing it for activation. See [Legal & Consent](LegalAndConsent.md)
and [Onboarding](Onboarding.md).

## Coordinator

A role assigned to humans who lead a team or a section. Coordinator scope
is specific (team coordinator, section coordinator, consent coordinator).
See [Teams](Teams.md).

## Facilitated message

A message sent between humans through the app so personal contact details
stay private. See [Profiles](Profiles.md).

## Human

The word the app uses for everyone in the system — we don't say "member",
"user", or "volunteer" for the population at large. Individual capabilities
depend on role.

## Section

A vertical area of the app (Profiles, Teams, Shifts, Camps, Budget, Admin,
etc.). Each section has its own guide file.

## Shared Drive

A Google Drive shared with a team. The app manages only Shared Drives — not
personal My Drive folders. See [Google Integration](GoogleIntegration.md).

## Team

A group of humans organized around a shared purpose. Teams have members,
coordinators, and optionally a Google Group and Shared Drive.
See [Teams](Teams.md).

## Tier

Membership tier — Volunteer (default), Colaborador, or Asociado. Tracked on
the profile. See [Governance](Governance.md).

## Volunteer

The default membership tier — the standard human. Everyone starts here after
completing signup and consent. See [Onboarding](Onboarding.md).
```

- [ ] **Step 2: Verify file**

```bash
wc -w docs/guide/Glossary.md
```

Expected: word count > 200.

- [ ] **Step 3: Commit**

```bash
git add docs/guide/Glossary.md
git commit -m "Add user-guide glossary skeleton"
```

---

### Task 3: Profiles.md

**Files:**
- Create: `docs/guide/Profiles.md`
- Read: `docs/sections/Profiles.md`, `docs/features/02-profiles.md`, `docs/features/10-contact-fields.md`, `docs/features/11-preferred-email.md`, `docs/features/14-profile-pictures-birthdays.md`, `docs/features/15-membership-tiers.md`, `docs/features/communication-preferences.md`

- [ ] **Step 1: Read all source docs listed above**

Use the Read tool on each source file. Note: routes, what a Volunteer can do on their own profile, what other active humans see, what Admin can do. Pay special attention to contact-field visibility levels (BoardOnly / CoordinatorsAndBoard / MyTeams / AllActiveProfiles) and the deletion flow.

- [ ] **Step 2: Write `docs/guide/Profiles.md` following the template**

Use the Per-Section Template from the plan header. Fill each section from the sources:

- **What this section is for** — profiles hold personal info + contact fields; visibility is per-field; birthday (month/day only).
- **Key pages at a glance** — `/Profile/Me`, `/Profile/Me/Edit`, `/Profile/Me/Emails`, `/Profile/Me/ShiftInfo`, `/Profile/Me/Notifications`, `/Profile/Me/Privacy`, `/Profile/Me/Outbox`, `/Profile/{id}`, `/Profile/Search`.
- **As a Volunteer** — view your profile, edit your profile, manage contact fields (with visibility), upload profile picture, manage email addresses, set communication preferences, send facilitated messages, request data export, request deletion.
- **As a Coordinator** — no coordinator-specific tasks in Profiles. OMIT this section.
- **As a Board member / Admin** — view any profile with full detail, suspend/unsuspend, approve/reject signup, manage roles on a human, view a human's outbox.
- **Related sections** — Legal & Consent (consent status gates activation), Teams (profile visibility depends on shared team membership), Onboarding (profile completion is a step).

At least one screenshot placeholder — `![TODO: screenshot — profile edit page showing contact-field visibility controls]` — on the edit page section is a good default.

Word count target: 700–1,000.

- [ ] **Step 3: Run quality checks on the file**

```bash
grep -n "TODO: screenshot" docs/guide/Profiles.md
grep -ni "date of birth" docs/guide/Profiles.md
grep -n "Nowhere" docs/guide/Profiles.md
wc -w docs/guide/Profiles.md
```

Expected:
- At least one `TODO: screenshot` line.
- Zero hits for "date of birth".
- Zero hits for "Nowhere".
- Word count between 400 and 1,200.

If any check fails, fix it before committing.

- [ ] **Step 4: Commit**

```bash
git add docs/guide/Profiles.md
git commit -m "Add Profiles user guide"
```

---

### Task 4: Teams.md

**Files:**
- Create: `docs/guide/Teams.md`
- Read: `docs/sections/Teams.md`, `docs/features/06-teams.md`, `docs/features/17-coordinator-roles.md`, `docs/features/36-hidden-teams.md`

- [ ] **Step 1: Read all source docs listed above**

Note: team hierarchy, member vs coordinator, hidden teams, team-linked Google Group / Shared Drive, joining/leaving a team.

- [ ] **Step 2: Write `docs/guide/Teams.md` following the template**

Key content:

- **What this section is for** — teams are how humans organize around shared purpose; each team can have coordinators and optionally a Google Group + Shared Drive; some teams are hidden.
- **Key pages at a glance** — team list, team detail, team members, join/leave, team admin pages (roles/settings).
- **As a Volunteer** — view teams you're in, see other team members (subject to visibility), leave a team, browse and join non-restricted teams.
- **As a Coordinator** — manage team members (add/remove), edit team description, manage Google Group/Drive settings, assign or revoke coordinator roles within the team.
- **As a Board member / Admin** — create new teams, delete teams, set hidden flag, manage all teams site-wide.
- **Related sections** — Profiles (team membership affects profile visibility), Shifts (shifts are team-owned), Camps (camps are team-owned), Google Integration (team ↔ Group/Drive).

At least one screenshot placeholder.

Word count target: 600–900.

- [ ] **Step 3: Run quality checks**

```bash
grep -n "TODO: screenshot" docs/guide/Teams.md
grep -ni "date of birth" docs/guide/Teams.md
grep -n "Nowhere" docs/guide/Teams.md
wc -w docs/guide/Teams.md
```

Expected: one screenshot placeholder, zero banned terms, word count in range.

- [ ] **Step 4: Commit**

```bash
git add docs/guide/Teams.md
git commit -m "Add Teams user guide"
```

---

### Task 5: Onboarding.md

**Files:**
- Create: `docs/guide/Onboarding.md`
- Read: `docs/sections/Onboarding.md`, `docs/features/16-onboarding-pipeline.md`, `docs/features/30-magic-link-auth.md`, `docs/features/01-authentication.md`

- [ ] **Step 1: Read all source docs listed above**

Note: the pipeline stages, auth options, what auto-approval requires, how Consent Coordinator clearing works.

- [ ] **Step 2: Write `docs/guide/Onboarding.md` following the template**

Key content:

- **What this section is for** — the path from account creation to activated Volunteer.
- **Key pages at a glance** — signup page, onboarding pipeline dashboard, consent flow pages.
- **As a Volunteer (new)** — sign up (Google or magic link), complete profile, consent to legal documents, wait for Consent Coordinator clearance, get added to Volunteers team.
- **As a Coordinator** — no section here unless you're a Consent Coordinator (mention that path exists and point to Legal & Consent).
- **As a Board member / Admin** — view onboarding pipeline, manually approve/reject stuck applicants, run the onboarding audit.
- **Related sections** — Profiles, Legal & Consent, Teams.

Cross-link to GettingStarted.md for the user-journey walkthrough.

Word count target: 500–800.

- [ ] **Step 3: Run quality checks**

```bash
grep -n "TODO: screenshot" docs/guide/Onboarding.md
grep -ni "date of birth" docs/guide/Onboarding.md
grep -n "Nowhere" docs/guide/Onboarding.md
wc -w docs/guide/Onboarding.md
```

Expected: at least one screenshot placeholder, zero banned terms.

- [ ] **Step 4: Commit**

```bash
git add docs/guide/Onboarding.md
git commit -m "Add Onboarding user guide"
```

---

### Task 6: LegalAndConsent.md

**Files:**
- Create: `docs/guide/LegalAndConsent.md`
- Read: `docs/sections/LegalAndConsent.md`, `docs/features/04-legal-documents-consent.md`, `docs/features/gdpr-export.md`

- [ ] **Step 1: Read all source docs listed above**

Note: ConsentRecord is append-only (immutable via DB trigger), document versioning, how a new version invalidates old consent, Consent Coordinator workflow, GDPR export and deletion.

- [ ] **Step 2: Write `docs/guide/LegalAndConsent.md` following the template**

Key content:

- **What this section is for** — required legal documents, consenting to them, GDPR rights.
- **Key pages at a glance** — consent dashboard (`/Profile/Me` shows consent status), legal document viewer, admin consent management pages.
- **As a Volunteer** — sign consent on each required document, request data export (Article 15), request account deletion (Article 17), view consent history.
- **As a Coordinator (Consent Coordinator)** — review pending volunteers, clear or flag consent status, investigate flagged accounts.
- **As a Board member / Admin** — manage legal documents, publish new document versions, view consent records site-wide, run GDPR reports.
- **Related sections** — Profiles, Onboarding, Admin.

Note: ConsentRecord immutability is a user-facing promise ("your signed consent is permanent record") — mention it in the Volunteer section.

Word count target: 500–800.

- [ ] **Step 3: Run quality checks**

```bash
grep -n "TODO: screenshot" docs/guide/LegalAndConsent.md
grep -ni "date of birth" docs/guide/LegalAndConsent.md
grep -n "Nowhere" docs/guide/LegalAndConsent.md
wc -w docs/guide/LegalAndConsent.md
```

- [ ] **Step 4: Commit**

```bash
git add docs/guide/LegalAndConsent.md
git commit -m "Add Legal & Consent user guide"
```

---

### Task 7: Shifts.md

**Files:**
- Create: `docs/guide/Shifts.md`
- Read: `docs/sections/Shifts.md`, `docs/features/25-shift-management.md`, `docs/features/26-shift-signup-visibility.md`, `docs/features/33-shift-preference-wizard.md`

- [ ] **Step 1: Read all source docs listed above**

Note: shift signup flow, visibility rules (who sees what shifts), the preference wizard, coordinator workflow for scheduling.

- [ ] **Step 2: Write `docs/guide/Shifts.md` following the template**

Key content:

- **What this section is for** — the system for scheduling and signing up for shifts.
- **Key pages at a glance** — shift browse, shift signup, my shifts, shift preferences (`/Profile/Me/ShiftInfo`), coordinator scheduling pages.
- **As a Volunteer** — set shift preferences (wizard), browse available shifts, sign up for shifts, see shifts you're scheduled for, cancel a shift signup.
- **As a Coordinator** — create shifts for your team, assign volunteers, edit shift details, cancel shifts, see team shift coverage.
- **As a Board member / Admin** — site-wide shift view, edit any team's shifts.
- **Related sections** — Teams, Profiles, Camps.

Word count target: 700–1,000.

- [ ] **Step 3: Run quality checks**

```bash
grep -n "TODO: screenshot" docs/guide/Shifts.md
grep -ni "date of birth" docs/guide/Shifts.md
grep -n "Nowhere" docs/guide/Shifts.md
wc -w docs/guide/Shifts.md
```

- [ ] **Step 4: Commit**

```bash
git add docs/guide/Shifts.md
git commit -m "Add Shifts user guide"
```

---

### Task 8: Camps.md

**Files:**
- Create: `docs/guide/Camps.md`
- Read: `docs/sections/Camps.md`, `docs/features/20-camps.md`

- [ ] **Step 1: Read all source docs listed above**

Note: camps are team-owned; barrio map and polygons; placement vs hold dates; camp admin vs team coordinator.

- [ ] **Step 2: Write `docs/guide/Camps.md` following the template**

Key content:

- **What this section is for** — camps are physical groups of humans sharing infrastructure at the event; each camp has a placement on the map.
- **Key pages at a glance** — camp browse, camp detail, barrio map, my camp (if in one).
- **As a Volunteer** — browse camps, join/apply to a camp, view camp members (subject to visibility), see camp location on map.
- **As a Coordinator (Camp Coordinator)** — manage camp roster, edit camp details, set camp location preference.
- **As a Board member / Admin (Camp Admin)** — all coordinator tasks on any camp, barrio map placement, polygon editing, publish placements.
- **Related sections** — Teams (camps are team-owned), City Planning (barrio map).

Word count target: 600–900.

- [ ] **Step 3: Run quality checks**

```bash
grep -n "TODO: screenshot" docs/guide/Camps.md
grep -ni "date of birth" docs/guide/Camps.md
grep -n "Nowhere" docs/guide/Camps.md
wc -w docs/guide/Camps.md
```

- [ ] **Step 4: Commit**

```bash
git add docs/guide/Camps.md
git commit -m "Add Camps user guide"
```

---

### Task 9: Tickets.md

**Files:**
- Create: `docs/guide/Tickets.md`
- Read: `docs/sections/Tickets.md`, `docs/features/24-ticket-vendor-integration.md`

- [ ] **Step 1: Read all source docs listed above**

- [ ] **Step 2: Write `docs/guide/Tickets.md` following the template**

Key content:

- **What this section is for** — the ticket system for event attendance (vendor-integrated).
- **Key pages at a glance** — my tickets, ticket purchase page, admin ticket list.
- **As a Volunteer** — see the tickets linked to your account, claim/redeem tickets, understand ticket statuses.
- **As a Coordinator** — omit unless specifically applicable.
- **As a Board member / Admin (Ticket Admin)** — manage tickets, reconcile vendor data, issue/revoke tickets.
- **Related sections** — Profiles, Admin.

Word count target: 400–700.

- [ ] **Step 3: Run quality checks**

```bash
grep -n "TODO: screenshot" docs/guide/Tickets.md
grep -ni "date of birth" docs/guide/Tickets.md
grep -n "Nowhere" docs/guide/Tickets.md
wc -w docs/guide/Tickets.md
```

- [ ] **Step 4: Commit**

```bash
git add docs/guide/Tickets.md
git commit -m "Add Tickets user guide"
```

---

### Task 10: Campaigns.md

**Files:**
- Create: `docs/guide/Campaigns.md`
- Read: `docs/sections/Campaigns.md`, `docs/features/22-campaigns.md`

- [ ] **Step 1: Read all source docs listed above**

- [ ] **Step 2: Write `docs/guide/Campaigns.md` following the template**

Key content:

- **What this section is for** — campaigns are coordinated pushes (recruitment, donation, communications) with codes and grants.
- **Key pages at a glance** — campaign list, campaign detail, my grants.
- **As a Volunteer** — redeem a campaign code, view your grants.
- **As a Coordinator** — omit unless applicable.
- **As a Board member / Admin** — create campaigns, manage codes, view metrics.
- **Related sections** — Profiles, Admin.

Word count target: 400–700.

- [ ] **Step 3: Run quality checks**

```bash
grep -n "TODO: screenshot" docs/guide/Campaigns.md
grep -ni "date of birth" docs/guide/Campaigns.md
grep -n "Nowhere" docs/guide/Campaigns.md
wc -w docs/guide/Campaigns.md
```

- [ ] **Step 4: Commit**

```bash
git add docs/guide/Campaigns.md
git commit -m "Add Campaigns user guide"
```

---

### Task 11: Feedback.md

**Files:**
- Create: `docs/guide/Feedback.md`
- Read: `docs/sections/Feedback.md`, `docs/features/27-feedback-system.md`

- [ ] **Step 1: Read all source docs listed above**

- [ ] **Step 2: Write `docs/guide/Feedback.md` following the template**

Key content:

- **What this section is for** — how humans report bugs, suggest features, and give feedback on the app.
- **Key pages at a glance** — feedback form, my submitted feedback.
- **As a Volunteer** — submit feedback, attach screenshots, see responses to your feedback.
- **As a Coordinator** — omit.
- **As a Board member / Admin** — triage feedback, respond, create GitHub issues, close resolved feedback.
- **Related sections** — Admin.

Word count target: 400–600.

- [ ] **Step 3: Run quality checks**

```bash
grep -n "TODO: screenshot" docs/guide/Feedback.md
grep -ni "date of birth" docs/guide/Feedback.md
grep -n "Nowhere" docs/guide/Feedback.md
wc -w docs/guide/Feedback.md
```

- [ ] **Step 4: Commit**

```bash
git add docs/guide/Feedback.md
git commit -m "Add Feedback user guide"
```

---

### Task 12: Governance.md

**Files:**
- Create: `docs/guide/Governance.md`
- Read: `docs/sections/Governance.md`, `docs/features/03-asociado-applications.md`, `docs/features/18-board-voting.md`, `docs/features/17-coordinator-roles.md`

- [ ] **Step 1: Read all source docs listed above**

Note: Volunteer ≠ tier application. Tier applications are Colaborador / Asociado only. 2-year terms. Board vote workflow.

- [ ] **Step 2: Write `docs/guide/Governance.md` following the template**

Key content — open with a callout that Governance is NOT about becoming a volunteer; it's about Colaborador and Asociado tiers:

- **What this section is for** — tier applications, Board voting, coordinator role assignments, 2-year terms.
- **Key pages at a glance** — my application, application history, board voting dashboard, role management.
- **As a Volunteer** — apply for Colaborador or Asociado, withdraw an application, see application status.
- **As a Coordinator** — omit unless you have a governance-specific coordinator role (e.g., Consent Coordinator is already covered in Legal & Consent).
- **As a Board member / Admin** — vote on applications, approve/reject, assign/revoke coordinator roles, see governance audit.
- **Related sections** — Profiles, Legal & Consent, Admin.

Word count target: 700–1,000.

- [ ] **Step 3: Run quality checks**

```bash
grep -n "TODO: screenshot" docs/guide/Governance.md
grep -ni "date of birth" docs/guide/Governance.md
grep -n "Nowhere" docs/guide/Governance.md
wc -w docs/guide/Governance.md
```

- [ ] **Step 4: Commit**

```bash
git add docs/guide/Governance.md
git commit -m "Add Governance user guide"
```

---

### Task 13: Budget.md

**Files:**
- Create: `docs/guide/Budget.md`
- Read: `docs/sections/Budget.md`, `docs/features/31-budget.md`

- [ ] **Step 1: Read all source docs listed above**

Note: BudgetYear → BudgetGroup → BudgetCategory → BudgetLineItem hierarchy; append-only audit log; typical roles (Treasurer, Team Coordinators, Board).

- [ ] **Step 2: Write `docs/guide/Budget.md` following the template**

Key content:

- **What this section is for** — annual budget planning and tracking at four levels (Year → Group → Category → Line Item).
- **Key pages at a glance** — budget year list, budget year detail, category editor, line item editor, budget audit log.
- **As a Volunteer** — view the approved budget (if public).
- **As a Coordinator (Team Coordinator with budget access)** — propose/edit line items for your team's category, see spending against your lines.
- **As a Board member / Admin / Treasurer** — create a BudgetYear, structure groups and categories, approve the budget, lock/unlock years, view the full audit log.
- **Related sections** — Teams, Admin, Governance.

Word count target: 800–1,000.

- [ ] **Step 3: Run quality checks**

```bash
grep -n "TODO: screenshot" docs/guide/Budget.md
grep -ni "date of birth" docs/guide/Budget.md
grep -n "Nowhere" docs/guide/Budget.md
wc -w docs/guide/Budget.md
```

- [ ] **Step 4: Commit**

```bash
git add docs/guide/Budget.md
git commit -m "Add Budget user guide"
```

---

### Task 14: CityPlanning.md

**Files:**
- Create: `docs/guide/CityPlanning.md`
- Read: `docs/sections/CityPlanning.md`, `docs/features/38-city-planning.md`

- [ ] **Step 1: Read all source docs listed above**

Note: barrio map, polygon editing, camp placement, sound zones, official-zone overlays, history.

- [ ] **Step 2: Write `docs/guide/CityPlanning.md` following the template**

Key content:

- **What this section is for** — the map-based tools for arranging camps, barrios, and sound zones.
- **Key pages at a glance** — barrio map, polygon editor, sound-zone overlay, placement history.
- **As a Volunteer** — view the map, find your camp.
- **As a Coordinator** — propose placement changes (if granted that capability).
- **As a Board member / Admin (City Planning Admin)** — draw polygons, set sound zones, publish placements, review history.
- **Related sections** — Camps, Admin.

Word count target: 500–800.

- [ ] **Step 3: Run quality checks**

```bash
grep -n "TODO: screenshot" docs/guide/CityPlanning.md
grep -ni "date of birth" docs/guide/CityPlanning.md
grep -n "Nowhere" docs/guide/CityPlanning.md
wc -w docs/guide/CityPlanning.md
```

- [ ] **Step 4: Commit**

```bash
git add docs/guide/CityPlanning.md
git commit -m "Add City Planning user guide"
```

---

### Task 15: GoogleIntegration.md

**Files:**
- Create: `docs/guide/GoogleIntegration.md`
- Read: `docs/sections/GoogleIntegration.md`, `docs/features/07-google-integration.md`, `docs/features/32-workspace-account-provisioning.md`, `docs/features/13-drive-activity-monitoring.md`

- [ ] **Step 1: Read all source docs listed above**

Note: Shared Drives only (no My Drive), sync modes (None / AddOnly / AddAndRemove), per-service reconciliation, workspace account provisioning.

- [ ] **Step 2: Write `docs/guide/GoogleIntegration.md` following the template**

Key content:

- **What this section is for** — how the app links teams to Google Groups and Shared Drives, and how accounts get provisioned.
- **Key pages at a glance** — admin sync settings (`/Admin/SyncSettings`), team Google settings, reconciliation dashboard, drive activity monitor.
- **As a Volunteer** — understand why they got a Google workspace account, set their Google service email (`/Profile/Me`).
- **As a Coordinator** — manage the Google Group/Drive links for your team.
- **As a Board member / Admin** — configure sync modes, run reconciliation, review sync audit, monitor drive activity.
- **Related sections** — Teams, Profiles, Admin.

Word count target: 700–1,000.

- [ ] **Step 3: Run quality checks**

```bash
grep -n "TODO: screenshot" docs/guide/GoogleIntegration.md
grep -ni "date of birth" docs/guide/GoogleIntegration.md
grep -n "Nowhere" docs/guide/GoogleIntegration.md
wc -w docs/guide/GoogleIntegration.md
```

- [ ] **Step 4: Commit**

```bash
git add docs/guide/GoogleIntegration.md
git commit -m "Add Google Integration user guide"
```

---

### Task 16: Admin.md

**Files:**
- Create: `docs/guide/Admin.md`
- Read: `docs/sections/Admin.md`, `docs/features/09-administration.md`, `docs/features/12-audit-log.md`, `docs/features/37-notification-inbox.md`

- [ ] **Step 1: Read all source docs listed above**

- [ ] **Step 2: Write `docs/guide/Admin.md` following the template**

Admin is admin-only, so the "As a Volunteer" and "As a Coordinator" sections should be omitted or reduced to a single note that Admin pages aren't visible at those roles.

Key content:

- **What this section is for** — global admin functions: human management, sync settings, audit log, notifications, seed data, feature flags.
- **Key pages at a glance** — admin dashboard, human list, sync settings, audit log, notifications inbox, other admin-only tools.
- **As a Volunteer** — a single sentence: Admin pages are not visible to you.
- **As a Coordinator** — omit (or one sentence: the Coordinator role does not include admin access; domain admins like Teams Admin / Camp Admin / Ticket Admin are separate roles covered in their sections).
- **As a Board member / Admin** — manage humans (suspend/unsuspend, roles), configure sync, read audit log, triage notifications.
- **Related sections** — Profiles, Teams, Google Integration, Feedback.

Word count target: 700–1,000.

- [ ] **Step 3: Run quality checks**

```bash
grep -n "TODO: screenshot" docs/guide/Admin.md
grep -ni "date of birth" docs/guide/Admin.md
grep -n "Nowhere" docs/guide/Admin.md
wc -w docs/guide/Admin.md
```

- [ ] **Step 4: Commit**

```bash
git add docs/guide/Admin.md
git commit -m "Add Admin user guide"
```

---

### Task 17: Refine Glossary.md

**Files:**
- Modify: `docs/guide/Glossary.md`

- [ ] **Step 1: Collect defined terms actually used**

```bash
grep -hn "\[.*\](Glossary.md" docs/guide/*.md | sort -u
```

Expected: a list of every term that section guides link to in the Glossary. Compare this list against the entries in `Glossary.md`.

- [ ] **Step 2: Add missing terms; remove unreferenced ones**

For each term referenced in a section guide but missing from the Glossary, add an entry following the existing format (heading + short definition + "See [Section](File.md)"). Remove Glossary entries that no section guide references.

- [ ] **Step 3: Verify**

```bash
wc -w docs/guide/Glossary.md
grep -c "^## " docs/guide/Glossary.md
```

Expected: word count grew; term count (second command) matches the unique list from Step 1.

- [ ] **Step 4: Commit**

```bash
git add docs/guide/Glossary.md
git commit -m "Refine Glossary to match section-guide references"
```

---

### Task 18: GettingStarted.md

**Files:**
- Create: `docs/guide/GettingStarted.md`
- Read: all completed guide files in `docs/guide/` (for accurate cross-links) and `docs/sections/Onboarding.md`.

- [ ] **Step 1: Re-read the completed guide files briefly**

Use Read or Grep to confirm the routes and task names in the other guides so the links from GettingStarted are accurate.

- [ ] **Step 2: Write `docs/guide/GettingStarted.md`**

Structure:

```markdown
# Getting Started

Welcome to the Humans app. This page walks you through your first hour.

## 1. Sign up

<Describe Google OAuth + magic-link options. Link to Onboarding.md for
detail on the pipeline.>

## 2. Complete your profile

<What you must fill in, why contact-field visibility matters. Link to
Profiles.md.>

## 3. Consent to legal documents

<Why, what happens next (Consent Coordinator review). Link to
LegalAndConsent.md.>

## 4. You're a Volunteer

<What gets added automatically (Volunteers team, Google account if
configured), what you can now do.>

## Where to go next

### If you're a Volunteer

- [Teams](Teams.md) — browse and join teams
- [Shifts](Shifts.md) — sign up for shifts
- [Profiles](Profiles.md) — manage your emails and preferences

### If you're a Coordinator

- [Teams](Teams.md) — manage your team
- [Shifts](Shifts.md) — schedule volunteers

### If you're a Board member or Admin

- [Governance](Governance.md) — tier applications, votes
- [Admin](Admin.md) — global tools
- [Budget](Budget.md) — fiscal planning

## Key terms

See the [Glossary](Glossary.md) for defined terms used throughout the guide.
```

Fill in the prose under each heading. Word count target: 600–900.

- [ ] **Step 3: Run quality checks**

```bash
grep -n "TODO: screenshot" docs/guide/GettingStarted.md
grep -ni "date of birth" docs/guide/GettingStarted.md
grep -n "Nowhere" docs/guide/GettingStarted.md
wc -w docs/guide/GettingStarted.md
```

Expected: at least one `TODO: screenshot`, zero banned terms.

- [ ] **Step 4: Commit**

```bash
git add docs/guide/GettingStarted.md
git commit -m "Add Getting Started user guide"
```

---

### Task 19: README.md index

**Files:**
- Create: `docs/guide/README.md`

- [ ] **Step 1: Write `docs/guide/README.md`**

Exact contents:

```markdown
# Humans — User Guide

This is the end-user guide for the Humans app. It covers what each section is
for and how to use it, organized by role (Volunteer → Coordinator → Board /
Admin) within each section. If you're new, start with Getting Started.

For developer-facing invariants and architecture, see
[`docs/sections/`](../sections/) and [`docs/architecture/`](../architecture/).

## Start here

- [Getting Started](GettingStarted.md)

## Section guides

1. [Profiles](Profiles.md)
2. [Onboarding](Onboarding.md)
3. [Legal & Consent](LegalAndConsent.md)
4. [Teams](Teams.md)
5. [Shifts](Shifts.md)
6. [Tickets](Tickets.md)
7. [Camps](Camps.md)
8. [Campaigns](Campaigns.md)
9. [Feedback](Feedback.md)
10. [Governance](Governance.md)
11. [Budget](Budget.md)
12. [City Planning](CityPlanning.md)
13. [Google Integration](GoogleIntegration.md)
14. [Admin](Admin.md)

## Appendix

- [Glossary](Glossary.md)
```

- [ ] **Step 2: Verify the file exists and links resolve**

```bash
ls -l docs/guide/README.md
for f in GettingStarted.md Profiles.md Onboarding.md LegalAndConsent.md Teams.md Shifts.md Tickets.md Camps.md Campaigns.md Feedback.md Governance.md Budget.md CityPlanning.md GoogleIntegration.md Admin.md Glossary.md; do
  test -f docs/guide/$f && echo "OK $f" || echo "MISSING $f"
done
```

Expected: every file prints `OK`. No `MISSING` lines.

- [ ] **Step 3: Commit**

```bash
git add docs/guide/README.md
git commit -m "Add user guide index"
```

---

### Task 20: Final verification and maintenance log update

**Files:**
- Modify: `docs/architecture/maintenance-log.md`

- [ ] **Step 1: Run full link-resolution check**

For every markdown link in `docs/guide/*.md`, verify the target exists. One-liner:

```bash
cd docs/guide && \
for f in *.md; do
  grep -oE '\]\(([^)]+\.md)(#[^)]+)?\)' "$f" | sed -E 's/^\]\(//; s/\).*//; s/#.*//' | while read link; do
    target="$link"
    case "$link" in
      /*) echo "ABSOLUTE-LINK $f -> $link (should be relative)" ;;
      http*) ;;
      *) [ -f "$target" ] || echo "BROKEN $f -> $link" ;;
    esac
  done
done
cd ../..
```

Expected: no output. Any `BROKEN` or `ABSOLUTE-LINK` line must be fixed in the offending file before proceeding.

- [ ] **Step 2: Run the full banned-terms sweep**

```bash
grep -rni "date of birth" docs/guide/ || echo "clean: date of birth"
grep -rn "Nowhere" docs/guide/ || echo "clean: Nowhere"
grep -rni "\bmember\b" docs/guide/ | grep -vi "board member\|team member"
```

Expected:
- `clean: date of birth`
- `clean: Nowhere`
- The third command may return a few matches; audit each — anything referring to "the member" or "this member" for the general population should be rewritten to "this human" or similar. Fix as needed.

- [ ] **Step 3: Verify every section file has a screenshot placeholder**

```bash
for f in docs/guide/Profiles.md docs/guide/Onboarding.md docs/guide/LegalAndConsent.md docs/guide/Teams.md docs/guide/Shifts.md docs/guide/Tickets.md docs/guide/Camps.md docs/guide/Campaigns.md docs/guide/Feedback.md docs/guide/Governance.md docs/guide/Budget.md docs/guide/CityPlanning.md docs/guide/GoogleIntegration.md docs/guide/Admin.md docs/guide/GettingStarted.md; do
  n=$(grep -c "TODO: screenshot" "$f")
  [ "$n" -ge 1 ] && echo "OK $f ($n placeholder(s))" || echo "MISSING $f"
done
```

Expected: every line prints `OK`. No `MISSING`.

- [ ] **Step 4: Append entry to `docs/architecture/maintenance-log.md`**

Read the file first and match its existing format. Add a new dated entry for today (2026-04-20) and a recurring cadence entry for screenshot review. Example shape (adjust to match the existing format in the file):

```markdown
## 2026-04-20

- Added user guide under `docs/guide/` (one file per section, plus README, GettingStarted, Glossary). See `docs/superpowers/plans/2026-04-20-user-guide.md`.
- New recurring task: **Screenshot review** — monthly. Review outstanding `TODO: screenshot` placeholders in `docs/guide/` and spot-check existing screenshots against the live UI at `nuc.home`. Process: `docs/architecture/screenshot-maintenance.md`.
```

- [ ] **Step 5: Commit**

```bash
git add docs/architecture/maintenance-log.md
git commit -m "Log user-guide addition and screenshot-review cadence"
```

- [ ] **Step 6: Final listing**

```bash
ls docs/guide/
```

Expected: README.md, GettingStarted.md, Glossary.md, and 14 section files (Admin.md, Budget.md, Campaigns.md, Camps.md, CityPlanning.md, Feedback.md, GoogleIntegration.md, Governance.md, LegalAndConsent.md, Onboarding.md, Profiles.md, Shifts.md, Teams.md, Tickets.md). 17 files total.

---

## Done criteria (must all be true)

- 17 files in `docs/guide/`: `README.md`, `GettingStarted.md`, `Glossary.md`, and one per section (14).
- `docs/architecture/screenshot-maintenance.md` exists and documents capture, naming, replacement, and cadence.
- `docs/architecture/maintenance-log.md` has today's entry and a screenshot-review cadence entry.
- Every section guide has at least one `TODO: screenshot` placeholder.
- Zero banned terms: no `Nowhere`, no `date of birth`.
- Every relative markdown link in `docs/guide/` resolves.
- All commits on `main` (per repo convention for small changes).
