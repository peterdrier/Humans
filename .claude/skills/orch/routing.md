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

Every spawn pays a fixed overhead — system prompt, tool schemas, the CLAUDE.md chain — at the
worker's rate before it reads the brief. So batch small related tasks into one worker rather than
one spawn each, and don't spawn for anything smaller than that overhead.

## Spawning

- Agents: `orch-haiku`, `orch-sonnet-{low,medium,high}`, `orch-opus-{low,medium,high}`,
  `orch-fable-high` — each
  pins model and effort. If they aren't available this session, use `general-purpose` with an
  explicit `model`. Never omit the model. Never use `fork`: it inherits the session's model and
  its whole context.
- Tag the choice so it can be checked at a glance: `name: <task>-<tier>` (e.g.
  `fix-profiles-sonnet-low`), `description: "… (<tier>)"`.
- A worker that writes files gets `isolation: "worktree"` on the Agent call itself; the user's
  CLAUDE.md worktree rules apply to it.

## Return contract

`orch-*` agents already follow this. For any other agent type, put it in the brief:

> Reply in ≤10 lines: STATUS (done | blocked | failed) · CHANGED (branch / commit / paths) ·
> VERIFIED (command → measured result) · DECISIONS NEEDED. No file contents, logs, or diffs —
> write those to the file the brief names and return its path.
