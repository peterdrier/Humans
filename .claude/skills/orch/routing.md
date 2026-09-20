# Subagent routing

The one home for "which model and effort does this subagent get". Any skill that spawns workers
and doesn't ship its own agent definitions routes through this file.

A spawned agent inherits the session's model and effort unless told otherwise, and the session is
usually the most expensive model there is. So every spawn names its tier.

## Pick the model by judgment, the effort by ambiguity

| Model | Use for |
|-------|---------|
| haiku | Mechanical, fully specified, list-driven: renames, applying a known pattern across files, lookups, "where is X" scouting, run-a-command-and-classify, verification runs |
| sonnet | Most real work: multi-file features and fixes, tests, debugging with a clear repro, docs, research |
| opus | Judgment where a wrong call is expensive: design, deletion decisions, security, subtle root-cause, adversarial review |
| fable | Rare. One hard, self-contained question where extra thought changes the answer and opus-high fell short or plainly would. Hand it a distilled input — a findings file or the specific excerpt, never "go read the repo" — because every token it reads is billed at the top rate. It returns an answer, not a build. |

| Effort | Use for |
|--------|---------|
| low | The brief already says exactly what to do; the worker only executes |
| medium | Default — the worker has to work out how |
| high | Ambiguous, subtle, or a review where a miss is costly |

Default is `sonnet-medium`. Start at the lowest tier that plausibly works; on failure escalate one
step — effort first, then model — handing over what the failed attempt learned. Wrong-low costs
one cheap retry; wrong-high is paid on every task.

Every spawn pays a fixed overhead — system prompt, tool schemas, the CLAUDE.md chain — before it
reads the brief: measured at 40–60k tokens (2026-09, Humans repo), roughly $0.05 on haiku,
$0.10–0.15 on sonnet, $0.50+ on fable. Every worker completion also wakes the orchestrator, which
re-reads its whole context at its own rate (about $0.12 a wake at 120k context on fable). So
prefer a few fat workers to many thin ones, batch small related tasks into one worker, and don't
spawn for anything smaller than the overhead. `/spend` shows a run's real numbers.

Resuming a running agent is not the cheap way round this. Its context isn't paid for once — every
wake re-reads all of it at its own rate, so an agent five rounds deep can cost more to wake than a
fresh haiku costs to spawn. Reuse it when the follow-up genuinely needs what it already knows;
otherwise spawn fresh, or do it yourself.

## Spawning

- Agents: `orch-haiku`, `orch-sonnet-{low,medium,high}`, `orch-opus-{low,medium,high}`,
  `orch-fable-high` — each
  pins model and effort. If they aren't available this session, use `general-purpose` with an
  explicit `model`. Never omit the model. Never use `fork`: it inherits the session's model and
  its whole context.
- Tag the choice so it can be checked at a glance: `name: <task>-<tier>` (e.g.
  `fix-profiles-sonnet-low`), `description: "… (<tier>)"`.
- **Where a writer works.** The rule is that no worker edits or commits in the main checkout —
  not that every worker needs a worktree of its own. Pick by deliverable:
  - *Independent deliverable* (its own branch and PR): `isolation: "worktree"` on the Agent call
    itself. It branches from `origin/main`, so it can't see unpushed work.
  - *One deliverable, several workers, different files:* no isolation flag. Give every worker the
    absolute path of the deliverable's worktree (yours, or one you create under
    `.claude/worktrees/`) and have it use absolute paths throughout. Parallel only when their
    file sets don't overlap, otherwise in sequence. One owner for git — you, or the last worker.
  - *One deliverable, several workers, same files — or work in waves:* no isolation flag (it
    always branches from `origin/main`, never from your branch — tested 2026-09-20). Create the
    feature branch, then cut each worker its own worktree from it:
    `git worktree add .claude/worktrees/<task> -b <feature>-<task> <feature>`, and give the worker
    that absolute path. Merge each sub-branch back into the feature branch yourself — it's all
    local, merge output is small — and hand any conflict to a worker. Later waves then start from
    what earlier ones merged.

## Return contract

`orch-*` agents already follow this. For any other agent type, put it in the brief:

> Reply in ≤10 lines: STATUS (done | blocked | failed) · CHANGED (branch / commit / paths) ·
> VERIFIED (command → measured result) · DECISIONS NEEDED. No file contents, logs, or diffs —
> write those to the file the brief names and return its path.
