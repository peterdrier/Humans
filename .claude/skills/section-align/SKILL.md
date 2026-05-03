---
name: section-align
description: "Multi-phase orchestrator that brings a section of the Humans codebase into line with current architecture rules — inventories drift, renames inconsistent surfaces, fixes open review backlog, runs /simplify, and polishes docs in a push-and-bot-review loop. Use when a sizable external PR just landed, a new section was just authored (yours or otherwise), an existing section is showing arch drift, or /pr-review surfaced a non-trivial violation list. Triggers include 'align this section', 'clean up X section', 'fix this PR' (when the PR is sizable, not one-line), 'refactor X to current standards', 'run section-align', or invoking with a PR or section target. Camps is the gold-standard reference for what aligned looks like. Do NOT use for single-concern bug fixes, doc-only changes, or refactors not tied to arch standards."
argument-hint: "PR 374 | section EventGuide | section Camps --inventory-only"
---

# Section Align

A section in this codebase is "aligned" when its URL surface, role names, controllers, namespaces, folders, services, repositories, and docs all use the same name; its routes follow project conventions (`/<Section>/*`, admin pages at `/<Section>/Admin/*`, API at `/api/<section>/*`); its services own their data and don't reach into other sections' tables; its interfaces obey the budget ratchet; its migrations are EF-auto-generated; its docs match `docs/sections/SECTION-TEMPLATE.md`; and its open review backlog is closed.

Sections drift from this state because rules harden faster than old code gets retrofitted. This skill is the retrofit loop. **Camps is the reference** — when a phase report needs a "what aligned looks like" anchor, point at Camps.

## Input

`$ARGUMENTS` accepts:

- `PR <n>` or `<n>` — PR number on `peterdrier/Humans`. Inventory runs against the PR's diff; phase work happens in a worktree on the PR branch.
- `section <Name>` — existing section. Resolves to `docs/sections/<Name>.md` and the controllers/services/views matching that section name. Phase work happens on a fresh feature branch off `main`.
- `<empty>` — ask which target.
- `--inventory-only` flag — run Phase 0 and stop. Useful for scoping work before committing to the loop.

## When to stop after Phase 0

If any of these are true, do not progress past Phase 0 without a decision from the user:

- The proposed section name collides with an existing controller/service/folder (e.g., `Guide` was taken by the user manual when EventGuide tried to rename).
- Cross-section DB access requires bumping another section's interface budget by more than 1–2 methods (the ratchet is hard — surface for a 1-for-1-swap discussion).
- An entity's owning section is genuinely ambiguous (would change the table-ownership map in `design-rules.md §8`).
- The section invariant doc is missing more than half the `SECTION-TEMPLATE.md` shape — phase 4 alone won't recover it; needs a fresh write.
- The PR branch has uncommitted work or open conflicts with `main`.

Surface as: "I hit X. Decision needed: [option A] / [option B] / [option C]." Wait for the user.

## Architecture

```
section-align <target>
   │
   ├─ Phase 0 — Inventory (always; cheap; output drives everything)
   │      → local/section-align-<target>.md (plan file)
   │      → stop conditions checked; ask user if any tripped
   │
   ├─ Phase 1 — Rename / surface alignment (Sonnet subagents, /reforge for impact)
   │      → folders, namespaces, controllers, routes, roles, config keys
   │      → push at end of phase
   │      → wait for Codex/Claude bot review; sub-loop until clean
   │
   ├─ Phase 2 — Fix open review items + arch violations (Sonnet → Opus as needed)
   │      → cross-section access, controller-DI-DbContext, hand-edited migrations,
   │        interface budget, append-only docstrings, URL aliases, prior comments
   │      → push; bot-review sub-loop until clean
   │
   ├─ Phase 3 — /simplify pass (Opus)
   │      → volume scaled to section LOC (6–10 fixes for ~7k LOC; 2–3 for ~1k)
   │      → push; sub-loop until clean
   │
   └─ Phase 4 — Doc review and final polish (Opus)
          → invariant doc, feature specs, data-model index, todos.md, About page
          → push; final review loop until merge-ready
```

Phases are sequential. Within a phase, dispatch subagents in parallel where independent — **hard cap of 3 parallel subagents** per `~/.claude-shared/shared/claude.md`.

Increase model strength on later passes: Sonnet for the rename mechanics in Phase 1, Opus for nuanced phase-2 fixes, Opus for /simplify, Opus for docs. Each pass is harder than the one before.

## Mode detection

Three modes follow from the target shape. The skill behaves slightly differently in each:

| Mode | Trigger | Branch | Phase 2 review backlog |
|------|---------|--------|-----------------------|
| **PR mode** | `PR <n>` | check out PR head branch in worktree | yes — fetch and address prior Codex/Claude/human comments |
| **Existing-section mode** | `section <Name>` and main is clean | new branch off `main` (e.g., `align/<section>`) | none — only the arch violations Phase 0 found |
| **Mid-build mode** | `section <Name>` and user is on a feature branch | continue on that branch | none |

In all modes, work happens in a worktree under `.worktrees/section-align-<target>/`. Never edit a non-main branch in the main checkout (per `feedback_always_use_worktree`).

---

## Phase 0 — Inventory

Cheap, deterministic, reusable. Output is `local/section-align-<target>.md` — both an audit-trail document and the input the next phases read.

Sections of the inventory, in this order:

### 1. Section name consistency
Folders, namespaces, controllers, views, role names, route prefixes, doc files. Surface the variants in use (`EventGuide`/`Guide`/`Moderation` was the failure mode). Propose the canonical name. Flag collisions with existing controllers, services, view folders, doc files.

### 2. URL surface
Every route the section adds. Catch:
- `/Admin/<X>/*` paths (violates `architecture_no_admin_url_section` — must move to `/<Section>/Admin/*`)
- URL aliases that aren't Barrios↔Camps (violates `feedback_no_url_aliases`)
- Cross-section URL exposure (e.g., `/api/<this-section>/camps` is camp data outside the Camps API namespace)
- Generic top-level controllers for section-specific concerns (`/Moderation` instead of `/<Section>/Moderation`)

### 3. Role surface
Every new role and role group. Domain-scoped roles must use `*Admin` suffix per `feedback_admin_superset` (`TeamsAdmin`, `CampAdmin`, etc.). Coordinator/Moderator roles are exceptions only when semantics genuinely don't fit the superset pattern (e.g., `ConsentCoordinator` has filter-bypass semantics specific to onboarding flow).

### 4. Cross-section access
Services and repos that touch tables they don't own. Run:

```bash
git -C <worktree> grep -nE '_db\.(\w+)\.(Where|FirstOrDefault|FindAsync|ToListAsync)' src/Humans.Infrastructure/Repositories/<Section>/
```

Cross-reference each `_db.X` access against the table-ownership map in `docs/architecture/design-rules.md §8`. Direct `_db.<ForeignTable>` access in a repo is the canonical violation.

### 5. Controller → DbContext
Any controller injecting `HumansDbContext`. Hard violation of Design Rule §2a. Run:

```bash
git -C <worktree> grep -nE 'HumansDbContext' src/Humans.Web/Controllers/
```

### 6. Interface budget
Any new service interface ≥10 methods that should be added to `tests/Humans.Application.Tests/Architecture/InterfaceMethodBudgetTests.cs`. Note any existing budgeted interface this work would push over budget — the ratchet is hard (see `architecture_interface_budget_ratchet_down_only`).

### 7. Migrations
Any hand-edited migration body or model snapshot. Hard violation of `architecture_no_hand_edited_migrations`. Check the PR/branch commits for migration files; check the PR body for explicit admissions of manual edits.

### 8. Section invariant doc
Present? Path matches the section name? Follows `docs/sections/SECTION-TEMPLATE.md` shape (Concepts, Data Model, Actors & Roles, Invariants, Negative Access Rules, Triggers, Cross-Section Dependencies, Architecture)? Note structural gaps.

### 9. Open prior-review items (PR mode only)

```bash
gh pr view <n> --repo peterdrier/Humans --json comments,reviews
gh api repos/peterdrier/Humans/pulls/<n>/comments
```

List every unresolved Codex/Claude/human comment, with the inline-comment ID for thread-replying in Phase 2. Also check `nobodies-collective/Humans` if the PR is cross-fork (`feedback_pr_review_both_repos`).

### Phase 0 output

Write `local/section-align-<target>.md` with this skeleton:

```markdown
# Section Align — <target>
**Run started:** <date>
**Mode:** PR / existing-section / mid-build
**Worktree:** <path>
**Canonical section name proposal:** <name>

## Phase 0 inventory

### 1. Section name consistency
<findings>

### 2. URL surface
<table of routes, with violation flags>

### 3. Role surface
<roles + violations>

### 4. Cross-section access
<list of foreign-table accesses>

### 5. Controller → DbContext
<list>

### 6. Interface budget
<list of new interfaces; any budget conflicts>

### 7. Migrations
<hand-edit findings>

### 8. Section invariant doc
<presence + structural gaps>

### 9. Open prior-review items (PR mode)
<list with comment IDs>

## Stop conditions tripped
<empty if none; otherwise list with proposed decisions>

## Phase plan
- Phase 1 (rename): <bullet list of renames to perform>
- Phase 2 (fix): <bullet list of items, marked which are blocking>
- Phase 3 (simplify): expected fix count = <N> based on section LOC
- Phase 4 (docs): <list of docs to refresh>
```

Surface this report to the user. If `--inventory-only`, stop here. Otherwise ask: "Phase 0 complete. Ready to proceed to Phase 1, or any adjustments first?"

---

## Phase 1 — Rename / surface alignment

Sonnet subagents. Use `/reforge` for symbol impact analysis before bulk edits.

Order matters — keep the build green at each step. Order:

1. **Section invariant doc** — `git mv docs/sections/<Old>.md docs/sections/<New>.md`. Update the `# Title` line.
2. **Folders** (Application interfaces, services, repositories, infrastructure repos, views) — `git mv` each.
3. **Namespaces** in lockstep with folders — Edit every `namespace` line.
4. **Class/interface symbols** — `IXService`, `IXRepository`, `XController`, `XAdminController`, `XApiController`, `XFeatureFilter`, etc. Use `/reforge` to find references first, then bulk Edit. Memory says ReSharper-driven renames are normally Peter's work, but in this skill the user has explicitly delegated.
5. **Route attributes** — class-level routes to `/<Section>/*` and `/api/<section>/*`. **Always** consolidate admin routes under `/<Section>/Admin/*`. Sub-routes on actions stay relative.
6. **Role names** — `RoleNames.X`, `RoleGroups.XAdminOrAdmin`, `BoardManageableRoles`. Apply `*Admin` suffix unless Phase 0 explicitly justified an exception.
7. **Config keys** — `Features:X`, etc. Update `appsettings*.json`.
8. **Entities** — only if entity prefix is awkward in the new section (`GuideEvent` in an Events section is awkward; `Camp` in a Camps section is fine). Surface entity rename decisions to user; default to keeping entities if collision risk.
9. **DB tables** — leave alone for now (per `architecture_no_drops_until_prod_verified`). Use EF `[Table("legacy_name")]` if entities renamed.

Build + test must be green at every commit:

```bash
dotnet build Humans.slnx -v quiet
dotnet test Humans.slnx -v quiet
```

Both with `-v quiet` always (`feedback_dotnet_verbosity`). Never pipe through tail/head/grep.

Commit cadence: every 3–5 logical units (e.g., one commit per controller rename + reroute). Push at end of phase.

After push, update the PR body if route names changed — PR bodies go stale fast and reviewers/release notes pull from them.

Thread-reply prior review comments that the rename addresses (PR mode), per `feedback_codex_thread_replies`:

```bash
gh api repos/peterdrier/Humans/pulls/<n>/comments/<id>/replies -X POST -f body="Addressed in <commit-sha> — <controller> moved to /<Section>/Admin/*."
```

### Phase 1 push and review sub-loop

Push to the working branch. Wait for fresh Codex/Claude bot reviews. Address findings:
- For each finding, decide: fix, or reply explaining why not. Don't auto-accept (`feedback_pr_review_both_repos`, `feedback_done_means_done`).
- Re-push.
- Loop until bots come back clean. Done = bot-review-clean, not pushed-and-green (`feedback_done_means_codex_clean`).

### Subagent prompt for Phase 1

```
You are a Phase 1 rename agent for section-align target <target>.

Working directory: <worktree-path>
Branch: <branch>
Plan file: local/section-align-<target>.md (Phase 1 section)

Your scope is RENAMES ONLY. Do not fix arch violations, simplify, or touch
docs other than the section invariant doc filename. Those are later phases.

Steps:
1. Read the Phase 1 section of the plan file.
2. Use /reforge to find all references to each symbol you're renaming.
3. Rename in the order listed in the plan file.
4. After each rename group: dotnet build Humans.slnx -v quiet; if it fails, fix
   before continuing.
5. After all renames: dotnet test Humans.slnx -v quiet.
6. Commit logically (one commit per controller-rename-and-reroute is the right
   granularity). Use clear messages.
7. Report back with a list of commits made and any blockers hit.

If the user sends a message, stop and answer them.
```

---

## Phase 2 — Fix open review items + arch violations

Sonnet by default; switch to Opus for nuanced items (cross-section refactors, interface-budget swaps, migration regenerations).

Each item from Phase 0's inventory becomes a sub-task. Common patterns:

- **Cross-section DB access**: route through the owning section's interface. If the addition would bust an interface budget, run `/audit-surface <OtherInterface>` to find candidates to remove in the same PR. **Stop and ask** if you can't find 1-for-1 swaps for budget-bumping additions.
- **Controller-DI-DbContext**: extract a service+repository layer. Reuse the section's existing service if there is one.
- **Interface budget**: add new section interfaces to `InterfaceMethodBudgetTests.Budgets` with exact counts. Run the test to confirm.
- **Hand-edited migration / snapshot**: regenerate from scratch. `dotnet ef migrations remove` (if not yet pushed), redo via `dotnet ef migrations add`. Verify snapshot is byte-for-byte EF output. Per `architecture_no_hand_edited_migrations` — pre-commit hook should catch hand edits; if it didn't, surface why.
- **Append-only / immutability docstrings** vs reality: align the docstring to what's actually enforced. Don't add DB triggers unless there's a ConsentRecord-grade reason (per `feedback_db_enforcement_minimal`).
- **Architecture tests**: add tests that would have caught the section's worst violations. Prime example: "no controller in `Humans.Web` injects `HumansDbContext`" — would have caught Round 1 EventGuide failures pre-review.
- **URL aliases / cross-section URL exposure**: remove the aliases. Coordinate with downstream consumers (PWA, integrations) if removal could break them — surface to user.
- **Resolve every prior review thread** (Codex, Claude, human). Fix if it makes sense; if it doesn't, reply explaining why. Per `feedback_codex_thread_replies`, thread-reply each finding directly via `gh api repos/.../pulls/<n>/comments/<id>/replies`, not as a top-level PR comment.

Build + tests green. Push at end of phase. Bot-review sub-loop until clean.

### Subagent prompt for Phase 2

```
You are a Phase 2 fix agent for section-align target <target>.

Working directory: <worktree-path>
Plan file: local/section-align-<target>.md (Phase 2 section)

Your scope is the items listed in Phase 2 of the plan file. Each item has a
fix strategy. Implement them in the order given.

Critical rules:
- Interface budget ratchet is HARD. If an addition busts a budget, find a
  1-for-1 swap or STOP and surface to the orchestrator.
- Don't over-engineer. The fix is the smallest change that addresses the
  finding without violating other rules.
- For each prior review comment you address, thread-reply via gh api with the
  commit SHA and a one-line explanation.
- For each prior review comment you intentionally don't fix, reply explaining
  the reasoning.

Build + tests green at every commit. Use -v quiet.

If the user sends a message, stop and answer them.
```

---

## Phase 3 — /simplify pass

Opus.

Run the `simplify` skill scoped to the section. Per `feedback_simplify_scope_to_section_size`: volume is sized to section LOC, not a fixed count. Expect ~6–10 fixes for a 7k-LOC section, ~2–3 for a 1k-LOC section. Match the discipline of prior /simplify PRs but don't overreach.

Common targets that show up in this codebase's /simplify runs:

- Duplicated logic across controllers (consolidate into service helpers — the recurring-event-expansion pattern in EventGuide was duplicated across 4 controllers)
- LINQ-on-EF-properties scattered across services (push to thick repos returning Lists, per `feedback_no_linq_at_db_layer`)
- `.ToList()` materializations that aren't needed
- `Cached*` type names (cache should be transparent per `feedback_caching_transparent`)
- Extension methods on owned types (per `feedback_no_extensions_for_owned_classes`)
- View-component caches (move to owning service per `feedback_viewcomponent_no_cache`)
- View-model boilerplate that could collapse to record types

Push at end of phase. Bot-review sub-loop until clean.

---

## Phase 4 — Doc review and final polish

Opus.

- **Section invariant doc** matches code post-rename + post-fixes. Verify against `docs/sections/SECTION-TEMPLATE.md` shape: Concepts, Data Model, Actors & Roles, Invariants, Negative Access Rules, Triggers, Cross-Section Dependencies, Architecture.
- **Feature spec docs** (`docs/features/*.md`) match implementation. Update or rename if section name changed.
- **`docs/architecture/data-model.md` index** reflects the section's owned entities. Per-entity tables live in the section invariant doc, NOT in `data-model.md` (which is an index + cross-cutting rules sheet).
- **`todos.md`** updated with completed and follow-up items per `feedback_todos_update_after_commits`.
- **`docs/architecture/maintenance-log.md`** if any recurring maintenance task ran.
- **About page** (`Views/About/Index.cshtml`) updated if dependencies changed (rare for an alignment PR — usually no).
- **`/freshness-sweep`** if the freshness catalog covers any of the touched docs.

Push at end of phase. Final bot-review sub-loop until merge-ready.

---

## Loop discipline

**"Done" means bot-review-clean.** Pushed + green CI ≠ done (`feedback_done_means_codex_clean`). Wait for fresh Codex/Claude review on each push and address findings before declaring a phase complete.

**Push pattern**:
- Within a phase: push every 3–5 commits to keep CI honest.
- At phase boundary: the trigger-review push. Do not progress to the next phase until that push's bot review is clean.
- **First phase push of the run**: ask user for explicit go-ahead. After that, standing approval is implied unless the user revokes it ("hold off pushing for a bit").
- Never push to `main` directly (`feedback_no_direct_to_main`). Always feature branch + PR.
- Never `--no-verify` or skip pre-commit hooks.

**Each push sub-loop**:
1. Push.
2. Wait for bot reviews to land (Codex + Claude both review on push for this repo).
3. For each finding: thread-reply per `feedback_codex_thread_replies`. Fix if it makes sense; reject with reasoning if not (`feedback_done_means_done`).
4. Commit fixes, push again.
5. Repeat until both bots come back clean.

**Concurrency**: max 3 parallel subagents per `~/.claude-shared/shared/claude.md`. Within a phase, dispatch independent work in parallel up to that cap; queue the rest.

---

## Resume behavior

If `local/section-align-<target>.md` exists when the skill is invoked, treat it as a resume:
1. Read the plan file.
2. Find the most recent phase marked complete (or partially complete).
3. Ask the user: "Found existing plan at <date>. Last phase complete: <phase>. Resume from there, or start fresh?"

The plan file is the single source of truth for run state. Update it at the end of each phase with what landed, what's pending, and what blockers remain.

---

## Project rule references

The skill leans on these rules — cite them inline in phase reports so users can audit the rule application:

- `architecture_no_admin_url_section` — `/Admin/<Section>/*` is forbidden; new admin pages live at `/<Section>/Admin/*`
- `feedback_no_url_aliases` — only Barrios↔Camps aliases sanctioned
- `feedback_admin_superset` — domain-scoped roles use `*Admin` suffix
- `architecture_no_hand_edited_migrations` — migrations are EF auto-generated only
- `architecture_no_drops_until_prod_verified` — table renames are multi-PR
- `architecture_interface_budget_ratchet_down_only` — interface budget; hit a wall → STOP and ask
- `feedback_db_enforcement_minimal` — only ConsentRecord has doctrinal DB-enforced immutability
- `feedback_no_linq_at_db_layer` — services call thick repos returning materialized results
- `feedback_caching_transparent` — never `Cached*` type names
- `feedback_no_extensions_for_owned_classes` — extensions only on types we don't control
- `feedback_viewcomponent_no_cache` — caching lives in the owning service
- `feedback_codex_thread_replies` — thread-reply each finding directly, not top-level
- `feedback_simplify_scope_to_section_size` — simplify volume scales with section LOC
- `feedback_pr_review_both_repos` — check peterdrier AND nobodies-collective for PR comments
- `feedback_done_means_codex_clean` — pushed-and-green isn't done
- `feedback_dotnet_verbosity` — `-v quiet` always; never pipe through tail/head/grep
- `feedback_always_use_worktree` — never edit a non-main branch in the main checkout
- `feedback_no_direct_to_main` — feature branch + PR for every change
- `feedback_no_schedule_offers` — do not offer `/schedule` follow-ups at the end
- Design Rule §2a (controller cannot inject DbContext) — `docs/architecture/design-rules.md`
- Design Rule §8 (table ownership) — `docs/architecture/design-rules.md`
- Design Rule §15 (service+repo migration status A/B/C) — `docs/architecture/design-rules.md`
- `docs/sections/SECTION-TEMPLATE.md` — section invariant doc shape

Camps is the gold-standard reference. When phase reports need a "what aligned looks like" anchor, point at Camps' controllers, services, routes, and section invariant doc.

---

## What "done" looks like

The target section: name is consistent across folders/namespaces/controllers/views/roles/routes/docs; URLs follow `/<Section>/*` and `/<Section>/Admin/*` patterns; roles use `*Admin` suffix; services own their data and call other sections via interfaces only; controllers don't inject `HumansDbContext`; new interfaces are budgeted; migrations are EF-auto-generated; section invariant doc matches the template; open review backlog (PR mode) is closed; bots come back clean on the final push.

Report back to the user with: PR/branch URL, commits per phase, and a one-line status. Do not offer `/schedule` follow-ups (per `feedback_no_schedule_offers`).
