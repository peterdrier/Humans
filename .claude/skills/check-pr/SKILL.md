---
name: check-pr
description: "Check an externally created PR for coding rule violations. Fetches the PR diff and reviews against the atomic project rules in memory/INDEX.md plus docs/architecture/code-review-rules.md."
argument-hint: "PR 64 | https://github.com/.../pull/64"
---

# Check PR for Coding Rule Compliance

Review an externally created pull request for violations of this project's coding rules. This is NOT a general code review — it specifically checks compliance with the atomic project rules cataloged in `memory/INDEX.md` (under `memory/code/` and `memory/architecture/`) plus the reviewer reject rules in `docs/architecture/code-review-rules.md`.

## Input

`$ARGUMENTS` can be:
- `PR <number>` — PR number on peterdrier/Humans
- `<number>` — PR number on peterdrier/Humans
- A full GitHub PR URL — extracts repo and number automatically

## Execution

### 1. Load the rules

Read these in full:
- `memory/INDEX.md` — catalog of atomic project rules. Scan descriptions to identify which atoms are likely relevant to the PR's diff. Then fetch and read each relevant atom under `memory/code/` and `memory/architecture/` (serialization, DbContext, enums, NodaTime, etc.).
- `docs/architecture/code-review-rules.md` — hard reject rules (auth gaps, missing Include, bool sentinels, etc.)
- `docs/architecture/design-rules.md` — architecture story; reference when an atom cites a §-section.

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

**From `memory/code/` and `memory/architecture/` atoms** (each item below maps to a specific atom — read the atom for the full rule before flagging a violation):
- Direct `ApplicationDbContext` injection or usage in controllers — `memory/architecture/no-linq-at-db-layer.md` + `design-rules.md` services-own-data
- `DateTime`/`DateOnly` instead of NodaTime types in non-view-model code — `memory/code/nodatime-for-dates.md`
- String comparisons without explicit `StringComparison` — `memory/code/string-comparisons-explicit.md`
- Enum comparison operators in EF queries — `memory/code/no-enum-compare-in-ef.md`
- Magic strings (missing `nameof()`, hardcoded role names) — `memory/code/no-magic-strings.md`
- Hand-edited migration files — `memory/architecture/no-hand-edited-migrations.md`
- `bi bi-*` icon classes (Bootstrap Icons not loaded) — `memory/code/icons-fa6-only.md`
- Missing `[JsonInclude]` / `[JsonConstructor]` / `[JsonPolymorphic]` — `memory/code/json-serialization.md`
- Inline `HtmlSanitizer` or `Markdig` usage instead of `@Html.SanitizedMarkdown` — `memory/code/sanitized-markdown-rendering.md`
- Inline date format strings instead of shared display extensions — `memory/code/datetime-display-formatting.md`
- New `_userManager.GetUserAsync(User)` calls instead of base class helpers — `memory/code/controller-base-conventions.md`
- Direct `TempData["SuccessMessage"]` assignments instead of `SetSuccess`/`SetError`/`SetInfo` — `memory/code/controller-base-conventions.md`

If the PR touches an area whose atom isn't in this quick-list, scan `memory/INDEX.md` for the relevant bucket and check those too. The list above is a starting menu, not the full surface.

**From code-review-rules.md:**
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
- **Rule:** <atom path under `memory/` or rule name from `code-review-rules.md`>
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

Use **BLOCK** if any `code-review-rules.md` hard-reject rule is violated.
Use **WARN** if only `memory/` atom rules are violated (no hard-reject hit).
Use **CLEAN** if no violations found.

## After Review

Present the report. Do NOT make any code changes. If there are BLOCK-level issues, clearly state what must change before the PR can merge.
