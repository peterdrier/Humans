---
name: test-site
description: "Run browser-based smoke tests against the running Humans site using the Claude Code Chrome extension. Argument: all | smoke | auth | profile | consent | teams | admin | gdpr | i18n | [url]"
argument-hint: "all | smoke | auth | profile | consent | teams | admin | gdpr | i18n | [url]"
---

# Browser Test Suite for Humans

## Prerequisites

- Site running at `http://localhost:5000` or `https://localhost:5001`
- Chrome extension connected (`/chrome` to check)
- Logged in with an Admin-role Google account for full coverage

## Step 1: Fetch live test plan

Navigate to `{base_url}/.well-known/test-plan.txt`. If it loads, use it as the authoritative plan (overrides defaults below). Fall back to built-in suites on 404 or if site isn't running.

## Step 2: Determine base URL

Try `http://localhost:5000`, then `https://localhost:5001`. If neither works, tell the user the site isn't running.

If `$ARGUMENTS` is a URL, navigate to it and report what you see — done.

## Step 3: Run test suites

Run suites matching `$ARGUMENTS` (default: all). Report PASS/FAIL after each step; stop a suite on first FAIL and continue to next.

---

### smoke

1. `/health` → returns "Healthy"
2. `/` → home page loads
3. `/Profile` → profile loads
4. `/Teams` → team list loads
5. `/Consent` → consent page loads
6. Check browser console for JS errors

---

### auth

1. Home page loads without errors
2. Logged in: profile link visible in nav; click it, confirm profile loads with user data
3. Not logged in: note it — most other suites require auth

---

### profile

1. `/Profile` → name, contact fields, team memberships visible
2. `/Profile/Edit` → form loads with current values; fields present: Burner Name, First/Last Name, Pronouns, City, Country, Bio, Birthday (month/day); contact fields section with "Add" button; volunteer history section
3. Make a minor edit (e.g., Bio), submit, verify change appears, then undo it
4. `/Profile/Emails` → email addresses with visibility controls

---

### consent

1. `/Consent` → documents grouped by team
2. If any show "Pending", click "Review"; verify: document title + content, language tabs (min: Spanish), consent checkbox, "Give Consent" button
3. Check the checkbox → no browser validation error
4. Uncheck and try to submit → localized validation message appears (NOT browser default "Please check this box if you want to proceed")
5. Do NOT submit — click "Back to List"

---

### teams

1. `/Teams` → cards with team names and member counts
2. Click any non-system team → name, description, member list with roles (Lead/Member), Google resources section
3. `/Teams/My` → user's memberships
4. `/Teams/Map` → loads (OK if "No API key" — just no crash)
5. `/Teams/Birthdays` → birthday calendar for current month

---

### admin

Requires Admin or Board role.

1. `/Admin` → dashboard with metric cards + recent activity
2. `/Admin/Humans` → member list loads; search box works
3. Click any member → profile info, role assignments, consent records, audit log entries
4. `/Admin/Teams` → team list, system teams marked
5. `/Admin/Roles` → current role assignments (Admin, Board, Lead)
6. `/Admin/LegalDocuments` → document list
7. `/Admin/AuditLog` → entries load; filter by action type works
8. `/Admin/Configuration` → config status shown, no secrets exposed
9. `/hangfire` → dashboard loads with scheduled jobs

---

### gdpr

1. `/Profile/Privacy` → page loads
2. "Export My Data" button exists; click it → JSON file downloads with profile data
3. "Request Account Deletion" button exists — do NOT click it

---

### i18n

For each language (en, es, fr, de, it): switch via nav/footer selector; check `/Profile`, `/Consent`, `/Teams` labels are translated (no raw English keys like `Profile_FieldName`). Switch back to user's preferred language when done.

---

## Step 4: Report results

```
| Suite    | Result | Notes |
|----------|--------|-------|
| auth     | PASS   |       |
| ...      | ...    | ...   |
```

For failures: expected vs actual, screenshot/DOM state if relevant. End with `X/Y suites passed`.
