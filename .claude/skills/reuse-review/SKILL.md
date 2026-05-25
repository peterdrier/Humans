---
name: reuse-review
description: "Review a local diff or PR for unnecessary durable surface: new files, public types, interface methods, DTOs/view models, helpers, routes, DI registrations, services, repositories, and dependencies that should reuse existing owners instead."
argument-hint: "PR 64 | (no args = current branch)"
---

# Reuse Review

This is not a correctness review. It checks whether the change added durable surface where reuse, caller-side composition, or an existing owner would have been better.

## Inputs

`$ARGUMENTS` can be:
- `PR <number>` - review an existing PR on `peterdrier/Humans`
- Empty - review the current branch against `origin/main`

## Steps

1. Read:
   - `CLAUDE.md` reuse-first section
   - `memory/process/reuse-first-change-discipline.md`
   - `memory/architecture/interface-method-additions-are-debt.md`
   - `docs/architecture/reuse-review-rules.md`
2. Fetch the diff:
   - PR: `gh pr diff <number> --repo peterdrier/Humans`
   - Local: `git diff --find-renames origin/main...HEAD`
3. Build a surface inventory:
   - new files
   - new public types
   - interface method changes
   - new/changed service and repository methods
   - new DTOs/view models/helpers
   - new controller actions/routes/pages
   - new DI registrations and package references
4. For each item, search for the existing owner/surface before flagging.
5. Report only concrete findings. A finding must name both the new surface and the existing surface that should absorb or replace it.

## Output

Use this format:

```markdown
## Reuse Review: PASS|WARN

### Findings
- REUSE - <new surface> should reuse <existing surface>.
  Why: <cost/risk>.
  Fix: <specific consolidation or caller-side composition>.

### Surface Inventory
- New files:
- Public/interface surface:
- Services/repositories:
- DTOs/view models/helpers:
- Routes/pages:
- DI/dependencies:
```

If there are no findings, say `PASS - no unnecessary durable surface found` and still include the surface inventory.
