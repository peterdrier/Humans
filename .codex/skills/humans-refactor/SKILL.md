---
name: humans-refactor
description: Autonomous Reforge-guided refactor workflow for the Humans repository. Use when the user wants Codex to target a section such as Shifts, Users, Teams, Tickets, Camps, or Store, create a dedicated worktree/branch from origin/main, run iterative architecture-focused improvements, score each stage with Reforge, gate commits through a score-blind read-only architecture review, append score and verdict details to commit messages after review acceptance, push progress, and open a PR when the loop reaches stasis.
---

# Humans Refactor

Run a higher-autonomy refactor pass for one Humans section. This is the v2 tech-debt loop: Reforge supplies deterministic score pressure to the implementer/coordinator, and a separate score-blind architecture-review pass decides whether the change is worth keeping without seeing the metric movement.

## Start

1. Confirm the repo root is a Humans checkout.
2. Read the current request carefully for target section, time budget, branch/PR expectations, and any overridden constraints.
3. Fetch `origin/main`, create `refactor/YYYY-MM-DD-<section>-N`, and attach `.worktrees/refactor-YYYY-MM-DD-<section>-N`.
4. Keep all scratch output under `local/refactor-runs/<run-id>/`.
5. Run `reforge stop` before and after every Reforge call. Do not use daemon mode.

If resuming, continue the existing refactor worktree/branch only when the user clearly refers to it.

## Section Architecture Discovery

Before editing, spend a short, bounded pass (about five minutes for a familiar
section) identifying the section's intended shape. Do this even when Reforge has
obvious point targets; the score is secondary to the section architecture.

Build a section thesis with:

- repository ownership: which repositories exist for the section, and whether
  the section is repo-backed or only controller/orchestrator logic
- primary read model: the existing or intended `<Section>Info` DTO; if a cache
  exists, the cached value is usually the primary read model (`UserInfo`,
  `TeamInfo`, `CampInfo`, etc.)
- read shards: any justified secondary read models keyed differently, such as
  Shifts by rota and by user; name the shard and its key
- read surface: `I<Section>ServiceRead` or equivalent, with a goal of a small
  primitive surface: primary read(s), settings, search, and occasionally one
  narrow lookup; projections and predicates are deletion candidates
- write surface: the mutation/full service for commands; cross-section callers
  should not depend on this unless they are truly orchestrating a mutation
- settings shape: a single `<Section>SettingsInfo` when section settings are
  read externally; avoid scattered settings scalar methods
- cache semantics: which DTO facts are warm/cached, invalidation responsibilities,
  and which reads may fall back to the inner service for cold keys
- cross-section links: any cross-section dependency on a write/full service,
  repository, or entity graph; these are high-priority debt and usually need a
  grandfathered exception until removed

Before starting the meat of the refactor, share the thesis and candidate
direction with the user when they are available. For explicitly autonomous or
overnight runs, write the thesis to the run notes/PR and proceed without
blocking, but let that thesis govern the candidate ledger.

Default repo-backed-section target:

```text
Repository-backed section => read surface + write surface + primary <Section>Info.
Cross-section reads use I<Section>ServiceRead and DTO facts.
Cross-section writes/orchestration use the write/full surface only when the
caller is intentionally performing a command.
```

## Autonomy Floor

For autonomous or end-of-day runs, do not stop after the first failed implementations. Unless the user gives a shorter budget, work for at least 60 minutes before declaring stasis. If an implementation is rejected, switch search strategy and continue.

Before stasis is allowed, the run must have:

- built a candidate ledger with at least eight researched opportunities
- inspected at least three different categories of opportunity
- attempted or explicitly rejected at least five candidates with concrete reasons
- run Reforge on the target section after any accepted change
- pushed every accepted stage

Two failed patches in a row are a signal that the search strategy is too shallow, not proof that the section has no valuable work.

## Hard Limits

- Avoid database/storage changes unless the user explicitly asks for them: no migrations, no schema configuration changes, no entity persistence-shape changes, no JSON serialization attribute changes.
- Do not revert user changes or unrelated work.
- Do not move debt across sections to win points. Regressions outside the target section count against the change.
- Public members may be removed when they are internal application surface and the call graph is updated. Preserve reflection/external contracts, Razor action routes, serialized DTO contracts, and public APIs that are intentionally consumed outside the repo.
- Prefer the best end-state architecture over the smallest diff. Large cohesive refactors are allowed when the call graph and tests support them.
- Do not satisfy Reforge by adding thin behavior to DTO/request/command/options/input records. Methods such as `ApplyTo`, `ToDto`, `RenderWith`, `BuildSummary`, `SaveAsync`, or service-delegating wrappers are score-gaming unless they enforce real invariants, validation, state transitions, or domain policy already owned by that type.

## Scoring

Reforge is a candidate-selection and checkpoint tool, not the definition of success. A lower score is useful only after the score-blind architecture review accepts the diff.

Capture baseline scores before editing:

```powershell
reforge stop | Out-Null
reforge surface-score --solution Humans.slnx --format Json --all --top-symbols 200 > local\refactor-runs\<run-id>\stage00-all.json
reforge stop | Out-Null
reforge surface-score --solution Humans.slnx --group <Section> --format Json --all --top-symbols 200 > local\refactor-runs\<run-id>\stage00-section.json
reforge stop | Out-Null
```

For each candidate stage, write `stageNN-all.json` and `stageNN-section.json`. Use `scripts/reforge_delta.py` to summarize score movement for commit messages and status updates.

Measure value as:

- target-section total improvement: positive value
- overall total improvement: useful context
- internal-complexity increases: cost unless the architecture-review pass says the trade is justified
- outside-target movement: compute `target improvement + 2 * outside-target improvement`, where improvement means score decrease; an outside-target regression of 5 cancels 10 target points
- score-neutral changes: acceptable only when the reviewer says the architecture clearly improved
- parameter DTOs, input bags, options bags, and command wrappers do not earn credit merely because they reduce public parameter count
- adding facts to an existing canonical `<Section>Info`/read DTO is not the same
  as creating a new DTO; it is often the preferred move when it lets callers
  derive projections/predicates without DB or service-specific reads

Never keep a patch only to hit a percentage target. If the remaining ledger is only reducible through score-gaming, stop and report that safe score reduction is exhausted.

## Work Loop

First build a candidate ledger from the section thesis. Do this before editing:

1. Read the target section's Reforge report with `--all --top-symbols 200`.
2. Inspect top symbols and also top rules; do not rely only on the first ten offenders.
3. Print and classify the section's read and write/full service interfaces.
   For each method, label it as primitive read, settings read, search, command,
   projection, predicate, scalar fact, UI view-model builder, or migration/admin
   escape hatch.
4. For every projection, predicate, scalar fact, or UI builder, ask whether it
   should be derived from the primary `<Section>Info`/read shard. If not, name
   the missing fact and decide whether that fact belongs on the canonical read
   model.
5. For key interfaces/services/repositories/controllers, use Reforge queries such
   as `members`, `dependencies`, `injected`, `callers`, `audit-surface`,
   `audit-downstream`, and `implementations`.
6. Use `rg` to inspect call sites and duplicate call structures.
7. List candidate ideas with expected score movement, likely files, risk,
   architecture rationale, and the concept deleted/added.

Search at least these categories before declaring stasis:

- repo-backed sections missing a clear read surface, write surface, or primary
  `<Section>Info` DTO
- read surface methods that are really LINQ projections/predicates over the
  primary read model, especially `Is...`, `Get...Summary`, `Build...Data`, and
  scalar lookup methods
- facts read through repositories/full services that should live on the
  canonical read DTO or read shard
- cross-section dependencies on write/full services when a read surface or DTO
  fact would suffice; these should be penalized heavily and usually need a
  grandfathered exception until removed
- interface or repository methods with low caller counts, overlapping behavior, or one implementation
- full-service dependencies that should be read DTO/read-service dependencies
- methods where callers only need one or two scalar values from a heavier service path
- duplicate query/include/call sequences that can become one cohesive read shape
- large service/repository/controller clusters whose methods naturally split by workflow
- stale public methods, DTO fields, or wrappers no longer used by production code
- cross-section reads, entity returns, or persistence joins that bypass canonical read models
- long parameter lists only when the fix is a real domain command/query object, not a parameter bag

Then repeat until stasis:

1. Inspect Reforge top symbols/rules for the target section.
2. Read the surrounding code and call graph before editing.
3. Pick the highest-leverage cohesive improvement, not just the highest scoring rule.
4. Write a candidate thesis in the run notes: what concept will be deleted, which responsibility moves to its rightful owner, or which duplicated/cross-section path disappears. If the thesis is just "the score drops", reject the candidate before editing.
5. Make the change.
6. Run targeted tests and `dotnet build Humans.slnx --disable-build-servers -v q`.
7. Run Reforge after the change.
8. Run the score-blind architecture-review gate.
9. If accepted, commit and push. If rework/reject, improve or abandon before committing.

Stasis means the autonomy floor has been met and the remaining ledger is exhausted, blocked by hard limits, score-negative without architectural upside, or too speculative to change without user/product input. A run may stop early only when the user explicitly asks, the repo cannot build for reasons outside the branch, or continuing risks data/schema/contract changes the user did not authorize.

## Architecture Review Gate

Use a separate read-only pass after every candidate stage. Prefer a subagent when multi-agent tools are available; otherwise perform an explicit second-pass review without editing. Read [architecture-reviewer.md](references/architecture-reviewer.md) for the prompt and JSON contract.

The architecture reviewer must be score-blind. Do not provide Reforge before/after scores, rule deltas, weighted values, point savings, "this improved score" framing, or suspicious-improvement labels to the reviewer. The implementer/coordinator still computes those values for candidate selection and commit messages, but the review verdict must be based on the diff and architecture rules alone.

Inputs to the reviewer:

- target section and stated objective
- git diff for the uncommitted stage
- candidate ledger entry and alternatives considered
- build/test results
- notes about behavior or ownership assumptions

Reviewer verdicts:

- `accept`: commit/push.
- `rework`: change the patch and rerun tests/Reforge/review.
- `reject`: abandon the patch or redesign before committing.

Treat Reforge as candidate-selection pressure, not review evidence. Good consolidation removes duplicated call structures, uses canonical read DTOs, and replaces interfaces with small data where appropriate. Bad consolidation hides domain verbs behind generic action/mode dispatchers, grows god methods, moves complexity to private helpers, or shifts debt out of the target section.

The reviewer must reject patches whose main effect is moving methods into a new
helper/static class, replacing parameters with a DTO bag, or reducing a score
without deleting a concept. Acceptable read-model growth should usually add
facts to the existing `<Section>Info`/read shard, not create another projection
service.

The reviewer must also reject request/command/DTO "behavior" whose body only
assigns fields, formats current fields, creates a DTO from the same fields, or
delegates to a service. Accept only when the type owns a real invariant,
validation rule, state transition, normalization rule, or domain policy that
callers should not duplicate.

After the reviewer returns `accept`, attach the Reforge deltas and weighted value
to the commit message. If the reviewer returns `rework` or `reject`, do not use
score movement to argue with the verdict; fix, redesign, or abandon the patch and
rerun tests/Reforge/review.

## Commit Messages

Every accepted commit must include a score and review appendix:

```text
<short imperative subject>

Reforge:
- Target: <Section> <before> -> <after> (<delta>); surface <delta>, internal <delta>
- Overall: <before> -> <after> (<delta>); surface <delta>, internal <delta>
- Weighted value: <target improvement + 2x outside-target improvement>

Architecture-review:
- Verdict: accept
- Grade: good|neutral
- Score-gaming risk: none|low|medium
- Reason: <one or two concise sentences>

Verification:
- <commands run and result>
```

Push after each accepted commit. If a PR does not exist by the time the user asks or the loop reaches stasis, open a draft PR with cumulative score movement, verification, and any commits the reviewer marked as tradeoffs.

## Judgment Rules

- Prefer explicit domain verbs for independent commands.
- Prefer a real state engine for lifecycle transitions.
- Penalize generic action/mode methods that are neither explicit domain verbs nor a real state engine.
- Treat read/query shape objects differently from mutation mode dispatchers; shaped reads are acceptable when they collapse duplicated include/query shapes without growing unbounded.
- For repo-backed sections, prefer a small `I<Section>ServiceRead`, a mutation/full write surface, and a primary `<Section>Info` DTO. Some sections may have explicit read shards keyed differently; document the shard and keep it cohesive.
- Treat the cached DTO as the source of truth for read facts. If a fact about the section is stable enough to be cached and broadly useful, prefer adding it to the existing `<Section>Info`/read shard over adding a service predicate/projection/scalar lookup.
- Prefer adding properties/behavior to an existing canonical read DTO over creating a new interface/service/repository path. Do not treat this as DTO-bag score gaming when it removes service surface and DB reads.
- Prefer canonical read services such as `IUserServiceRead`, `ITeamServiceRead`, and section view DTOs for cross-section reads.
- Penalize cross-section dependencies on write/full services. If unavoidable,
  call out the mutation/orchestration reason; otherwise move the caller to the
  read surface and DTO facts. Existing exceptions should be explicit and
  grandfathered, not silently normalized.
- Keep controllers thin, but allow UI-specific finite sorting/filtering/paging at the controller/view boundary.
- Reject parameter-object refactors whose primary effect is hiding a long method signature. Accept a new input/command object only when it has cohesive domain meaning, public readable semantics appropriate to its boundary, validation/invariants, reuse across related flows, or a materially simpler call site.
- Reject adding thin methods to existing parameter objects when the only deleted code is field assignment, string formatting, DTO construction, or direct service delegation. This is score-gaming even if Reforge reports a large improvement.
- Treat `public` input types with mostly `internal` state as a smell on service interfaces; decorators and cross-assembly callers should not need result objects or side channels to recover data the input already contained.
- For read-surface pruning, ask: "Could the caller express this as a LINQ query
  over `<Section>Info` or a known read shard?" If yes, add missing facts to the
  read model if needed, update callers, and delete the method.

## References

- Use [architecture-reviewer.md](references/architecture-reviewer.md) for the review gate.
- Use [reforge_delta.py](scripts/reforge_delta.py) for score summaries.
