# Playwright E2E Smoke Tests

## Business Context

The Humans app has unit and integration tests (xUnit + WebApplicationFactory) but no browser-based end-to-end tests. User feedback reports have concentrated around profile editing, shift signup, team pages, and the feedback widget — areas where server-side integration tests can't catch JavaScript bugs, rendering issues, or broken form flows. Adding Playwright smoke tests against preview/QA environments fills this gap with zero per-run cost.

## Scope

5 initial smoke tests targeting the highest-feedback areas. Manual execution only (no CI). Tests run against preview environments (`{PR_ID}.n.burn.camp`) or QA (`humans.n.burn.camp`).

## Non-Goals

- CI/GitHub Actions integration (future iteration)
- Cross-browser testing (Chromium only for now)
- Visual regression testing
- Load/performance testing
- Local development server testing

## Project Structure

```
tests/e2e/
├── package.json            # Playwright + TypeScript deps
├── tsconfig.json           # TypeScript config
├── playwright.config.ts    # Single Chromium project, BASE_URL from env
├── .gitignore              # node_modules/, test-results/, playwright-report/
├── helpers/
│   └── auth.ts             # Shared DevAuth login helper
└── tests/
    ├── login.spec.ts
    ├── profile-edit.spec.ts
    ├── shift-signup.spec.ts
    ├── team-pages.spec.ts
    └── feedback.spec.ts
```

This is a standalone Node.js project. It is **not** referenced by `Humans.slnx`, not included in the Docker build, and not deployed. A `.dockerignore` entry provides belt-and-suspenders isolation.

## Configuration

| Setting | Value |
|---------|-------|
| `BASE_URL` env var | Defaults to `https://humans.n.burn.camp` (QA) |
| Browser | Chromium only (headless) |
| Timeout | 30s per test (preview environments can be slow) |
| Screenshots | On failure, saved to `tests/e2e/test-results/` |
| Retries | 1 retry on failure (network flakiness on previews) |

## Auth Strategy

DevAuth is enabled on all preview/QA environments (`DevAuth__Enabled=true`). A shared helper navigates the browser to DevAuth endpoints:

- `/dev/login/volunteer` — standard volunteer persona
- `/dev/login/admin` — admin persona

The browser follows the redirect, DevAuth sets the ASP.NET Identity auth cookie, and subsequent navigation is authenticated. No manual cookie management needed.

```typescript
// helpers/auth.ts
export async function loginAsVolunteer(page: Page) {
  await page.goto('/dev/login/volunteer');
  // DevAuth redirects to "/" (Home/Index via default MVC routing)
  // Wait for nav element that proves auth succeeded rather than fragile URL matching
  await page.waitForSelector('[data-testid="user-nav"], .navbar .dropdown');
}

export async function loginAsAdmin(page: Page) {
  await page.goto('/dev/login/admin');
  await page.waitForSelector('[data-testid="user-nav"], .navbar .dropdown');
}
```

## Test Specifications

### 1. Login (`login.spec.ts`)

**Purpose**: Verify DevAuth login works and the authenticated dashboard loads correctly.

**Steps**:
1. Navigate to `/dev/login/volunteer`
2. Assert redirect to dashboard
3. Assert nav bar shows a user display name
4. Assert no error banners or 500 pages

### 2. Profile Edit (`profile-edit.spec.ts`)

**Purpose**: Verify a user can edit their profile fields and changes persist.

**Steps**:
1. Login as volunteer
2. Navigate to profile edit page
3. Change a text field to a timestamped value (e.g. `"E2E Test {timestamp}"`) for idempotency
4. Submit the form
5. Assert success feedback (redirect with TempData alert banner)
6. Reload and verify the changed value persisted
7. Restore the original value to avoid polluting the profile

### 3. Shift Signup (`shift-signup.spec.ts`)

**Purpose**: Verify a volunteer can browse available shifts and sign up.

**Steps**:
1. Login as volunteer
2. Navigate to shift browse page
3. Assert the page loads without errors
4. If shifts are available: click signup on one, assert confirmation feedback
5. If no shifts are available: assert the empty-state message renders correctly (test still passes)

**Note**: The test is resilient to missing shift data — it verifies the page works correctly whether or not future shifts exist in the DB. This avoids flakiness when preview DBs are cloned from QA with no upcoming shifts.

### 4. Team Pages (`team-pages.spec.ts`)

**Purpose**: Verify team listing and detail pages render correctly.

**Steps**:
1. Login as volunteer
2. Navigate to teams listing
3. Assert at least one team is visible
4. Click into a team detail page
5. Assert team name, description, and members section render

### 5. Feedback Widget (`feedback.spec.ts`)

**Purpose**: Verify the feedback widget opens and accepts submissions.

**Steps**:
1. Login as volunteer
2. Open the feedback modal (trigger button in layout)
3. Fill in feedback text in the `#feedbackModal` form
4. Submit (form POSTs to `/Feedback/Submit` with anti-forgery token already in DOM)
5. Assert page reloads with TempData success alert banner (rendered by `TempDataAlertsViewComponent`)

## Running Tests

**First-time setup** (one-time browser download):
```bash
cd tests/e2e && npm install && npx playwright install chromium
```

**Running**:
```bash
# Against QA (default)
cd tests/e2e && npx playwright test

# Against a preview environment
cd tests/e2e && BASE_URL=https://123.n.burn.camp npx playwright test

# Single test file
cd tests/e2e && npx playwright test tests/login.spec.ts

# With browser visible (debugging)
cd tests/e2e && npx playwright test --headed

# Generate HTML report
cd tests/e2e && npx playwright show-report
```

## Dockerfile Isolation

The existing Dockerfile copies only `src/` directories and runs `dotnet publish`. The `tests/e2e/` directory with its `package.json` and Node.js dependencies is never part of the build context. Creating a `.dockerignore` file with `tests/e2e/` as an explicit safeguard (no `.dockerignore` currently exists in the repo).

## Phase 2: Role-Based Authorization Tests

**Priority: High** — auth boundaries are undertested and a major regression risk. Current authorization relies on `User.IsInRole()` checks and Razor `@if` conditionals that are easy to break during refactoring.

**Approach:** Test the **UI divergence points** where different roles see different content on the same page. Not exhaustive page×role coverage (integration tests cover "does this return 403"), but verifying that the rendered page correctly shows/hides elements based on role.

**New auth helper personas:**
- `loginAsCoordinator(page)` — `/dev/login/consent-coordinator` or `/dev/login/coordinator`
- `loginAsBoard(page)` — `/dev/login/board`
- Existing: `loginAsVolunteer(page)`, `loginAsAdmin(page)`

**Test structure:**
```
tests/e2e/tests/auth/
├── nav-visibility.spec.ts       # Nav menu items per role
├── admin-views.spec.ts          # Admin dashboard, user mgmt, sync settings
├── coordinator-views.spec.ts    # Team admin, shift mgmt, member approval
└── volunteer-boundaries.spec.ts # Can't see admin/coord elements
```

**Test specifications:**

### nav-visibility.spec.ts
For each role, verify the nav bar shows the correct links:
- **Volunteer:** Home, Barrios, Teams, Volunteer. No Admin link.
- **Coordinator:** Same as volunteer + coordinator-specific team links
- **Board:** Same as volunteer + Governance link
- **Admin:** All of the above + Admin link

### admin-views.spec.ts
Login as admin:
- `/Admin` — dashboard loads, shows user management sections
- `/Admin/SyncSettings` — sync settings page renders with toggle controls
- `/Admin/Users` — user list renders

### coordinator-views.spec.ts
Login as coordinator:
- Team detail page shows "Manage Members" button
- `/TeamAdmin/Members/{slug}` — member management page loads
- Shift admin views show coordinator controls

### volunteer-boundaries.spec.ts
Login as volunteer (negative tests):
- Nav does NOT contain "Admin" link
- Navigate to `/Admin` — should redirect or return 403
- Team detail page does NOT show "Manage Members" button (for teams where user is not coordinator)

**When to implement:** Immediately after Phase 1 (smoke tests) merges. This is the highest-value Playwright work after the initial smoke tests.

## Future Iterations

- Add GitHub Actions workflow to run on PR events (after tests prove stable)
- Integrate with `/test-pr` skill — the skill would invoke `npx playwright test` against the preview URL after deployment completes
- Add more tests as new features land
- Consider cross-browser (Firefox, WebKit) once Chromium suite is stable
- Add Playwright test generation via `npx playwright codegen` for rapid test creation
