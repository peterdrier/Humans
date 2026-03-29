# Enhanced test-pr: Intelligent Test Selection — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Enhance the `/test-pr` skill to analyze PR diffs, auto-select relevant Playwright tests, run them, and flag coverage gaps.

**Architecture:** The skill is a single markdown prompt file (SKILL.md) that instructs Claude how to test PRs. We rewrite it to add diff analysis, Playwright test selection, and coverage gap detection between the existing "wait for deploy" and "execute test plan" steps. Two C# controller files get `@e2e:` annotations for non-obvious mappings.

**Tech Stack:** Skill prompt (markdown), Bash (gh CLI, grep), Playwright, existing test runners

**Spec:** `docs/superpowers/specs/2026-03-23-test-pr-intelligent-selection-design.md`

**Working directory:** `/home/drierp/source/humans/.worktrees/playwright-e2e`

**Important:** The skill file (`.claude/skills/test-pr/SKILL.md`) is **untracked** and lives only in the main working directory at `/home/drierp/source/humans/.claude/skills/test-pr/SKILL.md`. Task 2 must modify it there — it is not part of the worktree branch.

---

### Task 1: Add @e2e annotations to controllers with non-obvious mappings

**Files:**
- Modify: `src/Humans.Web/Controllers/HumanController.cs` (add annotations at top)
- Modify: `src/Humans.Web/Controllers/VolController.cs` (add annotation at top)

- [ ] **Step 1: Add @e2e annotations to HumanController.cs**

Add these two lines at the very top of the file, before any `using` statements:

```csharp
// @e2e: board.spec.ts
// @e2e: profile.spec.ts
```

- [ ] **Step 2: Add @e2e annotation to VolController.cs**

Add this line at the very top of the file, before any `using` statements:

```csharp
// @e2e: shifts.spec.ts
```

- [ ] **Step 3: Verify the annotations are grep-able**

Run: `grep -r "@e2e:" src/Humans.Web/Controllers/`
Expected: 3 lines — 2 from HumanController.cs, 1 from VolController.cs

- [ ] **Step 4: Commit**

```bash
git add src/Humans.Web/Controllers/HumanController.cs src/Humans.Web/Controllers/VolController.cs
git commit -m "chore: add @e2e annotations for non-obvious controller-to-test mappings"
```

---

### Task 2: Rewrite the test-pr SKILL.md

**Files:**
- Modify: `/home/drierp/source/humans/.claude/skills/test-pr/SKILL.md` (absolute path — NOT in the worktree)

**Note:** This file is untracked and not committed to git. No git commit needed for this task.

- [ ] **Step 1: Rewrite SKILL.md with the enhanced flow**

Replace the entire contents of `.claude/skills/test-pr/SKILL.md` with:

```markdown
---
name: test-pr
description: "Test PR preview deployments. Waits for deploy, analyzes diff to select relevant Playwright e2e tests, runs them, flags coverage gaps, and executes any PR-specific test plan steps. Use when the user says 'test pr', 'test PR 14', 'test prs 13 15 16', or wants to verify a preview deployment."
argument-hint: "PR number(s) [— test instruction]"
---

# Test PR Preview Deployments

Test one or more PRs on their Coolify preview environments. Automatically selects and runs relevant Playwright e2e tests based on the PR diff, then executes any explicit test plan steps.

## Parse Arguments from `$ARGUMENTS`

**Formats:**
- `14` — single PR
- `14 — call GET /api/feedback and verify responseCount exists` — single PR with explicit test instruction
- `13 15 16` — multiple PRs in parallel
- `13 15 16 — check the admin page loads` — multiple PRs, same test instruction for all

Extract: PR numbers (integers) and optional test instruction (everything after `—` or `--`).

## Step 1: Resolve PR Details

For each PR number, run:
```bash
gh pr view {n} --repo peterdrier/Humans --json headRefOid,body,title,number --jq '{sha: .headRefOid[:8], title, number, body}'
```

Extract:
- **Expected SHA** (first 8 chars of head commit)
- **PR title** for reporting
- **Test plan** from PR body (look for `## Test plan` section) — only used if no explicit test instruction given

## Step 2: Parallel Dispatch (if multiple PRs)

If multiple PR numbers were given, dispatch one Agent per PR:
```
Agent("test PR {n}", prompt: "Run /test-pr {n} — {instruction}")
```

All agents run in background. Collect results and report summary table when all complete. Then STOP — do not continue to Steps 3-6 yourself.

## Step 3: Wait for Deploy

Poll the preview environment until the expected SHA appears in the page footer.

```bash
while true; do
  sha=$(curl -s "https://{pr_id}.n.burn.camp/" 2>/dev/null | grep -oP '[0-9a-f]{8}(?=</a>)' | head -1)
  if [ "$sha" = "{expected_sha}" ]; then break; fi
  sleep 10
done
```

**Timeout:** If not deployed after 5 minutes, report failure and stop.

**Important:** Do NOT proceed until the SHA matches. Do NOT guess that "it's probably deployed."

## Step 4: Analyze Diff and Select Playwright Tests

### 4a: Get changed files

```bash
gh pr diff {n} --repo peterdrier/Humans --name-only
```

### 4b: Map files to spec sections

For each changed file, determine which spec file(s) to run:

**First: check for @e2e annotations.** Grep the file in the local working tree for `// @e2e: <spec-file>` comments. These take precedence over convention. The path from `gh pr diff --name-only` is repo-relative (e.g., `src/Humans.Web/Controllers/HumanController.cs`), so grep it relative to the repo root.

```bash
grep -h "// @e2e:" src/Humans.Web/Controllers/HumanController.cs 2>/dev/null | sed 's|.*// @e2e: ||'
```

**Second: convention-based inference (if no @e2e annotation found).** Use this explicit mapping table:

| Path contains | Spec file |
|---|---|
| `Controllers/TicketController` | tickets.spec.ts |
| `Controllers/CampController` | camps.spec.ts |
| `Controllers/CampAdminController` | camps.spec.ts |
| `Controllers/TeamController` | teams.spec.ts |
| `Controllers/ProfileController` | profile.spec.ts |
| `Controllers/ShiftsController` | shifts.spec.ts |
| `Controllers/ShiftAdminController` | shifts.spec.ts |
| `Controllers/AdminController` | admin.spec.ts |
| `Controllers/BoardController` | board.spec.ts |
| `Controllers/OnboardingReviewController` | onboarding.spec.ts |
| `Controllers/FeedbackController` | feedback.spec.ts |
| `Controllers/FeedbackApiController` | feedback.spec.ts |
| `Controllers/AdminFeedbackController` | feedback.spec.ts |
| `Controllers/AccountController` | login.spec.ts |
| `Controllers/DevLoginController` | login.spec.ts |
| `Views/Ticket/` | tickets.spec.ts |
| `Views/Camp/`, `Views/CampAdmin/` | camps.spec.ts |
| `Views/Team/`, `Views/TeamAdmin/` | teams.spec.ts |
| `Views/Profile/` | profile.spec.ts |
| `Views/Shifts/`, `Views/ShiftAdmin/`, `Views/ShiftDashboard/` | shifts.spec.ts |
| `Views/Admin/`, `Views/AdminEmail/` | admin.spec.ts |
| `Views/AdminFeedback/` | feedback.spec.ts |
| `Views/Board/` | board.spec.ts |
| `Views/OnboardingReview/` | onboarding.spec.ts |
| `Views/Home/`, `Views/Account/`, `Views/DevLogin/` | login.spec.ts |

For any controller/view not in this table: strip prefixes (`Admin`, `Camp`, `Shift`), strip `Controller` suffix, lowercase, try `{name}.spec.ts` and `{name}s.spec.ts`. If neither exists in `tests/e2e/tests/`, flag as unmapped in the report.

**Special rules:**
- `Views/Shared/**`, `wwwroot/**`, auth middleware → run ALL spec files (full suite)
- `Domain/**`, `Application/**`, `Infrastructure/**` → no section match (smoke only)
- Docs, configs, non-code files → no section match (smoke only)

### 4c: Build test set

- Take the **union** of all spec files mapped from all changed files
- Always include `login.spec.ts` as baseline smoke
- If no files match any pattern, run only `login.spec.ts`
- Deduplicate

Report the selected specs: "**Playwright:** {count} specs selected ({spec_names})"

### 4d: Run Playwright tests

```bash
cd tests/e2e
BASE_URL=https://{pr_id}.n.burn.camp npx playwright test tests/login.spec.ts tests/tickets.spec.ts ... --workers=2
```

**Important:** Spec files are passed as `tests/{name}.spec.ts` (relative to `tests/e2e/`).

Use `--workers=2` to avoid overwhelming the preview environment.

Parse the output to extract per-spec pass/fail/flaky counts.

## Step 5: Execute PR Test Plan (Existing Runners)

If the PR body has a `## Test plan` section (or an explicit test instruction was passed), execute those steps using the appropriate runner. This runs **in addition to** the Playwright tests.

### Runner: API (curl)
**Use when:** instruction mentions API, endpoint, GET, POST, JSON, response, field, curl, `/api/`

- Read API keys from `.claude/settings.local.json` (`HUMANS_QA_API_KEY` for preview environments)
- Make the HTTP requests
- Assert on response shape, status codes, field presence/values

### Runner: Chrome Extension
**Use when:** instruction mentions UI, page, click, form, button, visual, display, render, badge, layout

- Use the Chrome extension browser tools to navigate to `https://{pr_id}.n.burn.camp/...`
- If login required, use dev login (preview environments have `DevAuth__Enabled=true`)
- Navigate, interact, observe

### Runner: Human Verification
**Use when:** instruction says "manual", or the test can't be automated, or the skill can't determine how to automate it

- Describe exactly what to check, with the URL and expected behavior
- Ask the human for pass/fail using AskUserQuestion

### Runner: Mixed
Some test plans have multiple steps requiring different runners. Use the appropriate runner for each step.

## Step 6: Coverage Gap Detection and Report

### Detect coverage gaps

Analyze the diff for new controller actions:
1. Look for added lines containing `public IActionResult`, `public async Task<IActionResult>`, `[HttpGet]`, `[HttpPost]` in controller files
2. Infer the route from the controller's `[Route]` attribute + action name/route
3. Grep the selected spec files for the route path
4. Report any uncovered routes

**Limitation:** Only new/added actions are detected. Renamed or deleted actions will cause existing tests to fail at runtime, which is the correct signal.

### Report results

```
## PR #{n}: {title}

**Deploy:** ✓ {sha} live at https://{pr_id}.n.burn.camp
**Playwright:** {count} specs selected ({spec_names})

| Spec | Tests | Passed | Failed | Flaky |
|------|-------|--------|--------|-------|
| login.spec.ts | 2 | 2 | 0 | 0 |
| tickets.spec.ts | 3 | 3 | 0 | 0 |

**Coverage gaps:** (if any)
⚠ /Tickets/Refund (new action) — no test covers this route
  Suggested: "ticket admin can access refund page at /Tickets/Refund"

**PR Test Plan:** (if any)
| Step | Runner | Result | Evidence |
|------|--------|--------|----------|
| Verify refund button shows | Chrome | PASS | screenshot |

**Overall: PASS** (5/5 Playwright, 1/1 test plan, 1 coverage gap)
```

If ALL tests pass and no coverage gaps: ask "All tests passed. Want me to merge this PR?"
If tests fail: ask "Some tests failed. Want me to investigate and fix, or skip this PR?"
If coverage gaps exist: note them but don't block — they're advisory.

## Notes

- Preview URL pattern: `https://{pr_id}.n.burn.camp/`
- PR repo: `peterdrier/Humans` (origin fork, not upstream)
- API key env var: `HUMANS_QA_API_KEY` in `.claude/settings.local.json`
- SHA location: `<a>` tag in page footer linking to `/commit/{full_sha}`, display text is 8-char prefix
- Preview auth: dev login enabled, no Google OAuth needed
- Playwright tests live at `tests/e2e/tests/*.spec.ts`
- Playwright config at `tests/e2e/playwright.config.ts` (testDir: `./tests`, timeout: 60s, retries: 2)
```

- [ ] **Step 2: Verify the skill file is valid markdown**

Read back the file and confirm the frontmatter, headings, and code blocks are well-formed.

- [ ] **Step 3: No commit needed**

The skill file is untracked (`.claude/skills/` is in `.gitignore`). The file is saved locally and takes effect immediately when `/test-pr` is invoked.

---

### Task 3: Smoke test the enhanced skill

- [ ] **Step 1: Verify the skill is recognized**

Run: `/test-pr 26` (or whatever PR is currently deployed)

The skill should:
1. Resolve PR #26 details
2. Wait for deploy (should already be deployed)
3. Analyze the diff and report which specs were selected
4. Run those Playwright tests
5. Report results

This is a manual verification step — the implementer should run the skill and confirm the output matches the expected report format from the spec.

- [ ] **Step 2: Push changes**

```bash
git push origin feature/playwright-e2e
```
