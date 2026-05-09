---
name: section-align
description: "Multi-phase orchestrator: inventory drift, rename surfaces, fix arch violations + review backlog, /simplify, polish docs — all in a push-and-bot-review loop. Use when a sizable PR landed, a section shows arch drift, or /pr-review returned a non-trivial violation list. Camps is the gold-standard reference."
argument-hint: "PR 374 | section EventGuide | section Camps --inventory-only"
---

# Section Align

A section is "aligned" when folders/namespaces/controllers/views/roles/routes/docs share one name; routes follow `/<Section>/*`, `/<Section>/Admin/*`, `/api/<section>/*`; services own their data; interfaces obey the budget ratchet; migrations are EF-auto-generated; and the invariant doc matches `SECTION-TEMPLATE.md`. **Camps is the reference.**

## Input

- `PR <n>` — inventory against diff; work on PR branch in a worktree.
- `section <Name>` — resolves to `docs/sections/<Name>.md` + matching code; fresh branch off `main`.
- `<empty>` — ask which target.
- `--inventory-only` — Phase 0 only.

## Stop conditions (check after Phase 0 — ask user before continuing)

- Canonical name collides with an existing controller/service/folder.
- Cross-section DB fix would bump another section's budget by more than 1–2 methods.
- Owning section of an entity is genuinely ambiguous (affects `design-rules.md §8`).
- Invariant doc is missing more than half the `SECTION-TEMPLATE.md` shape.
- PR branch has uncommitted work or open conflicts.

Surface as: "I hit X. Decision needed: [A] / [B] / [C]."

## Phases

```
Phase 0  Inventory → local/section-align-<target>.md; stop conditions; ask user
Phase 1  Rename / surface alignment — Sonnet subagents; push; bot-review loop
Phase 2  Fix arch violations + prior review items — Sonnet/Opus; push; bot-review loop
Phase 3  /simplify pass — Opus; push; bot-review loop
Phase 4  Doc polish — Opus; push; final review loop until merge-ready
```

Sequential phases. Within a phase, parallel subagents up to **hard cap of 3** (`~/.claude-shared/shared/claude.md`). Escalate model: Sonnet for Phase 1 renames → Opus for Phases 2–4.

## Mode detection

| Mode | Trigger | Branch | Phase 2 prior comments |
|------|---------|--------|------------------------|
| PR | `PR <n>` | PR head branch in worktree | yes |
| Existing-section | `section <Name>`, main clean | new `align/<section>` | none |
| Mid-build | `section <Name>`, on feature branch | continue | none |

Always use a worktree under `.worktrees/section-align-<target>/` (`feedback_always_use_worktree`).

---

## Phase 0 — Inventory

Output: `local/section-align-<target>.md`.

**1. Section name consistency** — variants in use across folders/namespaces/controllers/views/roles/routes/docs. Propose canonical name. Flag collisions.

**2. URL surface** — every route. Catch: `/Admin/<X>/*` paths (`architecture_no_admin_url_section`), non-Barrios↔Camps aliases (`feedback_no_url_aliases`), cross-section URL exposure, generic top-level controllers for section concerns.

**3. Role surface** — domain-scoped roles need `*Admin` suffix (`feedback_admin_superset`). Exceptions only when semantics don't fit (e.g., `ConsentCoordinator`).

**4. Cross-section access**
```bash
git -C <worktree> grep -nE '_db\.(\w+)\.(Where|FirstOrDefault|FindAsync|ToListAsync)' src/Humans.Infrastructure/Repositories/<Section>/
```
Cross-reference each `_db.X` against `design-rules.md §8`.

**5. Controller → DbContext**
```bash
git -C <worktree> grep -nE 'HumansDbContext' src/Humans.Web/Controllers/
```
Any hit violates Design Rule §2a.

**6. Interface budget** — new interfaces ≥10 methods need an `InterfaceMethodBudgetTests.cs` entry. Flag any existing budgeted interface this work would push over budget.

**7. Migrations** — hand-edited body or snapshot violates `architecture_no_hand_edited_migrations`.

**8. Section invariant doc** — present? name matches? follows `SECTION-TEMPLATE.md` shape? structural gaps?

**9. Open prior-review items (PR mode)**
```bash
gh pr view <n> --repo peterdrier/Humans --json comments,reviews
gh api repos/peterdrier/Humans/pulls/<n>/comments
```
List each unresolved comment with inline-comment ID. Also check `nobodies-collective/Humans` (`feedback_pr_review_both_repos`).

### Plan file skeleton

```markdown
# Section Align — <target>
**Run started:** <date> | **Mode:** <mode> | **Worktree:** <path>
**Canonical section name proposal:** <name>

## Inventory
1. Name consistency: <findings>
2. URL surface: <routes table with violation flags>
3. Role surface: <violations>
4. Cross-section access: <list>
5. Controller→DbContext: <list>
6. Interface budget: <conflicts>
7. Migrations: <findings>
8. Invariant doc: <gaps>
9. Prior review items: <list with comment IDs>

## Stop conditions tripped
<none or list with proposed decisions>

## Phase plan
- Phase 1 (rename): <list>
- Phase 2 (fix): <list, blocking items flagged>
- Phase 3 (simplify): ~<N> fixes
- Phase 4 (docs): <list>
```

Surface to user. If `--inventory-only`, stop. Otherwise: "Phase 0 complete. Proceed to Phase 1?"

---

## Phase 1 — Rename / surface alignment

Sonnet subagents. `/reforge` for impact analysis before bulk edits. Build must stay green after each step.

Order:
1. **Invariant doc** — `git mv docs/sections/<Old>.md docs/sections/<New>.md`
2. **Folders** — `git mv` (Application, Infrastructure, Views)
3. **Namespaces** in lockstep
4. **Symbols** — `IXService`, `XController`, `XAdminController`, etc. via `/reforge` + bulk Edit
5. **Route attributes** — class-level to `/<Section>/*`; admin under `/<Section>/Admin/*`
6. **Role names** — `*Admin` suffix unless Phase 0 justified exception
7. **Config keys** — update `appsettings*.json`
8. **Entities** — only if prefix is awkward post-rename; surface to user, default keep
9. **DB tables** — leave alone (`architecture_no_drops_until_prod_verified`); use `[Table("legacy_name")]` if entity renamed

After each rename group:
```bash
dotnet build Humans.slnx -v quiet && dotnet test Humans.slnx -v quiet
```

Commit every 3–5 logical units. Update PR body if routes changed. Thread-reply prior comments addressed (`feedback_codex_thread_replies`):
```bash
gh api repos/peterdrier/Humans/pulls/<n>/comments/<id>/replies -X POST -f body="Addressed in <sha> — ..."
```

Push at end of phase. Bot-review sub-loop until clean (`feedback_done_means_codex_clean`).

---

## Phase 2 — Fix arch violations + prior review items

Sonnet by default; Opus for nuanced items.

- **Cross-section DB access** — route through owning interface. If addition busts budget, `/audit-surface <OtherInterface>` for 1-for-1 swap. **Stop and ask** if no swap found.
- **Controller-DI-DbContext** — extract service+repo layer.
- **Interface budget** — add to `InterfaceMethodBudgetTests.Budgets` with exact count; run the test.
- **Hand-edited migration** — `dotnet ef migrations remove` + redo `dotnet ef migrations add`. Verify snapshot is byte-for-byte EF output.
- **Docstring vs reality** — align to what's enforced; no DB triggers unless ConsentRecord-grade reason (`feedback_db_enforcement_minimal`).
- **Architecture tests** — add tests catching the section's worst violations (e.g., "no controller injects `HumansDbContext`").
- **URL aliases / cross-section exposure** — remove; surface downstream consumer risk.
- **Prior review threads** — fix or reply with reasoning via `gh api .../comments/<id>/replies` (`feedback_codex_thread_replies`); never top-level.

Build + tests green. Push at end of phase. Bot-review sub-loop until clean.

---

## Phase 3 — /simplify pass

Opus. Volume scales to section LOC (`feedback_simplify_scope_to_section_size`): ~6–10 fixes for 7k LOC, ~2–3 for 1k LOC.

Common targets: duplicated logic across controllers; LINQ-on-EF in services (push to thick repos, `feedback_no_linq_at_db_layer`); unnecessary `.ToList()`; `Cached*` names (`feedback_caching_transparent`); extensions on owned types (`feedback_no_extensions_for_owned_classes`); view-component caches (`feedback_viewcomponent_no_cache`).

Push at end of phase. Bot-review sub-loop until clean.

---

## Phase 4 — Doc polish

Opus.

- **Invariant doc** — verify all `SECTION-TEMPLATE.md` sections: Concepts, Data Model, Actors & Roles, Invariants, Negative Access Rules, Triggers, Cross-Section Dependencies, Architecture.
- **Feature specs** (`docs/features/*.md`) — match implementation; rename if section name changed.
- **`data-model.md`** — update owned-entity index (per-entity detail belongs in invariant doc, not here).
- **`todos.md`** — per `feedback_todos_update_after_commits`.
- **`maintenance-log.md`** — if a recurring task ran.
- **About page** — if dependencies changed.
- **`/freshness-sweep`** — if catalog covers touched docs.

Push. Final bot-review loop until merge-ready. Report: PR/branch URL, commits per phase, one-line status. Do not offer `/schedule` follow-ups (`feedback_no_schedule_offers`).

---

## Loop discipline

**"Done" = bot-review-clean**, not pushed+green (`feedback_done_means_codex_clean`).

- Push every 3–5 commits within a phase; trigger-review push at phase boundary.
- First push: get explicit user go-ahead. Standing approval after that unless revoked.
- Never push to `main`; never `--no-verify`.
- Each sub-loop: push → wait for Codex + Claude → thread-reply each finding (fix or reject with reasoning) → commit + push → repeat until clean.

---

## Resume behavior

If `local/section-align-<target>.md` exists: read it, find last completed phase, ask: "Found plan from <date>. Last complete: <phase>. Resume or start fresh?" Update the plan file at end of each phase.

---

## Rule references

- `architecture_no_admin_url_section` · `feedback_no_url_aliases` · `feedback_admin_superset`
- `architecture_no_hand_edited_migrations` · `architecture_no_drops_until_prod_verified`
- `architecture_interface_budget_ratchet_down_only` · `feedback_db_enforcement_minimal`
- `feedback_no_linq_at_db_layer` · `feedback_caching_transparent` · `feedback_no_extensions_for_owned_classes` · `feedback_viewcomponent_no_cache`
- `feedback_codex_thread_replies` · `feedback_pr_review_both_repos` · `feedback_done_means_codex_clean`
- `feedback_simplify_scope_to_section_size` · `feedback_dotnet_verbosity` · `feedback_always_use_worktree`
- `feedback_no_direct_to_main` · `feedback_no_schedule_offers` · `feedback_todos_update_after_commits`
- Design Rule §2a (no controller→DbContext) · §8 (table ownership) — `docs/architecture/design-rules.md`
- `docs/sections/SECTION-TEMPLATE.md`
