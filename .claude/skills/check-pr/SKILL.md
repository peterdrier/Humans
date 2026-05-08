---
name: check-pr
description: "Check an externally created PR for coding rule violations against memory/INDEX.md atoms and docs/architecture/code-review-rules.md."
argument-hint: "PR 64 | https://github.com/.../pull/64"
---

# Check PR for Coding Rule Compliance

Checks compliance with atomic project rules (`memory/code/`, `memory/architecture/`) and hard-reject rules in `docs/architecture/code-review-rules.md`. Not a general code review.

`$ARGUMENTS`: PR number, `PR <number>`, or full GitHub URL.

## Steps

### 1. Load rules

- `memory/INDEX.md` — scan, then read each relevant atom under `memory/code/` and `memory/architecture/`
- `docs/architecture/code-review-rules.md` — hard reject rules
- `docs/architecture/design-rules.md` — reference when an atom cites a §-section

### 2. Fetch the PR

```bash
gh pr diff <number> --repo <repo>
gh pr view <number> --repo <repo> --json title,body,files
```

### 3. Check each changed file

**`memory/` atoms** (read the atom before flagging):
- Direct `ApplicationDbContext` injection in controllers — `memory/architecture/no-linq-at-db-layer.md`
- `DateTime`/`DateOnly` instead of NodaTime in non-view-model code — `memory/code/nodatime-for-dates.md`
- String comparisons without explicit `StringComparison` — `memory/code/string-comparisons-explicit.md`
- Enum comparison operators in EF queries — `memory/code/no-enum-compare-in-ef.md`
- Magic strings (missing `nameof()`, hardcoded role names) — `memory/code/no-magic-strings.md`
- Hand-edited migration files — `memory/architecture/no-hand-edited-migrations.md`
- `bi bi-*` icon classes (Bootstrap Icons not loaded) — `memory/code/icons-fa6-only.md`
- Missing `[JsonInclude]` / `[JsonConstructor]` / `[JsonPolymorphic]` — `memory/code/json-serialization.md`
- Inline `HtmlSanitizer`/`Markdig` instead of `@Html.SanitizedMarkdown` — `memory/code/sanitized-markdown-rendering.md`
- Inline date format strings instead of shared display extensions — `memory/code/datetime-display-formatting.md`
- `_userManager.GetUserAsync(User)` instead of base class helpers — `memory/code/controller-base-conventions.md`
- Direct `TempData["SuccessMessage"]` instead of `SetSuccess`/`SetError`/`SetInfo` — `memory/code/controller-base-conventions.md`

For unlisted areas, scan `memory/INDEX.md` for the relevant bucket.

**`code-review-rules.md`:**
- `disabled="@boolValue"` Razor boolean attribute traps
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
- Dead code

### 4. Report

Before reporting a violation, read the actual file at those lines — do not flag from diff context alone.

Output per violation: **Rule** (atom path or rule name), **File** (path:line), **Code** (snippet), **Fix**.
List clean files under `### CLEAN`. End with files checked, violations found, and severity:

- **BLOCK** — any `code-review-rules.md` hard-reject violated
- **WARN** — only `memory/` atom violations
- **CLEAN** — no violations

Do NOT make code changes.
