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

**`memory/` atoms** — scan `memory/INDEX.md` and read every atom whose trigger matches a changed file (read the atom before flagging). The INDEX is the source of truth; an inline copy here would drift as atoms are added or renamed.

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
