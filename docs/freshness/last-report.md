# Freshness Sweep Report — 2026-06-14

**Anchor:** `18c308a52` → `909323e33` (upstream/main HEAD)
**Worktree base:** `origin/main` @ `c9749fe90`
**Mode:** diff (batch)
**Changed files in range:** 56
**Dirty entries processed:** 6 mechanical + 20 editorial = 26
**Outcome:** 2 docs updated · 3 husks pruned (4,480 lines) · rest verified clean · 0 errors

> **Anchors crossed.** At sweep start `git merge-base --is-ancestor upstream/main origin/main` was **false** — `upstream/main` (`909323e33`) and `origin/main` (`c9749fe90`) have diverged, the normal state right after a prod promotion. Per the spec the anchors are frozen at start and not reconciled mid-run; the PR diffs against the frozen `origin/main` base. Noted once, proceeding.

The entire `18c308a52..909323e33` range is **non-behavioural**. The bulk is the **display-sort burndown** (#1002 — repositories shed redundant `.OrderBy()` and gained `// arch:db-sort-ok` annotations; the dropped sorts re-appear at the display layer in the matching controller/view, so user-visible ordering is unchanged) plus the **access-matrix correction** (#1003 — dropped the "Member data export" row from the Board access matrix + Dashboard help because that bulk export was never built; only the self-service GDPR download exists). Neither changes documented behaviour, so every triggered editorial doc verified clean.

## Updated automatically

### Mechanical
- **dev-stats** — appended the 2026-06-14 codebase-growth row (script; reforge=1 day, regex-fallback=0).
- **reforge-history** — appended the 2026-06-14 semantic snapshot row (script; `c9749fe90`, 144,924 prod LOC / 2,169 classes / 233 interfaces).

## Verified clean (dirty but no drift)

### Mechanical (regen skipped — triggering change provably no-op)
- **docs-readme-index** — triggered by editorial body edits + the two scanner docs' new freshness markers; no docs added/removed/renamed and no first-paragraph/H1 changes, so every derived description is unchanged.
- **authorization-inventory** — only `UsersAdminAccountMergesController.cs` matched, and its diff is a `.OrderBy(r => r.CreatedAt)` added inside an existing action's `foreach` — no `[Authorize]`/policy/role/`RoleChecks`/`AuthorizeAsync` change. Output identical.
- **controller-architecture-audit** — same file; no new/renamed action and no route change. The action/route/purpose table is unchanged.
- **service-data-access-map** — all matches are sort-add/remove repository edits; no new DbSet access, repository method, or cache key. The service→repo→table→cache map is unchanged.

### Editorial — all 20 triggered `flag-on-change` docs, no contradictions
Every match is a display-sort-burndown edit. Repository-internal `OrderBy` relocation does not contradict any section/feature/guide invariant (the field that was sorted — e.g. `CampImage.SortOrder` — still exists and is still *tracked*; the sort merely moved to the view, preserving displayed order). The three concrete-fact candidates were each checked against source and confirmed clean:
- **architecture rule docs** (`design-rules`, `conventions`, `code-review-rules`, `roslyn-analysis`) — the edits *apply* the existing display-sort convention; they don't change it. `roslyn-analysis.md` describes `DisplaySortInControllersRule` as "baseline-ratcheted" and cites no baseline count, so the baseline shrinking (false-positives removed) contradicts nothing.
- **sections/Camps.md** — says image "display order is *tracked* per camp"; `CampRepository` dropping `.Include(b => b.Images.OrderBy(i => i.SortOrder))` leaves `CampImage.SortOrder` intact, so still true.
- **admin/GDPR docs** — no doc claims a Board/bulk member export; the #1003 removal of that never-built capability from the matrix contradicts nothing. `gdpr-export.md` correctly scopes the export to self-service own-data; `dietary-medical-nudge.md` even states "No bulk export."
- Section/feature/guide docs for Auth, Campaigns, Email, Events, Feedback, Governance, Guide, Profiles, Teams, administration, and the matching guides — all triggered by sort-only edits, no invariant contradicted.

## Pruned
| Husk | Lines | Reason |
|------|------:|--------|
| docs/plans/2026-05-14-section-align-notifications.md | 217 | all chaff (shipped section-alignment work plan; rationale already in `sections/Notifications.md`) |
| docs/superpowers/plans/2026-05-14-userinfo-debug-and-venn.md | 1,823 | all chaff (shipped impl plan; durable signal in `sections/Users.md` + `features/global/administration.md`; plan's `GetAllUserInfos()`/`HasTicket` claims now superseded) |
| docs/superpowers/plans/2026-05-14-mailer-outbound-audiences.md | 2,440 | all chaff (shipped impl plan; all invariants already in `sections/Mailer.md`; plan's bulk-import + cache-invalidation notes diverge from shipped code) |

Total **4,480 lines = 5.9% of docs/** (above the 5% soft target, under the 7% cap). No inbound references from any living doc (only each husk's self-reference to its own design spec, which stays). Each husk's wheat was mined by a dedicated analysis subagent and verified against current source before deletion.

### Wheat migrated
**None.** All three are fully-executed implementation/alignment plans for shipped features; every durable decision already lives in the corresponding `docs/sections/*.md` (verified against source). Two husks additionally contained *stale* implementer claims (superseded APIs, a bulk-import strategy that didn't ship) — migrating them would have injected inaccuracy.

`tech-debt-2026-04-23.md` was **not** pruned: it carries 19 still-open items (header preserves it as a historical record) and so fails the all-`[DONE]` gate.

## Flagged for human review
- **Access-matrix coverage gap (informational).** `sections/Auth.md` §"Access Matrix UI" documents the matrix mechanism and names `src/Humans.Web/Models/AccessMatrixDefinitions.cs` as its source, but Auth.md's `freshness:triggers` don't include that file (nor `AccessMatrixViewComponent`). This sweep demonstrated the gap: #1003 edited `AccessMatrixDefinitions.cs` + `SectionHelpContent.cs` with zero doc review. No drift resulted (Auth.md describes the mechanism — "static data, no table" — not the per-feature rows), so this was a future-coverage suggestion, not a broken fact. **Resolved this sweep** — both files added to Auth.md's triggers (Phase 7.5).
- **Unmarked-editorial blind spots persist** (carried from prior sweeps): `features/{26-events, 27-guide-browser, 43-google-group-membership-sync, test-system-reliability, user-search-overhaul}.md`, `features/agent/agent-section.md`, `guide/{AiHelper, EmailAccount, SigningIn, TicketTransfers, TwoStepVerification, YourData}.md`, `sections/{Agent, Mailer, _Index}.md`. None triggered this sweep; a future sweep should add `freshness:triggers` to scope them.

## Proposed for review
None — all prune candidates resolved this sweep (every husk verified all-chaff against source).

## Phase 7.5 — review items (raised inline, Peter said "add it")
1. **Access-matrix trigger gap** — added `src/Humans.Web/Models/AccessMatrixDefinitions.cs` and `src/Humans.Web/ViewComponents/AccessMatrixViewComponent.cs` to `sections/Auth.md`'s `freshness:triggers`, and extended its `flag-on-change` reason to name the access-matrix mechanism (§"Access Matrix UI"). The rendering view (`Views/Shared/Components/AccessMatrix/Default.cshtml`) was deliberately left out — cosmetic view edits shouldn't trigger an Auth.md review. **Resolved (fixed).**

## Skipped (errors)
None.
