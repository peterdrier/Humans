---
name: test-site
description: "Run browser-based smoke tests against the running Humans site using the Claude Code Chrome extension."
argument-hint: "all | smoke | auth | profile | consent | teams | admin | gdpr | i18n | [url]"
---

# Browser Test Suite for Humans

Run browser-based smoke tests against the running Humans site using the Chrome extension.

## Prerequisites

- The site must be running (typically `http://localhost:5000` or `https://localhost:5001`)
- The Chrome extension must be connected (`/chrome` to check)
- You must be logged in with a Google account that has the Admin role for full coverage

## Step 0: Determine scope from `$ARGUMENTS`

| Argument | What to test |
|----------|-------------|
| *(none)* or `all` | Run all test suites below in order |
| `smoke` | Quick health check: home, login state, profile loads |
| `auth` | Authentication flow only |
| `profile` | Profile viewing and editing |
| `consent` | Legal document consent flow |
| `teams` | Team browsing, joining, details |
| `admin` | Admin panel (requires Admin role) |
| `gdpr` | Privacy page, data export, deletion request/cancel |
| `i18n` | Language switching across key pages |
| A URL | Navigate to that URL and report what you see |

## Step 1: Fetch live test plan from the site

Before running tests, try to fetch the test plan from the running site:

```
Navigate to {base_url}/.well-known/test-plan.txt
```

If this page loads successfully, use it as the **authoritative test plan** — it may contain updated test scenarios, known issues, or temporary skip instructions that override the defaults below. Merge any site-served instructions with the suites below (site instructions take precedence for conflicts).

If the page returns 404 or the site isn't running, fall back to the built-in suites below.

## Step 2: Determine base URL

Check if the site is running:
1. Try `http://localhost:5000` first
2. If that fails, try `https://localhost:5001`
3. If neither works, tell the user the site doesn't appear to be running

## Step 3: Run test suites

For each suite in scope, follow the steps below. After each step, report PASS/FAIL with a brief note. Stop a suite on the first FAIL and move to the next suite (don't cascade failures).

---

### Suite: auth

**Goal:** Verify login state and session behavior.

1. Navigate to the home page. Confirm it loads without errors.
2. Check if you're logged in (look for a profile link or username in the nav bar vs a "Sign in" button).
3. If logged in: click the profile link and confirm the profile page loads with user data.
4. If NOT logged in: note this — most other suites require authentication.

---

### Suite: profile

**Goal:** Verify profile viewing and editing. Requires authentication.

1. Navigate to `/Profile`. Confirm it shows the user's profile with name, contact fields, and team memberships.
2. Navigate to `/Profile/Edit`. Confirm the edit form loads with current values pre-filled.
3. Verify these fields are present: Burner Name, First Name, Last Name, Pronouns, City, Country, Bio, Birthday (month/day).
4. Verify the contact fields section exists with an "Add" button.
5. Verify the volunteer history (Burner CV) section exists.
6. Make a minor edit (e.g., update the Bio field), submit the form, and verify the change appears on the profile page.
7. Undo the edit (restore the original value).
8. Navigate to `/Profile/Emails`. Confirm it shows email addresses with visibility controls.

---

### Suite: consent

**Goal:** Verify the consent review flow. Requires authentication.

1. Navigate to `/Consent`. Confirm it shows documents grouped by team.
2. If any documents show "Pending" status, click "Review" on one.
3. On the review page, verify:
   - The document title and content are displayed
   - Language tabs appear (at minimum Spanish/Castellano)
   - The consent checkbox is present with the label about reading and agreeing
   - The "Give Consent" button exists
4. Check the checkbox and verify no browser validation error appears (the custom validation message should match the site language, not the browser default).
5. Uncheck the checkbox, then try to submit. Verify a localized validation message appears (NOT the browser default "Please check this box if you want to proceed").
6. Do NOT actually submit consent (click "Back to List" instead).

---

### Suite: teams

**Goal:** Verify team browsing and detail pages. Requires authentication.

1. Navigate to `/Teams`. Confirm the team list loads with cards showing team names and member counts.
2. Click on any non-system team to view its details page.
3. On the detail page, verify: team name, description, member list with roles (Lead/Member), and Google resources section.
4. Navigate to `/Teams/My`. Confirm it shows the user's team memberships.
5. Navigate to `/Teams/Map`. Confirm the map loads (may show "No API key" if Google Maps isn't configured — that's OK, just verify no crash).
6. Navigate to `/Teams/Birthdays`. Confirm the birthday calendar loads for the current month.

---

### Suite: admin

**Goal:** Verify admin panel functionality. Requires Admin or Board role.

1. Navigate to `/Admin`. Confirm the dashboard loads with metric cards (members, pending approvals, pending consents, etc.) and recent activity.
2. Navigate to `/Admin/Members`. Confirm the member list loads. Try the search box with a partial name or email.
3. Click on any member to view their detail page. Verify it shows: profile info, role assignments, consent records, and audit log entries.
4. Navigate to `/Admin/Teams`. Confirm the team list loads with system teams marked.
5. Navigate to `/Admin/Roles`. Confirm it shows current role assignments (Admin, Board, Lead).
6. Navigate to `/Admin/LegalDocuments`. Confirm the document list loads.
7. Navigate to `/Admin/AuditLog`. Confirm the audit log loads with entries. Try filtering by action type.
8. Navigate to `/Admin/Configuration`. Confirm it shows configuration status without exposing secrets.
9. Navigate to `/hangfire`. Confirm the Hangfire dashboard loads and shows scheduled jobs.

---

### Suite: gdpr

**Goal:** Verify GDPR/privacy features. Requires authentication.

1. Navigate to `/Profile/Privacy`. Confirm the privacy page loads.
2. Verify the "Export My Data" button exists.
3. Click "Export My Data" and verify a JSON file downloads with profile data.
4. Verify the "Request Account Deletion" button exists (but do NOT click it).

---

### Suite: i18n

**Goal:** Verify language switching works across the site.

Test each language: English (en), Spanish (es), French (fr), German (de), Italian (it).

For each language:
1. Switch language using the language selector in the nav/footer.
2. Navigate to `/Profile`. Verify labels are translated (not showing English keys like "Profile_FieldName").
3. Navigate to `/Consent`. Verify the page title and labels are translated.
4. Navigate to `/Teams`. Verify the page title is translated.

After testing all languages, switch back to the user's preferred language.

---

### Suite: smoke

**Goal:** Quick health check — everything loads, nothing crashes.

1. Navigate to `/health`. Confirm it returns "Healthy".
2. Navigate to `/`. Confirm the home page loads.
3. Navigate to `/Profile`. Confirm the profile loads.
4. Navigate to `/Teams`. Confirm the team list loads.
5. Navigate to `/Consent`. Confirm the consent page loads.
6. Check the browser console for any JavaScript errors. Report any found.

---

## Step 4: Report results

Summarize results in a table:

```
| Suite    | Result | Notes |
|----------|--------|-------|
| auth     | PASS   |       |
| profile  | PASS   |       |
| consent  | FAIL   | Checkbox validation message showed browser default |
| ...      | ...    | ...   |
```

If any suite failed, list the specific failures with:
- What was expected
- What actually happened
- Screenshot or DOM state if relevant

End with a count: `X/Y suites passed`.
