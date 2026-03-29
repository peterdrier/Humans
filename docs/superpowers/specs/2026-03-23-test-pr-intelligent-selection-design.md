# Enhanced test-pr: Intelligent Test Selection Design

**Goal:** Enhance the `/test-pr` skill to automatically analyze PR diffs, select relevant Playwright e2e tests, run them against the preview environment, report results, and flag coverage gaps — while keeping existing runners (API, Chrome, human) for explicit test plan steps.

**Tech Stack:** Bash (diff analysis, grep), Playwright (test execution), existing test-pr runners

---

## Enhanced Flow

```
Parse PR args → Resolve PR details → Wait for deploy
  → Analyze diff (NEW)
  → Select Playwright tests (NEW)
  → Run Playwright tests (NEW)
  → Run PR body test plan steps (existing runners, if any)
  → Detect coverage gaps (NEW)
  → Report results (enhanced)
```

The existing Steps 1-3 (parse args, resolve PR, wait for deploy) are unchanged. The new logic inserts between deploy confirmation and test execution.

---

## Step 4a: Analyze Diff and Select Tests

### Get changed files

```bash
gh pr diff {n} --repo peterdrier/Humans --name-only
```

### Map files to spec sections

For each changed file, determine which spec file(s) to run using two mechanisms:

**1. Override annotation (checked first):**

Grep the changed file for `// @e2e: <spec-file>` comments. These take precedence over convention. A file can have multiple annotations.

```csharp
// @e2e: board.spec.ts
// @e2e: profile.spec.ts
[Route("Human")]
public class HumanController : Controller
```

**2. Convention-based inference (fallback):**

Extract the section name from the file path. Controller and view directory names are **singular** in this codebase (except `Shifts`).

| Path pattern | Section | Spec file |
|---|---|---|
| `Controllers/TicketController.cs` | tickets | tickets.spec.ts |
| `Controllers/CampController.cs` | camps | camps.spec.ts |
| `Controllers/TeamController.cs` | teams | teams.spec.ts |
| `Controllers/ProfileController.cs` | profile | profile.spec.ts |
| `Controllers/ShiftsController.cs` | shifts | shifts.spec.ts |
| `Controllers/AdminController.cs` | admin | admin.spec.ts |
| `Controllers/BoardController.cs` | board | board.spec.ts |
| `Controllers/OnboardingReviewController.cs` | onboarding | onboarding.spec.ts |
| `Controllers/FeedbackController.cs` | feedback | feedback.spec.ts |
| `Controllers/FeedbackApiController.cs` | feedback | feedback.spec.ts |
| `Controllers/AdminFeedbackController.cs` | feedback | feedback.spec.ts |
| `Controllers/CampAdminController.cs` | camps | camps.spec.ts |
| `Controllers/ShiftAdminController.cs` | shifts | shifts.spec.ts |
| `Controllers/AccountController.cs` | login | login.spec.ts |
| `Controllers/DevLoginController.cs` | login | login.spec.ts |
| `Views/Ticket/**` | tickets | tickets.spec.ts |
| `Views/Camp/**` | camps | camps.spec.ts |
| `Views/CampAdmin/**` | camps | camps.spec.ts |
| `Views/Team/**`, `Views/TeamAdmin/**` | teams | teams.spec.ts |
| `Views/Profile/**` | profile | profile.spec.ts |
| `Views/Shifts/**`, `Views/ShiftAdmin/**`, `Views/ShiftDashboard/**` | shifts | shifts.spec.ts |
| `Views/Admin/**`, `Views/AdminEmail/**` | admin | admin.spec.ts |
| `Views/AdminFeedback/**` | feedback | feedback.spec.ts |
| `Views/Board/**` | board | board.spec.ts |
| `Views/OnboardingReview/**` | onboarding | onboarding.spec.ts |
| `Views/Home/**`, `Views/Account/**`, `Views/DevLogin/**` | login | login.spec.ts |
| `Domain/**`, `Application/**`, `Infrastructure/**` | (none) | smoke only |
| `Views/Shared/**`, `wwwroot/**` | (all) | run full suite |

**Controllers/views without spec coverage** (no corresponding spec file — flagged as unmapped): `HumanController` (needs `@e2e:` annotation → board.spec.ts + profile.spec.ts), `VolController`, `GovernanceController`, `ApplicationController`, `ConsentController`, `CampaignController`.

Convention logic: strip `Controller` suffix, lowercase, try `{name}.spec.ts` and `{name}s.spec.ts` (to handle singular→plural like `camp`→`camps.spec.ts`). If neither exists, flag as unmapped.

**3. Shared/layout changes → run everything:**

If the diff touches `Views/Shared/_Layout.cshtml`, `wwwroot/` assets, or auth middleware, run the full suite since these affect all pages.

### Build test set

- The final test set is the **union** of all spec files mapped from all changed files
- Deduplicate into a set
- Always include `login.spec.ts` as baseline smoke
- If no files match any pattern (infrastructure-only PR, docs-only PR, empty diff), run only `login.spec.ts`

---

## Step 4b: Run Playwright Tests

```bash
cd tests/e2e
BASE_URL=https://{pr_id}.n.burn.camp npx playwright test tests/login.spec.ts tests/tickets.spec.ts ... --workers=2
```

Spec files must be passed as `tests/{name}.spec.ts` (relative to `tests/e2e/`, matching the `testDir: './tests'` config).

Use `--workers=2` to avoid overwhelming the preview environment (learned from implementation — 6 workers causes flaky timeouts).

Parse Playwright's output to extract per-spec pass/fail counts.

---

## Step 4c: Run PR Body Test Plan (Existing Runners)

Unchanged from current skill. If the PR body has a `## Test plan` section (or explicit instruction was passed), execute those steps using the appropriate runner (API/Chrome/Human). This runs in addition to the Playwright tests, not instead of.

---

## Step 5: Coverage Gap Detection

After selecting tests, analyze the diff for new or modified controller actions and check if they're covered:

1. **Extract route changes:** Parse the diff for new `[HttpGet]`, `[HttpPost]`, `public IActionResult`, `public async Task<IActionResult>` methods in controllers
2. **Infer routes:** Combine controller route prefix with action route to get the URL path
3. **Check coverage:** Grep the selected spec files for the inferred route paths
4. **Report uncovered routes:** List any new/changed routes not referenced in any test file

Report format:
```
⚠ /Tickets/Refund (new action in TicketsController) — no test covers this route
  Suggested test: "ticket admin can access refund page at /Tickets/Refund"
```

This is advisory only — no tests are auto-created.

**Limitation:** Only new/added actions are detected. Renamed or deleted actions are not flagged — existing tests referencing removed routes will fail at runtime, which is the correct signal.

---

## Enhanced Report Format

```
## PR #{n}: {title}

**Deploy:** ✓ {sha} live at https://{pr_id}.n.burn.camp
**Playwright:** {count} specs selected ({spec_names})

| Spec | Tests | Passed | Failed | Flaky |
|------|-------|--------|--------|-------|
| login.spec.ts | 2 | 2 | 0 | 0 |
| tickets.spec.ts | 3 | 3 | 0 | 0 |

**Coverage gaps:**
⚠ /Tickets/Refund (new action) — no test covers this route
  Suggested: "ticket admin can access refund page"

**PR Test Plan:** (if any)
| Step | Runner | Result | Evidence |
|------|--------|--------|----------|
| Verify refund button shows | Chrome | PASS | screenshot |

**Overall: PASS** (5/5 Playwright, 1/1 test plan, 1 coverage gap)
```

If ALL tests pass and no coverage gaps: offer to merge.
If tests fail: offer to investigate/fix.
If coverage gaps exist: note them but don't block — they're advisory.

---

## @e2e Annotation Convention

Place annotations at the top of any source file (controller, view model, service) that maps to e2e test coverage. Multiple annotations per file are allowed.

```csharp
// @e2e: board.spec.ts
// @e2e: profile.spec.ts
```

Annotations are optional. Files without annotations fall back to convention-based inference. Add annotations when:
- The convention would guess wrong (e.g., `HumanController` → should be `board.spec.ts` + `profile.spec.ts`, not `human.spec.ts`)
- A service or infrastructure file affects a specific user-facing section
- You want to be explicit about test coverage relationships

---

## Implementation Scope

**Modified file:** `.claude/skills/test-pr/SKILL.md` — the skill definition

**No new files needed** — the logic is all in the skill prompt. The skill instructs Claude to:
1. Run `gh pr diff --name-only`
2. Grep changed files for `@e2e:` annotations
3. Apply convention mapping for unannotated files
4. Run `npx playwright test` with selected specs
5. Parse output and report

**One-time setup:** Add `// @e2e:` annotations to controllers where convention fails. Required annotations:
- `HumanController.cs` → `@e2e: board.spec.ts` + `@e2e: profile.spec.ts`
- `VolController.cs` → `@e2e: shifts.spec.ts` (volunteer shift management)

Other unmapped controllers (`GovernanceController`, `ApplicationController`, `ConsentController`, `CampaignController`) don't have spec files yet — they'll be flagged as unmapped, which is correct.

---

## Constraints

- Tests run from `tests/e2e/` directory in the repo
- Preview URL: `https://{pr_id}.n.burn.camp`
- Use `--workers=2` for Playwright (preview env is resource-constrained)
- Timeout: 60s per test, 2 retries (from playwright.config.ts)
- login.spec.ts always runs as smoke baseline
- Future: smoke tests will run automatically for every PR/release (not part of this design)
