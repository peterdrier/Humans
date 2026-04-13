---
name: check-pr
description: "Check an externally created PR for coding rule violations. Fetches the PR diff and reviews against CODING_RULES.md and CODE_REVIEW_RULES.md."
argument-hint: "PR 64 | https://github.com/.../pull/64"
---

# Check PR for Coding Rule Compliance

Review an externally created pull request for violations of this project's coding rules. This is NOT a general code review — it specifically checks compliance with `.claude/CODING_RULES.md` and `.claude/CODE_REVIEW_RULES.md`.

## Input

`$ARGUMENTS` can be:
- `PR <number>` — PR number on peterdrier/Humans
- `<number>` — PR number on peterdrier/Humans
- A full GitHub PR URL — extracts repo and number automatically

## Execution

### 1. Load the rules

Read both rule files in full:
- `.claude/CODING_RULES.md` — coding standards (serialization, DbContext, enums, NodaTime, etc.)
- `.claude/CODE_REVIEW_RULES.md` — hard reject rules (auth gaps, missing Include, bool sentinels, etc.)

### 2. Fetch the PR diff

```bash
gh pr diff <number> --repo <repo>
```

Also fetch PR metadata for context:
```bash
gh pr view <number> --repo <repo> --json title,body,files
```

### 3. Review each changed file against rules

For every file in the diff, check for violations of ALL rules. Focus on:

**From CODING_RULES.md:**
- Direct `ApplicationDbContext` injection or usage in controllers (no exceptions)
- `DateTime`/`DateOnly` instead of NodaTime types in non-view-model code
- String comparisons without explicit `StringComparison`
- Enum comparison operators in EF queries
- Magic strings (missing `nameof()`, hardcoded role names)
- Hand-edited migration files
- `bi bi-*` icon classes (Bootstrap Icons not loaded)
- Missing `[JsonInclude]` on private-setter properties
- Inline `HtmlSanitizer` or `Markdig` usage instead of `@Html.SanitizedMarkdown`
- Inline date format strings instead of shared display extensions
- New `_userManager.GetUserAsync(User)` calls instead of base class helpers
- Direct `TempData["SuccessMessage"]` assignments instead of `SetSuccess`/`SetError`/`SetInfo`

**From CODE_REVIEW_RULES.md:**
- `disabled="@boolValue"` or similar Razor boolean attribute traps
- Missing `[Authorize]` or `[ValidateAntiForgeryToken]` on POST actions
- Missing `.Include()` for navigation property access
- Silent exception swallowing (catch without logging)
- Orphaned pages (new action with no nav link)
- Cache mutations without eviction
- Form field preservation on validation failure
- Renamed `[JsonPropertyName]` attributes
- `HasDefaultValue(false)` on bools
- Batch methods missing single-item guards
- Inline `onclick`/`onsubmit` handlers (CSP violation)
- Dead code (unused variables, unreachable code)

### 4. For each violation found

Read the full file at the relevant lines (not just the diff) to confirm the violation is real. **Do not report violations based on diff context alone** — always verify in the actual file.

### 5. Produce the report

```
## PR #<number>: <title>

### VIOLATIONS

For each violation:
- **Rule:** <rule name from CODING_RULES.md or CODE_REVIEW_RULES.md>
- **File:** <path>:<line>
- **Code:** <the offending code snippet>
- **Fix:** <what should change>

### CLEAN
<list of files checked with no violations>

### SUMMARY
- Files checked: N
- Violations found: N
- Severity: BLOCK / WARN / CLEAN
```

Use **BLOCK** if any CODE_REVIEW_RULES.md hard-reject rule is violated.
Use **WARN** if only CODING_RULES.md standards are violated.
Use **CLEAN** if no violations found.

## After Review

Present the report. Do NOT make any code changes. If there are BLOCK-level issues, clearly state what must change before the PR can merge.
