# Section-Based E2E Test Suite Design

**Goal:** Restructure the Playwright e2e tests from flat files + auth directory into vertical section-based organization. Each spec file maps to one or more feature PRDs and contains both functional happy-path tests (for the section's primary roles) and boundary tests (unauthorized roles blocked). Tests run against preview environments with DevAuth.

**Tech Stack:** Playwright, TypeScript, Chromium (headless), DevAuth personas

**Preview Environment:** `https://{PR_ID}.n.burn.camp` with `DevAuth__Enabled=true`

---

## File Structure

```
tests/e2e/
├── helpers/
│   └── auth.ts                 # Login helpers + expectBlocked utility
├── tests/
│   ├── login.spec.ts           → 01-authentication
│   ├── profile.spec.ts         → 02-profiles
│   ├── teams.spec.ts           → 06-teams + 17-coordinator-roles
│   ├── shifts.spec.ts          → 25-shift-management
│   ├── camps.spec.ts           → 20-camps
│   ├── board.spec.ts           → 09-administration (Board) + 18-board-voting
│   ├── onboarding.spec.ts      → 16-onboarding-pipeline + 17-coordinator-roles
│   ├── admin.spec.ts           → 09-administration (Admin)
│   ├── tickets.spec.ts         → 24-ticket-vendor-integration
│   └── feedback.spec.ts        → 27-feedback-system
├── playwright.config.ts
├── package.json
└── tsconfig.json
```

The existing `tests/auth/` directory is deleted — all auth tests are distributed into section files.

## Auth Helper

Expand `helpers/auth.ts` with all DevAuth personas:

```typescript
// Existing
loginAsVolunteer(page)          // slug: volunteer
loginAsAdmin(page)              // slug: admin
loginAsBoard(page)              // slug: board
loginAsConsentCoordinator(page) // slug: consent-coordinator

// New
loginAsTeamsAdmin(page)         // slug: teams-admin
loginAsCampAdmin(page)          // slug: camp-admin
loginAsTicketAdmin(page)        // slug: ticket-admin
loginAsNoInfoAdmin(page)        // slug: noinfo-admin
loginAsVolunteerCoordinator(page) // slug: volunteer-coordinator
```

All login helpers follow the same pattern: `page.goto('/dev/login/{slug}')` then wait for the user nav dropdown.

### Anonymous Tests

Tests marked with "anonymous" role (teams browsing, camps browsing) do NOT call any login function. They navigate directly to the page without authentication to test public-facing views.

## Boundary Test Pattern

All boundary tests use a shared `expectBlocked` helper. It handles three blocking mechanisms: redirect away, 404 page, or 403/Access Denied:

```typescript
async function expectBlocked(page: Page, url: string): Promise<void> {
  await page.goto(url);
  const pathSegment = new URL(url, 'https://placeholder').pathname;
  const isRedirected = !page.url().includes(pathSegment);
  const is404 = await page.getByText('Page Not Found').isVisible().catch(() => false);
  const isAccessDenied = await page.getByText('Access Denied').isVisible().catch(() => false);
  const isForbid = await page.locator('.status-code-page, [data-status="403"]').isVisible().catch(() => false);
  expect(isRedirected || is404 || isAccessDenied || isForbid).toBeTruthy();
}
```

This helper goes in `helpers/auth.ts` alongside the login functions.

---

## Section Specs

### 1. login.spec.ts (01-authentication)

**Keep as-is.** 2 existing tests.

| # | Test | Role | US |
|---|------|------|----|
| 1 | Volunteer can log in and see dashboard | volunteer | US-1.1 |
| 2 | Page does not show server error | volunteer | — |

---

### 2. profile.spec.ts (02-profiles)

**Restructure from `profile-edit.spec.ts`.** 4 tests.

| # | Test | Role | US |
|---|------|------|----|
| 1 | Profile view page shows status, team memberships | volunteer | US-2.1 |
| 2 | Edit page has all form sections (general, contributor, private) | volunteer | US-2.2 |
| 3 | Privacy page loads with data export and deletion options | volunteer | GDPR |
| 4 | Board human detail shows legal name and admin actions | board | US-9.2 |

**Test details:**

- **Test 1:** Navigate to `/Profile`. Verify heading, membership status indicator, and team memberships section are visible.
- **Test 2:** Navigate to `/Profile/Edit`. Verify burner name input, location fields, bio textarea, emergency contact section, and submit button are present.
- **Test 3:** Navigate to `/Profile/Privacy`. Verify "Download My Data" link and deletion section are visible.
- **Test 4:** Login as board, navigate to `/Human/Admin`. Find a user link, click through to detail at `/Human/{id}/Admin`. Verify legal name fields and admin action buttons (Suspend) are visible.

---

### 3. teams.spec.ts (06-teams + 17-coordinator-roles)

**Deepest section.** Restructure from `team-pages.spec.ts` + `auth/coordinator-views.spec.ts`. 19 tests.

#### Browsing (US-6.1, US-6.2, US-6.9, US-6.11)

| # | Test | Role | US |
|---|------|------|----|
| 1 | Team listing shows My Teams and Other Teams sections | volunteer | US-6.1 |
| 2 | Team detail shows name, description, members section | volunteer | US-6.2 |
| 3 | Anonymous sees only public teams on /Teams | anonymous | US-6.11 |
| 4 | Anonymous can view public team detail page | anonymous | US-6.9 |

#### My Teams & Membership (US-6.3, US-6.4, US-6.6)

| # | Test | Role | US |
|---|------|------|----|
| 5 | Open team shows Join button for non-members | volunteer | US-6.3 |
| 6 | Approval-required team shows Request to Join | volunteer | US-6.4 |
| 7 | My Teams page loads with Leave buttons at /Teams/My | volunteer | US-6.6 |

#### Management (US-6.5, US-6.7, US-6.8, US-6.10)

| # | Test | Role | US |
|---|------|------|----|
| 8 | Admin can access team member management page | admin | US-6.7 |
| 9 | Admin can access Create Team form at /Teams/Create | admin | US-6.8 |
| 10 | Admin can access Edit Team Page at /Teams/{slug}/EditPage | admin | US-6.10 |
| 11 | TeamsAdmin can access Team Summary with hierarchy | teams-admin | US-6.8 |

#### Coordinator & Role Auth

| # | Test | Role | US |
|---|------|------|----|
| 12 | Admin sees Team Management card on team detail | admin | — |
| 13 | Volunteer does NOT see Team Management card | volunteer | — |
| 14 | Role management section visible on team detail for admin | admin | Roles |

#### Hierarchy & Cross-Team Views

| # | Test | Role | US |
|---|------|------|----|
| 15 | Department detail shows sub-teams section | volunteer | Hierarchy |
| 16 | Cross-team Roster page loads at /Teams/Roster | volunteer | Roles |

#### Boundaries

| # | Test | Role | US |
|---|------|------|----|
| 17 | Volunteer cannot access /Teams/Sync | volunteer | — |
| 18 | Volunteer cannot access /Teams/Summary | volunteer | — |
| 19 | Volunteer cannot access team member management | volunteer | — |

**Test details:**

- **Test 5-6:** Browse /Teams, find teams with different RequiresApproval settings. The Join button text differs: "Join" for open teams, "Request to Join" for approval-required. If the volunteer is already a member, the button won't show — the test checks for its presence on at least one team, or skips gracefully.
- **Test 7:** Navigate to `/Teams/My`. Verify page loads and shows team memberships. Leave buttons should be visible on non-system teams.
- **Test 8:** Login as admin, navigate to a team's member management page via `/Teams/{slug}/Members` (using a known team slug). Verify member list is visible.
- **Test 15:** Find a department (team with sub-teams) on the listing page. Navigate to its detail and verify sub-team cards or section is present. Skip gracefully if no departments exist.
- **Test 16:** Navigate to `/Teams/Roster`. Verify cross-team roster summary table loads.

---

### 4. shifts.spec.ts (25-shift-management)

**Restructure from `shift-signup.spec.ts`.** 5 tests.

| # | Test | Role | US |
|---|------|------|----|
| 1 | Browse shifts page loads with department grouping | volunteer | US-25.3 |
| 2 | My Shifts page loads at /Shifts/Mine | volunteer | US-25.5 |
| 3 | Shift Settings page loads for admin | admin | US-25.1 |
| 4 | Volunteer cannot access /Shifts/Settings | volunteer | Boundary |
| 5 | Volunteer cannot access /Vol/Management | volunteer | Boundary |

**Test details:**

- **Test 1:** Navigate to `/Shifts`. Verify heading is visible, no server error. If shifts exist, verify department grouping structure. If browsing is closed, an info alert is acceptable.
- **Test 2:** Navigate to `/Shifts/Mine`. Verify the page loads with sections for upcoming/pending/past signups.
- **Test 3:** Login as admin, navigate to `/Shifts/Settings`. Verify event settings form is visible.
- **Test 5:** `/Vol/Management` returns 403 Forbid for volunteers — `expectBlocked` handles this via the isForbid check.

---

### 5. camps.spec.ts (20-camps)

**New section.** 5 tests.

| # | Test | Role | US |
|---|------|------|----|
| 1 | Browse camps listing with filter controls | anonymous | US-20.1 |
| 2 | Camp detail page loads with season data | anonymous | US-20.2 |
| 3 | Register form accessible at /Camps/Register | volunteer | US-20.3 |
| 4 | Camp admin dashboard loads with season management | camp-admin | US-20.6 |
| 5 | Volunteer cannot access /Camps/Admin | volunteer | Boundary |

**Test details:**

- **Test 1:** Navigate to `/Camps` without logging in. Verify camp cards are visible. Verify filter controls (vibe, sound zone, etc.) exist.
- **Test 2:** Click first camp card to navigate to detail. Verify camp name, description, and season data section.
- **Test 3:** Navigate to `/Camps/Register`. Verify form fields are present (or info message if no open season).
- **Test 4:** Login as camp-admin, navigate to `/Camps/Admin`. Verify pending seasons section and season management controls.

---

### 6. board.spec.ts (09-administration Board routes + 18-board-voting)

**New section.** 5 tests.

| # | Test | Role | US |
|---|------|------|----|
| 1 | Board dashboard loads with stats and quick actions | board | US-9.1 |
| 2 | Humans list loads with search at /Human/Admin | board | US-9.2 |
| 3 | Voting dashboard loads at /OnboardingReview/BoardVoting | board | US-18.1 |
| 4 | Board sees Board nav link, not Admin link | board | Nav |
| 5 | Volunteer cannot access /Board | volunteer | Boundary |

**Test details:**

- **Test 1:** Login as board, navigate to `/Board`. Verify dashboard loads with stats cards (pending volunteers, pending applications) and quick action links.
- **Test 2:** Navigate to `/Human/Admin` (HumanController). Verify search input and human list table are visible.
- **Test 3:** Navigate to `/OnboardingReview/BoardVoting`. Verify voting dashboard loads (may show "No pending applications" if queue is empty).
- **Test 4:** Verify nav contains Board link, does NOT contain Admin link.

---

### 7. onboarding.spec.ts (16-onboarding-pipeline + 17-coordinator-roles)

**New section.** 4 tests.

| # | Test | Role | US |
|---|------|------|----|
| 1 | Review queue loads with status filter tabs | consent-coordinator | US-17.2 |
| 2 | Consent coordinator sees Clear/Flag action buttons on detail | consent-coordinator | US-17.2 |
| 3 | Volunteer coordinator can view queue but no action buttons | volunteer-coordinator | US-17.3 |
| 4 | Volunteer cannot access /OnboardingReview | volunteer | Boundary |

**Test details:**

- **Test 1:** Login as consent-coordinator, navigate to `/OnboardingReview`. Verify filter tabs (Pending/Flagged/Cleared/All) are visible.
- **Test 2:** If there are humans in the queue, click through to a detail page. Verify Clear and Flag buttons are visible. Skip gracefully if queue is empty.
- **Test 3:** Login as volunteer-coordinator, navigate to `/OnboardingReview`. Verify queue loads but Clear/Flag buttons are NOT visible (even if queue items exist).

---

### 8. admin.spec.ts (09-administration Admin routes)

**Restructure from `auth/admin-views.spec.ts`.** 4 tests.

| # | Test | Role | US |
|---|------|------|----|
| 1 | Admin dashboard loads with metrics cards | admin | US-9.1 |
| 2 | Sync settings page loads | admin | Admin |
| 3 | Configuration status page loads | admin | Admin |
| 4 | Board member cannot access /Admin | board | Boundary |

**Test details:**

- **Test 1:** Login as admin, navigate to `/Admin`. Verify dashboard cards (Total Members, Active Members, etc.) are visible.
- **Test 2:** Navigate to `/Admin/SyncSettings`. Verify sync mode controls are visible.
- **Test 3:** Navigate to `/Admin/Configuration`. Verify configuration status indicators are visible.

---

### 9. tickets.spec.ts (24-ticket-vendor-integration)

**New section.** 3 tests.

| # | Test | Role | US |
|---|------|------|----|
| 1 | Ticket dashboard loads with summary cards | ticket-admin | — |
| 2 | Orders page loads at /Tickets/Orders | ticket-admin | — |
| 3 | Volunteer cannot access /Tickets | volunteer | Boundary |

**Test details:**

- **Test 1:** Login as ticket-admin (primary role for this section), navigate to `/Tickets`. Verify dashboard loads with summary cards.
- **Test 2:** Navigate to `/Tickets/Orders`. Verify orders table/list is visible.

---

### 10. feedback.spec.ts (27-feedback-system)

**Restructure from existing `feedback.spec.ts`.** 3 tests.

| # | Test | Role | US |
|---|------|------|----|
| 1 | Volunteer can open modal and submit feedback | volunteer | US-27.1 |
| 2 | Admin feedback triage page loads at /Admin/Feedback | admin | US-27.2 |
| 3 | Volunteer cannot access /Admin/Feedback | volunteer | Boundary |

**Test details:**

- **Test 1:** Keep existing test (open modal, fill form, submit, see success).
- **Test 2:** Login as admin, navigate to `/Admin/Feedback`. Verify feedback list with status/category filters is visible.

---

## Nav Visibility

The existing `auth/nav-visibility.spec.ts` tests are distributed:

| Original test | New location |
|---|---|
| Volunteer sees no Admin/Board links | admin.spec.ts (boundary test implies this) |
| Admin sees Admin link | admin.spec.ts test 1 |
| Board sees Board link, not Admin | board.spec.ts test 4 |

---

## Summary

| Section | Tests | Primary roles tested | Feature docs |
|---------|-------|---------------------|--------------|
| login | 2 | volunteer | 01 |
| profile | 4 | volunteer, board | 02, 09 |
| teams | 19 | volunteer, admin, teams-admin, anonymous | 06, 17 |
| shifts | 5 | volunteer, admin | 25 |
| camps | 5 | anonymous, volunteer, camp-admin | 20 |
| board | 5 | board, volunteer | 09, 18 |
| onboarding | 4 | consent-coord, volunteer-coord, volunteer | 16, 17 |
| admin | 4 | admin, board | 09 |
| tickets | 3 | ticket-admin, volunteer | 24 |
| feedback | 3 | volunteer, admin | 27 |
| **Total** | **54** | | |

## Future Additions

These feature areas are intentionally deferred from this phase but should be added later:

- **Consent (04-legal-documents-consent):** Document list rendering, consent signing flow, admin document management
- **Campaigns (22-campaigns):** Admin campaign management at `/Admin/Campaigns`
- **Tier Applications (03-asociado-applications):** User-facing application create/view flow
- **Governance:** Board roles page at `/Governance/Roles`
- **NoInfoAdmin shift approval:** NoInfoAdmin-specific behavior in shift admin (approve but not create)

## Constraints

- Tests run against preview environments with DevAuth enabled
- Dev personas are auto-seeded with minimal data (profile complete, consents may not be signed)
- Some pages may redirect to onboarding dashboard — tests must handle this gracefully
- The preview DB is cloned from QA — mutations are safe, no cleanup needed
- Use `getByRole`/`getByText` locators (not CSS attribute selectors) for reliability
- Anonymous tests (teams, camps browsing) skip the login step entirely
